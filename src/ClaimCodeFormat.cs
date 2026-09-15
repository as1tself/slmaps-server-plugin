using System;

namespace SlmapsServerPlugin
{
    // Claim codes are "slclm_" plus base64url characters. Only the shape is checked here; whether a code still works is up to the server.
    internal static class ClaimCodeFormat
    {
        public const string Prefix = "slclm_";
        public const int MaxLength = 128;

        private const string RegistrationTokenPrefix = "slreg_";
        private const int MaxEchoedNumberLength = 12;
        private const string CopyHint = "Copy the whole code that /server claim showed you in Discord.";
        private static readonly string PrefixHint = "The claim code starts with \"" + Prefix + "\" and comes from /server claim in the slmaps Discord.";

        // Quotes and brackets that get pasted around a code; none of them can be part of one.
        private static readonly char[] WrapChars = { '"', '\'', '`', '<', '>', (char)0x201C, (char)0x201D, (char)0x2018, (char)0x2019 };

        public static string Problem(string code)
        {
            string c = (code ?? "").Trim();
            if (c.Length == 0)
            {
                return "The claim code is empty.";
            }
            string inner = c.Trim(WrapChars).Trim();
            bool wrapped = inner.Length != c.Length;
            string digits;
            if (IsRequestNumber(inner, out digits))
            {
                return (digits.Length <= MaxEchoedNumberLength ? "#" + digits + " is a claim request number, not a claim code." : "That is a request number, not a claim code.")
                    + "\n" + PrefixHint;
            }
            if (inner.StartsWith(RegistrationTokenPrefix, StringComparison.Ordinal))
            {
                return "That is a registration token, not a claim code.\nPut it into registration_token in config.yml, or use the code from /server claim in the slmaps Discord.";
            }
            if (wrapped && inner.StartsWith(Prefix, StringComparison.Ordinal))
            {
                return "The claim code is wrapped in quotes or angle brackets.\nRemove them and enter only the code itself.";
            }
            if (!c.StartsWith(Prefix, StringComparison.Ordinal))
            {
                return "A claim code starts with \"" + Prefix + "\".\n" + CopyHint;
            }
            if (c.Length == Prefix.Length || c.Length > MaxLength)
            {
                return "The claim code is " + (c.Length == Prefix.Length ? "too short" : "too long") + ".\n" + CopyHint;
            }
            for (int i = Prefix.Length; i < c.Length; i++)
            {
                if (!IsCodeChar(c[i]))
                {
                    return "The claim code contains a character that codes never have.\n" + CopyHint;
                }
            }
            return null;
        }

        public static bool IsValid(string code)
        {
            return Problem(code) == null;
        }

        private static bool IsRequestNumber(string c, out string digits)
        {
            digits = c.StartsWith("#", StringComparison.Ordinal) ? c.Substring(1).Trim() : c;
            if (digits.Length == 0)
            {
                return false;
            }
            for (int i = 0; i < digits.Length; i++)
            {
                if (digits[i] < '0' || digits[i] > '9')
                {
                    return false;
                }
            }
            return true;
        }

        private static bool IsCodeChar(char ch)
        {
            return (ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9') || ch == '-' || ch == '_';
        }
    }
}
