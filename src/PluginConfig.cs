using System.ComponentModel;

namespace SlmapsServerPlugin
{
    // A config reload replaces this instance, so never hold on to a reference; read Plugin.Config again every time.
    public sealed class PluginConfig
    {
        public const int MinReportIntervalSeconds = 10;
        public const int MinRequestTimeoutSeconds = 3;
        public const int MaxRequestTimeoutSeconds = 60;

        [Description("slmaps API base URL. Keep the default unless the slmaps admin tells you otherwise.")]
        public string ApiBaseUrl { get; set; } = "https://slmaps.com";

        [Description("One-time registration token issued by the slmaps admin. It is cleared automatically after a successful registration. Not used while a working credential.yml exists.")]
        public string RegistrationToken { get; set; } = "";

        [Description("Seconds between periodic reports of the current seed. 0 disables periodic reports; values from 1 to 9 are treated as 10.")]
        public int ReportIntervalSeconds { get; set; } = 60;

        [Description("Include a random per-map round id (a new GUID every map generation) in reports.")]
        public bool SendRoundId { get; set; } = false;

        [Description("Include the UTC time the current round started (null before the round starts) in reports.")]
        public bool SendRoundStartTime { get; set; } = false;

        [Description("Include the seconds elapsed since the round started (null before the round starts) in reports.")]
        public bool SendElapsedTime { get; set; } = false;

        [Description("HTTP request timeout in seconds. Clamped to 3..60.")]
        public int RequestTimeoutSeconds { get; set; } = 10;

        [Description("Print verbose debug logs. Tokens and credentials are never printed.")]
        public bool Debug { get; set; } = false;

        public static int EffectiveReportInterval(int configured)
        {
            if (configured <= 0)
            {
                return 0;
            }
            return configured < MinReportIntervalSeconds ? MinReportIntervalSeconds : configured;
        }

        public static int EffectiveRequestTimeout(int configured)
        {
            if (configured < MinRequestTimeoutSeconds)
            {
                return MinRequestTimeoutSeconds;
            }
            return configured > MaxRequestTimeoutSeconds ? MaxRequestTimeoutSeconds : configured;
        }
    }
}
