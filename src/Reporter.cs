using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace SlmapsServerPlugin
{
    // Registration and reporting state machine. Everything except NotifyConfigReloaded runs on the main thread; HTTP runs in the background and comes back through a result queue.
    internal sealed class Reporter
    {
        private enum State
        {
            Idle,
            Registering,
            Registered,
            WaitingForReload,
            Revoked,
            Stopped,
        }

        private enum ClaimState
        {
            None,
            Claiming,
            Review,
        }

        private const int MaxTokenLength = 128;
        private const int MaxResultsPerTick = 64;
        private const int MinClaimRetrySeconds = 30;
        private const int MaxClaimRetrySeconds = 600;
        private const int ShownCodeLength = 12;

        private readonly IReporterHost _host;
        private readonly IApiTransport _transport;
        private readonly Func<double> _now;
        private readonly Func<DateTime> _utcNow;
        private readonly ConcurrentQueue<Action> _results = new ConcurrentQueue<Action>();
        private readonly ReportQueue _queue = new ReportQueue(ReportQueue.DefaultCapacity);
        private readonly RetryBackoff _registerBackoff = new RetryBackoff();
        private readonly RetryBackoff _reportBackoff = new RetryBackoff();
        private readonly RetryBackoff _claimBackoff = new RetryBackoff();
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();

        private ClaimState _claimState = ClaimState.None;
        private string _claimCode;
        private bool _claimFromConfig;
        private string _finishedClaimCode;
        private double _nextClaimAt;
        private string _serverId;

        private State _state = State.Idle;
        private int _generation;
        private int _reloadPending;
        private bool _inFlight;
        private double _nextRegisterAt;
        private double _nextReportAt;
        private string _credential;
        private string _revokedCredential;
        private bool _urlErrorLogged;

        private int _seed;
        private string _roundId;
        private DateTime? _roundStartedAtUtc;
        private double _periodicBaseline;
        // No periodic report between the round end report and the next map, or it would reopen the block window.
        private bool _roundEnded;

        public Reporter(IReporterHost host, IApiTransport transport, Func<double> monotonicSeconds, Func<DateTime> utcNow)
        {
            _host = host;
            _transport = transport;
            _now = monotonicSeconds;
            _utcNow = utcNow;
        }

        internal string StateName
        {
            get { return _state.ToString(); }
        }

        internal bool IsInFlight
        {
            get { return _inFlight; }
        }

        internal int PendingRoundEvents
        {
            get { return _queue.RoundEventCount; }
        }

        internal bool HasPendingPeriodic
        {
            get { return _queue.HasPeriodic; }
        }

        internal int CurrentSeed
        {
            get { return _seed; }
        }

        internal string ClaimStateName
        {
            get { return _claimState.ToString(); }
        }

        public bool StartClaim(string code, out string message)
        {
            string trimmed = (code ?? "").Trim();
            if (_state == State.Stopped)
            {
                message = "The slmaps plugin is not running.";
                return false;
            }
            if (trimmed.Length == 0 || trimmed.Length > MaxTokenLength)
            {
                message = "Usage: slmaps claim <code>  (get the code with /server claim in the slmaps Discord)";
                return false;
            }
            BeginClaim(trimmed, false);
            message = "Claiming this server with code " + ShortCode(trimmed) + ". Watch this console for the result.";
            return true;
        }

        public string DescribeStatus()
        {
            string claim;
            switch (_claimState)
            {
                case ClaimState.Claiming:
                    claim = "in progress (code " + ShortCode(_claimCode) + ")";
                    break;
                case ClaimState.Review:
                    claim = "waiting for manual review by slmaps staff (code " + ShortCode(_claimCode) + ")";
                    break;
                default:
                    claim = "none";
                    break;
            }
            return PluginInfo.Name + " " + PluginInfo.Version
                + "\nstate: " + _state
                + "\nserverId: " + (string.IsNullOrEmpty(_serverId) ? "-" : _serverId)
                + "\nclaim: " + claim
                + "\ncurrent seed: " + (_seed > 0 ? _seed.ToString() : "-")
                + "\napi_base_url: " + (Cfg().ApiBaseUrl ?? "");
        }

        public void Start()
        {
            ResolveRegistration();
        }

        public void Stop()
        {
            _state = State.Stopped;
            _generation++;
            _inFlight = false;
            _queue.Clear();
            try
            {
                _cts.Cancel();
            }
            catch (Exception)
            {
            }
            Action ignored;
            while (_results.TryDequeue(out ignored))
            {
            }
        }

        public void NotifyConfigReloaded()
        {
            Interlocked.Exchange(ref _reloadPending, 1);
        }

        public void OnMapGenerated(int seed)
        {
            if (_state == State.Stopped || seed <= 0)
            {
                return;
            }
            _seed = seed;
            _roundId = Guid.NewGuid().ToString();
            _roundStartedAtUtc = null;
            _roundEnded = false;
            _queue.DropPeriodic();
            _periodicBaseline = _now();
            _host.Debug("Map generated, seed " + seed + ".");
            EnqueueRoundEvent(ReportEvents.RoundStart);
        }

        public void OnRoundStarted()
        {
            if (_state == State.Stopped)
            {
                return;
            }
            _roundStartedAtUtc = _utcNow();
        }

        public void OnRoundEnded()
        {
            if (_state == State.Stopped || _seed <= 0)
            {
                return;
            }
            _roundEnded = true;
            _queue.DropPeriodic();
            EnqueueRoundEvent(ReportEvents.RoundEnd);
        }

        public void Tick()
        {
            if (_state == State.Stopped)
            {
                return;
            }

            Action result;
            int handled = 0;
            while (handled < MaxResultsPerTick && _results.TryDequeue(out result))
            {
                handled++;
                try
                {
                    result();
                }
                catch (Exception ex)
                {
                    _host.Error("Internal error while handling an slmaps response: " + ex.GetType().Name + ": " + ex.Message);
                }
            }

            if (Interlocked.Exchange(ref _reloadPending, 0) == 1)
            {
                HandleReload();
            }

            double now = _now();
            GeneratePeriodic(now);

            if (_inFlight)
            {
                return;
            }
            if (_claimState != ClaimState.None && now >= _nextClaimAt)
            {
                SendClaim();
            }
            else if (_state == State.Registering && now >= _nextRegisterAt)
            {
                SendRegister();
            }
            else if (_state == State.Registered && now >= _nextReportAt)
            {
                SendNextReport();
            }
        }

        private void ResolveRegistration()
        {
            StartConfigClaimIfNew();
            bool claiming = _claimState != ClaimState.None;
            string token = CurrentToken();

            if (_state == State.Revoked)
            {
                if (claiming)
                {
                    _host.Info("Reporting stays stopped until the Discord claim finishes.");
                    return;
                }
                if (token.Length > 0)
                {
                    BeginRegistering();
                    return;
                }
                StoredCredential replacement = SafeLoadCredential();
                if (replacement != null && !string.Equals(replacement.Credential, _revokedCredential, StringComparison.Ordinal))
                {
                    _host.Info("Using the credential in credential.yml (serverId " + replacement.ServerId + ").");
                    UseCredential(replacement.Credential);
                }
                else
                {
                    _host.Warn("Reporting is still stopped because the credential was rejected. Get a new code with /server claim in the slmaps Discord and run `slmaps claim <code>`, "
                        + "or request a new registration token from the slmaps admin, delete " + _host.CredentialFilePath + ", set registration_token and run `labapi reload configs`.");
                }
                return;
            }

            StoredCredential stored = SafeLoadCredential();
            if (stored != null)
            {
                if (token.Length > 0)
                {
                    _host.Info("credential.yml already exists, so registration_token is not used.");
                }
                _host.Info("Using the stored credential (serverId " + stored.ServerId + ").");
                _serverId = stored.ServerId;
                UseCredential(stored.Credential);
                return;
            }

            if (claiming)
            {
                if (token.Length > 0)
                {
                    _host.Info("claim_code takes priority, so registration_token is not used while the claim runs.");
                }
                _state = State.Idle;
                return;
            }

            if (token.Length > 0)
            {
                BeginRegistering();
                return;
            }

            EnterIdle();
        }

        private void HandleReload()
        {
            _urlErrorLogged = false;
            _host.Debug("Configuration reloaded.");
            StartConfigClaimIfNew();
            if (_state == State.Registered || _state == State.Stopped)
            {
                return;
            }
            ResolveRegistration();
        }

        private void BeginRegistering()
        {
            if (_state != State.Registering)
            {
                _host.Info("Registering this server with slmaps...");
            }
            _state = State.Registering;
            _registerBackoff.Reset();
            _nextRegisterAt = 0;
        }

        private void EnterIdle()
        {
            _state = State.Idle;
            _queue.Clear();
            _host.Warn("Not registered with slmaps: there is no credential.yml, claim_code is empty and registration_token is empty. "
                + "Run /server claim in the slmaps Discord and then `slmaps claim <code>` in this console, "
                + "or ask the slmaps admin for a registration token, put it into registration_token in " + _host.ConfigFilePath
                + " and run `labapi reload configs` (or restart the server).");
        }

        private void EnterWaitingForReload(string message)
        {
            _state = State.WaitingForReload;
            _queue.Clear();
            _host.Error(message);
        }

        private void SendRegister()
        {
            string token = CurrentToken();
            if (token.Length == 0)
            {
                EnterIdle();
                return;
            }
            if (token.Length > MaxTokenLength)
            {
                EnterWaitingForReload("registration_token is longer than " + MaxTokenLength + " characters. Paste the token exactly as issued, then run `labapi reload configs`.");
                return;
            }
            string url;
            if (!TryBuildUrl(PluginInfo.RegisterPath, out url))
            {
                return;
            }
            int port = _host.Port;
            string json = Payloads.BuildRegister(token, port, PluginInfo.Version, _host.GameVersion, _host.LabApiVersion);
            _host.Debug("POST " + PluginInfo.RegisterPath + " (port " + port + ").");
            Send(url, json, null, r => OnRegisterResult(r, token));
        }

        private void OnRegisterResult(ApiResult r, string usedToken)
        {
            if (r.IsSuccess)
            {
                // The token is used up the moment this succeeds, so the credential is always saved.
                string credential = r.GetString("credential");
                string serverId = r.GetString("serverId") ?? "";
                string issuedAt = r.GetString("issuedAt") ?? "";
                if (string.IsNullOrEmpty(credential))
                {
                    EnterWaitingForReload("slmaps accepted the registration (HTTP " + r.StatusCode + ") but returned no credential. "
                        + "The token is used up; request a new one from the slmaps admin.");
                    return;
                }
                StoredCredential stored = new StoredCredential { ServerId = serverId, Credential = credential, IssuedAt = issuedAt };
                if (!_host.SaveCredential(stored))
                {
                    _host.Error("Registered, but " + _host.CredentialFilePath + " could not be written. Reporting works until the server restarts; "
                        + "fix the file permissions and request a new registration token afterwards.");
                }
                _host.ClearRegistrationToken(usedToken);
                _host.Info("Registered with slmaps (serverId " + serverId + "). The credential was saved to credential.yml and registration_token was cleared.");
                _serverId = serverId;
                UseCredential(credential);
                return;
            }

            if (_state != State.Registering)
            {
                return;
            }

            int timeout = CurrentTimeout();
            if (r.HasResponse && r.StatusCode == 401)
            {
                EnterWaitingForReload("slmaps rejected registration_token (HTTP 401): the token is invalid, already used or expired. "
                    + "Request a new token from the slmaps admin, set registration_token and run `labapi reload configs`.");
                return;
            }
            if (r.HasResponse && r.StatusCode == 400)
            {
                EnterWaitingForReload("slmaps rejected the registration request as invalid (" + r.Describe(timeout) + "). "
                    + "Check registration_token, then run `labapi reload configs`.");
                return;
            }

            int delay = _registerBackoff.NextDelaySeconds();
            _nextRegisterAt = _now() + delay;
            _host.Warn("Registration failed (" + r.Describe(timeout) + "); retrying in " + delay + "s." + RedirectHint(r));
        }

        private void UseCredential(string credential)
        {
            _credential = credential;
            _revokedCredential = null;
            _state = State.Registered;
            _reportBackoff.Reset();
            _nextReportAt = 0;
            _periodicBaseline = _now();
            if (_seed > 0 && !_queue.HasRoundEventFor(_seed))
            {
                EnqueueRoundEvent(ReportEvents.RoundStart);
            }
        }

        private void EnqueueRoundEvent(string eventName)
        {
            if (_state != State.Registering && _state != State.Registered)
            {
                return;
            }
            int dropped = _queue.EnqueueRoundEvent(Snapshot(eventName));
            if (dropped > 0)
            {
                _host.Warn("Report queue is full; dropped " + dropped + " old round event(s).");
            }
        }

        private void GeneratePeriodic(double now)
        {
            if (_state != State.Registered || _seed <= 0 || _roundEnded)
            {
                return;
            }
            int interval = PluginConfig.EffectiveReportInterval(Cfg().ReportIntervalSeconds);
            if (interval <= 0)
            {
                _queue.DropPeriodic();
                return;
            }
            if (now - _periodicBaseline < interval)
            {
                return;
            }
            _periodicBaseline = now;
            _queue.SetPeriodic(Snapshot(ReportEvents.Periodic));
        }

        private ReportSnapshot Snapshot(string eventName)
        {
            double elapsed;
            bool started = _host.TryGetRoundElapsedSeconds(out elapsed);
            return new ReportSnapshot
            {
                Event = eventName,
                Seed = _seed,
                Port = _host.Port,
                RoundId = _roundId,
                RoundStartedAtUtc = _roundStartedAtUtc,
                ElapsedSeconds = started ? elapsed : (double?)null,
                CreatedAt = _now(),
            };
        }

        private void SendNextReport()
        {
            string url;
            if (!TryBuildUrl(PluginInfo.ReportPath, out url))
            {
                return;
            }
            PluginConfig cfg = Cfg();
            ReportSnapshot item = _queue.TakeNext(_seed, PluginConfig.EffectiveReportInterval(cfg.ReportIntervalSeconds) > 0);
            if (item == null)
            {
                return;
            }
            string json = Payloads.BuildReport(item, cfg.SendRoundId, cfg.SendRoundStartTime, cfg.SendElapsedTime, PluginInfo.Version);
            string credential = _credential;
            _host.Debug("POST " + PluginInfo.ReportPath + " " + item.Event + " seed " + item.Seed + ".");
            Send(url, json, credential, r => OnReportResult(r, item, credential));
        }

        private void OnReportResult(ApiResult r, ReportSnapshot item, string credential)
        {
            if (_state != State.Registered || !string.Equals(credential, _credential, StringComparison.Ordinal))
            {
                return;
            }

            int timeout = CurrentTimeout();
            if (r.IsSuccess)
            {
                _reportBackoff.Reset();
                _nextReportAt = 0;
                string blockedUntil = r.GetString("blockedUntil");
                _host.Debug("Report " + item.Event + " seed " + item.Seed + " accepted (blockedUntil " + (blockedUntil ?? "null") + ").");
                return;
            }

            if (r.HasResponse && r.StatusCode == 401)
            {
                _revokedCredential = credential;
                _credential = null;
                _state = State.Revoked;
                _queue.Clear();
                _host.Error("slmaps rejected the server credential (HTTP 401): it was revoked, replaced by a newer claim, "
                    + "or the report came from an IP other than the verified one. Reporting has stopped. "
                    + "Get a new code with /server claim in the slmaps Discord and run `slmaps claim <code>`, "
                    + "or request a new registration token from the slmaps admin and delete " + _host.CredentialFilePath
                    + ", then set registration_token and run `labapi reload configs`.");
                if (_claimState == ClaimState.None && CurrentToken().Length > 0)
                {
                    BeginRegistering();
                }
                return;
            }

            if (r.HasResponse && r.StatusCode == 400)
            {
                _host.Warn("slmaps rejected a " + item.Event + " report as invalid (" + r.Describe(timeout) + "); dropping it.");
                return;
            }

            bool kept = _queue.ReturnFailed(item, _seed);
            int delay = _reportBackoff.NextDelaySeconds();
            _nextReportAt = _now() + delay;
            _host.Warn("Report " + item.Event + " failed (" + r.Describe(timeout) + "); "
                + (kept ? "retrying in " : "dropped as stale; next report in ") + delay + "s." + RedirectHint(r));
        }

        private void StartConfigClaimIfNew()
        {
            string code = CurrentClaimCode();
            if (code.Length == 0
                || string.Equals(code, _claimCode, StringComparison.Ordinal)
                || string.Equals(code, _finishedClaimCode, StringComparison.Ordinal))
            {
                return;
            }
            if (code.Length > MaxTokenLength)
            {
                _finishedClaimCode = code;
                _host.Error("claim_code is longer than " + MaxTokenLength + " characters. Paste the code exactly as the Discord bot sent it.");
                return;
            }
            BeginClaim(code, true);
        }

        private void BeginClaim(string code, bool fromConfig)
        {
            _claimCode = code;
            _claimFromConfig = fromConfig;
            _claimState = ClaimState.Claiming;
            _claimBackoff.Reset();
            _nextClaimAt = 0;
            _host.Info("Claiming this server with a slmaps Discord code (" + ShortCode(code) + ").");
        }

        private void SendClaim()
        {
            string code = _claimCode;
            if (string.IsNullOrEmpty(code))
            {
                _claimState = ClaimState.None;
                return;
            }
            string url;
            if (!TryBuildUrl(PluginInfo.ClaimPath, out url))
            {
                _nextClaimAt = _now() + MinClaimRetrySeconds;
                return;
            }
            int port = _host.Port;
            string json = Payloads.BuildClaim(code, port, PluginInfo.Version, _host.GameVersion, _host.LabApiVersion);
            _host.Debug("POST " + PluginInfo.ClaimPath + " (port " + port + ").");
            Send(url, json, null, r => OnClaimResult(r, code));
        }

        private void OnClaimResult(ApiResult r, string code)
        {
            if (r.HasResponse && r.StatusCode == 200)
            {
                // The code is used up the moment this succeeds, so the credential is always saved.
                string credential = r.GetString("credential");
                string serverId = r.GetString("serverId") ?? "";
                string issuedAt = r.GetString("issuedAt") ?? "";
                bool wasConfig = _claimFromConfig && string.Equals(code, _claimCode, StringComparison.Ordinal);
                if (string.IsNullOrEmpty(credential))
                {
                    FailClaim(code, "slmaps accepted the claim (HTTP 200) but returned no credential. The code is used up; run /server claim again.");
                    return;
                }
                StoredCredential stored = new StoredCredential { ServerId = serverId, Credential = credential, IssuedAt = issuedAt };
                if (!_host.SaveCredential(stored))
                {
                    _host.Error("Claimed, but " + _host.CredentialFilePath + " could not be written. Reporting works until the server restarts; "
                        + "fix the file permissions and claim again afterwards.");
                }
                if (wasConfig)
                {
                    _host.ClearClaimCode(code);
                }
                FinishClaim(code);
                _host.Info("slmaps verified this server (serverId " + serverId + "). The credential was saved to credential.yml"
                    + (wasConfig ? " and claim_code was cleared." : "."));
                _serverId = serverId;
                UseCredential(credential);
                return;
            }

            if (!string.Equals(code, _claimCode, StringComparison.Ordinal))
            {
                return;
            }

            int timeout = CurrentTimeout();
            if (r.HasResponse && r.StatusCode == 202)
            {
                string reason = r.GetString("reason");
                int retry = (int)Math.Round(r.GetNumber("retryAfterSeconds", 60));
                retry = Math.Max(MinClaimRetrySeconds, Math.Min(MaxClaimRetrySeconds, retry));
                if (_claimState != ClaimState.Review)
                {
                    _host.Info("slmaps staff must review this claim manually (" + ReasonText(reason) + "). "
                        + "Keep the server running; the plugin checks again every " + retry + "s and you will be notified on Discord.");
                }
                else
                {
                    _host.Debug("Claim still under review; checking again in " + retry + "s.");
                }
                _claimState = ClaimState.Review;
                _claimBackoff.Reset();
                _nextClaimAt = _now() + retry;
                return;
            }
            if (r.HasResponse && r.StatusCode == 401)
            {
                FailClaim(code, "slmaps rejected the claim code (HTTP 401): it is invalid, expired, already used or cancelled. "
                    + "Run /server claim in the slmaps Discord to get a new code.");
                return;
            }
            if (r.HasResponse && r.StatusCode == 403)
            {
                FailClaim(code, "slmaps staff rejected this claim (HTTP 403). The reason was sent to you on Discord.");
                return;
            }
            if (r.HasResponse && r.StatusCode == 400)
            {
                FailClaim(code, "slmaps rejected the claim request as invalid (" + r.Describe(timeout) + "). Check the code and try again.");
                return;
            }

            int delay = _claimBackoff.NextDelaySeconds();
            _nextClaimAt = _now() + delay;
            _host.Warn("Claim request failed (" + r.Describe(timeout) + "); retrying in " + delay + "s." + RedirectHint(r));
        }

        private void FinishClaim(string code)
        {
            _finishedClaimCode = code;
            if (string.Equals(code, _claimCode, StringComparison.Ordinal))
            {
                _claimCode = null;
                _claimState = ClaimState.None;
            }
        }

        private void FailClaim(string code, string message)
        {
            _host.Error(message);
            FinishClaim(code);
            if (_state != State.Registered && _state != State.Stopped)
            {
                ResolveRegistration();
            }
        }

        private static string ReasonText(string reason)
        {
            switch (reason)
            {
                case "ip_mismatch":
                    return "this server's IP differs from the address given on Discord";
                case "port_mismatch":
                    return "this server's port differs from the address given on Discord";
                default:
                    return string.IsNullOrEmpty(reason) ? "no reason given" : reason;
            }
        }

        // Only the first characters of a code ever reach a log line.
        private static string ShortCode(string code)
        {
            if (string.IsNullOrEmpty(code))
            {
                return "-";
            }
            return code.Length <= ShownCodeLength ? code : code.Substring(0, ShownCodeLength) + "...";
        }

        private void Send(string url, string json, string bearer, Action<ApiResult> onResult)
        {
            _inFlight = true;
            int generation = _generation;
            int timeout = CurrentTimeout();
            CancellationToken token = _cts.Token;
            IApiTransport transport = _transport;
            ConcurrentQueue<Action> results = _results;
            try
            {
                Task.Run(async () =>
                {
                    ApiResult result;
                    try
                    {
                        result = await transport.PostJsonAsync(url, json, bearer, timeout, token).ConfigureAwait(false);
                        if (result == null)
                        {
                            result = ApiResult.FromFailure(ApiFailure.Network, "no result");
                        }
                    }
                    catch (Exception ex)
                    {
                        result = ApiResult.FromFailure(ApiFailure.Network, ex.GetType().Name + ": " + ex.Message);
                    }
                    results.Enqueue(() =>
                    {
                        if (generation != _generation)
                        {
                            return;
                        }
                        _inFlight = false;
                        onResult(result);
                    });
                });
            }
            catch (Exception ex)
            {
                _inFlight = false;
                _host.Error("Could not start an slmaps request: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private bool TryBuildUrl(string path, out string url)
        {
            url = null;
            string baseUrl = (Cfg().ApiBaseUrl ?? "").Trim().TrimEnd('/');
            Uri uri;
            if (baseUrl.Length == 0
                || !Uri.TryCreate(baseUrl + path, UriKind.Absolute, out uri)
                || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            {
                if (!_urlErrorLogged)
                {
                    _urlErrorLogged = true;
                    _host.Error("api_base_url is not a valid http(s) URL. Fix it in " + _host.ConfigFilePath + " and run `labapi reload configs`.");
                }
                return false;
            }
            url = uri.AbsoluteUri;
            return true;
        }

        private StoredCredential SafeLoadCredential()
        {
            try
            {
                StoredCredential c = _host.LoadCredential();
                if (c == null || string.IsNullOrEmpty(c.Credential))
                {
                    return null;
                }
                return c;
            }
            catch (Exception ex)
            {
                _host.Error("Could not read credential.yml: " + ex.GetType().Name + ": " + ex.Message);
                return null;
            }
        }

        private PluginConfig Cfg()
        {
            return _host.Config ?? new PluginConfig();
        }

        private string CurrentToken()
        {
            return (Cfg().RegistrationToken ?? "").Trim();
        }

        private string CurrentClaimCode()
        {
            return (Cfg().ClaimCode ?? "").Trim();
        }

        private int CurrentTimeout()
        {
            return PluginConfig.EffectiveRequestTimeout(Cfg().RequestTimeoutSeconds);
        }

        private static string RedirectHint(ApiResult r)
        {
            if (r.HasResponse && r.StatusCode >= 300 && r.StatusCode < 400)
            {
                return " The server answered with a redirect; check api_base_url (it must be https://slmaps.com unless told otherwise).";
            }
            return "";
        }
    }
}
