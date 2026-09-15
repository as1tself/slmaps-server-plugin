namespace SlmapsServerPlugin
{
    internal static class PluginInfo
    {
        public const string Name = "SlmapsServerPlugin";
        public const string Author = "slmaps";
        public const string Version = "1.1.0";
        public const string AssemblyVersion = "1.1.0.0";
        public const string ProductToken = "slmaps-server-plugin";
        public const string LogPrefix = "[slmaps] ";

        public const string RegisterPath = "/api/plugin/v1/register";
        public const string ReportPath = "/api/plugin/v1/report";
        public const string ClaimPath = "/api/plugin/v1/claim";

        public static string BuildUserAgent(string gameVersion, string labApiVersion)
        {
            return ProductToken + "/" + Version
                + " (SCPSL " + (string.IsNullOrEmpty(gameVersion) ? "unknown" : gameVersion)
                + "; LabAPI " + (string.IsNullOrEmpty(labApiVersion) ? "unknown" : labApiVersion) + ")";
        }
    }
}
