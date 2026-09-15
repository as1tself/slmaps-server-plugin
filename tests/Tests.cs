using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SlmapsServerPlugin.Tests
{
    // Offline tests: only the game-independent sources are compiled in.
    internal static class Program
    {
        private static readonly List<string> Failures = new List<string>();
        private static int _checks;
        private static string _group = "";

        private static int Main()
        {
            Run("RetryBackoff", TestBackoff);
            Run("PluginConfig clamps", TestConfigClamps);
            Run("Payloads.BuildRegister", TestRegisterPayload);
            Run("Payloads.BuildReport", TestReportPayload);
            Run("Json parser", TestJsonParser);
            Run("ReportQueue", TestReportQueue);
            Run("Reporter: idle without token", TestReporterIdle);
            Run("Reporter: register + report flow", TestReporterFlow);
            Run("Reporter: register 401 waits for reload", TestRegisterRejected);
            Run("Reporter: stop ignores late results", TestStopIgnoresLateResult);
            Run("Reporter: claim_code verified replaces credential", TestClaimVerified);
            Run("Reporter: console claim waits for review then verifies", TestClaimReview);
            Run("Reporter: claim 401/403 stop", TestClaimRejected);
            Run("ClaimCodeFormat", TestClaimCodeFormat);
            Run("Reporter: malformed console claim codes are never sent", TestConsoleClaimValidation);
            Run("Reporter: malformed claim_code logged once, never sent", TestConfigClaimValidation);
            Run("Reporter: claim sent promptly, newer code replaces older", TestClaimPromptAndReplace);
            Run("Reporter: slmaps cancel", TestClaimCancel);
            Run("Reporter: claim_code replaced from the console is retired", TestConfigClaimReplacedFromConsole);
            Run("Reporter: slmaps report", TestReportNow);
            Run("EventLog bounds, merging and redaction", TestEventLog);
            Run("ConsoleCommandParser", TestCommandParser);
            Run("VersionNumber parse and compare", TestVersionNumber);
            Run("VersionInfo / 426 body parsing", TestVersionBodies);
            Run("Reporter: version check schedule (12 h, fake clock) and unknown answers", TestVersionSchedule);
            Run("Reporter: newer version WARN once per latest, check_for_updates", TestVersionWarn);
            Run("Reporter: 426 on register -> Outdated, recovery", TestOutdatedOnRegister);
            Run("Reporter: 426 on claim -> Outdated, recovery", TestOutdatedOnClaim);
            Run("Reporter: 426 on report -> Outdated, recovery", TestOutdatedOnReport);
            Run("Reporter: minimum from the version check, raise, unknown, lower", TestOutdatedFromVersionCheck);
            Run("Reporter: version check never blocks claims or reports", TestVersionNeverBlocks);
            Run("Reporter: 12 h measured from the send, one request in flight", TestVersionScheduleFromSend);
            Run("Reporter: version check keeps running while revoked / waiting for reload", TestVersionCheckAcrossStates);
            Run("Reporter: Outdated entry, console commands and in-flight requests", TestOutdatedConsoleAndInFlight);
            Run("HttpApiTransport against a loopback listener", TestHttpTransport);

            if (Failures.Count == 0)
            {
                Console.WriteLine("ok (" + _checks + " checks)");
                return 0;
            }
            Console.WriteLine("FAILED " + Failures.Count + "/" + _checks);
            foreach (string failure in Failures)
            {
                Console.WriteLine("  " + failure);
            }
            return 1;
        }

        private static void Run(string name, Action test)
        {
            _group = name;
            try
            {
                test();
            }
            catch (Exception ex)
            {
                Failures.Add(name + ": threw " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void Check(bool condition, string what)
        {
            _checks++;
            if (!condition)
            {
                Failures.Add(_group + ": " + what);
            }
        }

        private static void Equal<T>(T expected, T actual, string what)
        {
            _checks++;
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
            {
                Failures.Add(_group + ": " + what + " (expected " + expected + ", got " + actual + ")");
            }
        }


        private static void TestBackoff()
        {
            RetryBackoff b = new RetryBackoff();
            Equal(5, b.NextDelaySeconds(), "1st delay");
            Equal(15, b.NextDelaySeconds(), "2nd delay");
            Equal(60, b.NextDelaySeconds(), "3rd delay");
            Equal(300, b.NextDelaySeconds(), "4th delay");
            Equal(300, b.NextDelaySeconds(), "5th delay (capped)");
            b.Reset();
            Equal(5, b.NextDelaySeconds(), "delay after reset");
        }

        private static void TestConfigClamps()
        {
            Equal(0, PluginConfig.EffectiveReportInterval(-5), "interval -5");
            Equal(0, PluginConfig.EffectiveReportInterval(0), "interval 0");
            Equal(10, PluginConfig.EffectiveReportInterval(1), "interval 1");
            Equal(10, PluginConfig.EffectiveReportInterval(9), "interval 9");
            Equal(10, PluginConfig.EffectiveReportInterval(10), "interval 10");
            Equal(60, PluginConfig.EffectiveReportInterval(60), "interval 60");
            Equal(3, PluginConfig.EffectiveRequestTimeout(0), "timeout 0");
            Equal(3, PluginConfig.EffectiveRequestTimeout(3), "timeout 3");
            Equal(10, PluginConfig.EffectiveRequestTimeout(10), "timeout 10");
            Equal(60, PluginConfig.EffectiveRequestTimeout(61), "timeout 61");

            PluginConfig d = new PluginConfig();
            Equal("https://slmaps.com", d.ApiBaseUrl, "default api_base_url");
            Equal("", d.RegistrationToken, "default registration_token");
            Equal(60, d.ReportIntervalSeconds, "default report_interval_seconds");
            Check(!d.SendRoundId && !d.SendRoundStartTime && !d.SendElapsedTime && !d.Debug, "default flags off");
            Equal(10, d.RequestTimeoutSeconds, "default request_timeout_seconds");
            Check(d.CheckForUpdates, "default check_for_updates true");
        }

        private static void TestRegisterPayload()
        {
            Equal("{\"token\":\"slreg_abc\",\"port\":7777,\"pluginVersion\":\"1.0.0\",\"gameVersion\":\"14.2.7\",\"labApiVersion\":\"1.1.7\"}",
                Payloads.BuildRegister("slreg_abc", 7777, "1.0.0", "14.2.7", "1.1.7"), "full register body");
            Equal("{\"token\":\"a\\\"b\\\\c\",\"port\":1,\"pluginVersion\":\"1.0.0\"}",
                Payloads.BuildRegister("a\"b\\c", 1, "1.0.0", "", new string('9', 33)), "escaped token, empty/too-long versions omitted");
        }

        private static void TestReportPayload()
        {
            ReportSnapshot s = new ReportSnapshot
            {
                Event = ReportEvents.RoundStart,
                Seed = 1848055747,
                Port = 7777,
                RoundId = "0f6f0000-0000-0000-0000-000000000000",
                RoundStartedAtUtc = new DateTime(2026, 9, 15, 8, 0, 0, DateTimeKind.Utc),
                ElapsedSeconds = 312.54,
            };
            Equal("{\"event\":\"round_start\",\"seed\":1848055747,\"port\":7777,\"pluginVersion\":\"1.0.0\"}",
                Payloads.BuildReport(s, false, false, false, "1.0.0"), "options off -> optional keys omitted");
            Equal("{\"event\":\"round_start\",\"seed\":1848055747,\"port\":7777,\"roundId\":\"0f6f0000-0000-0000-0000-000000000000\",\"roundStartedAt\":\"2026-09-15T08:00:00Z\",\"elapsedSeconds\":312.5,\"pluginVersion\":\"1.0.0\"}",
                Payloads.BuildReport(s, true, true, true, "1.0.0"), "options on");

            s.RoundStartedAtUtc = null;
            s.ElapsedSeconds = null;
            Equal("{\"event\":\"round_start\",\"seed\":1848055747,\"port\":7777,\"roundStartedAt\":null,\"elapsedSeconds\":null,\"pluginVersion\":\"1.0.0\"}",
                Payloads.BuildReport(s, false, true, true, "1.0.0"), "round not started -> null values");

            s.ElapsedSeconds = -3;
            Check(Payloads.BuildReport(s, false, false, true, "1.0.0").Contains("\"elapsedSeconds\":0.0"), "negative elapsed clamped to 0");

            Dictionary<string, object> parsed = Json.TryParseObject(Payloads.BuildReport(s, true, true, true, "1.0.0"));
            Check(parsed != null && (string)parsed["event"] == "round_start" && (double)parsed["seed"] == 1848055747d, "report body is valid JSON");
        }

        private static void TestJsonParser()
        {
            Dictionary<string, object> o = Json.TryParseObject(
                " {\"serverId\":\"abc\",\"credential\":\"slsrv_x\",\"issuedAt\":\"2026-09-15T08:00:00.123Z\",\"n\":-1.5e2,\"b\":true,\"z\":null,\"arr\":[1,{\"a\":\"\\u00e9\\n\"}]} ");
            Check(o != null, "object parsed");
            if (o != null)
            {
                Equal("abc", (string)o["serverId"], "string field");
                Equal(-150d, (double)o["n"], "number field");
                Equal(true, (bool)o["b"], "bool field");
                Check(o.ContainsKey("z") && o["z"] == null, "null field");
                List<object> arr = o["arr"] as List<object>;
                Check(arr != null && arr.Count == 2 && ((Dictionary<string, object>)arr[1])["a"] as string == ((char)0xE9).ToString() + "\n", "nested array/object + escapes");
            }
            Check(Json.TryParseObject("{\"error\":\"rate limited\"}") != null, "error body");
            Check(Json.TryParseObject("") == null, "empty -> null");
            Check(Json.TryParseObject("<html>502</html>") == null, "html -> null");
            Check(Json.TryParseObject("{\"a\":1,}") == null, "trailing comma -> null");
            Check(Json.TryParseObject("[1,2]") == null, "array top-level -> null");
            Check(Json.TryParseObject("{\"a\":tru}") == null, "bad literal -> null");
        }

        private static ReportSnapshot Snap(string ev, int seed)
        {
            return new ReportSnapshot { Event = ev, Seed = seed, Port = 7777 };
        }

        private static void TestReportQueue()
        {
            ReportQueue q = new ReportQueue(4);
            q.EnqueueRoundEvent(Snap(ReportEvents.RoundStart, 1));
            q.EnqueueRoundEvent(Snap(ReportEvents.RoundEnd, 1));
            q.EnqueueRoundEvent(Snap(ReportEvents.RoundStart, 1)); // restart on the same seed (fixed-seed server)
            Equal(2, q.RoundEventCount, "coalesced per (event, seed)");
            q.SetPeriodic(Snap(ReportEvents.Periodic, 1));
            ReportSnapshot a = q.TakeNext(1, true);
            ReportSnapshot b = q.TakeNext(1, true);
            ReportSnapshot c = q.TakeNext(1, true);
            Check(a != null && a.Event == ReportEvents.RoundEnd, "first = round_end (older)");
            Check(b != null && b.Event == ReportEvents.RoundStart, "second = latest round_start");
            Check(c != null && c.Event == ReportEvents.Periodic, "periodic after round events");
            Check(q.TakeNext(1, true) == null, "queue empty");

            q.SetPeriodic(Snap(ReportEvents.Periodic, 1));
            Check(q.TakeNext(2, true) == null && !q.HasPeriodic, "stale periodic (seed changed) dropped");
            q.SetPeriodic(Snap(ReportEvents.Periodic, 2));
            Check(q.TakeNext(2, false) == null && !q.HasPeriodic, "periodic dropped when disabled");

            ReportSnapshot failed = Snap(ReportEvents.RoundStart, 5);
            q.EnqueueRoundEvent(Snap(ReportEvents.RoundEnd, 4));
            Check(q.ReturnFailed(failed, 5), "failed round_start returned");
            Check(q.TakeNext(5, true) == failed, "returned item goes to the front");
            q.EnqueueRoundEvent(Snap(ReportEvents.RoundStart, 5));
            Check(!q.ReturnFailed(failed, 5), "failed item superseded by newer same key");
            q.Clear();

            for (int i = 1; i <= 6; i++)
            {
                q.EnqueueRoundEvent(Snap(ReportEvents.RoundStart, i));
            }
            Equal(4, q.RoundEventCount, "capacity enforced");
            Check(q.TakeNext(0, true).Seed == 3, "oldest dropped first");

            q.Clear();
            ReportSnapshot p = Snap(ReportEvents.Periodic, 9);
            Check(q.ReturnFailed(p, 9) && q.HasPeriodic, "failed periodic re-slotted");
            Check(!q.ReturnFailed(Snap(ReportEvents.Periodic, 9), 9), "second failed periodic not re-slotted");
        }

        // Fake host and transport that drive the Reporter.

        private sealed class FakeHost : IReporterHost
        {
            public PluginConfig Cfg = new PluginConfig();
            public StoredCredential Stored;
            public int SaveConfigCalls;
            public bool KeepClaimCode; // config.yml could not be written
            public double Elapsed = -1;
            public readonly List<string> Logs = new List<string>();

            public PluginConfig Config { get { return Cfg; } }
            public int Port { get { return 7777; } }
            public string GameVersion { get { return "14.2.7"; } }
            public string LabApiVersion { get { return "1.1.7"; } }
            public string ConfigFilePath { get { return "configs/7777/SlmapsServerPlugin/config.yml"; } }
            public string CredentialFilePath { get { return "configs/7777/SlmapsServerPlugin/credential.yml"; } }

            public bool TryGetRoundElapsedSeconds(out double seconds)
            {
                seconds = Elapsed;
                return Elapsed >= 0;
            }

            public StoredCredential LoadCredential() { return Stored; }

            public bool SaveCredential(StoredCredential credential)
            {
                Stored = credential;
                return true;
            }

            public void ClearRegistrationToken(string usedToken)
            {
                if (Cfg.RegistrationToken.Trim() == usedToken)
                {
                    Cfg.RegistrationToken = "";
                    SaveConfigCalls++;
                }
            }

            public void ClearClaimCode(string usedCode)
            {
                if (!KeepClaimCode && Cfg.ClaimCode.Trim() == usedCode)
                {
                    Cfg.ClaimCode = "";
                    SaveConfigCalls++;
                }
            }

            public void Debug(string message) { Logs.Add("DEBUG " + message); }
            public void Info(string message) { Logs.Add("INFO " + message); }
            public void Warn(string message) { Logs.Add("WARN " + message); }
            public void Error(string message) { Logs.Add("ERROR " + message); }

            public bool HasLog(string level, string fragment)
            {
                return Logs.Exists(l => l.StartsWith(level + " ", StringComparison.Ordinal) && l.IndexOf(fragment, StringComparison.Ordinal) >= 0);
            }
        }

        private sealed class Request
        {
            public string Url;
            public string Json;
            public string Bearer;
            public int Timeout;
        }

        private sealed class FakeTransport : IApiTransport
        {
            public readonly List<Request> Requests = new List<Request>();
            public readonly Queue<Func<ApiResult>> Script = new Queue<Func<ApiResult>>();
            public TaskCompletionSource<ApiResult> Hold; // set to hold the answer
            private readonly Queue<TaskCompletionSource<ApiResult>> _holds = new Queue<TaskCompletionSource<ApiResult>>();

            // The version GET has its own script; without one it answers 404 like an older backend.
            public readonly List<Request> VersionRequests = new List<Request>();
            public readonly Queue<Func<ApiResult>> VersionScript = new Queue<Func<ApiResult>>();
            private readonly Queue<TaskCompletionSource<ApiResult>> _versionHolds = new Queue<TaskCompletionSource<ApiResult>>();

            public int VersionCount
            {
                get
                {
                    lock (Requests)
                    {
                        return VersionRequests.Count;
                    }
                }
            }

            public TaskCompletionSource<ApiResult> HoldNextVersion()
            {
                TaskCompletionSource<ApiResult> hold = new TaskCompletionSource<ApiResult>();
                lock (Requests)
                {
                    _versionHolds.Enqueue(hold);
                }
                return hold;
            }

            public Task<ApiResult> GetJsonAsync(string url, int timeoutSeconds, CancellationToken cancellationToken)
            {
                Func<ApiResult> next;
                lock (Requests)
                {
                    VersionRequests.Add(new Request { Url = url, Timeout = timeoutSeconds });
                    if (_versionHolds.Count > 0)
                    {
                        return _versionHolds.Dequeue().Task;
                    }
                    next = VersionScript.Count > 0 ? VersionScript.Dequeue() : () => ApiResult.FromResponse(404, "{\"error\":\"not found\"}");
                }
                return Task.FromResult(next());
            }

            public int Count
            {
                get
                {
                    lock (Requests)
                    {
                        return Requests.Count;
                    }
                }
            }

            // Holds the next request; SetResult on the returned source releases the answer.
            public TaskCompletionSource<ApiResult> HoldNext()
            {
                TaskCompletionSource<ApiResult> hold = new TaskCompletionSource<ApiResult>();
                lock (Requests)
                {
                    _holds.Enqueue(hold);
                }
                return hold;
            }

            public Task<ApiResult> PostJsonAsync(string url, string json, string bearerToken, int timeoutSeconds, CancellationToken cancellationToken)
            {
                lock (Requests)
                {
                    Requests.Add(new Request { Url = url, Json = json, Bearer = bearerToken, Timeout = timeoutSeconds });
                    if (_holds.Count > 0)
                    {
                        return _holds.Dequeue().Task;
                    }
                }
                if (Hold != null)
                {
                    return Hold.Task;
                }
                Func<ApiResult> next = Script.Count > 0 ? Script.Dequeue() : () => ApiResult.FromResponse(200, "{\"ok\":true,\"blockedUntil\":null}");
                return Task.FromResult(next());
            }
        }

        private static ApiResult Http(int status, string body)
        {
            return ApiResult.FromResponse(status, body);
        }

        // Ticks until nothing is in flight.
        private static void Pump(Reporter r)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(3);
            while (true)
            {
                r.Tick();
                if (!r.IsInFlight)
                {
                    return;
                }
                if (DateTime.UtcNow > deadline)
                {
                    throw new TimeoutException("request did not complete");
                }
                Thread.Sleep(2);
            }
        }

        // Ticks until count requests have been recorded, without moving the clock.
        private static void TickUntilRequests(Reporter r, FakeTransport t, int count)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(3);
            while (true)
            {
                r.Tick();
                if (t.Count >= count)
                {
                    return;
                }
                if (DateTime.UtcNow > deadline)
                {
                    throw new TimeoutException("expected " + count + " requests, saw " + t.Count);
                }
                Thread.Sleep(2);
            }
        }

        // Waits for an already started background send, without ticking.
        private static void WaitForRequests(FakeTransport t, int count)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(3);
            while (t.Count < count)
            {
                if (DateTime.UtcNow > deadline)
                {
                    throw new TimeoutException("expected " + count + " requests, saw " + t.Count);
                }
                Thread.Sleep(2);
            }
        }

        // Waits for an already started version GET, without ticking.
        private static void WaitForVersionRequests(FakeTransport t, int count)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(3);
            while (t.VersionCount < count)
            {
                if (DateTime.UtcNow > deadline)
                {
                    throw new TimeoutException("expected " + count + " version requests, saw " + t.VersionCount);
                }
                Thread.Sleep(2);
            }
        }

        // A claim code shaped like the ones the server issues today.
        private const int IssuedRandomLength = 24;

        private static string Code(string stem)
        {
            if (stem.Length > IssuedRandomLength)
            {
                throw new ArgumentException("stem too long: " + stem);
            }
            return ClaimCodeFormat.Prefix + stem.PadRight(IssuedRandomLength, '0');
        }

        private static void CheckNoLeak(IEnumerable<string> lines, string[] secrets, string what)
        {
            foreach (string line in lines)
            {
                foreach (string secret in secrets)
                {
                    Check(line.IndexOf(secret, StringComparison.Ordinal) < 0, what + " leaks no secret (" + secret.Substring(0, Math.Min(8, secret.Length)) + "...): " + line);
                }
            }
        }

        private static void TestReporterIdle()
        {
            FakeHost host = new FakeHost();
            FakeTransport transport = new FakeTransport();
            double now = 0;
            Reporter r = new Reporter(host, transport, () => now, () => DateTime.UtcNow);
            r.Start();
            r.OnMapGenerated(42);
            now += 1000;
            Pump(r);
            Equal("Idle", r.StateName, "state");
            Equal(0, transport.Requests.Count, "no requests");
            Check(host.HasLog("WARN", "registration_token is empty"), "idle warning logged");
        }

        private static void TestReporterFlow()
        {
            const string token = "slreg_SECRET_TOKEN_VALUE";
            const string credential = "slsrv_SECRET_CREDENTIAL_VALUE";
            FakeHost host = new FakeHost();
            host.Cfg.RegistrationToken = "  " + token + " ";
            host.Cfg.SendRoundId = true;
            FakeTransport transport = new FakeTransport();
            double now = 0;
            DateTime utc = new DateTime(2026, 9, 15, 8, 0, 0, DateTimeKind.Utc);
            Reporter r = new Reporter(host, transport, () => now, () => utc);

            // Register: 503, retry after 5s, then 200.
            transport.Script.Enqueue(() => Http(503, "{\"error\":\"db unavailable\"}"));
            transport.Script.Enqueue(() => Http(200, "{\"serverId\":\"srv-1\",\"credential\":\"" + credential + "\",\"issuedAt\":\"2026-09-15T08:00:00Z\"}"));
            r.Start();
            Equal("Registering", r.StateName, "registering after start");
            Pump(r);
            Equal(1, transport.Requests.Count, "first register attempt");
            Check(transport.Requests[0].Url == "https://slmaps.com/api/plugin/v1/register", "register url");
            Check(transport.Requests[0].Bearer == null, "register has no bearer");
            Check(transport.Requests[0].Json.Contains("\"token\":\"" + token + "\"") && transport.Requests[0].Json.Contains("\"port\":7777"), "register body (trimmed token, port)");
            Check(host.HasLog("WARN", "retrying in 5s"), "backoff 5s logged");

            r.OnMapGenerated(1848055747); // map generated while registering, so it waits in the queue
            now += 4;
            Pump(r);
            Equal(1, transport.Requests.Count, "no retry before 5s");
            now += 1;
            Pump(r);
            Pump(r);
            Equal("Registered", r.StateName, "registered");
            Check(host.Stored != null && host.Stored.Credential == credential && host.Stored.ServerId == "srv-1", "credential saved");
            Equal("", host.Cfg.RegistrationToken, "token cleared");
            Equal(1, host.SaveConfigCalls, "config saved once");

            // The round_start waiting since before registration is sent.
            Request start = transport.Requests[transport.Requests.Count - 1];
            Check(start.Url == "https://slmaps.com/api/plugin/v1/report", "report url");
            Check(start.Bearer == credential, "report bearer");
            Check(start.Json.StartsWith("{\"event\":\"round_start\",\"seed\":1848055747,\"port\":7777,\"roundId\":\"", StringComparison.Ordinal), "round_start body with roundId");
            Check(!start.Json.Contains("roundStartedAt") && !start.Json.Contains("elapsedSeconds"), "disabled optional keys omitted");
            int afterStart = transport.Requests.Count;

            // Periodic report after 60s.
            host.Elapsed = 12.3;
            r.OnRoundStarted();
            now += 59;
            Pump(r);
            Equal(afterStart, transport.Requests.Count, "no periodic before interval");
            now += 1;
            Pump(r);
            Equal(afterStart + 1, transport.Requests.Count, "periodic sent at interval");
            Check(transport.Requests[transport.Requests.Count - 1].Json.StartsWith("{\"event\":\"periodic\"", StringComparison.Ordinal), "periodic body");

            // round_end gets 429 and succeeds on the retry 5s later.
            transport.Script.Enqueue(() => Http(429, "{\"error\":\"rate limited\"}"));
            host.Cfg.SendElapsedTime = true;
            host.Cfg.SendRoundStartTime = true;
            r.OnRoundEnded();
            Pump(r);
            int afterEnd = transport.Requests.Count;
            Check(transport.Requests[afterEnd - 1].Json.StartsWith("{\"event\":\"round_end\"", StringComparison.Ordinal), "round_end sent");
            Check(transport.Requests[afterEnd - 1].Json.Contains("\"roundStartedAt\":\"2026-09-15T08:00:00Z\",\"elapsedSeconds\":12.3"), "optional fields when enabled");
            Equal(1, r.PendingRoundEvents, "round_end kept for retry");
            now += 5;
            Pump(r);
            Equal(afterEnd + 1, transport.Requests.Count, "round_end retried");
            Equal(0, r.PendingRoundEvents, "round_end delivered");

            // No periodic report between the round end and the next map, or it would reopen the block window.
            int afterEndDelivered = transport.Requests.Count;
            now += 600;
            Pump(r);
            Pump(r);
            Equal(afterEndDelivered, transport.Requests.Count, "no periodic after round_end");

            // A new map reports round_start; a 401 then stops reporting.
            transport.Script.Enqueue(() => Http(401, "{\"error\":\"invalid credential\"}"));
            r.OnMapGenerated(-1); // ignored
            r.OnMapGenerated(99);
            Pump(r);
            Equal("Revoked", r.StateName, "revoked after 401");
            Check(host.HasLog("ERROR", "delete configs/7777/SlmapsServerPlugin/credential.yml"), "revocation guidance logged");
            int afterRevoke = transport.Requests.Count;
            r.OnRoundEnded();
            now += 1000;
            Pump(r);
            Equal(afterRevoke, transport.Requests.Count, "no reports after revocation");

            // A new token plus a reload registers again and announces the current map.
            host.Cfg.RegistrationToken = "slreg_SECOND_TOKEN";
            transport.Script.Enqueue(() => Http(200, "{\"serverId\":\"srv-2\",\"credential\":\"slsrv_SECOND_CREDENTIAL\",\"issuedAt\":\"2026-09-15T09:00:00Z\"}"));
            r.NotifyConfigReloaded();
            Pump(r);
            Pump(r);
            Equal("Registered", r.StateName, "registered again after reload");
            Request announce = transport.Requests[transport.Requests.Count - 1];
            Check(announce.Bearer == "slsrv_SECOND_CREDENTIAL" && announce.Json.StartsWith("{\"event\":\"round_start\",\"seed\":99,", StringComparison.Ordinal), "current seed announced with new credential");

            foreach (string line in host.Logs)
            {
                Check(line.IndexOf("SECRET", StringComparison.Ordinal) < 0 && line.IndexOf("SECOND_CREDENTIAL", StringComparison.Ordinal) < 0 && line.IndexOf("SECOND_TOKEN", StringComparison.Ordinal) < 0,
                    "log line leaks no token/credential: " + line);
            }
            string[] flowSecrets = { "SECRET", "SECOND_CREDENTIAL", "SECOND_TOKEN" };
            CheckNoLeak(r.DescribeEvents(ConsoleCommandParser.MaxLogCount).Split('\n'), flowSecrets, "slmaps log");
            CheckNoLeak(r.DescribeStatus().Split('\n'), flowSecrets, "slmaps status");
            string events = r.DescribeEvents(ConsoleCommandParser.MaxLogCount);
            Check(events.Contains("Registered with slmaps (serverId srv-1)") && events.Contains("Registration failed (HTTP 503")
                && events.Contains("Report rejected (HTTP 401)") && events.Contains("Configuration reloaded."), "event log records registration, report and reload results");
            Check(r.DescribeStatus().Contains("last report: ") && r.DescribeStatus().Contains("round_start seed 99 accepted"), "status shows the last report result");
            Equal(10, transport.Requests[0].Timeout, "timeout passed to transport");
        }

        private static void TestRegisterRejected()
        {
            FakeHost host = new FakeHost();
            host.Cfg.RegistrationToken = "slreg_bad";
            FakeTransport transport = new FakeTransport();
            transport.Script.Enqueue(() => Http(401, "{\"error\":\"invalid token\"}"));
            double now = 0;
            Reporter r = new Reporter(host, transport, () => now, () => DateTime.UtcNow);
            r.Start();
            Pump(r);
            Equal("WaitingForReload", r.StateName, "waiting for reload after 401");
            Check(host.HasLog("ERROR", "invalid, already used or expired"), "clear 401 error");
            now += 10000;
            Pump(r);
            Equal(1, transport.Requests.Count, "no retry without reload");

            transport.Script.Enqueue(() => Http(503, ""));
            r.NotifyConfigReloaded();
            Pump(r);
            Equal(2, transport.Requests.Count, "retry after reload");
            Equal("Registering", r.StateName, "503 keeps registering");

            host.Cfg.RegistrationToken = "";
            r.NotifyConfigReloaded();
            Pump(r);
            Equal("Idle", r.StateName, "token removed -> idle");
        }

        private static void TestClaimVerified()
        {
            string code = Code("SECRET_CLAIM_CODE_1");
            FakeHost host = new FakeHost();
            host.Stored = new StoredCredential { ServerId = "old", Credential = "slsrv_OLD_CREDENTIAL", IssuedAt = "" };
            host.Cfg.ClaimCode = " " + code + " ";
            host.Cfg.RegistrationToken = "slreg_IGNORED_TOKEN";
            FakeTransport transport = new FakeTransport();
            double now = 0;
            Reporter r = new Reporter(host, transport, () => now, () => DateTime.UtcNow);
            transport.Script.Enqueue(() => Http(200, "{\"status\":\"verified\",\"serverId\":\"srv-claim\",\"credential\":\"slsrv_NEW_CLAIM_CREDENTIAL\",\"issuedAt\":\"2026-09-15T10:00:00Z\"}"));
            r.Start();
            Equal("Registered", r.StateName, "old credential keeps reporting while claiming");
            Equal("Claiming", r.ClaimStateName, "claim started from config");
            Pump(r);
            Request claim = transport.Requests[0];
            Check(claim.Url == "https://slmaps.com/api/plugin/v1/claim", "claim url");
            Check(claim.Bearer == null, "claim has no bearer");
            Check(claim.Json.StartsWith("{\"code\":\"" + code + "\",\"port\":7777,\"pluginVersion\":\"" + PluginInfo.Version + "\"", StringComparison.Ordinal), "claim body (trimmed code, port, version)");
            Version parsedVersion;
            Check(Version.TryParse(PluginInfo.Version, out parsedVersion) && parsedVersion.Build >= 0 && parsedVersion.Revision < 0, "plugin version is major.minor.patch");
            Equal(PluginInfo.Version + ".0", PluginInfo.AssemblyVersion, "assembly version matches the plugin version");
            Equal("None", r.ClaimStateName, "claim finished");
            Check(host.Stored != null && host.Stored.Credential == "slsrv_NEW_CLAIM_CREDENTIAL" && host.Stored.ServerId == "srv-claim", "credential replaced");
            Equal("", host.Cfg.ClaimCode, "claim_code cleared");
            Equal("slreg_IGNORED_TOKEN", host.Cfg.RegistrationToken, "registration_token untouched");

            r.OnMapGenerated(555);
            Pump(r);
            Request report = transport.Requests[transport.Requests.Count - 1];
            Check(report.Url.EndsWith("/report", StringComparison.Ordinal) && report.Bearer == "slsrv_NEW_CLAIM_CREDENTIAL", "reports use the claimed credential");

            int count = transport.Requests.Count;
            r.NotifyConfigReloaded();
            now += 1000;
            Pump(r);
            Check(!transport.Requests.Exists(x => x.Url.EndsWith("/claim", StringComparison.Ordinal) && transport.Requests.IndexOf(x) >= count), "reload does not re-claim a finished code");
            Check(r.DescribeStatus().Contains("serverId: srv-claim"), "status shows the claimed server id");
            foreach (string line in host.Logs)
            {
                Check(line.IndexOf("SECRET_CLAIM_CODE", StringComparison.Ordinal) < 0 && line.IndexOf("NEW_CLAIM_CREDENTIAL", StringComparison.Ordinal) < 0,
                    "log line leaks no claim code/credential: " + line);
            }
            string[] claimSecrets = { "SECRET_CLAIM_CODE", "NEW_CLAIM_CREDENTIAL", "OLD_CREDENTIAL", "IGNORED_TOKEN" };
            CheckNoLeak(r.DescribeEvents(ConsoleCommandParser.MaxLogCount).Split('\n'), claimSecrets, "slmaps log");
            CheckNoLeak(r.DescribeStatus().Split('\n'), claimSecrets, "slmaps status");
            Check(r.DescribeEvents(ConsoleCommandParser.MaxLogCount).Contains("Claim verified (serverId srv-claim, code slclm_SECRET...)"), "event log records the verified claim with a 12-char code");
            Check(r.DescribeStatus().Contains("last claim result: ") && r.DescribeStatus().Contains("verified (serverId srv-claim)"), "status shows the last claim result");
        }

        private static void TestClaimReview()
        {
            string code = Code("CONSOLE_CODE_2");
            FakeHost host = new FakeHost();
            FakeTransport transport = new FakeTransport();
            double now = 0;
            Reporter r = new Reporter(host, transport, () => now, () => DateTime.UtcNow);
            r.Start();
            Equal("Idle", r.StateName, "idle before claim");
            string message;
            Check(!r.StartClaim("   ", out message) && message.StartsWith("Usage", StringComparison.Ordinal), "empty console code rejected");
            Check(r.StartClaim(code, out message) && message.Contains("slclm_CONSOL...") && !message.Contains(code), "console claim accepted without echoing the full code");

            transport.Script.Enqueue(() => Http(202, "{\"status\":\"review\",\"reason\":\"ip_mismatch\",\"retryAfterSeconds\":60}"));
            Pump(r);
            Equal(1, transport.Requests.Count, "first claim sent");
            Equal("Review", r.ClaimStateName, "review after 202");
            Check(host.HasLog("INFO", "differs from the address given on Discord"), "review reason explained");
            Check(r.DescribeStatus().Contains("waiting for manual review"), "status shows review");

            now += 59;
            Pump(r);
            Equal(1, transport.Requests.Count, "no poll before retryAfterSeconds");
            transport.Script.Enqueue(() => Http(202, "{\"status\":\"review\",\"reason\":\"ip_mismatch\",\"retryAfterSeconds\":5}"));
            now += 1;
            Pump(r);
            Equal(2, transport.Requests.Count, "polled after 60s");
            Check(transport.Requests[1].Json.Contains("\"code\":\"" + code + "\""), "poll resends the same code");

            transport.Script.Enqueue(() => Http(200, "{\"status\":\"verified\",\"serverId\":\"srv-r\",\"credential\":\"slsrv_REVIEWED\",\"issuedAt\":\"\"}"));
            now += 29;
            Pump(r);
            Equal(2, transport.Requests.Count, "retryAfterSeconds clamped to at least 30");
            now += 1;
            Pump(r);
            Pump(r);
            Equal("Registered", r.StateName, "registered after approval");
            Equal("None", r.ClaimStateName, "claim finished after approval");
            Equal(0, host.SaveConfigCalls, "console claim does not rewrite config.yml");
        }

        private static void TestClaimRejected()
        {
            FakeHost host = new FakeHost();
            host.Cfg.ClaimCode = Code("BAD");
            FakeTransport transport = new FakeTransport();
            double now = 0;
            Reporter r = new Reporter(host, transport, () => now, () => DateTime.UtcNow);
            transport.Script.Enqueue(() => Http(401, "{\"error\":\"invalid code\"}"));
            r.Start();
            Pump(r);
            Equal("None", r.ClaimStateName, "claim stopped after 401");
            Check(host.HasLog("ERROR", "/server claim"), "401 tells the owner to get a new code");
            Check(host.HasLog("WARN", "registration_token is empty"), "falls back to the idle guidance");
            Check(host.HasLog("WARN", "claim_code holds a code that was already rejected") && !host.HasLog("WARN", "claim_code is empty"), "idle guidance says the rejected claim_code is not sent again");
            now += 10000;
            Pump(r);
            Equal(1, transport.Requests.Count, "no retry after 401");
            r.NotifyConfigReloaded();
            Pump(r);
            Equal(1, transport.Requests.Count, "reload does not retry the same rejected code");

            string message;
            transport.Script.Enqueue(() => Http(403, "{\"error\":\"claim rejected\"}"));
            r.StartClaim(Code("OTHER"), out message);
            Pump(r);
            Equal("None", r.ClaimStateName, "claim stopped after 403");
            Check(host.HasLog("ERROR", "rejected this claim"), "403 explained");

            transport.Script.Enqueue(() => Http(503, "{\"error\":\"db unavailable\"}"));
            r.StartClaim(Code("THIRD"), out message);
            Pump(r);
            Equal("Claiming", r.ClaimStateName, "503 keeps claiming");
            Check(host.HasLog("WARN", "Claim request failed"), "503 retried with backoff");
        }

        private static void TestCommandParser()
        {
            ConsoleCommandParser.Parsed p;
            Equal(ConsoleCommandParser.Action.Help, ConsoleCommandParser.Parse(new string[0]).Action, "no args -> help");
            Equal(ConsoleCommandParser.Action.Help, ConsoleCommandParser.Parse(null).Action, "null args -> help");
            Equal(ConsoleCommandParser.Action.Help, ConsoleCommandParser.Parse(new[] { "  " }).Action, "blank subcommand -> help");
            Equal(ConsoleCommandParser.Action.Help, ConsoleCommandParser.Parse(new[] { "HELP" }).Action, "help (case-insensitive)");
            Equal(ConsoleCommandParser.Action.Help, ConsoleCommandParser.Parse(new[] { "help", "claim" }).Action, "help ignores extra words");

            Equal(ConsoleCommandParser.Action.Status, ConsoleCommandParser.Parse(new[] { "STATUS" }).Action, "status (case-insensitive)");
            p = ConsoleCommandParser.Parse(new[] { "status", "x" });
            Check(p.Action == ConsoleCommandParser.Action.Error && p.Message.Contains("takes no arguments") && p.Message.Contains("slmaps help"), "status with an argument -> error");

            p = ConsoleCommandParser.Parse(new[] { "claim" });
            Check(p.Action == ConsoleCommandParser.Action.Error && p.Message == ConsoleCommandParser.ClaimUsage, "claim without code -> claim usage");
            p = ConsoleCommandParser.Parse(new[] { "claim", "   " });
            Check(p.Action == ConsoleCommandParser.Action.Error && p.Message == ConsoleCommandParser.ClaimUsage, "claim with blank code -> claim usage");
            p = ConsoleCommandParser.Parse(new[] { "Claim", " slclm_abc " });
            Check(p.Action == ConsoleCommandParser.Action.Claim && p.Code == "slclm_abc", "claim with code (trimmed; format is checked by the Reporter)");
            p = ConsoleCommandParser.Parse(new[] { "claim", "slclm_a", "b" });
            Check(p.Action == ConsoleCommandParser.Action.Error && p.Message == ConsoleCommandParser.ClaimUsage, "claim with two words -> claim usage");

            Equal(ConsoleCommandParser.Action.Cancel, ConsoleCommandParser.Parse(new[] { "cancel" }).Action, "cancel");
            Equal(ConsoleCommandParser.Action.Error, ConsoleCommandParser.Parse(new[] { "cancel", "now" }).Action, "cancel with an argument -> error");
            Equal(ConsoleCommandParser.Action.Report, ConsoleCommandParser.Parse(new[] { "Report" }).Action, "report");
            Equal(ConsoleCommandParser.Action.Error, ConsoleCommandParser.Parse(new[] { "report", "123" }).Action, "report with a seed -> error");

            p = ConsoleCommandParser.Parse(new[] { "log" });
            Check(p.Action == ConsoleCommandParser.Action.Log && p.Count == 10, "log -> 10");
            p = ConsoleCommandParser.Parse(new[] { "log", " 25 " });
            Check(p.Action == ConsoleCommandParser.Action.Log && p.Count == 25, "log 25");
            p = ConsoleCommandParser.Parse(new[] { "log", "1" });
            Check(p.Action == ConsoleCommandParser.Action.Log && p.Count == 1, "log 1");
            p = ConsoleCommandParser.Parse(new[] { "log", "50" });
            Check(p.Action == ConsoleCommandParser.Action.Log && p.Count == 50, "log 50");
            p = ConsoleCommandParser.Parse(new[] { "log", "500" });
            Check(p.Action == ConsoleCommandParser.Action.Log && p.Count == 50, "log 500 -> capped at 50");
            foreach (string bad in new[] { "0", "-3", "+5", "abc", "1.5", "99999999999" })
            {
                p = ConsoleCommandParser.Parse(new[] { "log", bad });
                Check(p.Action == ConsoleCommandParser.Action.Error && p.Message == ConsoleCommandParser.LogUsage, "log " + bad + " -> log usage");
            }
            p = ConsoleCommandParser.Parse(new[] { "log", "5", "6" });
            Check(p.Action == ConsoleCommandParser.Action.Error && p.Message == ConsoleCommandParser.LogUsage, "log with two numbers -> log usage");

            p = ConsoleCommandParser.Parse(new[] { "register", "x" });
            Check(p.Action == ConsoleCommandParser.Action.Error && p.Message.Contains("Unknown subcommand \"register\"") && p.Message.Contains("slmaps help"), "unknown subcommand -> error with help pointer");
            p = ConsoleCommandParser.Parse(new[] { "slclm_SECRETSECRETSECRET00" });
            Check(p.Action == ConsoleCommandParser.Action.Error && p.Message.Contains("\"slclm_SECRET...\"") && !p.Message.Contains("SECRETSECRET"), "pasted code as subcommand is not echoed in full");

            Equal(ConsoleCommandParser.Action.Version, ConsoleCommandParser.Parse(new[] { "VERSION" }).Action, "version (case-insensitive)");
            p = ConsoleCommandParser.Parse(new[] { "version", "check" });
            Check(p.Action == ConsoleCommandParser.Action.Error && p.Message.Contains("slmaps version takes no arguments"), "version with an argument -> error");

            foreach (string sub in new[] { "help", "status", "claim <code>", "cancel", "report", "log [n]", "version" })
            {
                Check(ConsoleCommandParser.Help.Contains("\n  slmaps " + sub + " "), "help lists slmaps " + sub);
                Check(ConsoleCommandParser.Usage.Contains(sub), "usage lists " + sub);
            }
        }

        private static void TestClaimCodeFormat()
        {
            Equal(30, Code("x").Length, "test helper builds 30-char codes");
            Check(ClaimCodeFormat.IsValid(Code("Ab9-_zZ")), "valid code (letters, digits, '-', '_')");
            Check(ClaimCodeFormat.IsValid("  " + Code("trim") + " "), "surrounding spaces are trimmed");

            string p = ClaimCodeFormat.Problem("19");
            Check(p != null && p.Contains("request number") && p.Contains("#19") && p.Contains("slclm_") && p.Contains("/server claim"), "19 -> request number hint");
            p = ClaimCodeFormat.Problem("#19");
            Check(p != null && p.Contains("request number") && p.Contains("#19"), "#19 -> request number hint");
            p = ClaimCodeFormat.Problem("58723");
            Check(p != null && p.Contains("request number"), "58723 -> request number hint");
            p = ClaimCodeFormat.Problem("123456789012345678901");
            Check(p != null && p.Contains("number") && !p.Contains("123456789012345678901"), "very long number -> hint without echo");
            Check(ClaimCodeFormat.Problem("#") != null && !ClaimCodeFormat.Problem("#").Contains("request number"), "lone # is not a request number");

            p = ClaimCodeFormat.Problem("slclm_");
            Check(p != null && p.Contains("too short"), "prefix only -> too short");
            // Only 1 to 128 characters are promised; the exact length is left to the server.
            Check(ClaimCodeFormat.IsValid("slclm_a"), "one char after the prefix is left to the server");
            Check(ClaimCodeFormat.IsValid("slclm_" + new string('a', 23)) && ClaimCodeFormat.IsValid("slclm_" + new string('a', 25)), "other lengths than today's 24 are left to the server");
            Check(ClaimCodeFormat.IsValid("slclm_" + new string('a', ClaimCodeFormat.MaxLength - 6)), "128 chars (contract maximum) accepted");
            p = ClaimCodeFormat.Problem("slclm_" + new string('a', ClaimCodeFormat.MaxLength - 5));
            Check(p != null && p.Contains("too long"), "129 chars -> too long");
            foreach (char bad in new[] { '=', '+', '/', ' ', '.', (char)0xE9, (char)0x200B })
            {
                p = ClaimCodeFormat.Problem("slclm_" + new string('a', 12) + bad + new string('a', 11));
                Check(p != null && p.Contains("character"), "wrong charset rejected (0x" + ((int)bad).ToString("X4") + ")");
            }
            p = ClaimCodeFormat.Problem("SLCLM_" + new string('a', 24));
            Check(p != null && p.Contains("starts with"), "prefix is case-sensitive");
            p = ClaimCodeFormat.Problem("abc");
            Check(p != null && p.Contains("starts with \"slclm_\""), "random text -> prefix hint");
            p = ClaimCodeFormat.Problem("slreg_" + new string('a', 24));
            Check(p != null && p.Contains("registration token"), "registration token -> registration_token hint");
            p = ClaimCodeFormat.Problem("   ");
            Check(p != null && p.Contains("empty"), "blank -> empty");
            p = ClaimCodeFormat.Problem("slclm_SECRETSECRETSECRETSECRE!");
            Check(p != null && !p.Contains("SECRETSECRET"), "problem text never echoes a code");

            // Codes pasted with the usage brackets or quotes still around them.
            string wrappedCode = Code("SECRETWRAP");
            foreach (string wrapped in new[] { "<" + wrappedCode + ">", "\"" + wrappedCode + "\"", "'" + wrappedCode + "'", "`" + wrappedCode + "`",
                (char)0x201C + wrappedCode + (char)0x201D, "<" + wrappedCode, wrappedCode + "\"", "\"<" + wrappedCode + ">\"" })
            {
                p = ClaimCodeFormat.Problem(wrapped);
                Check(p != null && p.Contains("wrapped in quotes or angle brackets") && !p.Contains("starts with") && !p.Contains("SECRETWRAP"),
                    "wrapped code -> remove quotes/brackets hint (" + wrapped.Substring(0, 3) + "...)");
            }
            p = ClaimCodeFormat.Problem("<19>");
            Check(p != null && p.Contains("request number") && p.Contains("#19"), "<19> -> request number hint");
            p = ClaimCodeFormat.Problem("\"#19\"");
            Check(p != null && p.Contains("request number") && p.Contains("#19"), "\"#19\" -> request number hint");
            p = ClaimCodeFormat.Problem("<code>");
            Check(p != null && p.Contains("starts with \"slclm_\""), "literal <code> -> prefix hint");
            p = ClaimCodeFormat.Problem("\"");
            Check(p != null && p.Contains("starts with \"slclm_\""), "lone quote -> prefix hint");
        }

        private static void TestConsoleClaimValidation()
        {
            FakeHost host = new FakeHost();
            host.Stored = new StoredCredential { ServerId = "srv", Credential = "slsrv_VALIDATION", IssuedAt = "" };
            FakeTransport transport = new FakeTransport();
            double now = 0;
            Reporter r = new Reporter(host, transport, () => now, () => DateTime.UtcNow);
            r.Start();

            string message;
            Check(!r.StartClaim("19", out message) && message.StartsWith("Claim code not sent.", StringComparison.Ordinal) && message.Contains("request number"), "console 19 -> request number hint");
            Check(!r.StartClaim("#19", out message) && message.Contains("request number"), "console #19 -> request number hint");
            Check(!r.StartClaim("slclm_", out message) && message.Contains("too short"), "console prefix-only code rejected");
            Check(!r.StartClaim("slclm_" + new string('a', 23) + "=", out message) && message.Contains("character"), "console wrong charset rejected");
            Check(!r.StartClaim("<" + Code("CONSOLE_WRAP") + ">", out message) && message.Contains("wrapped in quotes"), "console code in angle brackets rejected");
            Equal("None", r.ClaimStateName, "no claim started");
            now += 1000;
            Pump(r);
            Pump(r);
            Check(!transport.Requests.Exists(x => x.Url.EndsWith("/claim", StringComparison.Ordinal)), "malformed console codes are never sent");
            Check(r.DescribeEvents(10).Contains("Console claim code 19 was not sent"), "event log records the rejected input");
            Check(r.DescribeStatus().Contains("claim: none"), "status shows no claim");
        }

        private static void TestConfigClaimValidation()
        {
            FakeHost host = new FakeHost();
            host.Cfg.ClaimCode = " 19 ";
            FakeTransport transport = new FakeTransport();
            double now = 0;
            Reporter r = new Reporter(host, transport, () => now, () => DateTime.UtcNow);
            r.Start();
            Equal("None", r.ClaimStateName, "malformed claim_code does not start a claim");
            Equal("Idle", r.StateName, "falls back to idle");
            for (int i = 0; i < 5; i++)
            {
                now += 100;
                Pump(r);
            }
            r.NotifyConfigReloaded();
            Pump(r);
            r.NotifyConfigReloaded();
            Pump(r);
            Equal(1, host.Logs.FindAll(l => l.StartsWith("ERROR ", StringComparison.Ordinal) && l.Contains("claim_code in")).Count, "malformed claim_code logged once across ticks and reloads");
            Check(host.HasLog("ERROR", "request number") && host.HasLog("ERROR", "labapi reload configs"), "config error explains the request number and the fix");
            Equal(0, transport.Count, "malformed claim_code never sent");
            Check(!host.HasLog("WARN", "claim_code is empty"), "malformed claim_code never produces 'claim_code is empty'");
            Equal(3, host.Logs.FindAll(l => l.StartsWith("WARN ", StringComparison.Ordinal) && l.Contains("claim_code is not a valid claim code") && l.Contains("registration_token is empty")).Count,
                "idle warning names the invalid claim_code on start and on every reload");
            Check(r.DescribeEvents(ConsoleCommandParser.MaxLogCount).Contains("Not registered: claim_code is not a valid claim code"), "event log names the invalid claim_code");

            string otherMalformed = "slclm_" + new string('b', 20) + "=";
            host.Cfg.ClaimCode = otherMalformed;
            r.NotifyConfigReloaded();
            Pump(r);
            Equal(2, host.Logs.FindAll(l => l.StartsWith("ERROR ", StringComparison.Ordinal) && l.Contains("claim_code in")).Count, "a different malformed value is logged once more");
            Equal(0, transport.Count, "still nothing sent");

            // Clearing and entering the same bad value again warns again.
            host.Cfg.ClaimCode = "";
            r.NotifyConfigReloaded();
            Pump(r);
            Check(host.HasLog("WARN", "claim_code is empty"), "empty claim_code -> empty wording");
            host.Cfg.ClaimCode = otherMalformed;
            r.NotifyConfigReloaded();
            Pump(r);
            Equal(3, host.Logs.FindAll(l => l.StartsWith("ERROR ", StringComparison.Ordinal) && l.Contains("claim_code in")).Count, "the same malformed value entered again after clearing is logged again");
            Equal(0, transport.Count, "still nothing sent");

            // The same holds while a stored credential keeps the plugin registered.
            FakeHost hostWithCredential = new FakeHost();
            hostWithCredential.Stored = new StoredCredential { ServerId = "srv", Credential = "slsrv_MALFORMED_AGAIN", IssuedAt = "" };
            hostWithCredential.Cfg.ClaimCode = "19";
            Reporter r2 = new Reporter(hostWithCredential, new FakeTransport(), () => now, () => DateTime.UtcNow);
            r2.Start();
            hostWithCredential.Cfg.ClaimCode = "";
            r2.NotifyConfigReloaded();
            Pump(r2);
            hostWithCredential.Cfg.ClaimCode = "19";
            r2.NotifyConfigReloaded();
            Pump(r2);
            Equal(2, hostWithCredential.Logs.FindAll(l => l.StartsWith("ERROR ", StringComparison.Ordinal) && l.Contains("claim_code in")).Count, "registered: malformed, empty, malformed again -> logged twice");
            Equal("Registered", r2.StateName, "stored credential keeps reporting");

            host.Cfg.ClaimCode = Code("CONFIG_FIXED");
            transport.Script.Enqueue(() => Http(202, "{\"status\":\"review\",\"reason\":\"port_mismatch\",\"retryAfterSeconds\":60}"));
            r.NotifyConfigReloaded();
            Pump(r);
            Equal(1, transport.Count, "fixed claim_code is sent after reload");
            Check(transport.Requests[0].Url.EndsWith("/claim", StringComparison.Ordinal) && transport.Requests[0].Json.Contains(Code("CONFIG_FIXED")), "claim body uses the fixed code");
            Equal("Review", r.ClaimStateName, "server stays the authority (202 review)");
        }

        private static void TestClaimPromptAndReplace()
        {
            FakeHost host = new FakeHost();
            host.Stored = new StoredCredential { ServerId = "srv", Credential = "slsrv_PROMPT_OLD", IssuedAt = "" };
            FakeTransport transport = new FakeTransport();
            double now = 0;
            Reporter r = new Reporter(host, transport, () => now, () => DateTime.UtcNow);
            r.Start();
            string message;

            // With nothing in flight, a console claim goes out on the next tick.
            TaskCompletionSource<ApiResult> holdA = transport.HoldNext();
            Check(r.StartClaim(Code("PROMPT_A"), out message) && message.Contains("Sending it now"), "idle console claim says it is sent now");
            r.Tick();
            Check(r.IsInFlight, "claim sent on the very next tick");
            WaitForRequests(transport, 1);
            Check(transport.Requests[0].Url.EndsWith("/claim", StringComparison.Ordinal) && transport.Requests[0].Json.Contains(Code("PROMPT_A")), "claim A body");
            Check(r.DescribeStatus().Contains("request sent, waiting for the answer"), "status shows the claim in flight");
            Check(r.StartClaim(Code("PROMPT_A"), out message) && message.Contains("already sent"), "same code while in flight is not queued again");

            // A claim entered while a report is in flight goes out in the tick that handles the answer.
            TaskCompletionSource<ApiResult> holdB = transport.HoldNext();
            Check(r.StartClaim(Code("PROMPT_B"), out message) && message.Contains("replaces the pending code slclm_PROMPT...") && message.Contains("current slmaps request finishes"),
                "replacement while a request is out explains the wait");
            Check(r.DescribeStatus().Contains("sent as soon as the current slmaps request finishes"), "status shows the queued replacement");
            holdA.SetResult(Http(401, "{\"error\":\"invalid code\"}"));
            TickUntilRequests(r, transport, 2);
            Check(transport.Requests[1].Json.Contains(Code("PROMPT_B")), "new code sent right after the old answer was handled");
            Equal("Claiming", r.ClaimStateName, "old 401 not applied to the new code");
            Check(!host.HasLog("ERROR", "rejected the claim code"), "no rejection logged for the replaced code");
            Check(r.DescribeEvents(ConsoleCommandParser.MaxLogCount).Contains("Ignored the answer for replaced or cancelled code slclm_PROMPT..."), "ignored answer recorded");

            // A new code does not wait for the backoff of the old one.
            holdB.SetResult(Http(503, "{\"error\":\"db unavailable\"}"));
            Pump(r);
            Equal(2, transport.Count, "503 -> waiting for backoff");
            Check(r.DescribeStatus().Contains("next attempt in 5s"), "status shows the retry delay");
            TaskCompletionSource<ApiResult> holdC = transport.HoldNext();
            r.StartClaim(Code("PROMPT_C"), out message);
            r.Tick();
            Check(r.IsInFlight, "replacement sent on the next tick without waiting for the old backoff");
            WaitForRequests(transport, 3);
            Check(transport.Requests[2].Json.Contains(Code("PROMPT_C")), "claim C body");

            // A code waiting for review is replaced at once.
            holdC.SetResult(Http(202, "{\"status\":\"review\",\"reason\":\"ip_mismatch\",\"retryAfterSeconds\":600}"));
            Pump(r);
            Equal("Review", r.ClaimStateName, "C under review");
            TaskCompletionSource<ApiResult> holdD = transport.HoldNext();
            r.StartClaim(Code("PROMPT_D"), out message);
            r.Tick();
            Check(r.IsInFlight, "replacement of a code under review sent at once");
            WaitForRequests(transport, 4);

            // A late 202 for the old code does not put the new one into review.
            TaskCompletionSource<ApiResult> holdE = transport.HoldNext();
            r.StartClaim(Code("PROMPT_E"), out message);
            holdD.SetResult(Http(202, "{\"status\":\"review\",\"reason\":\"ip_mismatch\",\"retryAfterSeconds\":60}"));
            TickUntilRequests(r, transport, 5);
            Equal("Claiming", r.ClaimStateName, "old 202 does not put the new code under review");
            Check(transport.Requests[4].Json.Contains(Code("PROMPT_E")), "claim E body");

            holdE.SetResult(Http(200, "{\"status\":\"verified\",\"serverId\":\"srv-e\",\"credential\":\"slsrv_PROMPT_NEW\",\"issuedAt\":\"\"}"));
            Pump(r);
            Equal("None", r.ClaimStateName, "E verified");
            Check(host.Stored != null && host.Stored.Credential == "slsrv_PROMPT_NEW", "credential from E saved");
            Equal(0.0, now, "whole sequence ran without advancing the clock");

            string[] secrets = { "PROMPT_A", "PROMPT_B", "PROMPT_C", "PROMPT_D", "PROMPT_E", "PROMPT_NEW", "PROMPT_OLD" };
            CheckNoLeak(host.Logs, secrets, "console log");
            CheckNoLeak(r.DescribeEvents(ConsoleCommandParser.MaxLogCount).Split('\n'), secrets, "slmaps log");
            CheckNoLeak(r.DescribeStatus().Split('\n'), secrets, "slmaps status");
        }

        private static void TestClaimCancel()
        {
            FakeHost host = new FakeHost();
            host.Cfg.ClaimCode = Code("CANCEL_CFG");
            FakeTransport transport = new FakeTransport();
            double now = 0;
            Reporter r = new Reporter(host, transport, () => now, () => DateTime.UtcNow);
            r.Start();
            Equal("Claiming", r.ClaimStateName, "config claim started");
            string message;
            Check(r.CancelClaim(out message) && message.Contains("Cancelled the claim with code slclm_CANCEL...") && message.Contains("claim_code was cleared"), "cancel config claim");
            Equal("None", r.ClaimStateName, "claim state none after cancel");
            Equal("", host.Cfg.ClaimCode, "claim_code cleared like on success");
            Equal(1, host.SaveConfigCalls, "config saved once");
            Check(host.HasLog("WARN", "registration_token is empty"), "falls back to the idle guidance");
            Check(r.DescribeStatus().Contains("cancelled from the console"), "status shows the cancel");
            now += 1000;
            Pump(r);
            Equal(0, transport.Count, "cancelled claim never sent");
            host.Cfg.ClaimCode = Code("CANCEL_CFG");
            r.NotifyConfigReloaded();
            Pump(r);
            Equal(0, transport.Count, "reload does not resend a cancelled code");
            Check(!r.CancelClaim(out message) && message.Contains("No claim is in progress"), "nothing to cancel");

            // Cancel while in flight: a late 401 is ignored, a late 200 still saves the credential.
            TaskCompletionSource<ApiResult> hold1 = transport.HoldNext();
            r.StartClaim(Code("CANCEL_1"), out message);
            r.Tick();
            Check(r.IsInFlight, "claim 1 in flight");
            Check(r.CancelClaim(out message) && message.Contains("already sent") && !message.Contains("claim_code was cleared"), "cancel while in flight explains the late answer");
            Equal(1, host.SaveConfigCalls, "console cancel leaves a different claim_code alone");
            hold1.SetResult(Http(401, "{\"error\":\"invalid code\"}"));
            Pump(r);
            Check(!host.HasLog("ERROR", "rejected the claim code"), "late 401 for a cancelled code ignored");
            Equal("None", r.ClaimStateName, "still no claim");

            TaskCompletionSource<ApiResult> hold2 = transport.HoldNext();
            r.StartClaim(Code("CANCEL_2"), out message);
            r.Tick();
            r.CancelClaim(out message);
            hold2.SetResult(Http(200, "{\"status\":\"verified\",\"serverId\":\"srv-late\",\"credential\":\"slsrv_CANCEL_LATE\",\"issuedAt\":\"\"}"));
            Pump(r);
            Check(host.Stored != null && host.Stored.Credential == "slsrv_CANCEL_LATE", "late 200 for a cancelled code still saves the credential");
            Equal("Registered", r.StateName, "registered with the late credential");

            // With a token present, cancelling falls back to token registration.
            FakeHost host2 = new FakeHost();
            host2.Cfg.ClaimCode = Code("CANCEL_TOKEN");
            host2.Cfg.RegistrationToken = "slreg_AFTER_CANCEL";
            FakeTransport transport2 = new FakeTransport();
            Reporter r2 = new Reporter(host2, transport2, () => now, () => DateTime.UtcNow);
            r2.Start();
            Equal("Idle", r2.StateName, "token waits while the claim runs");
            r2.CancelClaim(out message);
            Equal("Registering", r2.StateName, "cancel falls back to registration_token");
        }

        private static bool ClaimRequestWith(FakeTransport t, string code, int fromIndex)
        {
            lock (t.Requests)
            {
                for (int i = fromIndex; i < t.Requests.Count; i++)
                {
                    if (t.Requests[i].Url.EndsWith("/claim", StringComparison.Ordinal) && t.Requests[i].Json.Contains("\"code\":\"" + code + "\""))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        private static void TestConfigClaimReplacedFromConsole()
        {
            const string review60 = "{\"status\":\"review\",\"reason\":\"ip_mismatch\",\"retryAfterSeconds\":60}";
            string y = Code("CFG_Y");
            string x = Code("CONSOLE_X");
            string message;
            double now = 0;

            // (a) A config code replaced from the console is not sent again after a reload.
            FakeHost host = new FakeHost();
            host.Cfg.ClaimCode = y;
            FakeTransport transport = new FakeTransport();
            Reporter r = new Reporter(host, transport, () => now, () => DateTime.UtcNow);
            r.Start();
            transport.Script.Enqueue(() => Http(202, review60));
            Pump(r);
            Equal("Review", r.ClaimStateName, "(a) config Y under review");
            Check(r.StartClaim(x, out message) && message.Contains("replaces the pending code slclm_CFG_Y0...") && message.Contains("claim_code was cleared from config.yml"),
                "(a) console X replaces config Y and says claim_code was cleared");
            Equal("", host.Cfg.ClaimCode, "(a) replaced claim_code cleared like on cancel");
            Equal(1, host.SaveConfigCalls, "(a) config saved once");
            transport.Script.Enqueue(() => Http(202, review60));
            Pump(r);
            Equal(2, transport.Count, "(a) X sent");
            Check(ClaimRequestWith(transport, x, 1), "(a) second request carries X");
            Equal("Review", r.ClaimStateName, "(a) X under review");
            host.Cfg.ClaimCode = y; // the old value came back in config.yml
            r.NotifyConfigReloaded();
            Pump(r);
            Equal("Review", r.ClaimStateName, "(a) reload keeps X under review");
            Equal(2, transport.Count, "(a) reload sends nothing");
            transport.Script.Enqueue(() => Http(202, review60));
            now += 60;
            Pump(r);
            Equal(3, transport.Count, "(a) X polled after retryAfterSeconds");
            Check(ClaimRequestWith(transport, x, 2) && !ClaimRequestWith(transport, y, 1), "(a) poll carries X, never Y again");

            // (c) After a successful claim, the leftover config code is not sent on reload.
            transport.Script.Enqueue(() => Http(200, "{\"status\":\"verified\",\"serverId\":\"srv-x\",\"credential\":\"slsrv_REPLACED_X\",\"issuedAt\":\"\"}"));
            now += 60;
            Pump(r);
            Equal("Registered", r.StateName, "(c) X verified");
            Equal("None", r.ClaimStateName, "(c) claim finished");
            Equal(y, host.Cfg.ClaimCode, "(c) success of X leaves the different claim_code alone");
            r.NotifyConfigReloaded();
            now += 1000;
            Pump(r);
            Pump(r);
            Equal(4, transport.Count, "(c) reload after success sends nothing");
            Equal("None", r.ClaimStateName, "(c) Y not restarted after success");
            Check(!host.HasLog("ERROR", "rejected the claim code"), "(c) no rejection logged after a successful claim");
            Check(host.Stored != null && host.Stored.Credential == "slsrv_REPLACED_X", "(c) X credential kept");

            // (b) When config.yml could not be written, cancelling does not restart the old code.
            FakeHost hostB = new FakeHost();
            hostB.KeepClaimCode = true;
            hostB.Cfg.ClaimCode = y;
            FakeTransport transportB = new FakeTransport();
            Reporter rB = new Reporter(hostB, transportB, () => now, () => DateTime.UtcNow);
            rB.Start();
            transportB.Script.Enqueue(() => Http(202, review60));
            Pump(rB);
            Equal("Review", rB.ClaimStateName, "(b) config Y under review");
            Check(rB.StartClaim(x, out message) && message.Contains("Remove the old claim_code from config.yml by hand"), "(b) replacement asks to remove the old claim_code by hand when it could not be cleared");
            Equal(y, hostB.Cfg.ClaimCode, "(b) claim_code still Y");
            Check(rB.CancelClaim(out message) && message.Contains("Cancelled the claim with code slclm_CONSOL..."), "(b) cancel X");
            Equal("None", rB.ClaimStateName, "(b) cancel really stops claiming; Y is not restarted");
            Check(!rB.DescribeStatus().Contains("sending now"), "(b) status shows no claim being sent");
            Check(hostB.HasLog("WARN", "claim_code holds a code that was already rejected, used, replaced or cancelled"), "(b) idle guidance explains why claim_code is not sent");
            now += 1000;
            Pump(rB);
            rB.NotifyConfigReloaded();
            Pump(rB);
            Equal(1, transportB.Count, "(b) nothing sent after cancel or reload");

            // (d) A late 401 for the replaced code never brings it back, not even after the new code fails.
            string a = Code("CFG_A");
            string b = Code("CONSOLE_B");
            FakeHost hostD = new FakeHost();
            hostD.KeepClaimCode = true;
            hostD.Cfg.ClaimCode = a;
            FakeTransport transportD = new FakeTransport();
            Reporter rD = new Reporter(hostD, transportD, () => now, () => DateTime.UtcNow);
            rD.Start();
            TaskCompletionSource<ApiResult> holdA = transportD.HoldNext();
            rD.Tick();
            WaitForRequests(transportD, 1);
            Check(rD.StartClaim(b, out message) && message.Contains("current slmaps request finishes"), "(d) console B queued behind A");
            transportD.Script.Enqueue(() => Http(202, review60));
            holdA.SetResult(Http(401, "{\"error\":\"invalid code\"}"));
            TickUntilRequests(rD, transportD, 2);
            Pump(rD);
            Check(ClaimRequestWith(transportD, b, 1), "(d) B sent after A's answer");
            Equal("Review", rD.ClaimStateName, "(d) B under review");
            Check(!hostD.HasLog("ERROR", "rejected the claim code"), "(d) A's late 401 not applied");
            rD.NotifyConfigReloaded();
            Pump(rD);
            Equal("Review", rD.ClaimStateName, "(d) reload keeps B under review");
            Equal(2, transportD.Count, "(d) reload does not resend A");
            transportD.Script.Enqueue(() => Http(401, "{\"error\":\"invalid code\"}"));
            now += 60;
            Pump(rD);
            Equal("None", rD.ClaimStateName, "(d) B rejected");
            now += 1000;
            Pump(rD);
            rD.NotifyConfigReloaded();
            Pump(rD);
            Equal(3, transportD.Count, "(d) failure of B does not restart A");
            Check(!ClaimRequestWith(transportD, a, 1), "(d) A never sent again");

            string[] secrets = { y, x, a, b, "REPLACED_X" };
            CheckNoLeak(host.Logs, secrets, "console log");
            CheckNoLeak(hostB.Logs, secrets, "console log");
            CheckNoLeak(hostD.Logs, secrets, "console log");
        }

        private static void TestReportNow()
        {
            FakeHost host = new FakeHost();
            FakeTransport transport = new FakeTransport();
            double now = 0;
            Reporter r = new Reporter(host, transport, () => now, () => DateTime.UtcNow);
            r.Start();
            string message;
            Check(!r.QueueReportNow(out message) && message.Contains("not registered") && message.Contains("slmaps claim"), "report refused when not registered");

            // While a claim runs, the answer points at it instead of asking for a new code.
            r.StartClaim(Code("REPORT_CLAIM"), out message);
            Check(!r.QueueReportNow(out message) && message.Contains("is in progress (being sent)") && message.Contains("reports start automatically") && !message.Contains("/server claim"),
                "report while a claim is being sent points to the running claim");
            transport.Script.Enqueue(() => Http(202, "{\"status\":\"review\",\"reason\":\"ip_mismatch\",\"retryAfterSeconds\":60}"));
            Pump(r);
            Equal("Review", r.ClaimStateName, "claim under review");
            Check(!r.QueueReportNow(out message) && message.Contains("waiting for manual review by slmaps staff") && message.Contains("slmaps status") && !message.Contains("/server claim"),
                "report while the claim waits for review points to the review");
            Check(r.CancelClaim(out message), "cancel the review claim");
            Check(!r.QueueReportNow(out message) && message.Contains("not registered"), "report refused again once the claim is cancelled");

            host.Stored = new StoredCredential { ServerId = "srv", Credential = "slsrv_REPORT_NOW", IssuedAt = "" };
            r.NotifyConfigReloaded();
            Pump(r);
            Equal("Registered", r.StateName, "registered from credential.yml after reload");
            Check(!r.QueueReportNow(out message) && message.Contains("No map"), "report refused before a map exists");

            host.Cfg.ReportIntervalSeconds = 0; // periodic reports off
            r.OnMapGenerated(4242);
            Pump(r);
            int before = transport.Count;
            Check(r.QueueReportNow(out message) && message.Contains("seed 4242") && message.Contains("Sending it now"), "report queued");
            Check(r.HasPendingPeriodic, "manual report waits in the periodic slot");
            r.Tick();
            Check(r.IsInFlight, "manual report sent on the next tick even with periodic reports off");
            Pump(r);
            Equal(before + 1, transport.Count, "one manual report");
            Check(transport.Requests[before].Json.StartsWith("{\"event\":\"periodic\",\"seed\":4242,", StringComparison.Ordinal) && transport.Requests[before].Bearer == "slsrv_REPORT_NOW",
                "manual report uses the v1 periodic event");
            Check(host.HasLog("INFO", "Report of seed 4242 accepted (no block window)"), "manual report result shown without debug");
            Check(r.DescribeStatus().Contains("periodic seed 4242 accepted, no block window"), "status shows the last report");
            now += 10000;
            Pump(r);
            Equal(before + 1, transport.Count, "no automatic periodic while disabled");

            // A manual report restarts the periodic interval.
            host.Cfg.ReportIntervalSeconds = 60;
            transport.Script.Enqueue(() => Http(200, "{\"ok\":true,\"blockedUntil\":\"2026-09-15T05:11:52Z\"}"));
            r.QueueReportNow(out message);
            Pump(r);
            Equal(before + 2, transport.Count, "second manual report");
            Check(r.DescribeStatus().Contains("blocked until "), "status shows blockedUntil");
            now += 59;
            Pump(r);
            Equal(before + 2, transport.Count, "no automatic periodic right after a manual one");
            now += 1;
            Pump(r);
            Equal(before + 3, transport.Count, "automatic periodic resumes one interval after the manual report");

            // Queued behind a request in flight.
            TaskCompletionSource<ApiResult> hold = transport.HoldNext();
            r.QueueReportNow(out message);
            r.Tick();
            Check(r.IsInFlight, "manual report in flight");
            Check(r.QueueReportNow(out message) && message.Contains("after the slmaps requests ahead of it"), "queued behind an in-flight request");
            hold.SetResult(Http(200, "{\"ok\":true,\"blockedUntil\":null}"));
            Pump(r);
            Pump(r);
            Equal(before + 5, transport.Count, "queued manual report sent after the in-flight one");

            transport.Script.Enqueue(() => Http(401, "{\"error\":\"invalid credential\"}"));
            r.QueueReportNow(out message);
            Pump(r);
            Equal("Revoked", r.StateName, "revoked");
            Check(!r.QueueReportNow(out message) && message.Contains("Reporting is stopped"), "report refused after revocation");
            TaskCompletionSource<ApiResult> reclaimHold = transport.HoldNext();
            r.StartClaim(Code("REPORT_RECLAIM"), out message);
            Check(!r.QueueReportNow(out message) && message.Contains("is in progress") && !message.Contains("Claim the server again"), "report while re-claiming after revocation points to the running claim");
            r.Tick();
            Check(!r.QueueReportNow(out message) && message.Contains("is in progress (being sent)"), "report while the re-claim is in flight");
            reclaimHold.SetResult(Http(503, "{\"error\":\"db unavailable\"}"));
            Pump(r);
            CheckNoLeak(r.DescribeEvents(ConsoleCommandParser.MaxLogCount).Split('\n'), new[] { "REPORT_NOW" }, "slmaps log");
        }

        private static void TestEventLog()
        {
            DateTime t0 = new DateTime(2026, 9, 15, 5, 0, 0, DateTimeKind.Utc);
            EventLog log = new EventLog();
            Equal(0, log.Last(10).Count, "empty log");
            for (int i = 1; i <= 60; i++)
            {
                log.Add(t0.AddSeconds(i), "info", "event " + i);
            }
            Equal(EventLog.Capacity, log.Count, "capacity enforced");
            List<string> last = log.Last(10);
            Check(last.Count == 10 && last[0].EndsWith(" event 51", StringComparison.Ordinal) && last[9].EndsWith(" event 60", StringComparison.Ordinal), "last 10, oldest first");
            List<string> all = log.Last(1000);
            Check(all.Count == 50 && all[0].EndsWith(" event 11", StringComparison.Ordinal), "oldest 10 dropped");
            Equal(0, log.Last(0).Count, "n = 0 -> nothing");
            Check(System.Text.RegularExpressions.Regex.IsMatch(last[0], "^\\d{4}-\\d{2}-\\d{2} \\d{2}:\\d{2}:\\d{2} INFO  event 51$"), "line format: local timestamp, level, text");

            log.Add(t0.AddSeconds(100), "info", "Report periodic seed 1 accepted.");
            log.Add(t0.AddSeconds(110), "info", "Claim request sent (code slclm_ABCDEF...).");
            log.Add(t0.AddSeconds(120), "info", "Report periodic seed 1 accepted.");
            log.Add(t0.AddSeconds(130), "info", "Report periodic seed 1 accepted.");
            log.Add(t0.AddSeconds(140), "warn", "Report periodic seed 1 accepted.");
            List<string> merged = log.Last(3);
            Check(merged[0].Contains("Claim request sent"), "other event stays before the merged one");
            Check(merged[1].Contains("INFO  Report periodic seed 1 accepted. (x3 since "), "repeated event merged with a count");
            Check(merged[2].Contains("WARN  Report periodic seed 1 accepted.") && !merged[2].Contains("(x"), "different level not merged");
            Equal(EventLog.Capacity, log.Count, "merging does not grow the buffer");

            EventLog secrets = new EventLog();
            secrets.Add(t0, "error", "code slclm_ABCDEFGHIJKLMNOPQRSTUVWX credential slsrv_SECRETCREDENTIAL token slreg_SECRETTOKEN12 shown slclm_ABCDEF... prefix slclm_");
            string line = secrets.Last(1)[0];
            Check(!line.Contains("GHIJKL") && !line.Contains("SECRETCREDENTIAL") && !line.Contains("SECRETTOKEN"), "tokens, credentials and codes cut to 12 chars");
            Check(line.Contains("code slclm_ABCDEF... credential slsrv_SECRET... token slreg_SECRET... shown slclm_ABCDEF... prefix slclm_"), "12-char prefixes kept");

            FakeHost host = new FakeHost();
            FakeTransport transport = new FakeTransport();
            double now = 0;
            Reporter r = new Reporter(host, transport, () => now, () => t0);
            Check(r.DescribeEvents(10).StartsWith("No plugin events", StringComparison.Ordinal), "reporter log empty before start");
            r.Start();
            string message;
            for (int i = 0; i < 70; i++)
            {
                r.StartClaim("slclm_bad=" + i, out message);
            }
            string ten = r.DescribeEvents(ConsoleCommandParser.DefaultLogCount);
            Equal(11, ten.Split('\n').Length, "slmaps log 10 -> header + 10 lines");
            Equal(51, r.DescribeEvents(ConsoleCommandParser.MaxLogCount).Split('\n').Length, "slmaps log 50 -> header + 50 lines");
            Equal(51, r.DescribeEvents(1000).Split('\n').Length, "slmaps log is capped at 50 lines");
            Check(ten.Contains("Console claim code slclm_bad=69 was not sent"), "latest event last");
        }

        private static void TestStopIgnoresLateResult()
        {
            FakeHost host = new FakeHost();
            host.Stored = new StoredCredential { ServerId = "srv", Credential = "slsrv_x", IssuedAt = "" };
            FakeTransport transport = new FakeTransport();
            transport.Hold = new TaskCompletionSource<ApiResult>();
            double now = 0;
            Reporter r = new Reporter(host, transport, () => now, () => DateTime.UtcNow);
            r.Start();
            Equal("Registered", r.StateName, "stored credential used");
            r.OnMapGenerated(7);
            r.Tick();
            Check(r.IsInFlight, "request in flight");
            r.Stop();
            transport.Hold.SetResult(Http(401, "{\"error\":\"invalid credential\"}"));
            Thread.Sleep(20);
            r.Tick();
            Equal("Stopped", r.StateName, "late 401 ignored after stop");
        }


        private const string TestDownloadUrl = "https://github.com/as1tself/slmaps-server-plugin/releases/latest";
        private const string Review60 = "{\"status\":\"review\",\"reason\":\"ip_mismatch\",\"retryAfterSeconds\":60}";
        private const double Hours12 = Reporter.VersionCheckIntervalSeconds;

        // Built from the current version so that no version string is pinned in the tests.
        private static string VersionPlus(int major, int minor, int patch)
        {
            int[] p;
            if (!VersionNumber.TryParse(PluginInfo.Version, out p))
            {
                throw new InvalidOperationException("PluginInfo.Version is not major.minor.patch");
            }
            return (p[0] + major) + "." + (p[1] + minor) + "." + (p[2] + patch);
        }

        private static string JsonText(string s)
        {
            return s == null ? "null" : "\"" + s + "\"";
        }

        private static ApiResult VersionAnswer(string latest, string minimum, string url)
        {
            return Http(200, "{\"latest\":" + JsonText(latest) + ",\"minimum\":" + JsonText(minimum) + ",\"downloadUrl\":" + JsonText(url) + "}");
        }

        private static ApiResult Outdated426(string minimum, string url)
        {
            return Http(426, "{\"error\":\"plugin_outdated\",\"minimum\":" + JsonText(minimum) + ",\"downloadUrl\":" + JsonText(url) + "}");
        }

        // Ticks until both request slots are free.
        private static void PumpAll(Reporter r)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(3);
            while (true)
            {
                r.Tick();
                if (!r.IsInFlight && !r.IsVersionCheckInFlight)
                {
                    return;
                }
                if (DateTime.UtcNow > deadline)
                {
                    throw new TimeoutException("requests did not complete");
                }
                Thread.Sleep(2);
            }
        }

        // Ticks until the version check is done; other requests may stay held.
        private static void PumpVersion(Reporter r)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(3);
            while (true)
            {
                r.Tick();
                if (!r.IsVersionCheckInFlight)
                {
                    return;
                }
                if (DateTime.UtcNow > deadline)
                {
                    throw new TimeoutException("version check did not complete");
                }
                Thread.Sleep(2);
            }
        }

        private static int CountLogs(FakeHost host, string level, string fragment)
        {
            return host.Logs.FindAll(l => l.StartsWith(level + " ", StringComparison.Ordinal) && l.IndexOf(fragment, StringComparison.Ordinal) >= 0).Count;
        }

        private static void TestVersionNumber()
        {
            Check(VersionNumber.IsValid(PluginInfo.Version), "PluginInfo.Version is a valid version");
            foreach (string ok in new[] { "1.2.0", " 1.2.0 ", "v1.2.0", "V10.0.3", "1.2.0-beta.1", "1.2.0+build.5", "1.2.0-rc.1+sha.abc", "0.0.0", "123456789.0.0" })
            {
                Check(VersionNumber.IsValid(ok), "valid version '" + ok + "'");
            }
            foreach (string bad in new[] { null, "", "   ", "1", "1.2", "1.2.0.0", "1..0", ".1.2", "a.b.c", "1.2.x", "-1.2.0", "+1.2.0", "1.2.0-", "1.2.0+",
                "1.2.0-bad suffix", "1.2.0-x\ny", "1234567890.0.0", "1.2.0 beta", "vv1.2.0", "1.2." + (char)0x0660, "1.2.0-" + (char)0xE9, new string('1', 70) })
            {
                Check(!VersionNumber.IsValid(bad), "invalid version '" + (bad == null ? "null" : bad.Length > 20 ? bad.Substring(0, 20) + "..." : bad) + "'");
            }
            int c;
            Check(VersionNumber.TryCompare("1.2.0", "1.10.0", out c) && c < 0, "1.2.0 < 1.10.0 (numeric, not text)");
            Check(VersionNumber.TryCompare("1.10.0", "1.9.9", out c) && c > 0, "1.10.0 > 1.9.9");
            Check(VersionNumber.TryCompare("2.0.0", "1.99.99", out c) && c > 0, "major decides first");
            Check(VersionNumber.TryCompare("1.2.1", "1.2.0", out c) && c > 0, "patch compared");
            Check(VersionNumber.TryCompare("1.2.0", "1.3.0", out c) && c < 0, "minor compared");
            Check(VersionNumber.TryCompare("1.2.0", "1.2.0-beta.1", out c) && c == 0, "-suffix ignored");
            Check(VersionNumber.TryCompare("v1.2.0+build.9", "1.2.0", out c) && c == 0, "v prefix and +suffix ignored");
            Check(VersionNumber.TryCompare("1.02.0", "1.2.0", out c) && c == 0, "leading zeros compare numerically");
            Check(!VersionNumber.TryCompare("1.2.0", "garbage", out c) && !VersionNumber.TryCompare(null, "1.2.0", out c), "invalid side -> no comparison");
            Equal("1.2.3", VersionNumber.Normalize("v01.02.3-rc.1"), "normalized key drops v, zeros and suffix");
            Check(VersionNumber.Normalize("1.2") == null, "normalize invalid -> null");
            Check(VersionNumber.TryCompare(PluginInfo.Version, VersionPlus(0, 0, 1), out c) && c < 0, "test helper builds a newer version");
        }

        private static void TestVersionBodies()
        {
            VersionInfo v = VersionInfo.TryParse(Json.TryParseObject("{\"latest\":\"1.3.0\",\"minimum\":\"1.1.0\",\"downloadUrl\":\"" + TestDownloadUrl + "\"}"));
            Check(v != null && v.Latest == "1.3.0" && v.Minimum == "1.1.0" && v.DownloadUrl == TestDownloadUrl, "full body");
            v = VersionInfo.TryParse(Json.TryParseObject("{\"latest\":null,\"minimum\":null,\"downloadUrl\":null}"));
            Check(v != null && v.Latest == null && v.Minimum == null && v.DownloadUrl == null, "all null -> known: nothing published, no minimum");
            v = VersionInfo.TryParse(Json.TryParseObject("{}"));
            Check(v != null && v.Latest == null && v.Minimum == null && v.DownloadUrl == null, "missing keys -> null");
            v = VersionInfo.TryParse(Json.TryParseObject("{\"releaseNotes\":\"x\",\"latest\":\" 1.3.0-rc.1 \",\"assets\":[{\"name\":\"a.dll\",\"size\":1}],\"minimum\":null,\"n\":5,\"b\":true,\"downloadUrl\":null}"));
            Check(v != null && v.Latest == "1.3.0-rc.1" && v.Minimum == null, "unknown extra fields ignored, value trimmed");
            foreach (string garbage in new[] { null, "", "<html>502 Bad Gateway</html>", "[1,2]", "\"1.3.0\"", "{\"latest\":1.3}", "{\"latest\":\"banana\"}",
                "{\"latest\":\"1.3.0\",\"minimum\":\"1.1\"}", "{\"minimum\":true}", "{\"latest\":[\"1.3.0\"]}", "{\"latest\":{\"v\":\"1.3.0\"}}", "{\"latest\":\"1.3.0\"" })
            {
                Check(VersionInfo.TryParse(Json.TryParseObject(garbage)) == null, "garbage body -> unknown: " + (garbage ?? "null"));
            }
            foreach (string badUrl in new[] { "\"ftp://example.com/a.dll\"", "\"javascript:alert(1)\"", "123", "\"https://exa mple.com/\"", "\"/releases/latest\"", "\"\"",
                "\"https://example.com/" + new string('a', VersionInfo.MaxUrlLength) + "\"" })
            {
                v = VersionInfo.TryParse(Json.TryParseObject("{\"latest\":\"1.3.0\",\"minimum\":null,\"downloadUrl\":" + badUrl + "}"));
                Check(v != null && v.Latest == "1.3.0" && v.DownloadUrl == null, "unusable downloadUrl dropped, body still known: " + badUrl.Substring(0, Math.Min(30, badUrl.Length)));
            }

            ApiResult r = Outdated426("1.3.0", TestDownloadUrl);
            Check(OutdatedAnswer.Matches(r) && OutdatedAnswer.Minimum(r) == "1.3.0" && OutdatedAnswer.DownloadUrl(r) == TestDownloadUrl, "426 plugin_outdated parsed");
            r = Http(426, "{\"error\":\"plugin_outdated\"}");
            Check(OutdatedAnswer.Matches(r) && OutdatedAnswer.Minimum(r) == null && OutdatedAnswer.DownloadUrl(r) == null, "426 without minimum/downloadUrl still matches");
            r = Http(426, "{\"error\":\"plugin_outdated\",\"minimum\":\"soon\",\"downloadUrl\":\"nope\",\"extra\":1}");
            Check(OutdatedAnswer.Matches(r) && OutdatedAnswer.Minimum(r) == null && OutdatedAnswer.DownloadUrl(r) == null, "426 with garbage minimum/url -> nulls");
            Check(!OutdatedAnswer.Matches(Http(426, "")) && !OutdatedAnswer.Matches(Http(426, "{\"error\":\"upgrade required\"}")), "426 without plugin_outdated is not the version answer");
            Check(!OutdatedAnswer.Matches(Http(400, "{\"error\":\"plugin_outdated\"}")) && !OutdatedAnswer.Matches(ApiResult.FromFailure(ApiFailure.Timeout, "timeout")), "only HTTP 426 matches");
        }

        private static void TestVersionSchedule()
        {
            FakeHost host = new FakeHost();
            host.Stored = new StoredCredential { ServerId = "srv", Credential = "slsrv_VERSION_SCHEDULE", IssuedAt = "" };
            host.Cfg.ReportIntervalSeconds = 0;
            FakeTransport transport = new FakeTransport();
            double now = 0;
            Reporter r = new Reporter(host, transport, () => now, () => DateTime.UtcNow);
            r.Start();
            string message;
            Check(r.DescribeStatus().Contains("\nversion check: not checked yet"), "status before the first check");

            now = Reporter.FirstVersionCheckDelaySeconds - 0.5;
            PumpAll(r);
            Check(!r.IsVersionCheckInFlight && transport.VersionCount == 0, "no version check right at enable");
            now = Reporter.FirstVersionCheckDelaySeconds;
            r.Tick();
            Check(r.IsVersionCheckInFlight, "first version check shortly after enable");
            Check(Reporter.FirstVersionCheckDelaySeconds > 0 && Reporter.FirstVersionCheckDelaySeconds <= 60, "first check within a minute of enable");
            PumpAll(r);
            Equal(1, transport.VersionCount, "one version request");
            Equal("https://slmaps.com/api/plugin/v1/version", transport.VersionRequests[0].Url, "version url");
            Equal(10, transport.VersionRequests[0].Timeout, "version check uses request_timeout_seconds");
            Equal(0, transport.Count, "the version check is not a register/claim/report request");
            Check(r.DescribeStatus().Contains("\nversion check: unknown"), "404 (older backend) -> unknown");

            double first = Reporter.FirstVersionCheckDelaySeconds;
            Equal(12 * 3600.0, Hours12, "interval is 12 hours");
            now = first + Hours12 - 1;
            PumpAll(r);
            Equal(1, transport.VersionCount, "no second check before 12 h");
            now = first + Hours12;
            r.Tick();
            Check(r.IsVersionCheckInFlight, "second check exactly 12 h after the first");
            PumpAll(r);
            Equal(2, transport.VersionCount, "two version requests after 12 h");

            Func<ApiResult>[] unknowns =
            {
                () => Http(429, "{\"error\":\"rate limited\"}"),
                () => Http(503, "{\"error\":\"db unavailable\"}"),
                () => Http(500, ""),
                () => ApiResult.FromFailure(ApiFailure.Timeout, "timeout"),
                () => ApiResult.FromFailure(ApiFailure.Network, "connection refused"),
                () => Http(200, "<html>ok</html>"),
                () => Http(200, "{\"latest\":42,\"minimum\":null}"),
                () => Http(204, ""),
                () => Http(301, ""),
            };
            foreach (Func<ApiResult> unknown in unknowns)
            {
                transport.VersionScript.Enqueue(unknown);
                now += Hours12;
                PumpAll(r);
                now += 3600;
                PumpAll(r); // no early retry after an unknown answer
            }
            Equal(2 + unknowns.Length, transport.VersionCount, "unknown answers are retried only at the next 12 h check");
            Check(!host.Logs.Exists(l => (l.StartsWith("WARN ", StringComparison.Ordinal) || l.StartsWith("ERROR ", StringComparison.Ordinal))), "unknown answers log no warning or error");
            Check(!r.IsOutdated, "unknown answers never mark the plugin outdated");
            Check(r.DescribeEvents(50).Contains("Version check gave no usable answer (HTTP 301)"), "event log records the unknown answer");

            // slmaps version checks now, prints to the console, starts nothing while one runs and restarts the interval.
            transport.VersionScript.Enqueue(() => VersionAnswer(PluginInfo.Version, null, TestDownloadUrl));
            now += 100;
            int before = transport.VersionCount;
            Check(r.CheckVersionNow(out message) && message.Contains("Checking slmaps now; the result is printed to this console."), "slmaps version triggers a check now");
            Check(message.StartsWith(PluginInfo.Name + " " + PluginInfo.Version + "\n", StringComparison.Ordinal) && message.Contains("\nlatest: unknown") && message.Contains("\nminimum: unknown")
                && message.Contains("\ndownload: -") && message.Contains("\nnext check: running now") && message.Contains("\ncheck_for_updates: true"), "slmaps version lists current, latest, minimum, download, next check and the option");
            Check(message.Contains("\nlast check: ") && message.Contains(" unknown (HTTP 301)"), "slmaps version shows the last check time and result");
            Check(r.IsVersionCheckInFlight, "manual check in flight");
            Check(r.CheckVersionNow(out message) && message.Contains("A version check is already running"), "a second slmaps version while one runs");
            PumpAll(r);
            Equal(before + 1, transport.VersionCount, "exactly one request for two slmaps version commands");
            Equal(1, CountLogs(host, "INFO", "Version check: up to date (latest " + PluginInfo.Version + ")."), "manual check result printed once to the console");
            Check(r.DescribeStatus().Contains("\nversion check: up to date (latest " + PluginInfo.Version + ")"), "status shows the checked version");

            double manualAt = now;
            now = manualAt + Hours12 - 1;
            PumpAll(r);
            Equal(before + 1, transport.VersionCount, "a manual check restarts the 12 h interval");
            now = manualAt + Hours12;
            PumpAll(r);
            Equal(before + 2, transport.VersionCount, "next scheduled check 12 h after the manual one");
            Equal(1, CountLogs(host, "INFO", "Version check:"), "scheduled checks print no INFO line");
            Check(r.DescribeStatus().Contains("\nversion check: up to date (latest " + PluginInfo.Version + ")"), "an unknown answer keeps the last known values");
            Check(r.CheckVersionNow(out message) && message.Contains("\nlatest: " + PluginInfo.Version + " (this version)") && message.Contains("\nminimum: none")
                && message.Contains("\ndownload: " + TestDownloadUrl) && message.Contains("unknown (HTTP 404"), "slmaps version shows the last known values and the last unknown result");
            PumpAll(r);

            // A bad api_base_url sends nothing and is retried soon after it is fixed.
            host.Cfg.ApiBaseUrl = "not a url";
            int beforeBadUrl = transport.VersionCount;
            Check(r.CheckVersionNow(out message) && message.Contains("Could not check now: api_base_url is not a valid http(s) URL"), "slmaps version with a bad api_base_url");
            now += Hours12;
            PumpAll(r);
            now += 60;
            PumpAll(r);
            Equal(beforeBadUrl, transport.VersionCount, "nothing sent with a bad api_base_url");
            Equal(1, CountLogs(host, "ERROR", "api_base_url is not a valid http(s) URL"), "bad api_base_url logged once");
            host.Cfg.ApiBaseUrl = "https://slmaps.com";
            now += 60;
            PumpAll(r);
            Equal(beforeBadUrl + 1, transport.VersionCount, "fixed api_base_url is checked within a minute");

            // A version answer that arrives after Stop is dropped.
            TaskCompletionSource<ApiResult> late = transport.HoldNextVersion();
            Check(r.CheckVersionNow(out message) && r.IsVersionCheckInFlight, "check before stop");
            r.Stop();
            Check(!r.IsVersionCheckInFlight, "stop clears the version flag");
            late.SetResult(VersionAnswer(VersionPlus(1, 0, 0), VersionPlus(1, 0, 0), TestDownloadUrl));
            Thread.Sleep(20);
            r.Tick();
            Check(!r.IsOutdated && !host.HasLog("ERROR", "below the minimum"), "a version answer after stop is ignored");
            Check(!r.CheckVersionNow(out message) && message.Contains("not running"), "slmaps version after stop");
        }

        private static void TestVersionWarn()
        {
            string newer1 = VersionPlus(0, 0, 1);
            string newer2 = VersionPlus(0, 1, 0);
            FakeHost host = new FakeHost();
            FakeTransport transport = new FakeTransport();
            double now = 0;
            Reporter r = new Reporter(host, transport, () => now, () => DateTime.UtcNow);
            r.Start();
            string message;
            int idleWarnings = host.Logs.FindAll(l => l.StartsWith("WARN ", StringComparison.Ordinal)).Count;

            transport.VersionScript.Enqueue(() => VersionAnswer(newer1, null, TestDownloadUrl));
            now = Reporter.FirstVersionCheckDelaySeconds;
            PumpAll(r);
            Equal(1, CountLogs(host, "WARN", "A newer SlmapsServerPlugin version is available: " + newer1 + " (this server runs " + PluginInfo.Version + "). Download: " + TestDownloadUrl),
                "WARN for a newer latest names both versions and the download URL");
            Check(r.DescribeStatus().Contains("\nversion check: newer version " + newer1 + " available (download: " + TestDownloadUrl + ")"), "status shows the newer version");
            Check(!r.IsOutdated, "a newer latest alone pauses nothing");

            // The same latest is warned about once, whatever its spelling and whatever comes in between.
            foreach (string same in new[] { newer1, "v" + newer1, newer1 + "-hotfix.1", null, newer1 + "+build.7", newer1 })
            {
                string s = same;
                transport.VersionScript.Enqueue(s == null ? (Func<ApiResult>)(() => Http(503, "")) : () => VersionAnswer(s, null, TestDownloadUrl));
                now += Hours12;
                PumpAll(r);
            }
            Equal(7, transport.VersionCount, "seven checks so far");
            Equal(idleWarnings + 1, host.Logs.FindAll(l => l.StartsWith("WARN ", StringComparison.Ordinal)).Count, "one WARN per distinct latest, not every 12 h");

            transport.VersionScript.Enqueue(() => VersionAnswer(newer2, null, null));
            now += Hours12;
            PumpAll(r);
            Equal(1, CountLogs(host, "WARN", "version is available: " + newer2 + " (this server runs " + PluginInfo.Version + ")."), "a different newer latest warns once more");
            Check(!host.HasLog("WARN", newer2 + " (this server runs " + PluginInfo.Version + "). Download"), "no download text when the answer has no URL");
            transport.VersionScript.Enqueue(() => VersionAnswer(newer2, null, null));
            now += Hours12;
            PumpAll(r);
            Equal(idleWarnings + 2, host.Logs.FindAll(l => l.StartsWith("WARN ", StringComparison.Ordinal)).Count, "still two WARN lines");

            transport.VersionScript.Enqueue(() => VersionAnswer(PluginInfo.Version, null, null));
            now += Hours12;
            PumpAll(r);
            transport.VersionScript.Enqueue(() => VersionAnswer("0.0.1", null, null));
            now += Hours12;
            PumpAll(r);
            Equal(idleWarnings + 2, host.Logs.FindAll(l => l.StartsWith("WARN ", StringComparison.Ordinal)).Count, "no WARN when latest is not newer");
            Check(r.DescribeStatus().Contains("\nversion check: up to date (latest 0.0.1)"), "status: up to date");
            transport.VersionScript.Enqueue(() => VersionAnswer(null, null, null));
            Check(r.CheckVersionNow(out message) && message.Contains("\nlatest: 0.0.1 (this version is newer)"), "slmaps version compares latest with this version");
            PumpAll(r);
            Check(r.DescribeStatus().Contains("\nversion check: up to date (no release published)"), "latest null -> no release published");
            Check(r.DescribeEvents(50).Contains("Newer plugin version " + newer1 + " is available"), "event log records the newer version");

            // check_for_updates false hides only the warning; the minimum is still enforced.
            FakeHost quiet = new FakeHost();
            quiet.Cfg.CheckForUpdates = false;
            FakeTransport quietTransport = new FakeTransport();
            double start = now;
            Reporter rq = new Reporter(quiet, quietTransport, () => now, () => DateTime.UtcNow);
            rq.Start();
            quietTransport.VersionScript.Enqueue(() => VersionAnswer(newer1, null, TestDownloadUrl));
            now = start + Reporter.FirstVersionCheckDelaySeconds;
            PumpAll(rq);
            Equal(0, CountLogs(quiet, "WARN", "A newer"), "check_for_updates=false: no WARN for a newer version");
            Check(rq.DescribeStatus().Contains("\nversion check: newer version " + newer1 + " available"), "check_for_updates=false: status still shows the newer version");
            quietTransport.VersionScript.Enqueue(() => VersionAnswer(newer1, newer1, TestDownloadUrl));
            Check(rq.CheckVersionNow(out message) && message.Contains("\ncheck_for_updates: false (no warning about newer versions; the minimum version is still enforced)")
                && message.Contains("\nlatest: " + newer1 + " (newer than this version)"), "slmaps version shows the option and the latest");
            PumpAll(rq);
            Check(rq.IsOutdated, "check_for_updates=false still enforces the minimum");
            Equal(1, CountLogs(quiet, "ERROR", "below the minimum version slmaps accepts (" + newer1 + ")"), "check_for_updates=false: Outdated ERROR still logged");
            Equal(0, CountLogs(quiet, "WARN", "A newer"), "still no WARN");
            quiet.Cfg.CheckForUpdates = true; // turned on by a reload
            rq.NotifyConfigReloaded();
            quietTransport.VersionScript.Enqueue(() => VersionAnswer(newer1, null, TestDownloadUrl));
            now += Hours12;
            PumpAll(rq);
            Equal(1, CountLogs(quiet, "WARN", "version is available: " + newer1), "turning check_for_updates on warns about a latest not warned yet");
            Check(!rq.IsOutdated, "minimum removed -> Outdated left");
        }

        private static void TestOutdatedOnRegister()
        {
            string minimum = VersionPlus(0, 1, 0);
            FakeHost host = new FakeHost();
            host.Cfg.RegistrationToken = "slreg_OUTDATED_TOKEN";
            FakeTransport transport = new FakeTransport();
            double now = 0;
            Reporter r = new Reporter(host, transport, () => now, () => DateTime.UtcNow);
            transport.Script.Enqueue(() => Outdated426(minimum, TestDownloadUrl));
            r.Start();
            r.OnMapGenerated(31337);
            Equal(1, r.PendingRoundEvents, "round_start queued while registering");
            Pump(r);
            Equal(1, transport.Count, "register sent");
            Check(r.IsOutdated, "426 on register -> Outdated");
            Equal("Registering", r.StateName, "registration state kept");
            Equal("slreg_OUTDATED_TOKEN", host.Cfg.RegistrationToken, "token kept (it was not used)");
            Equal(0, r.PendingRoundEvents, "queued reports dropped on entering Outdated");
            Equal(1, CountLogs(host, "ERROR", "slmaps rejected a request from this plugin version (HTTP 426 on register). This plugin version (" + PluginInfo.Version
                + ") is below the minimum version slmaps accepts (" + minimum + "), so registration, claims and reports are paused.\nInstall the newer SlmapsServerPlugin.dll from "
                + TestDownloadUrl + " and restart the server."), "one ERROR naming the minimum and the download URL");
            Check(!host.HasLog("WARN", "Registration failed"), "426 is not treated as a retryable failure");

            for (int i = 0; i < 5; i++)
            {
                now += 3600;
                PumpAll(r);
            }
            r.NotifyConfigReloaded();
            PumpAll(r);
            Equal(1, transport.Count, "no register retries while outdated (ticks, backoff time and reload)");
            Check(transport.VersionCount >= 1, "scheduled version checks continue while outdated");
            Check(r.IsOutdated, "an unknown version answer (404) keeps Outdated");
            Equal(1, CountLogs(host, "ERROR", "below the minimum"), "still one ERROR");
            string status = r.DescribeStatus();
            Check(status.Contains("\nversion check: OUTDATED: slmaps requires " + minimum + " or newer; registration, claims and reports are paused (download: " + TestDownloadUrl + ")")
                && status.Contains("[paused: this plugin version is below the slmaps minimum]"), "status shows Outdated");
            string message;
            Check(!r.QueueReportNow(out message) && message.StartsWith("Report not queued. This plugin version", StringComparison.Ordinal) && message.Contains(minimum), "slmaps report refused while outdated");
            Check(!r.StartClaim(Code("OUTDATED_CONSOLE"), out message) && message.StartsWith("Claim code not sent. This plugin version", StringComparison.Ordinal)
                && message.Contains(TestDownloadUrl) && message.EndsWith("Then run slmaps claim <code> again.", StringComparison.Ordinal), "slmaps claim refused while outdated");
            Equal("None", r.ClaimStateName, "no claim started while outdated");
            PumpAll(r);
            Equal(1, transport.Count, "still nothing sent");

            // Lowering the minimum releases the pause and sends the pending registration.
            transport.VersionScript.Enqueue(() => VersionAnswer(minimum, PluginInfo.Version, TestDownloadUrl));
            transport.Script.Enqueue(() => Http(200, "{\"serverId\":\"srv-o\",\"credential\":\"slsrv_OUTDATED_CRED\",\"issuedAt\":\"\"}"));
            Check(r.CheckVersionNow(out message) && message.Contains("\nminimum: " + minimum + " (this version is below it)") && message.Contains("\nstatus: OUTDATED: slmaps requires " + minimum),
                "slmaps version shows the minimum while outdated");
            PumpAll(r);
            PumpAll(r);
            Check(!r.IsOutdated, "minimum lowered to this version -> Outdated left");
            Equal(1, CountLogs(host, "INFO", "slmaps accepts plugin " + PluginInfo.Version + " again (minimum " + PluginInfo.Version + "); registration, claims and reports resume."), "recovery logged");
            Equal("Registered", r.StateName, "registration resumed at once and succeeded");
            Equal(3, transport.Count, "register, then the current map");
            Check(transport.Requests[1].Url.EndsWith("/register", StringComparison.Ordinal) && transport.Requests[1].Json.Contains("slreg_OUTDATED_TOKEN"), "the same token was sent after recovery");
            Check(transport.Requests[2].Json.StartsWith("{\"event\":\"round_start\",\"seed\":31337,", StringComparison.Ordinal), "current map announced after registration");
            CheckNoLeak(host.Logs, new[] { "OUTDATED_TOKEN", "OUTDATED_CRED", "OUTDATED_CONSOLE" }, "console log");
            CheckNoLeak(r.DescribeEvents(50).Split('\n'), new[] { "OUTDATED_TOKEN", "OUTDATED_CRED", "OUTDATED_CONSOLE" }, "slmaps log");
        }

        private static void TestOutdatedOnClaim()
        {
            string code = Code("OUTDATED_CLAIM");
            string minimum = VersionPlus(1, 0, 0);
            FakeHost host = new FakeHost();
            host.Cfg.ClaimCode = code;
            FakeTransport transport = new FakeTransport();
            double now = 0;
            Reporter r = new Reporter(host, transport, () => now, () => DateTime.UtcNow);
            transport.Script.Enqueue(() => Outdated426(minimum, null));
            r.Start();
            Pump(r);
            Equal(1, transport.Count, "claim sent");
            Check(r.IsOutdated, "426 on claim -> Outdated");
            Equal("Claiming", r.ClaimStateName, "claim kept (the code is not the problem)");
            Equal(code, host.Cfg.ClaimCode, "claim_code kept");
            Check(!host.HasLog("ERROR", "rejected the claim code") && host.HasLog("ERROR", "(HTTP 426 on claim)"), "426 is not reported as a rejected code");
            Check(host.HasLog("ERROR", "SlmapsServerPlugin.dll and restart the server.") && !host.HasLog("ERROR", "dll from"), "no download URL in the ERROR when none was given");
            string status = r.DescribeStatus();
            Check(status.Contains("paused until the plugin is updated") && status.Contains("\nlast claim result: ") && status.Contains("paused: HTTP 426"), "status shows the paused claim");

            for (int i = 0; i < 4; i++)
            {
                now += 3600;
                PumpAll(r);
            }
            r.NotifyConfigReloaded();
            PumpAll(r);
            Equal(1, transport.Count, "no claim resend while outdated (ticks and reload)");
            Equal(1, CountLogs(host, "ERROR", "below the minimum"), "one ERROR");

            // A later answer without a minimum releases the pause and resends the same code.
            transport.VersionScript.Enqueue(() => VersionAnswer(null, null, null));
            transport.Script.Enqueue(() => Http(202, Review60));
            now += Hours12;
            PumpAll(r);
            PumpAll(r);
            Check(!r.IsOutdated, "no minimum in the version answer -> Outdated left");
            Equal(2, transport.Count, "claim resent right after recovery");
            Check(ClaimRequestWith(transport, code, 1), "resent claim carries the same code");
            Equal("Review", r.ClaimStateName, "claim continues normally (review)");

            // A 426 during review pauses; after recovery the code is resent without waiting.
            transport.Script.Enqueue(() => Outdated426(minimum, TestDownloadUrl));
            now += 60;
            PumpAll(r);
            Equal(3, transport.Count, "review poll answered 426");
            Check(r.IsOutdated && r.ClaimStateName == "Review", "426 during review -> Outdated, review kept");
            Equal(2, CountLogs(host, "ERROR", "below the minimum"), "second Outdated period, second ERROR");
            now += 600;
            PumpAll(r);
            Equal(3, transport.Count, "no review poll while outdated");
            transport.VersionScript.Enqueue(() => VersionAnswer(minimum, "0.0.1", TestDownloadUrl));
            transport.Script.Enqueue(() => Http(200, "{\"status\":\"verified\",\"serverId\":\"srv-oc\",\"credential\":\"slsrv_OUTDATED_CLAIM_CRED\",\"issuedAt\":\"\"}"));
            string message;
            r.CheckVersionNow(out message);
            PumpAll(r);
            PumpAll(r);
            Check(!r.IsOutdated, "minimum below this version -> Outdated left");
            Equal(4, transport.Count, "review poll sent right after recovery");
            Equal("None", r.ClaimStateName, "claim verified");
            Equal("Registered", r.StateName, "registered after recovery");
            Equal("", host.Cfg.ClaimCode, "claim_code cleared after verification");

            // A 426 for a replaced attempt still pauses.
            FakeHost host2 = new FakeHost();
            FakeTransport transport2 = new FakeTransport();
            Reporter r2 = new Reporter(host2, transport2, () => now, () => DateTime.UtcNow);
            r2.Start();
            TaskCompletionSource<ApiResult> holdA = transport2.HoldNext();
            r2.StartClaim(Code("OUTDATED_A"), out message);
            r2.Tick();
            WaitForRequests(transport2, 1);
            r2.StartClaim(Code("OUTDATED_B"), out message);
            holdA.SetResult(Outdated426(minimum, null));
            PumpAll(r2);
            Check(r2.IsOutdated && r2.ClaimStateName == "Claiming", "426 for a replaced attempt -> Outdated, the new code waits");
            Equal(1, transport2.Count, "new code not sent while outdated");

            CheckNoLeak(host.Logs, new[] { code, "OUTDATED_CLAIM_CRED" }, "console log");
            CheckNoLeak(r.DescribeStatus().Split('\n'), new[] { code }, "slmaps status");
            CheckNoLeak(host2.Logs, new[] { Code("OUTDATED_A"), Code("OUTDATED_B") }, "console log");
        }

        private static void TestOutdatedOnReport()
        {
            string minimum = VersionPlus(0, 0, 1);
            FakeHost host = new FakeHost();
            host.Stored = new StoredCredential { ServerId = "srv", Credential = "slsrv_OUTDATED_REPORT", IssuedAt = "" };
            FakeTransport transport = new FakeTransport();
            double now = 0;
            Reporter r = new Reporter(host, transport, () => now, () => DateTime.UtcNow);
            r.Start();
            string message;

            // A 426 without plugin_outdated is an ordinary retryable failure.
            transport.Script.Enqueue(() => Http(426, "{\"error\":\"upgrade required\"}"));
            r.OnMapGenerated(700);
            Pump(r);
            Check(!r.IsOutdated && r.PendingRoundEvents == 1 && host.HasLog("WARN", "Report round_start failed (HTTP 426"), "426 without plugin_outdated is retried like any failure");

            transport.Script.Enqueue(() => Outdated426(minimum, TestDownloadUrl));
            now += 5;
            Pump(r);
            Equal(2, transport.Count, "round_start retried and answered 426 plugin_outdated");
            Check(r.IsOutdated, "426 on report -> Outdated");
            Equal(0, r.PendingRoundEvents, "the rejected report is dropped");
            Equal("Registered", r.StateName, "credential kept");
            Check(r.DescribeStatus().Contains("round_start seed 700 rejected (HTTP 426: plugin version below the slmaps minimum), dropped"), "status shows the rejected report");
            Check(host.HasLog("ERROR", "(HTTP 426 on report)"), "ERROR names the report 426");

            r.OnRoundStarted();
            r.OnRoundEnded();
            r.OnMapGenerated(701);
            Equal(0, r.PendingRoundEvents, "round events are not queued while outdated");
            for (int i = 0; i < 6; i++)
            {
                now += 600;
                PumpAll(r);
            }
            Check(!r.HasPendingPeriodic && r.PendingRoundEvents == 0, "periodic reports are not generated while outdated");
            Equal(2, transport.Count, "no reports sent while outdated");
            Equal(1, CountLogs(host, "ERROR", "below the minimum"), "one ERROR");

            // When the minimum drops to this version, the current map is announced once.
            transport.VersionScript.Enqueue(() => VersionAnswer(minimum, PluginInfo.Version, TestDownloadUrl));
            r.CheckVersionNow(out message);
            PumpAll(r);
            Check(!r.IsOutdated, "minimum equal to this version -> Outdated left");
            PumpAll(r);
            Equal(3, transport.Count, "one report after recovery");
            Check(transport.Requests[2].Json.StartsWith("{\"event\":\"round_start\",\"seed\":701,", StringComparison.Ordinal), "recovery announces the current map once");
            now += 59;
            PumpAll(r);
            Equal(3, transport.Count, "nothing from the paused time is sent");
            now += 1;
            PumpAll(r);
            Equal(4, transport.Count, "periodic reports resume one interval after recovery");
            Check(transport.Requests[3].Json.StartsWith("{\"event\":\"periodic\",\"seed\":701,", StringComparison.Ordinal), "periodic of the current seed");
            Check(r.QueueReportNow(out message) && message.Contains("seed 701"), "slmaps report works again after recovery");
            PumpAll(r);
            Equal(5, transport.Count, "manual report sent");

            // A minimum raised while a report is in flight drops the late failure instead of retrying it.
            TaskCompletionSource<ApiResult> hold = transport.HoldNext();
            r.OnRoundEnded();
            r.Tick();
            Check(r.IsInFlight, "round_end in flight");
            transport.VersionScript.Enqueue(() => VersionAnswer(VersionPlus(0, 0, 2), VersionPlus(0, 0, 2), TestDownloadUrl));
            r.CheckVersionNow(out message);
            PumpVersion(r);
            Check(r.IsOutdated && r.IsInFlight, "Outdated while the report is still in flight");
            hold.SetResult(Http(503, "{\"error\":\"db unavailable\"}"));
            Pump(r);
            Equal(0, r.PendingRoundEvents, "a failed in-flight report is dropped, not re-queued, while outdated");
            Check(host.HasLog("WARN", "Report round_end failed (HTTP 503 \"db unavailable\"); dropped because reports are paused until the plugin is updated."), "late failure explains the drop");
            now += 1000;
            PumpAll(r);
            Equal(6, transport.Count, "nothing sent after the late failure");
            Equal(2, CountLogs(host, "ERROR", "below the minimum"), "a new Outdated period logs one more ERROR");
            CheckNoLeak(host.Logs, new[] { "OUTDATED_REPORT" }, "console log");
        }

        private static void TestOutdatedFromVersionCheck()
        {
            string min1 = VersionPlus(0, 1, 0);
            string min2 = VersionPlus(1, 0, 0);
            FakeHost host = new FakeHost();
            host.Stored = new StoredCredential { ServerId = "srv", Credential = "slsrv_OUTDATED_VERSION", IssuedAt = "" };
            FakeTransport transport = new FakeTransport();
            double now = 0;
            Reporter r = new Reporter(host, transport, () => now, () => DateTime.UtcNow);
            r.Start();
            r.OnMapGenerated(900);
            Pump(r);
            Equal(1, transport.Count, "round_start sent before the version check");

            transport.VersionScript.Enqueue(() => VersionAnswer(min1, min1, TestDownloadUrl));
            now = Reporter.FirstVersionCheckDelaySeconds;
            PumpAll(r);
            Check(r.IsOutdated, "minimum above this version -> Outdated");
            Equal(1, CountLogs(host, "ERROR", "This plugin version (" + PluginInfo.Version + ") is below the minimum version slmaps accepts (" + min1 + ")"), "one ERROR");
            Check(!host.HasLog("ERROR", "rejected a request"), "a version check ERROR does not say a request was rejected");
            Equal(1, CountLogs(host, "WARN", "version is available: " + min1), "the newer latest still warns once");

            // A raised minimum updates the shown value without a second ERROR.
            transport.VersionScript.Enqueue(() => VersionAnswer(min2, min2, TestDownloadUrl));
            now += Hours12;
            PumpAll(r);
            Equal(1, CountLogs(host, "ERROR", "below the minimum"), "a raised minimum while outdated logs no second ERROR");
            Check(r.DescribeStatus().Contains("slmaps requires " + min2 + " or newer"), "status shows the raised minimum");

            // An unknown answer does not release the pause.
            transport.VersionScript.Enqueue(() => Http(503, "{\"error\":\"db unavailable\"}"));
            now += Hours12;
            PumpAll(r);
            Check(r.IsOutdated, "503 keeps Outdated");
            transport.VersionScript.Enqueue(() => ApiResult.FromFailure(ApiFailure.Timeout, "timeout"));
            now += Hours12;
            PumpAll(r);
            Check(r.IsOutdated, "timeout keeps Outdated");
            now += 60;
            PumpAll(r);
            Equal(1, transport.Count, "no reports while outdated");

            // No minimum releases the pause.
            transport.VersionScript.Enqueue(() => VersionAnswer(min2, null, TestDownloadUrl));
            now += Hours12;
            PumpAll(r);
            PumpAll(r);
            Check(!r.IsOutdated, "minimum removed -> Outdated left");
            Equal(1, CountLogs(host, "INFO", "again (no minimum version); registration, claims and reports resume."), "recovery logged once");
            Equal(2, transport.Count, "current map announced after recovery");
            Check(transport.Requests[1].Json.StartsWith("{\"event\":\"round_start\",\"seed\":900,", StringComparison.Ordinal), "announcement carries the current seed");
            string events = r.DescribeEvents(50);
            Check(events.Contains("is below the slmaps minimum version " + min1 + " (version check); registration, claims and reports paused.") && events.Contains("registration, claims and reports resume."),
                "event log records pause and resume");

            // Recovery after the round end announces nothing until the next map.
            r.OnRoundEnded();
            PumpAll(r);
            Equal(3, transport.Count, "round_end sent");
            transport.VersionScript.Enqueue(() => VersionAnswer(min2, min2, null));
            now += Hours12;
            PumpAll(r);
            Check(r.IsOutdated, "outdated again");
            Equal(2, CountLogs(host, "ERROR", "below the minimum"), "a second Outdated period logs a second ERROR");
            transport.VersionScript.Enqueue(() => VersionAnswer(min2, "0.0.1", null));
            now += Hours12;
            PumpAll(r);
            PumpAll(r);
            Check(!r.IsOutdated, "minimum below this version -> Outdated left");
            Equal(3, transport.Count, "no round_start re-announced after round_end");
            r.OnMapGenerated(901);
            PumpAll(r);
            Equal(4, transport.Count, "next map reported normally");
            CheckNoLeak(host.Logs, new[] { "OUTDATED_VERSION" }, "console log");
        }

        private static void TestVersionNeverBlocks()
        {
            FakeHost host = new FakeHost();
            host.Stored = new StoredCredential { ServerId = "srv", Credential = "slsrv_NOBLOCKS", IssuedAt = "" };
            FakeTransport transport = new FakeTransport();
            double now = 0;
            Reporter r = new Reporter(host, transport, () => now, () => DateTime.UtcNow);
            r.Start();
            string message;

            // A held version check does not delay a claim.
            TaskCompletionSource<ApiResult> versionHold = transport.HoldNextVersion();
            now = Reporter.FirstVersionCheckDelaySeconds;
            r.Tick();
            Check(r.IsVersionCheckInFlight && !r.IsInFlight, "version check in flight, nothing else");
            TaskCompletionSource<ApiResult> claimHold = transport.HoldNext();
            Check(r.StartClaim(Code("NOBLOCK"), out message) && message.Contains("Sending it now"), "a claim is not queued behind the version check");
            r.Tick();
            Check(r.IsInFlight, "claim sent on the next tick while the version check is out");
            WaitForRequests(transport, 1);
            Check(r.DescribeStatus().Contains("request sent, waiting for the answer"), "status shows the claim in flight");

            // A version answer during a claim leaves the claim alone.
            versionHold.SetResult(VersionAnswer(PluginInfo.Version, null, null));
            PumpVersion(r);
            Check(r.IsInFlight && r.ClaimStateName == "Claiming", "the version answer does not touch the claim in flight");
            claimHold.SetResult(Http(202, Review60));
            Pump(r);
            Equal("Review", r.ClaimStateName, "claim answer handled");

            // Reports go out while a version check is in flight.
            TaskCompletionSource<ApiResult> versionHold2 = transport.HoldNextVersion();
            Check(r.CheckVersionNow(out message) && r.IsVersionCheckInFlight, "manual version check in flight");
            r.OnMapGenerated(4711);
            r.Tick();
            Check(r.IsInFlight, "round_start sent while the version check is out");
            Pump(r);
            Equal(2, transport.Count, "claim + round_start");
            Check(transport.Requests[1].Json.StartsWith("{\"event\":\"round_start\",\"seed\":4711,", StringComparison.Ordinal), "round_start body");
            Check(r.QueueReportNow(out message) && message.Contains("Sending it now"), "slmaps report is not queued behind the version check");
            r.Tick();
            Check(r.IsInFlight, "manual report sent while the version check is out");
            Pump(r);
            Equal(3, transport.Count, "manual report delivered");
            Check(r.IsVersionCheckInFlight, "version check still out after both reports");
            versionHold2.SetResult(Http(503, ""));
            PumpAll(r);

            // A claim in flight does not delay the scheduled version check.
            TaskCompletionSource<ApiResult> claimHold2 = transport.HoldNext();
            now += 60;
            r.Tick();
            Check(r.IsInFlight, "review poll in flight");
            now += Hours12;
            r.Tick();
            Check(r.IsVersionCheckInFlight, "scheduled version check sent while the claim is in flight");
            PumpVersion(r);
            Check(r.IsInFlight, "claim still in flight after the version check finished");
            claimHold2.SetResult(Http(200, "{\"status\":\"verified\",\"serverId\":\"srv-nb\",\"credential\":\"slsrv_NOBLOCKS_NEW\",\"issuedAt\":\"\"}"));
            PumpAll(r);
            Equal("None", r.ClaimStateName, "claim verified");
            Equal(3, transport.VersionCount, "three version checks");
            CheckNoLeak(host.Logs, new[] { Code("NOBLOCK"), "NOBLOCKS_NEW" }, "console log");
        }

        // The interval is measured from the send, and nothing is sent while a check is in flight.
        private static void TestVersionScheduleFromSend()
        {
            FakeHost host = new FakeHost();
            host.Stored = new StoredCredential { ServerId = "srv", Credential = "slsrv_VERSION_SEND_CLOCK", IssuedAt = "" };
            host.Cfg.ReportIntervalSeconds = 0;
            FakeTransport transport = new FakeTransport();
            double now = 0;
            Reporter r = new Reporter(host, transport, () => now, () => DateTime.UtcNow);
            r.Start();

            // (a) A slow answer does not move the next check.
            TaskCompletionSource<ApiResult> slow = transport.HoldNextVersion();
            now = Reporter.FirstVersionCheckDelaySeconds;
            r.Tick();
            Check(r.IsVersionCheckInFlight, "(a) first version check sent");
            WaitForVersionRequests(transport, 1);
            double sentAt = now;
            now = sentAt + 3600;
            slow.SetResult(VersionAnswer(PluginInfo.Version, null, TestDownloadUrl));
            PumpVersion(r);
            Equal(1, transport.VersionCount, "(a) still one request after the slow answer");
            Check(r.DescribeStatus().Contains("\nversion check: up to date"), "(a) the slow answer is applied");
            now = sentAt + Hours12 - 1;
            PumpAll(r);
            Equal(1, transport.VersionCount, "(a) no check before 12 h after the send");
            now = sentAt + Hours12;
            PumpAll(r);
            Equal(2, transport.VersionCount, "(a) next check 12 h after the send, not 12 h after the answer");

            // (b) Nothing new is sent while a check is in flight, not even after a reload.
            TaskCompletionSource<ApiResult> stuck = transport.HoldNextVersion();
            now = sentAt + 2 * Hours12;
            r.Tick();
            Check(r.IsVersionCheckInFlight, "(b) third check sent");
            WaitForVersionRequests(transport, 3);
            now += Hours12 + 3600;
            r.NotifyConfigReloaded();
            r.Tick();
            r.Tick();
            r.Tick();
            Equal(3, transport.VersionCount, "(b) no second version request while one is in flight (past the due time, across a reload)");
            string busy;
            Check(r.CheckVersionNow(out busy) && busy.Contains("A version check is already running"), "(b) slmaps version does not start a second request either");
            Equal(3, transport.VersionCount, "(b) still one request");
            stuck.SetResult(Http(503, ""));
            PumpAll(r);
            Equal(4, transport.VersionCount, "(b) the overdue check goes out as soon as the stuck one is answered");
            Check(r.DescribeStatus().Contains("\nversion check: up to date"), "(b) the unknown answers keep the last known values");
            Equal(0, transport.Count, "(b) no register/claim/report request was ever sent");
            CheckNoLeak(host.Logs, new[] { "VERSION_SEND_CLOCK" }, "console log");
        }

        // Version checks keep running while registration is stopped.
        private static void TestVersionCheckAcrossStates()
        {
            string minimum = VersionPlus(0, 0, 1);

            // (a) After a 401 stopped reporting.
            FakeHost host = new FakeHost();
            host.Stored = new StoredCredential { ServerId = "srv", Credential = "slsrv_REVOKED_VERSION", IssuedAt = "" };
            FakeTransport transport = new FakeTransport();
            double now = 0;
            Reporter r = new Reporter(host, transport, () => now, () => DateTime.UtcNow);
            transport.Script.Enqueue(() => Http(401, "{\"error\":\"invalid credential\"}"));
            r.Start();
            r.OnMapGenerated(510);
            Pump(r);
            Equal("Revoked", r.StateName, "(a) report 401 -> Revoked");
            int posts = transport.Count;

            transport.VersionScript.Enqueue(() => VersionAnswer(minimum, minimum, TestDownloadUrl));
            now = Reporter.FirstVersionCheckDelaySeconds;
            PumpAll(r);
            Equal(1, transport.VersionCount, "(a) the version check runs while reporting is revoked");
            Check(r.IsOutdated && r.StateName == "Revoked", "(a) a minimum above this version pauses without changing the registration state");

            transport.VersionScript.Enqueue(() => VersionAnswer(minimum, null, TestDownloadUrl));
            now += Hours12;
            PumpAll(r);
            PumpAll(r);
            Check(!r.IsOutdated && r.StateName == "Revoked", "(a) leaving Outdated does not revive a revoked credential");
            Equal(posts, transport.Count, "(a) a revoked server still reports nothing after recovery");

            // (b) While waiting for a reload after a rejected token.
            FakeHost host2 = new FakeHost();
            host2.Cfg.RegistrationToken = "slreg_WAITING_VERSION";
            FakeTransport transport2 = new FakeTransport();
            double start = now;
            Reporter r2 = new Reporter(host2, transport2, () => now, () => DateTime.UtcNow);
            transport2.Script.Enqueue(() => Http(401, "{\"error\":\"invalid token\"}"));
            r2.Start();
            Pump(r2);
            Equal("WaitingForReload", r2.StateName, "(b) register 401 -> WaitingForReload");
            transport2.VersionScript.Enqueue(() => VersionAnswer(minimum, null, TestDownloadUrl));
            now = start + Reporter.FirstVersionCheckDelaySeconds;
            PumpAll(r2);
            Equal(1, transport2.VersionCount, "(b) the version check runs while waiting for a config reload");
            Equal(1, transport2.Count, "(b) no extra register attempt");
            Check(r2.DescribeStatus().Contains("newer version " + minimum + " available"), "(b) status shows the newer version while waiting for a reload");
            CheckNoLeak(host.Logs, new[] { "REVOKED_VERSION" }, "console log");
            CheckNoLeak(host2.Logs, new[] { "WAITING_VERSION" }, "console log");
        }

        // Queue, console commands and in-flight requests when the pause starts.
        private static void TestOutdatedConsoleAndInFlight()
        {
            string minimum = VersionPlus(0, 0, 1);
            string message;

            // (a) A console periodic is dropped with the rest of the queue.
            FakeHost host = new FakeHost();
            host.Stored = new StoredCredential { ServerId = "srv", Credential = "slsrv_DROP_MANUAL", IssuedAt = "" };
            host.Cfg.ReportIntervalSeconds = 0;
            FakeTransport transport = new FakeTransport();
            double now = 0;
            Reporter r = new Reporter(host, transport, () => now, () => DateTime.UtcNow);
            r.Start();
            TaskCompletionSource<ApiResult> heldReport = transport.HoldNext();
            r.OnMapGenerated(600);
            r.Tick();
            Check(r.IsInFlight, "(a) round_start in flight");
            WaitForRequests(transport, 1);
            Check(r.QueueReportNow(out message) && message.Contains("after the slmaps requests ahead of it"), "(a) slmaps report queued behind the round_start");
            Check(r.HasPendingPeriodic, "(a) the console report waits in the queue");
            TaskCompletionSource<ApiResult> heldVersion = transport.HoldNextVersion();
            Check(r.CheckVersionNow(out message) && r.IsVersionCheckInFlight, "(a) manual version check in flight");
            WaitForVersionRequests(transport, 1);
            heldVersion.SetResult(VersionAnswer(minimum, minimum, TestDownloadUrl));
            PumpVersion(r);
            Check(r.IsOutdated, "(a) the version answer pauses the plugin");
            Check(!r.HasPendingPeriodic && r.PendingRoundEvents == 0, "(a) entering Outdated drops the queued console report too");
            heldReport.SetResult(Http(200, "{\"ok\":true,\"blockedUntil\":null}"));
            Pump(r);
            now += 3600;
            PumpAll(r);
            Equal(1, transport.Count, "(a) the dropped console report is never sent");
            CheckNoLeak(host.Logs, new[] { "DROP_MANUAL" }, "console log");

            // (b) slmaps cancel still works and still sends nothing.
            FakeHost host2 = new FakeHost();
            host2.Cfg.ClaimCode = Code("OUTDATED_CANCEL");
            FakeTransport transport2 = new FakeTransport();
            Reporter r2 = new Reporter(host2, transport2, () => now, () => DateTime.UtcNow);
            transport2.Script.Enqueue(() => Outdated426(minimum, TestDownloadUrl));
            r2.Start();
            Pump(r2);
            Check(r2.IsOutdated && r2.ClaimStateName == "Claiming", "(b) the claim is paused by a 426");
            Check(r2.CancelClaim(out message) && message.Contains("Cancelled the claim"), "(b) slmaps cancel works while outdated");
            Equal("None", r2.ClaimStateName, "(b) claim cancelled");
            Equal("", host2.Cfg.ClaimCode, "(b) claim_code cleared by the cancel");
            now += 600;
            PumpAll(r2);
            Equal(1, transport2.Count, "(b) nothing is sent after cancelling while outdated");
            Check(r2.IsOutdated, "(b) cancelling a claim does not clear the pause");
            CheckNoLeak(host2.Logs, new[] { Code("OUTDATED_CANCEL") }, "console log");

            // (c) A registration that succeeds after the pause saves the credential but queues no report.
            FakeHost host3 = new FakeHost();
            host3.Cfg.RegistrationToken = "slreg_INFLIGHT_TOKEN";
            host3.Cfg.ReportIntervalSeconds = 0;
            FakeTransport transport3 = new FakeTransport();
            double start3 = now;
            Reporter r3 = new Reporter(host3, transport3, () => now, () => DateTime.UtcNow);
            r3.Start();
            r3.OnMapGenerated(777);
            TaskCompletionSource<ApiResult> heldRegister = transport3.HoldNext();
            r3.Tick();
            Check(r3.IsInFlight, "(c) register in flight");
            WaitForRequests(transport3, 1);
            TaskCompletionSource<ApiResult> heldVersion3 = transport3.HoldNextVersion();
            now = start3 + Reporter.FirstVersionCheckDelaySeconds;
            r3.Tick();
            Check(r3.IsVersionCheckInFlight && r3.IsInFlight, "(c) the version check goes out beside the register request");
            WaitForVersionRequests(transport3, 1);
            heldVersion3.SetResult(VersionAnswer(minimum, minimum, TestDownloadUrl));
            PumpVersion(r3);
            Check(r3.IsOutdated, "(c) paused while the register request is still out");
            heldRegister.SetResult(Http(200, "{\"serverId\":\"srv-if\",\"credential\":\"slsrv_INFLIGHT_CRED\",\"issuedAt\":\"\"}"));
            Pump(r3);
            Equal("Registered", r3.StateName, "(c) the in-flight registration is still saved");
            Check(host3.Stored != null && host3.Stored.Credential == "slsrv_INFLIGHT_CRED", "(c) credential written");
            Equal("", host3.Cfg.RegistrationToken, "(c) the used token is cleared");
            Equal(0, r3.PendingRoundEvents, "(c) no report is queued while paused");
            now += 600;
            PumpAll(r3);
            Equal(1, transport3.Count, "(c) nothing is reported while paused");
            transport3.VersionScript.Enqueue(() => VersionAnswer(minimum, null, TestDownloadUrl));
            now += Hours12;
            PumpAll(r3);
            PumpAll(r3);
            Check(!r3.IsOutdated, "(c) recovered");
            Equal(2, transport3.Count, "(c) the current map is announced once after recovery");
            Check(transport3.Requests[1].Json.StartsWith("{\"event\":\"round_start\",\"seed\":777,", StringComparison.Ordinal), "(c) recovery announces the seed that was current while paused");

            // (d) The version output leaks no credential or token.
            r3.CheckVersionNow(out message);
            CheckNoLeak(message.Split('\n'), new[] { "INFLIGHT_TOKEN", "INFLIGHT_CRED" }, "slmaps version output");
            PumpAll(r3);
            CheckNoLeak(host3.Logs, new[] { "INFLIGHT_TOKEN", "INFLIGHT_CRED" }, "console log");
            CheckNoLeak(r3.DescribeEvents(50).Split('\n'), new[] { "INFLIGHT_TOKEN", "INFLIGHT_CRED" }, "slmaps log");
        }

        // The real transport against a loopback listener: headers, status codes and timeout classification.
        // Runs on .NET Framework, not Mono.
        private static void TestHttpTransport()
        {
            System.Net.HttpListener listener = null;
            string prefix = null;
            Random rng = new Random();
            for (int attempt = 0; attempt < 10 && listener == null; attempt++)
            {
                int port = rng.Next(20000, 40000);
                System.Net.HttpListener candidate = new System.Net.HttpListener();
                prefix = "http://localhost:" + port + "/";
                candidate.Prefixes.Add(prefix);
                try
                {
                    candidate.Start();
                    listener = candidate;
                }
                catch (Exception)
                {
                    candidate.Close();
                }
            }
            if (listener == null)
            {
                Console.WriteLine("  SKIP could not open a loopback listener");
                return;
            }

            string seenContentType = null, seenAuth = null, seenUa = null, seenExpect = null, seenBody = null, seenMethod = null;
            string versionMethod = null, versionAuth = null, versionUa = null, versionAccept = null, versionBody = null;
            bool versionHasContentType = true;
            Thread server = new Thread(() =>
            {
                try
                {
                    for (int i = 0; i < 4; i++)
                    {
                        System.Net.HttpListenerContext ctx = listener.GetContext();
                        string path = ctx.Request.Url.AbsolutePath;
                        if (path.EndsWith("/slow", StringComparison.Ordinal))
                        {
                            Thread.Sleep(2500);
                        }
                        else if (path.EndsWith("/version", StringComparison.Ordinal))
                        {
                            versionMethod = ctx.Request.HttpMethod;
                            versionAuth = ctx.Request.Headers["Authorization"];
                            versionUa = ctx.Request.UserAgent;
                            versionAccept = ctx.Request.Headers["Accept"];
                            versionHasContentType = ctx.Request.ContentType != null;
                            using (System.IO.StreamReader reader = new System.IO.StreamReader(ctx.Request.InputStream, System.Text.Encoding.UTF8))
                            {
                                versionBody = reader.ReadToEnd();
                            }
                        }
                        else
                        {
                            seenMethod = ctx.Request.HttpMethod;
                            seenContentType = ctx.Request.ContentType;
                            seenAuth = ctx.Request.Headers["Authorization"];
                            seenUa = ctx.Request.UserAgent;
                            seenExpect = ctx.Request.Headers["Expect"];
                            using (System.IO.StreamReader reader = new System.IO.StreamReader(ctx.Request.InputStream, System.Text.Encoding.UTF8))
                            {
                                seenBody = reader.ReadToEnd();
                            }
                        }
                        int status = path.EndsWith("/report", StringComparison.Ordinal) ? 401 : path.EndsWith("/redirect", StringComparison.Ordinal) ? 301 : 200;
                        string responseText = status == 401 ? "{\"error\":\"invalid credential\"}"
                            : path.EndsWith("/version", StringComparison.Ordinal) ? "{\"latest\":\"9.8.7\",\"minimum\":null,\"downloadUrl\":\"https://github.com/as1tself/slmaps-server-plugin/releases/tag/v9.8.7\",\"publishedAt\":\"2026-09-15T00:00:00Z\"}"
                            : "{\"ok\":true}";
                        byte[] payload = System.Text.Encoding.UTF8.GetBytes(responseText);
                        ctx.Response.StatusCode = status;
                        if (status == 301)
                        {
                            ctx.Response.RedirectLocation = prefix + "elsewhere";
                        }
                        ctx.Response.ContentType = "application/json; charset=utf-8";
                        try
                        {
                            ctx.Response.OutputStream.Write(payload, 0, payload.Length);
                            ctx.Response.Close();
                        }
                        catch (Exception)
                        {
                            // The client disconnects first in the timeout case.
                        }
                    }
                }
                catch (Exception)
                {
                }
            });
            server.IsBackground = true;
            server.Start();

            try
            {
                HttpApiTransport transport = new HttpApiTransport(PluginInfo.BuildUserAgent("14.2.7", "1.1.7"));
                string body = Payloads.BuildReport(new ReportSnapshot { Event = ReportEvents.Periodic, Seed = 5, Port = 7777 }, false, false, false, PluginInfo.Version);
                ApiResult r401 = transport.PostJsonAsync(prefix + "api/plugin/v1/report", body, "slsrv_abc", 5, CancellationToken.None).GetAwaiter().GetResult();
                Equal(401, r401.StatusCode, "401 status surfaced");
                Equal("invalid credential", r401.GetString("error"), "error body parsed");
                Equal("POST", seenMethod, "method");
                Equal("application/json; charset=utf-8", seenContentType, "content type");
                Equal("Bearer slsrv_abc", seenAuth, "authorization header");
                Equal("slmaps-server-plugin/" + PluginInfo.Version + " (SCPSL 14.2.7; LabAPI 1.1.7)", seenUa, "user agent");
                Check(string.IsNullOrEmpty(seenExpect), "no Expect: 100-continue");
                Equal(body, seenBody, "body bytes");

                ApiResult r301 = transport.PostJsonAsync(prefix + "redirect", "{}", null, 5, CancellationToken.None).GetAwaiter().GetResult();
                Equal(301, r301.StatusCode, "redirect not followed");

                ApiResult version = transport.GetJsonAsync(prefix + "api/plugin/v1/version", 5, CancellationToken.None).GetAwaiter().GetResult();
                Equal(200, version.StatusCode, "version GET status");
                Equal("GET", versionMethod, "version check uses GET");
                Check(string.IsNullOrEmpty(versionAuth), "version GET sends no Authorization header");
                Check(!versionHasContentType && string.IsNullOrEmpty(versionBody), "version GET has no body");
                Equal("slmaps-server-plugin/" + PluginInfo.Version + " (SCPSL 14.2.7; LabAPI 1.1.7)", versionUa, "version GET user agent");
                Equal("application/json", versionAccept, "version GET accept header");
                VersionInfo parsedVersion = VersionInfo.TryParse(version.Body);
                Check(parsedVersion != null && parsedVersion.Latest == "9.8.7" && parsedVersion.Minimum == null
                    && parsedVersion.DownloadUrl == "https://github.com/as1tself/slmaps-server-plugin/releases/tag/v9.8.7", "version body parsed over HTTP (extra field ignored)");

                ApiResult slow = transport.PostJsonAsync(prefix + "slow", "{}", null, 1, CancellationToken.None).GetAwaiter().GetResult();
                Equal(ApiFailure.Timeout, slow.Failure, "timeout classified");

                CancellationTokenSource cts = new CancellationTokenSource();
                cts.Cancel();
                ApiResult canceled = transport.PostJsonAsync(prefix + "x", "{}", null, 5, cts.Token).GetAwaiter().GetResult();
                Equal(ApiFailure.Canceled, canceled.Failure, "cancel classified");

                ApiResult refused = transport.PostJsonAsync("http://127.0.0.1:1/api", "{}", null, 3, CancellationToken.None).GetAwaiter().GetResult();
                Equal(ApiFailure.Network, refused.Failure, "connection refused classified as network error");
            }
            finally
            {
                listener.Close();
            }
        }
    }
}
