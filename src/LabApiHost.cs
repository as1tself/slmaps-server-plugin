using System;
using LabApi.Features.Console;
using LabApi.Features.Wrappers;
using LabApi.Loader;

namespace SlmapsServerPlugin
{
    // All LabAPI and game access goes through here, inside try/catch.
    internal sealed class LabApiHost : IReporterHost
    {
        private readonly SlmapsPlugin _plugin;
        private readonly CredentialStore _store;
        private readonly string _gameVersion;
        private readonly string _labApiVersion;

        public LabApiHost(SlmapsPlugin plugin, CredentialStore store, string gameVersion, string labApiVersion)
        {
            _plugin = plugin;
            _store = store;
            _gameVersion = gameVersion;
            _labApiVersion = labApiVersion;
        }

        public PluginConfig Config
        {
            get { return _plugin.Config; }
        }

        public int Port
        {
            get
            {
                try
                {
                    int port = Server.Port;
                    return port >= 1 && port <= 65535 ? port : 0;
                }
                catch (Exception)
                {
                    return 0;
                }
            }
        }

        public string GameVersion
        {
            get { return _gameVersion; }
        }

        public string LabApiVersion
        {
            get { return _labApiVersion; }
        }

        public string ConfigFilePath
        {
            get
            {
                try
                {
                    return ConfigurationLoader.GetConfigPath(_plugin, _plugin.ConfigFileName);
                }
                catch (Exception)
                {
                    return "config.yml";
                }
            }
        }

        public string CredentialFilePath
        {
            get { return _store.FilePath; }
        }

        public bool TryGetRoundElapsedSeconds(out double seconds)
        {
            seconds = 0;
            try
            {
                if (!Round.IsRoundStarted)
                {
                    return false;
                }
                seconds = Round.Duration.TotalSeconds;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public StoredCredential LoadCredential()
        {
            return _store.Load(Warn);
        }

        public bool SaveCredential(StoredCredential credential)
        {
            string error;
            if (_store.Save(credential, out error))
            {
                return true;
            }
            Error("Could not write " + _store.FilePath + ": " + error);
            return false;
        }

        public void ClearRegistrationToken(string usedToken)
        {
            try
            {
                PluginConfig config = _plugin.Config;
                if (config == null)
                {
                    return;
                }
                if (!string.Equals((config.RegistrationToken ?? "").Trim(), usedToken, StringComparison.Ordinal))
                {
                    return; // The owner put a different token in while the request was running.
                }
                config.RegistrationToken = "";
                _plugin.SaveConfig();
            }
            catch (Exception ex)
            {
                Warn("Could not clear registration_token in config.yml (" + ex.GetType().Name + "). Remove it by hand; it is already used up.");
            }
        }

        public void ClearClaimCode(string usedCode)
        {
            try
            {
                PluginConfig config = _plugin.Config;
                if (config == null)
                {
                    return;
                }
                if (!string.Equals((config.ClaimCode ?? "").Trim(), usedCode, StringComparison.Ordinal))
                {
                    return; // The owner put a different code in while the request was running.
                }
                config.ClaimCode = "";
                _plugin.SaveConfig();
            }
            catch (Exception ex)
            {
                Warn("Could not clear claim_code in config.yml (" + ex.GetType().Name + "). Remove it by hand; it is already used up.");
            }
        }

        public void Debug(string message)
        {
            PluginConfig config = _plugin.Config;
            if (config != null && config.Debug)
            {
                Logger.Debug(PluginInfo.LogPrefix + message, true);
            }
        }

        public void Info(string message)
        {
            Logger.Info(PluginInfo.LogPrefix + message);
        }

        public void Warn(string message)
        {
            Logger.Warn(PluginInfo.LogPrefix + message);
        }

        public void Error(string message)
        {
            Logger.Error(PluginInfo.LogPrefix + message);
        }
    }
}
