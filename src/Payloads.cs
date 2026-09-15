using System;
using System.Globalization;

namespace SlmapsServerPlugin
{
    // Request bodies for the v1 API. Do not rename or reorder the fields.
    internal static class Payloads
    {
        private const int MaxVersionLength = 32;
        private const int MaxRoundIdLength = 64;

        public static string BuildRegister(string token, int port, string pluginVersion, string gameVersion, string labApiVersion)
        {
            JsonObjectBuilder w = new JsonObjectBuilder();
            w.Add("token", token);
            w.Add("port", port);
            AddOptionalShort(w, "pluginVersion", pluginVersion);
            AddOptionalShort(w, "gameVersion", gameVersion);
            AddOptionalShort(w, "labApiVersion", labApiVersion);
            return w.ToString();
        }

        public static string BuildClaim(string code, int port, string pluginVersion, string gameVersion, string labApiVersion)
        {
            JsonObjectBuilder w = new JsonObjectBuilder();
            w.Add("code", code);
            w.Add("port", port);
            AddOptionalShort(w, "pluginVersion", pluginVersion);
            AddOptionalShort(w, "gameVersion", gameVersion);
            AddOptionalShort(w, "labApiVersion", labApiVersion);
            return w.ToString();
        }

        public static string BuildReport(ReportSnapshot s, bool sendRoundId, bool sendRoundStartTime, bool sendElapsedTime, string pluginVersion)
        {
            JsonObjectBuilder w = new JsonObjectBuilder();
            w.Add("event", s.Event);
            w.Add("seed", s.Seed);
            w.Add("port", s.Port);
            if (sendRoundId && !string.IsNullOrEmpty(s.RoundId) && s.RoundId.Length <= MaxRoundIdLength)
            {
                w.Add("roundId", s.RoundId);
            }
            if (sendRoundStartTime)
            {
                if (s.RoundStartedAtUtc.HasValue)
                {
                    w.Add("roundStartedAt", FormatUtc(s.RoundStartedAtUtc.Value));
                }
                else
                {
                    w.AddNull("roundStartedAt");
                }
            }
            if (sendElapsedTime)
            {
                if (s.ElapsedSeconds.HasValue && !double.IsNaN(s.ElapsedSeconds.Value) && !double.IsInfinity(s.ElapsedSeconds.Value))
                {
                    w.Add("elapsedSeconds", Math.Max(0d, s.ElapsedSeconds.Value));
                }
                else
                {
                    w.AddNull("elapsedSeconds");
                }
            }
            AddOptionalShort(w, "pluginVersion", pluginVersion);
            return w.ToString();
        }

        public static string FormatUtc(DateTime value)
        {
            DateTime utc = value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : DateTime.SpecifyKind(value, DateTimeKind.Utc);
            return utc.ToString("yyyy'-'MM'-'dd'T'HH':'mm':'ss'Z'", CultureInfo.InvariantCulture);
        }

        private static void AddOptionalShort(JsonObjectBuilder w, string name, string value)
        {
            if (!string.IsNullOrEmpty(value) && value.Length <= MaxVersionLength)
            {
                w.Add(name, value);
            }
        }
    }
}
