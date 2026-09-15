using System;
using System.IO;
using System.Text;
using LabApi.Loader;
using LabApi.Loader.Features.Plugins;
using LabApi.Loader.Features.Yaml;

namespace SlmapsServerPlugin
{
    // The credential is written atomically and never appears in a log line.
    internal sealed class CredentialStore
    {
        public const string FileName = "credential.yml";

        private readonly Plugin _plugin;

        public CredentialStore(Plugin plugin)
        {
            _plugin = plugin;
        }

        public string FilePath
        {
            get
            {
                try
                {
                    return ConfigurationLoader.GetConfigPath(_plugin, FileName);
                }
                catch (Exception)
                {
                    return FileName;
                }
            }
        }

        public StoredCredential Load(Action<string> warn)
        {
            StoredCredential credential;
            if (!ConfigurationLoader.TryReadConfig<StoredCredential>(_plugin, FileName, out credential))
            {
                return null;
            }
            if (credential == null || string.IsNullOrWhiteSpace(credential.Credential))
            {
                warn(FilePath + " exists but holds no credential; ignoring it.");
                return null;
            }
            credential.Credential = credential.Credential.Trim();
            credential.ServerId = (credential.ServerId ?? "").Trim();
            credential.IssuedAt = (credential.IssuedAt ?? "").Trim();
            return credential;
        }

        public bool Save(StoredCredential credential, out string error)
        {
            error = null;
            string path = null;
            string temp = null;
            try
            {
                path = ConfigurationLoader.GetConfigPath(_plugin, FileName);
                temp = path + ".tmp";
                string yaml = YamlConfigParser.Serializer.Serialize(credential);
                File.WriteAllText(temp, yaml, new UTF8Encoding(false));
                if (File.Exists(path))
                {
                    try
                    {
                        File.Replace(temp, path, null);
                    }
                    catch (Exception)
                    {
                        // Some file systems have no File.Replace, so fall back to a copy.
                        File.Copy(temp, path, true);
                        File.Delete(temp);
                    }
                }
                else
                {
                    File.Move(temp, path);
                }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
                try
                {
                    if (temp != null && File.Exists(temp))
                    {
                        File.Delete(temp);
                    }
                }
                catch (Exception)
                {
                }
                return false;
            }
        }
    }
}
