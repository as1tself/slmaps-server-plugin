using System.ComponentModel;

namespace SlmapsServerPlugin
{
    // slmaps keeps only a hash of the credential, so losing this file means registering the server again.
    public sealed class StoredCredential
    {
        [Description("Server id assigned by slmaps.")]
        public string ServerId { get; set; } = "";

        [Description("Server credential issued by slmaps. Keep this file private. Delete it only after the plugin logs that the credential was revoked.")]
        public string Credential { get; set; } = "";

        [Description("Time the credential was issued (UTC).")]
        public string IssuedAt { get; set; } = "";
    }
}
