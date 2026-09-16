using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
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
        private const int MaxFinishedClaimCodes = 16;

        internal const double FirstVersionCheckDelaySeconds = 10;
        internal const double VersionCheckIntervalSeconds = 12 * 60 * 60;
        private const double VersionUrlRetrySeconds = 60;

        private const string LevelInfo = "info";
        private const string LevelWarn = "warn";
        private const string LevelError = "error";

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
        private readonly EventLog _events = new EventLog();

        private ClaimState _claimState = ClaimState.None;
        private string _claimCode;
        private bool _claimFromConfig;
        private int _claimAttempt;
        private int _claimInFlightAttempt;
        private readonly List<string> _finishedClaimCodes = new List<string>();
        private string _malformedConfigClaimCode;
        private double _nextClaimAt;
        private string _serverId;
        private string _lastClaimResult;
        private string _lastReportResult;

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

        private bool _versionInFlight;
        private bool _versionManualPending;
        private double _nextVersionCheckAt;
        private bool _versionKnown;
        private string _knownLatest;
        private string _knownMinimum;
        private string _downloadUrl;
        private string _lastVersionCheckResult;
        private string _warnedLatest;
        private bool _outdated;
        private string _outdatedMinimum;

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

        internal bool IsOutdated
        {
            get { return _outdated; }
        }

        internal bool IsVersionCheckInFlight
        {
            get { return _versionInFlight; }
        }

        public bool StartClaim(string code, out string message)
        {
            string trimmed = (code ?? "").Trim();
            if (_state == State.Stopped)
            {
                message = "The slmaps plugin is not running.";
                return false;
            }
            if (trimmed.Length == 0)
            {
                message = ConsoleCommandParser.ClaimUsage;
                return false;
            }
            if (_outdated)
            {
                Record(LevelWarn, "Console claim code " + ShortCode(trimmed) + " was not sent: this plugin version is below the slmaps minimum.");
                message = "Claim code not sent. " + OutdatedSentence() + " Then run slmaps claim <code> again.";
                return false;
            }
            string problem = ClaimCodeFormat.Problem(trimmed);
            if (problem != null)
            {
                Record(LevelWarn, "Console claim code " + ShortCode(trimmed) + " was not sent: " + FirstLine(problem));
                message = "Claim code not sent. " + problem;
                return false;
            }
            if (_claimState != ClaimState.None && string.Equals(trimmed, _claimCode, StringComparison.Ordinal))
            {
                if (CurrentClaimInFlight())
                {
                    message = "The claim with code " + ShortCode(trimmed) + " was already sent.\nThe result appears in this console.";
                    return true;
                }
                _nextClaimAt = 0;
                message = "A claim with code " + ShortCode(trimmed) + " is already in progress.\n"
                    + (_inFlight ? "It is sent as soon as the current slmaps request finishes." : "Checking again now.");
                return true;
            }
            string replaced = _claimState != ClaimState.None ? _claimCode : null;
            bool configHadReplaced = replaced != null && string.Equals(CurrentClaimCode(), replaced, StringComparison.Ordinal);
            if (configHadReplaced)
            {
                _host.ClearClaimCode(replaced);
            }
            bool configCleared = configHadReplaced && CurrentClaimCode().Length == 0;
            BeginClaim(trimmed, false);
            message = "Claiming this server with code " + ShortCode(trimmed)
                + (replaced != null ? " (replaces the pending code " + ShortCode(replaced) + ")" : "") + ".\n"
                + (configCleared ? "claim_code was cleared from config.yml. " : configHadReplaced ? "Remove the old claim_code from config.yml by hand. " : "")
                + (_inFlight ? "It is sent as soon as the current slmaps request finishes." : "Sending it now.");
            return true;
        }

        public bool CancelClaim(out string message)
        {
            if (_state == State.Stopped)
            {
                message = "The slmaps plugin is not running.";
                return false;
            }
            if (_claimState == ClaimState.None)
            {
                message = "No claim is in progress.";
                return false;
            }
            string code = _claimCode;
            bool alreadySent = _claimInFlightAttempt != 0;
            bool configHadCode = string.Equals(CurrentClaimCode(), code, StringComparison.Ordinal);
            _claimAttempt++;
            MarkClaimFinished(code);
            _claimCode = null;
            _claimFromConfig = false;
            _claimState = ClaimState.None;
            _claimBackoff.Reset();
            _nextClaimAt = 0;
            if (configHadCode)
            {
                _host.ClearClaimCode(code);
            }
            bool configCleared = configHadCode && CurrentClaimCode().Length == 0;
            SetLastClaim("cancelled from the console (code " + ShortCode(code) + ")");
            Record(LevelInfo, "Claim with code " + ShortCode(code) + " cancelled from the console" + (configCleared ? "; claim_code was cleared." : "."));
            message = "Cancelled the claim with code " + ShortCode(code) + "."
                + (configCleared ? "\nclaim_code was cleared from config.yml." : configHadCode ? "\nRemove claim_code from config.yml by hand." : "")
                + (alreadySent ? "\nThe request was already sent; if slmaps verifies it, the credential is still saved." : "");
            if (_state == State.Idle || _state == State.Revoked)
            {
                ResolveRegistration();
            }
            return true;
        }

        public bool QueueReportNow(out string message)
        {
            if (_outdated && _state != State.Stopped)
            {
                message = "Report not queued. " + OutdatedSentence();
                return false;
            }
            if (_state != State.Registered && _state != State.Stopped && _claimState != ClaimState.None)
            {
                message = "A claim with code " + ShortCode(_claimCode) + " is in progress ("
                    + (_claimState == ClaimState.Review ? "waiting for manual review by slmaps staff" : "being sent")
                    + "); reports start automatically once slmaps verifies it.\nSee slmaps status.";
                return false;
            }
            switch (_state)
            {
                case State.Registered:
                    break;
                case State.Stopped:
                    message = "The slmaps plugin is not running.";
                    return false;
                case State.Registering:
                    message = "This server is still registering with slmaps.\nTry again when registration has finished; see slmaps status.";
                    return false;
                case State.Revoked:
                    message = "Reporting is stopped: slmaps rejected the server credential.\nRun /server claim in the slmaps Discord, then slmaps claim <code>.";
                    return false;
                default:
                    message = "This server is not registered with slmaps.\nRun /server claim in the slmaps Discord, then slmaps claim <code>.";
                    return false;
            }
            if (_seed <= 0)
            {
                message = "No map has been generated yet, so there is no seed to report.";
                return false;
            }
            double now = _now();
            bool behindOthers = _inFlight || _queue.RoundEventCount > 0 || (_claimState != ClaimState.None && now >= _nextClaimAt);
            ReportSnapshot snapshot = Snapshot(ReportEvents.Periodic);
            snapshot.Manual = true;
            _queue.SetPeriodic(snapshot);
            _periodicBaseline = now;
            _nextReportAt = 0;
            Record(LevelInfo, "Report of seed " + _seed + " queued from the console.");
            message = "Queued a report of seed " + _seed + ".\n"
                + (behindOthers ? "It is sent after the slmaps requests ahead of it." : "Sending it now.");
            return true;
        }

        public string DescribeStatus()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(PluginInfo.Name).Append(' ').Append(PluginInfo.Version)
                .Append("\nversion check: ").Append(VersionStatusText(true))
                .Append("\nstate: ").Append(_state).Append(StateHint())
                .Append(_outdated && _state != State.Stopped ? " [paused: this plugin version is below the slmaps minimum]" : "")
                .Append("\nserverId: ").Append(string.IsNullOrEmpty(_serverId) ? "-" : _serverId)
                .Append("\nclaim: ").Append(DescribeClaim(_now()))
                .Append("\nlast claim result: ").Append(_lastClaimResult ?? "-")
                .Append("\ncurrent seed: ").Append(_seed > 0 ? _seed.ToString(CultureInfo.InvariantCulture) : "-")
                .Append("\nlast report: ").Append(_lastReportResult ?? "none yet")
                .Append("\napi_base_url: ").Append(Cfg().ApiBaseUrl ?? "");
            return sb.ToString();
        }

        public string DescribeEvents(int count)
        {
            int n = Math.Max(1, Math.Min(count, EventLog.Capacity));
            List<string> lines = _events.Last(n);
            if (lines.Count == 0)
            {
                return "No plugin events recorded yet.";
            }
            return "Last " + lines.Count + " plugin event(s), oldest first, server local time:\n" + string.Join("\n", lines.ToArray());
        }

        private string StateHint()
        {
            switch (_state)
            {
                case State.Idle:
                    return " (not registered)";
                case State.Registering:
                    return " (registering with registration_token)";
                case State.Registered:
                    return " (reporting)";
                case State.WaitingForReload:
                    return " (fix config.yml, then run labapi reload configs)";
                case State.Revoked:
                    return " (reporting stopped: the credential was rejected; claim again)";
                default:
                    return "";
            }
        }

        private string DescribeClaim(double now)
        {
            if (_claimState == ClaimState.None)
            {
                return "none";
            }
            string timing;
            if (CurrentClaimInFlight())
            {
                timing = "request sent, waiting for the answer";
            }
            else if (_outdated)
            {
                timing = "paused until the plugin is updated";
            }
            else if (_inFlight)
            {
                timing = "sent as soon as the current slmaps request finishes";
            }
            else if (_nextClaimAt > now)
            {
                timing = "next attempt in " + (int)Math.Ceiling(_nextClaimAt - now) + "s";
            }
            else
            {
                timing = "sending now";
            }
            return (_claimState == ClaimState.Review ? "waiting for manual review by slmaps staff" : "in progress")
                + " (code " + ShortCode(_claimCode) + "; " + timing + ")";
        }

        public void Start()
        {
            Record(LevelInfo, PluginInfo.Name + " " + PluginInfo.Version + " started.");
            _nextVersionCheckAt = _now() + FirstVersionCheckDelaySeconds;
            ResolveRegistration();
        }

        public void Stop()
        {
            _state = State.Stopped;
            _generation++;
            _inFlight = false;
            _versionInFlight = false;
            _versionManualPending = false;
            _claimInFlightAttempt = 0;
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

        /// <summary>
        /// A round restart without RoundEnded (forced restart). Sends round_end for the current seed so the server
        /// releases its block window at once; after a normal RoundEnded nothing is sent twice.
        /// </summary>
        public void OnRoundRestarted()
        {
            if (_roundEnded)
            {
                return;
            }
            _host.Debug("Round restarted without RoundEnded, reporting round_end.");
            OnRoundEnded();
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
            // The version check has its own slot: it never waits for a register, claim or report, and never delays one.
            if (!_versionInFlight && now >= _nextVersionCheckAt)
            {
                StartVersionCheck(now);
            }
            GeneratePeriodic(now);

            if (_inFlight)
            {
                return;
            }
            if (_outdated)
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
                    Record(LevelInfo, "Reporting stays stopped until the Discord claim finishes.");
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
                    Record(LevelInfo, "Using the credential in credential.yml (serverId " + replacement.ServerId + ").");
                    UseCredential(replacement.Credential);
                }
                else
                {
                    _host.Warn("Reporting is still stopped because the credential was rejected.\n"
                        + "Run /server claim in the slmaps Discord, then slmaps claim <code>.");
                    Record(LevelWarn, "Reporting is still stopped because the credential was rejected.");
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
                Record(LevelInfo, "Using the stored credential (serverId " + stored.ServerId + ").");
                _serverId = stored.ServerId;
                UseCredential(stored.Credential);
                return;
            }

            if (claiming)
            {
                if (token.Length > 0)
                {
                    _host.Info("claim_code takes priority, so registration_token is not used while the claim runs.");
                    Record(LevelInfo, "registration_token is not used while the claim runs.");
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
            Record(LevelInfo, "Configuration reloaded.");
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
                Record(LevelInfo, "Registering with registration_token.");
            }
            _state = State.Registering;
            _registerBackoff.Reset();
            _nextRegisterAt = 0;
        }

        private void EnterIdle()
        {
            _state = State.Idle;
            _queue.Clear();
            string claimCode = CurrentClaimCode();
            string claimPart;
            string claimEvent;
            if (claimCode.Length == 0)
            {
                claimPart = "claim_code is empty";
                claimEvent = "no credential.yml, claim_code or registration_token.";
            }
            else if (ClaimCodeFormat.Problem(claimCode) != null)
            {
                claimPart = "claim_code is not a valid claim code";
                claimEvent = "claim_code is not a valid claim code; no credential.yml or registration_token.";
            }
            else
            {
                claimPart = "claim_code holds a code that was already rejected, used, replaced or cancelled";
                claimEvent = "claim_code was already rejected, used, replaced or cancelled; no credential.yml or registration_token.";
            }
            _host.Warn("Not registered with slmaps: there is no credential.yml, " + claimPart + " and registration_token is empty.\n"
                + "Run /server claim in the slmaps Discord, then slmaps claim <code> in this console.");
            Record(LevelWarn, "Not registered: " + claimEvent);
        }

        private void EnterWaitingForReload(string message, string eventText)
        {
            _state = State.WaitingForReload;
            _queue.Clear();
            _host.Error(message);
            Record(LevelError, eventText);
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
                EnterWaitingForReload("registration_token is longer than " + MaxTokenLength + " characters.\nPaste the token exactly as issued, then run labapi reload configs.",
                    "registration_token is too long; it was not sent.");
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
                    EnterWaitingForReload("slmaps accepted the registration (HTTP " + r.StatusCode + ") but returned no credential.\n"
                        + "The token is used up; ask slmaps staff for a new one.", "Registration accepted, but no credential was returned.");
                    return;
                }
                StoredCredential stored = new StoredCredential { ServerId = serverId, Credential = credential, IssuedAt = issuedAt };
                if (!_host.SaveCredential(stored))
                {
                    _host.Error("Registered, but " + _host.CredentialFilePath + " could not be written.\n"
                        + "Reporting works until the server restarts; fix the file permissions and register again.");
                    Record(LevelError, "Registered, but credential.yml could not be written.");
                }
                _host.ClearRegistrationToken(usedToken);
                _host.Info("Registered with slmaps (serverId " + serverId + ").\nThe credential was saved to credential.yml and registration_token was cleared.");
                Record(LevelInfo, "Registered with slmaps (serverId " + serverId + ").");
                _serverId = serverId;
                UseCredential(credential);
                return;
            }

            if (OutdatedAnswer.Matches(r))
            {
                EnterOutdated(OutdatedAnswer.Minimum(r), OutdatedAnswer.DownloadUrl(r), "HTTP 426 on register");
                return;
            }

            if (_state != State.Registering)
            {
                return;
            }

            int timeout = CurrentTimeout();
            if (r.HasResponse && r.StatusCode == 401)
            {
                EnterWaitingForReload("slmaps rejected registration_token (HTTP 401): the token is invalid, already used or expired.\n"
                    + "Ask slmaps staff for a new token, set registration_token and run labapi reload configs.",
                    "Registration rejected (HTTP 401): registration_token is invalid, already used or expired.");
                return;
            }
            if (r.HasResponse && r.StatusCode == 400)
            {
                EnterWaitingForReload("slmaps rejected the registration request as invalid (" + r.Describe(timeout) + ").\n"
                    + "Check registration_token, then run labapi reload configs.",
                    "Registration rejected as invalid (" + r.Describe(timeout) + ").");
                return;
            }

            int delay = _registerBackoff.NextDelaySeconds();
            _nextRegisterAt = _now() + delay;
            _host.Warn("Registration failed (" + r.Describe(timeout) + "); retrying in " + delay + "s." + RedirectHint(r));
            Record(LevelWarn, "Registration failed (" + r.Describe(timeout) + "); retrying in " + delay + "s.");
        }

        private void UseCredential(string credential)
        {
            _credential = credential;
            _revokedCredential = null;
            _state = State.Registered;
            _reportBackoff.Reset();
            _nextReportAt = 0;
            _periodicBaseline = _now();
            // 1.2.2: a credential that arrives after RoundEnded (lobby before the next map) must not announce the finished
            // seed — that opened a window for a dead seed and, with one window per server, delayed the next map's block.
            if (_seed > 0 && !_roundEnded && !_queue.HasRoundEventFor(_seed))
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
            if (_outdated)
            {
                return;
            }
            int dropped = _queue.EnqueueRoundEvent(Snapshot(eventName));
            if (dropped > 0)
            {
                _host.Warn("Report queue is full; dropped " + dropped + " old round event(s).");
                Record(LevelWarn, "Report queue is full; dropped " + dropped + " old round event(s).");
            }
        }

        private void GeneratePeriodic(double now)
        {
            if (_state != State.Registered || _seed <= 0 || _roundEnded || _outdated)
            {
                return;
            }
            int interval = PluginConfig.EffectiveReportInterval(Cfg().ReportIntervalSeconds);
            if (interval <= 0)
            {
                _queue.DropAutomaticPeriodic();
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
            if (OutdatedAnswer.Matches(r))
            {
                SetLastReport(item.Event + " seed " + item.Seed + " rejected (HTTP 426: plugin version below the slmaps minimum), dropped");
                EnterOutdated(OutdatedAnswer.Minimum(r), OutdatedAnswer.DownloadUrl(r), "HTTP 426 on report");
                return;
            }
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
                string blocked = blockedUntil == null ? "no block window" : "blocked until " + LocalTimeText(blockedUntil);
                if (item.Manual)
                {
                    _host.Info("Report of seed " + item.Seed + " accepted (" + blocked + ").");
                }
                else
                {
                    _host.Debug("Report " + item.Event + " seed " + item.Seed + " accepted (blockedUntil " + (blockedUntil ?? "null") + ").");
                }
                SetLastReport(item.Event + " seed " + item.Seed + " accepted, " + blocked);
                Record(LevelInfo, "Report " + item.Event + " seed " + item.Seed + " accepted" + (item.Manual ? " (requested from the console)." : "."));
                return;
            }

            if (r.HasResponse && r.StatusCode == 401)
            {
                _revokedCredential = credential;
                _credential = null;
                _state = State.Revoked;
                _queue.Clear();
                _host.Error("slmaps rejected the server credential (HTTP 401) and reporting has stopped: it was revoked, replaced by a newer claim, or sent from an IP other than the verified one.\n"
                    + "Run /server claim in the slmaps Discord, then slmaps claim <code>. With a registration token, delete " + _host.CredentialFilePath + " first.");
                SetLastReport(item.Event + " seed " + item.Seed + " rejected (HTTP 401), reporting stopped");
                Record(LevelError, "Report rejected (HTTP 401): the credential was revoked, replaced or used from another IP. Reporting stopped.");
                if (_claimState == ClaimState.None && CurrentToken().Length > 0)
                {
                    BeginRegistering();
                }
                return;
            }

            if (r.HasResponse && r.StatusCode == 400)
            {
                _host.Warn("slmaps rejected a " + item.Event + " report as invalid (" + r.Describe(timeout) + "); dropping it.");
                SetLastReport(item.Event + " seed " + item.Seed + " rejected as invalid (" + r.Describe(timeout) + ")");
                Record(LevelWarn, "Report " + item.Event + " seed " + item.Seed + " rejected as invalid (" + r.Describe(timeout) + "); dropped.");
                return;
            }

            bool kept = !_outdated && _queue.ReturnFailed(item, _seed);
            int delay = _reportBackoff.NextDelaySeconds();
            _nextReportAt = _now() + delay;
            string next = _outdated ? "dropped because reports are paused until the plugin is updated."
                : (kept ? "retrying in " : "dropped as stale; next report in ") + delay + "s.";
            _host.Warn("Report " + item.Event + " failed (" + r.Describe(timeout) + "); " + next + RedirectHint(r));
            SetLastReport(item.Event + " seed " + item.Seed + " failed (" + r.Describe(timeout) + ")");
            Record(LevelWarn, "Report " + item.Event + " seed " + item.Seed + " failed (" + r.Describe(timeout) + "); " + next);
        }

        private void StartConfigClaimIfNew()
        {
            string code = CurrentClaimCode();
            string problem = code.Length == 0 ? null : ClaimCodeFormat.Problem(code);
            if (problem == null)
            {
                _malformedConfigClaimCode = null;
            }
            if (code.Length == 0
                || string.Equals(code, _claimCode, StringComparison.Ordinal)
                || _finishedClaimCodes.Contains(code)
                || string.Equals(code, _malformedConfigClaimCode, StringComparison.Ordinal))
            {
                return;
            }
            if (problem != null)
            {
                _malformedConfigClaimCode = code;
                _host.Error("claim_code in " + _host.ConfigFilePath + " was not sent. " + problem
                    + " Then run labapi reload configs, or run slmaps claim <code> in this console.");
                Record(LevelError, "claim_code " + ShortCode(code) + " was not sent: " + FirstLine(problem));
                return;
            }
            BeginClaim(code, true);
        }

        private void BeginClaim(string code, bool fromConfig)
        {
            if (_claimState != ClaimState.None && !string.Equals(_claimCode, code, StringComparison.Ordinal))
            {
                MarkClaimFinished(_claimCode);
            }
            _claimAttempt++;
            _claimCode = code;
            _claimFromConfig = fromConfig;
            _claimState = ClaimState.Claiming;
            _claimBackoff.Reset();
            _nextClaimAt = 0;
            if (fromConfig)
            {
                _host.Info("Claiming this server with claim_code (" + ShortCode(code) + ").");
            }
            Record(LevelInfo, "Claim started with code " + ShortCode(code) + (fromConfig ? " from claim_code." : " from the console."));
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
            int attempt = _claimAttempt;
            string json = Payloads.BuildClaim(code, port, PluginInfo.Version, _host.GameVersion, _host.LabApiVersion);
            _host.Debug("POST " + PluginInfo.ClaimPath + " (port " + port + ").");
            Record(LevelInfo, "Claim request sent (code " + ShortCode(code) + ").");
            _claimInFlightAttempt = attempt;
            Send(url, json, null, r => OnClaimResult(r, code, attempt));
        }

        private void OnClaimResult(ApiResult r, string code, int attempt)
        {
            _claimInFlightAttempt = 0;
            if (r.HasResponse && r.StatusCode == 200)
            {
                // The code is used up the moment this succeeds, so the credential is always saved.
                string credential = r.GetString("credential");
                string serverId = r.GetString("serverId") ?? "";
                string issuedAt = r.GetString("issuedAt") ?? "";
                if (string.IsNullOrEmpty(credential))
                {
                    FailClaim(code, "slmaps accepted the claim (HTTP 200) but returned no credential.\nThe code is used up; run /server claim again.",
                        "accepted (HTTP 200) but no credential was returned");
                    return;
                }
                StoredCredential stored = new StoredCredential { ServerId = serverId, Credential = credential, IssuedAt = issuedAt };
                if (!_host.SaveCredential(stored))
                {
                    _host.Error("Claimed, but " + _host.CredentialFilePath + " could not be written.\n"
                        + "Reporting works until the server restarts; fix the file permissions and claim again.");
                    Record(LevelError, "Claimed, but credential.yml could not be written.");
                }
                bool configHadCode = string.Equals(CurrentClaimCode(), code, StringComparison.Ordinal);
                if (configHadCode)
                {
                    _host.ClearClaimCode(code);
                }
                FinishClaim(code);
                _host.Info("slmaps verified this server (serverId " + serverId + ").\nThe credential was saved to credential.yml"
                    + (configHadCode ? " and claim_code was cleared." : "."));
                SetLastClaim("verified (serverId " + serverId + ")");
                Record(LevelInfo, "Claim verified (serverId " + serverId + ", code " + ShortCode(code) + ").");
                _serverId = serverId;
                UseCredential(credential);
                return;
            }

            if (OutdatedAnswer.Matches(r))
            {
                // A 426 says nothing about the code, so the claim state and the code are kept for a retry.
                if (attempt == _claimAttempt)
                {
                    SetLastClaim("paused: HTTP 426, this plugin version is below the slmaps minimum (code " + ShortCode(code) + ")");
                }
                EnterOutdated(OutdatedAnswer.Minimum(r), OutdatedAnswer.DownloadUrl(r), "HTTP 426 on claim");
                return;
            }

            if (attempt != _claimAttempt)
            {
                // This answer belongs to a code that was replaced or cancelled; a rejection still retires that code.
                if (r.HasResponse && (r.StatusCode == 401 || r.StatusCode == 403 || r.StatusCode == 400))
                {
                    MarkClaimFinished(code);
                }
                _host.Debug("Ignoring a claim answer for a replaced or cancelled code (" + ShortCode(code) + ").");
                Record(LevelInfo, "Ignored the answer for replaced or cancelled code " + ShortCode(code) + " (" + r.Describe(CurrentTimeout()) + ").");
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
                    _host.Info("slmaps staff must review this claim manually (" + ReasonText(reason) + ").\n"
                        + "Keep the server running; the plugin asks again every " + retry + "s and Discord tells you the result.");
                }
                else
                {
                    _host.Debug("Claim still under review; checking again in " + retry + "s.");
                }
                _claimState = ClaimState.Review;
                _claimBackoff.Reset();
                _nextClaimAt = _now() + retry;
                SetLastClaim("waiting for manual review (" + ReasonText(reason) + ")");
                Record(LevelInfo, "Claim waiting for manual review (" + ReasonText(reason) + "); checking again every " + retry + "s.");
                return;
            }
            if (r.HasResponse && r.StatusCode == 401)
            {
                FailClaim(code, "slmaps rejected the claim code (HTTP 401): it is invalid, expired, already used or cancelled.\n"
                    + "Run /server claim in the slmaps Discord for a new code.", "rejected (HTTP 401): invalid, expired, already used or cancelled");
                return;
            }
            if (r.HasResponse && r.StatusCode == 403)
            {
                FailClaim(code, "slmaps staff rejected this claim (HTTP 403).\nThe reason was sent to you on Discord.", "rejected by slmaps staff (HTTP 403)");
                return;
            }
            if (r.HasResponse && r.StatusCode == 400)
            {
                FailClaim(code, "slmaps rejected the claim request as invalid (" + r.Describe(timeout) + ").\nCheck the code and try again.",
                    "rejected as invalid (" + r.Describe(timeout) + ")");
                return;
            }

            int delay = _claimBackoff.NextDelaySeconds();
            _nextClaimAt = _now() + delay;
            _host.Warn("Claim request failed (" + r.Describe(timeout) + "); retrying in " + delay + "s." + RedirectHint(r));
            SetLastClaim("request failed (" + r.Describe(timeout) + "), retrying");
            Record(LevelWarn, "Claim request failed (" + r.Describe(timeout) + "); retrying in " + delay + "s.");
        }

        private void FinishClaim(string code)
        {
            MarkClaimFinished(code);
            if (string.Equals(code, _claimCode, StringComparison.Ordinal))
            {
                _claimCode = null;
                _claimState = ClaimState.None;
            }
        }

        private void MarkClaimFinished(string code)
        {
            if (string.IsNullOrEmpty(code) || _finishedClaimCodes.Contains(code))
            {
                return;
            }
            _finishedClaimCodes.Add(code);
            if (_finishedClaimCodes.Count > MaxFinishedClaimCodes)
            {
                _finishedClaimCodes.RemoveAt(0);
            }
        }

        private bool CurrentClaimInFlight()
        {
            return _claimInFlightAttempt != 0 && _claimInFlightAttempt == _claimAttempt;
        }

        private void FailClaim(string code, string message, string resultText)
        {
            _host.Error(message);
            SetLastClaim(resultText + " (code " + ShortCode(code) + ")");
            Record(LevelError, "Claim " + resultText + " (code " + ShortCode(code) + ").");
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

        // Event log lines are joined with newlines, so only the first line of a hint is recorded.
        private static string FirstLine(string text)
        {
            int end = text.IndexOf('\n');
            return end < 0 ? text : text.Substring(0, end);
        }

        public bool CheckVersionNow(out string message)
        {
            if (_state == State.Stopped)
            {
                message = "The slmaps plugin is not running.";
                return false;
            }
            string tail;
            if (_versionInFlight)
            {
                _versionManualPending = true;
                tail = "A version check is already running; its result is printed to this console.";
            }
            else
            {
                string problem = StartVersionCheck(_now());
                if (problem == null)
                {
                    _versionManualPending = true;
                    tail = "Checking slmaps now; the result is printed to this console.";
                }
                else
                {
                    tail = "Could not check now: " + problem;
                }
            }
            message = DescribeVersion() + "\n" + tail;
            return true;
        }

        private string StartVersionCheck(double now)
        {
            string url;
            if (!TryBuildUrl(PluginInfo.VersionPath, out url))
            {
                _nextVersionCheckAt = now + VersionUrlRetrySeconds;
                string problem = "api_base_url is not a valid http(s) URL.\nFix it in " + _host.ConfigFilePath + " and run labapi reload configs.";
                SetLastVersionCheck("not sent: api_base_url is not a valid http(s) URL");
                return problem;
            }
            _nextVersionCheckAt = now + VersionCheckIntervalSeconds;
            _versionInFlight = true;
            int timeout = CurrentTimeout();
            IApiTransport transport = _transport;
            _host.Debug("GET " + PluginInfo.VersionPath + ".");
            if (!Dispatch(token => transport.GetJsonAsync(url, timeout, token), () => _versionInFlight = false, r => OnVersionResult(r, timeout)))
            {
                // Try again soon instead of half a day later.
                _versionInFlight = false;
                _nextVersionCheckAt = now + VersionUrlRetrySeconds;
                SetLastVersionCheck("not sent: the request could not be started");
                return "the request could not be started.";
            }
            return null;
        }

        private void OnVersionResult(ApiResult r, int timeout)
        {
            bool manual = _versionManualPending;
            _versionManualPending = false;
            VersionInfo info = r.HasResponse && r.StatusCode == 200 ? VersionInfo.TryParse(r.Body) : null;
            if (info == null)
            {
                // 404, 429, 5xx, timeouts and unusable bodies all mean unknown: keep the last values and stay quiet.
                string why = r.HasResponse && r.StatusCode == 200 ? "HTTP 200 with an unusable body" : r.Describe(timeout);
                SetLastVersionCheck("unknown (" + why + ")");
                _host.Debug("Version check gave no usable answer (" + why + ").");
                Record(LevelInfo, "Version check gave no usable answer (" + why + ").");
                if (manual)
                {
                    _host.Info("Version check: slmaps gave no usable answer (" + why + "). The plugin checks again in 12 hours.");
                }
                return;
            }

            _versionKnown = true;
            _knownLatest = info.Latest;
            _knownMinimum = info.Minimum;
            _downloadUrl = info.DownloadUrl;
            string summary = "latest " + (info.Latest ?? "none") + ", minimum " + (info.Minimum ?? "none");
            SetLastVersionCheck("ok (" + summary + ")");
            Record(LevelInfo, "Version check: " + summary + ".");

            int cmp;
            if (info.Minimum != null && VersionNumber.TryCompare(PluginInfo.Version, info.Minimum, out cmp) && cmp < 0)
            {
                EnterOutdated(info.Minimum, info.DownloadUrl, "version check");
            }
            else if (_outdated)
            {
                LeaveOutdated(info.Minimum);
            }

            if (info.Latest != null && VersionNumber.TryCompare(PluginInfo.Version, info.Latest, out cmp) && cmp < 0)
            {
                string key = VersionNumber.Normalize(info.Latest);
                if (Cfg().CheckForUpdates && !string.Equals(key, _warnedLatest, StringComparison.Ordinal))
                {
                    _warnedLatest = key;
                    _host.Warn("A newer " + PluginInfo.Name + " version is available: " + info.Latest + " (this server runs " + PluginInfo.Version + ")."
                        + (info.DownloadUrl != null ? " Download: " + info.DownloadUrl : ""));
                    Record(LevelWarn, "Newer plugin version " + info.Latest + " is available (this server runs " + PluginInfo.Version + ").");
                }
            }
            if (manual)
            {
                _host.Info("Version check: " + VersionStatusText(true) + ".");
            }
        }

        private void EnterOutdated(string minimum, string downloadUrl, string source)
        {
            if (downloadUrl != null)
            {
                _downloadUrl = downloadUrl;
            }
            if (_outdated)
            {
                if (minimum != null)
                {
                    _outdatedMinimum = minimum;
                }
                return;
            }
            _outdated = true;
            _outdatedMinimum = minimum;
            _queue.Clear();
            bool rejected = source.StartsWith("HTTP", StringComparison.Ordinal);
            _host.Error((rejected ? "slmaps rejected a request from this plugin version (" + source + "). " : "") + OutdatedSentence());
            Record(LevelError, "Plugin " + PluginInfo.Version + " is below the slmaps minimum version" + (minimum != null ? " " + minimum : "")
                + " (" + source + "); registration, claims and reports paused.");
        }

        private void LeaveOutdated(string minimum)
        {
            _outdated = false;
            _outdatedMinimum = null;
            _registerBackoff.Reset();
            _nextRegisterAt = 0;
            _claimBackoff.Reset();
            _nextClaimAt = 0;
            _reportBackoff.Reset();
            _nextReportAt = 0;
            _periodicBaseline = _now();
            string text = "slmaps accepts plugin " + PluginInfo.Version + " again (" + (minimum != null ? "minimum " + minimum : "no minimum version")
                + "); registration, claims and reports resume.";
            _host.Info(text);
            Record(LevelInfo, text);
            if (_state == State.Registered && _seed > 0 && !_roundEnded && !_queue.HasRoundEventFor(_seed))
            {
                EnqueueRoundEvent(ReportEvents.RoundStart);
            }
        }

        private string OutdatedSentence()
        {
            return "This plugin version (" + PluginInfo.Version + ") is below the minimum version slmaps accepts"
                + (_outdatedMinimum != null ? " (" + _outdatedMinimum + ")" : "")
                + ", so registration, claims and reports are paused.\nInstall the newer SlmapsServerPlugin.dll"
                + (_downloadUrl != null ? " from " + _downloadUrl : "")
                + " and restart the server.";
        }

        private string VersionStatusText(bool withUrl)
        {
            string download = withUrl && _downloadUrl != null ? " (download: " + _downloadUrl + ")" : "";
            if (_outdated)
            {
                return "OUTDATED: " + (_outdatedMinimum != null ? "slmaps requires " + _outdatedMinimum + " or newer" : "slmaps no longer accepts this version")
                    + "; registration, claims and reports are paused" + download;
            }
            if (!_versionKnown)
            {
                return _lastVersionCheckResult == null ? "not checked yet" : "unknown (no usable answer from slmaps yet)";
            }
            int cmp;
            if (_knownLatest != null && VersionNumber.TryCompare(PluginInfo.Version, _knownLatest, out cmp) && cmp < 0)
            {
                return "newer version " + _knownLatest + " available" + download;
            }
            return "up to date" + (_knownLatest != null ? " (latest " + _knownLatest + ")" : " (no release published)");
        }

        private string DescribeVersion()
        {
            int cmp;
            string latest;
            if (!_versionKnown)
            {
                latest = "unknown";
            }
            else if (_knownLatest == null)
            {
                latest = "none published";
            }
            else if (VersionNumber.TryCompare(PluginInfo.Version, _knownLatest, out cmp))
            {
                latest = _knownLatest + (cmp < 0 ? " (newer than this version)" : cmp == 0 ? " (this version)" : " (this version is newer)");
            }
            else
            {
                latest = _knownLatest;
            }
            string minimum;
            if (_outdated)
            {
                minimum = (_outdatedMinimum ?? "not given") + " (this version is below it)";
            }
            else
            {
                minimum = _versionKnown ? _knownMinimum ?? "none" : "unknown";
            }
            StringBuilder sb = new StringBuilder();
            sb.Append(PluginInfo.Name).Append(' ').Append(PluginInfo.Version)
                .Append("\nlatest: ").Append(latest)
                .Append("\nminimum: ").Append(minimum)
                .Append("\nstatus: ").Append(VersionStatusText(false))
                .Append("\ndownload: ").Append(_downloadUrl ?? "-")
                .Append("\nlast check: ").Append(_lastVersionCheckResult ?? "not yet")
                .Append("\nnext check: ").Append(_versionInFlight ? "running now" : "in " + FormatDuration(_nextVersionCheckAt - _now()))
                .Append("\ncheck_for_updates: ").Append(Cfg().CheckForUpdates
                    ? "true (warns once per newer version)"
                    : "false (no warning about newer versions; the minimum version is still enforced)");
            return sb.ToString();
        }

        private void SetLastVersionCheck(string text)
        {
            _lastVersionCheckResult = LocalStamp() + " " + EventLog.Redact(text);
        }

        private static string FormatDuration(double seconds)
        {
            long s = (long)Math.Ceiling(Math.Max(0, seconds));
            if (s < 60)
            {
                return s + "s";
            }
            if (s < 3600)
            {
                return (s / 60) + "m " + (s % 60) + "s";
            }
            return (s / 3600) + "h " + ((s % 3600) / 60) + "m";
        }

        private void Record(string level, string text)
        {
            _events.Add(_utcNow(), level, text);
        }

        private string LocalStamp()
        {
            return EventLog.ToLocal(_utcNow()).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        }

        private void SetLastClaim(string text)
        {
            _lastClaimResult = LocalStamp() + " " + EventLog.Redact(text);
        }

        private void SetLastReport(string text)
        {
            _lastReportResult = LocalStamp() + " " + EventLog.Redact(text);
        }

        private static string LocalTimeText(string iso)
        {
            DateTime parsed;
            if (DateTime.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out parsed))
            {
                return EventLog.ToLocal(DateTime.SpecifyKind(parsed, DateTimeKind.Utc)).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            }
            return iso.Length <= 40 ? iso : iso.Substring(0, 40);
        }

        private void Send(string url, string json, string bearer, Action<ApiResult> onResult)
        {
            _inFlight = true;
            int timeout = CurrentTimeout();
            IApiTransport transport = _transport;
            if (!Dispatch(token => transport.PostJsonAsync(url, json, bearer, timeout, token), () => _inFlight = false, onResult))
            {
                _inFlight = false;
                _claimInFlightAttempt = 0;
            }
        }

        private bool Dispatch(Func<CancellationToken, Task<ApiResult>> call, Action release, Action<ApiResult> onResult)
        {
            int generation = _generation;
            CancellationToken token = _cts.Token;
            ConcurrentQueue<Action> results = _results;
            try
            {
                Task.Run(async () =>
                {
                    ApiResult result;
                    try
                    {
                        result = await call(token).ConfigureAwait(false);
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
                        release();
                        onResult(result);
                    });
                });
                return true;
            }
            catch (Exception ex)
            {
                _host.Error("Could not start an slmaps request: " + ex.GetType().Name + ": " + ex.Message);
                return false;
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
                    _host.Error("api_base_url is not a valid http(s) URL.\nFix it in " + _host.ConfigFilePath + " and run labapi reload configs.");
                    Record(LevelError, "api_base_url is not a valid http(s) URL; nothing is sent until it is fixed.");
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
                return "\nThe server answered with a redirect; check api_base_url.";
            }
            return "";
        }
    }
}
