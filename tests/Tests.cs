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

            public Task<ApiResult> PostJsonAsync(string url, string json, string bearerToken, int timeoutSeconds, CancellationToken cancellationToken)
            {
                lock (Requests)
                {
                    Requests.Add(new Request { Url = url, Json = json, Bearer = bearerToken, Timeout = timeoutSeconds });
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
            Thread server = new Thread(() =>
            {
                try
                {
                    for (int i = 0; i < 3; i++)
                    {
                        System.Net.HttpListenerContext ctx = listener.GetContext();
                        string path = ctx.Request.Url.AbsolutePath;
                        if (path.EndsWith("/slow", StringComparison.Ordinal))
                        {
                            Thread.Sleep(2500);
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
                        byte[] payload = System.Text.Encoding.UTF8.GetBytes(status == 401 ? "{\"error\":\"invalid credential\"}" : "{\"ok\":true}");
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
