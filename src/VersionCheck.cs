using System;
using System.Collections.Generic;
using System.Globalization;

namespace SlmapsServerPlugin
{
    // Versions are compared as numbers, never as text; one leading v and any -suffix or +suffix are ignored.
    internal static class VersionNumber
    {
        public const int MaxLength = 64;
        private const int MaxComponentDigits = 9;

        public static bool IsValid(string text)
        {
            int[] parts;
            return TryParse(text, out parts);
        }

        public static bool TryParse(string text, out int[] parts)
        {
            parts = null;
            if (text == null)
            {
                return false;
            }
            string s = text.Trim();
            if (s.Length == 0 || s.Length > MaxLength)
            {
                return false;
            }
            if (s[0] == 'v' || s[0] == 'V')
            {
                s = s.Substring(1);
            }
            int suffixAt = s.IndexOfAny(new[] { '-', '+' });
            string core = suffixAt >= 0 ? s.Substring(0, suffixAt) : s;
            if (suffixAt >= 0)
            {
                string suffix = s.Substring(suffixAt + 1);
                if (suffix.Length == 0)
                {
                    return false;
                }
                foreach (char c in suffix)
                {
                    if (!IsSuffixChar(c))
                    {
                        return false;
                    }
                }
            }
            string[] pieces = core.Split('.');
            if (pieces.Length != 3)
            {
                return false;
            }
            int[] result = new int[3];
            for (int i = 0; i < 3; i++)
            {
                string p = pieces[i];
                if (p.Length == 0 || p.Length > MaxComponentDigits)
                {
                    return false;
                }
                foreach (char c in p)
                {
                    if (c < '0' || c > '9')
                    {
                        return false;
                    }
                }
                result[i] = int.Parse(p, NumberStyles.None, CultureInfo.InvariantCulture);
            }
            parts = result;
            return true;
        }

        public static bool TryCompare(string a, string b, out int result)
        {
            result = 0;
            int[] pa;
            int[] pb;
            if (!TryParse(a, out pa) || !TryParse(b, out pb))
            {
                return false;
            }
            for (int i = 0; i < 3; i++)
            {
                if (pa[i] != pb[i])
                {
                    result = pa[i] < pb[i] ? -1 : 1;
                    return true;
                }
            }
            return true;
        }

        public static string Normalize(string text)
        {
            int[] parts;
            if (!TryParse(text, out parts))
            {
                return null;
            }
            return parts[0].ToString(CultureInfo.InvariantCulture) + "." + parts[1].ToString(CultureInfo.InvariantCulture) + "." + parts[2].ToString(CultureInfo.InvariantCulture);
        }

        private static bool IsSuffixChar(char c)
        {
            return (c >= '0' && c <= '9') || (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || c == '.' || c == '-' || c == '+';
        }
    }

    internal sealed class VersionInfo
    {
        public const int MaxUrlLength = 512;

        public string Latest;
        public string Minimum;
        public string DownloadUrl;

        public static VersionInfo TryParse(Dictionary<string, object> body)
        {
            if (body == null)
            {
                return null;
            }
            string latest;
            string minimum;
            if (!TryReadVersion(body, "latest", out latest) || !TryReadVersion(body, "minimum", out minimum))
            {
                return null;
            }
            object url;
            body.TryGetValue("downloadUrl", out url);
            return new VersionInfo { Latest = latest, Minimum = minimum, DownloadUrl = SafeUrl(url as string) };
        }

        public static string SafeUrl(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return null;
            }
            string s = text.Trim();
            if (s.Length == 0 || s.Length > MaxUrlLength)
            {
                return null;
            }
            foreach (char c in s)
            {
                if (c <= ' ' || c == (char)0x7F)
                {
                    return null;
                }
            }
            Uri uri;
            if (!Uri.TryCreate(s, UriKind.Absolute, out uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            {
                return null;
            }
            return s;
        }

        private static bool TryReadVersion(Dictionary<string, object> body, string name, out string version)
        {
            version = null;
            object value;
            if (!body.TryGetValue(name, out value) || value == null)
            {
                return true;
            }
            string s = value as string;
            if (s == null || !VersionNumber.IsValid(s))
            {
                return false;
            }
            version = s.Trim();
            return true;
        }
    }

    internal static class OutdatedAnswer
    {
        public const int StatusCode = 426;
        public const string ErrorCode = "plugin_outdated";

        // Only a 426 that says plugin_outdated counts; any other 426 stays an ordinary failure.
        public static bool Matches(ApiResult r)
        {
            return r != null && r.HasResponse && r.StatusCode == StatusCode && string.Equals(r.GetString("error"), ErrorCode, StringComparison.Ordinal);
        }

        public static string Minimum(ApiResult r)
        {
            string minimum = r.GetString("minimum");
            return VersionNumber.IsValid(minimum) ? minimum.Trim() : null;
        }

        public static string DownloadUrl(ApiResult r)
        {
            return VersionInfo.SafeUrl(r.GetString("downloadUrl"));
        }
    }
}
