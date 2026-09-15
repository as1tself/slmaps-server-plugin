namespace SlmapsServerPlugin
{
    // The game side of the reporter, called on the main thread only. Config is read again on every access because a config reload replaces the instance.
    internal interface IReporterHost
    {
        PluginConfig Config { get; }

        int Port { get; }

        string GameVersion { get; }

        string LabApiVersion { get; }

        string ConfigFilePath { get; }

        string CredentialFilePath { get; }

        bool TryGetRoundElapsedSeconds(out double seconds);

        StoredCredential LoadCredential();

        bool SaveCredential(StoredCredential credential);

        void ClearRegistrationToken(string usedToken);

        void Debug(string message);

        void Info(string message);

        void Warn(string message);

        void Error(string message);
    }
}
