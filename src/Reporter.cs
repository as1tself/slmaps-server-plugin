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

        private const int MaxTokenLength = 128;
        private const int MaxResultsPerTick = 64;

        private readonly IReporterHost _host;
        private readonly IApiTransport _transport;
        private readonly Func<double> _now;
        private readonly Func<DateTime> _utcNow;
        private readonly ConcurrentQueue<Action> _results = new ConcurrentQueue<Action>();
        private readonly ReportQueue _queue = new ReportQueue(ReportQueue.DefaultCapacity);
        private readonly RetryBackoff _registerBackoff = new RetryBackoff();
        private readonly RetryBackoff _reportBackoff = new RetryBackoff();
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();

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
            if (_state == State.Registering && now >= _nextRegisterAt)
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
            string token = CurrentToken();

            if (_state == State.Revoked)
            {
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
                    _host.Warn("Reporting is still stopped because the credential was revoked. Request a new registration token from the slmaps admin, delete "
                        + _host.CredentialFilePath + ", set registration_token and run `labapi reload configs`.");
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
                UseCredential(stored.Credential);
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
            _host.Warn("Not registered with slmaps: there is no credential.yml and registration_token is empty. "
                + "Ask the slmaps admin for a registration token, put it into registration_token in " + _host.ConfigFilePath
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
                _host.Error("slmaps rejected the server credential (HTTP 401): it was revoked or no longer exists. Reporting has stopped. "
                    + "Request a new registration token from the slmaps admin and delete " + _host.CredentialFilePath
                    + ", then set registration_token and run `labapi reload configs`.");
                if (CurrentToken().Length > 0)
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
