using System.ComponentModel;

namespace SlmapsServerPlugin
{
    // A config reload replaces this instance, so never hold on to a reference; read Plugin.Config again every time.
    public sealed class PluginConfig
    {
        public const int MinReportIntervalSeconds = 10;
        public const int MinRequestTimeoutSeconds = 3;
        public const int MaxRequestTimeoutSeconds = 60;

        [Description("slmaps API base URL. Keep the default unless slmaps staff tell you otherwise.")]
        public string ApiBaseUrl { get; set; } = "https://slmaps.com";

        [Description("One-time registration token issued by slmaps staff. It is cleared after a successful registration and is not used while credential.yml exists.")]
        public string RegistrationToken { get; set; } = "";

        [Description("One-time claim code from /server claim in the slmaps Discord: the value starting with slclm_, not the request number. It takes priority over registration_token, replaces an existing credential once verified, and is cleared when the claim finishes. You can run slmaps claim <code> in the server console instead.")]
        public string ClaimCode { get; set; } = "";

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

        [Description("Warn once in the console when slmaps reports a newer plugin release. Turning this off hides only that warning: the version check still runs every 12 hours, and a version below the minimum slmaps accepts still pauses registration, claims and reports.")]
        public bool CheckForUpdates { get; set; } = true;

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
