using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SlmapsServerPlugin
{
    // In-memory only, main thread only. Repeated lines are merged so that rare events stay visible.
    internal sealed class EventLog
    {
        public const int Capacity = 50;

        private const int MergeWindow = 8;
        private const int ShownSecretLength = 12;

        // Second line of defense: callers already keep tokens, credentials and codes out of the text.
        private static readonly Regex SecretPattern = new Regex("sl(?:clm|srv|reg)_[A-Za-z0-9_-]*", RegexOptions.CultureInvariant);

        private readonly List<Entry> _entries = new List<Entry>();

        private sealed class Entry
        {
            public DateTime FirstLocal;
            public DateTime LastLocal;
            public string Level;
            public string Text;
            public int Count;
        }

        public int Count
        {
            get { return _entries.Count; }
        }

        public void Add(DateTime utcNow, string level, string text)
        {
            string clean = Redact(text);
            DateTime local = ToLocal(utcNow);
            // Merging within a window also catches two kinds of line that keep alternating.
            for (int i = _entries.Count - 1; i >= 0 && i >= _entries.Count - MergeWindow; i--)
            {
                Entry e = _entries[i];
                if (string.Equals(e.Level, level, StringComparison.Ordinal) && string.Equals(e.Text, clean, StringComparison.Ordinal))
                {
                    _entries.RemoveAt(i);
                    e.LastLocal = local;
                    if (e.Count < int.MaxValue)
                    {
                        e.Count++;
                    }
                    _entries.Add(e);
                    return;
                }
            }
            _entries.Add(new Entry { FirstLocal = local, LastLocal = local, Level = level, Text = clean, Count = 1 });
            while (_entries.Count > Capacity)
            {
                _entries.RemoveAt(0);
            }
        }

        public List<string> Last(int n)
        {
            List<string> lines = new List<string>();
            int take = Math.Max(0, Math.Min(n, _entries.Count));
            for (int i = _entries.Count - take; i < _entries.Count; i++)
            {
                Entry e = _entries[i];
                StringBuilder sb = new StringBuilder();
                sb.Append(e.LastLocal.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))
                    .Append(' ').Append(e.Level.ToUpperInvariant().PadRight(5))
                    .Append(' ').Append(e.Text);
                if (e.Count > 1)
                {
                    sb.Append(" (x").Append(e.Count).Append(" since ")
                        .Append(e.FirstLocal.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append(')');
                }
                lines.Add(sb.ToString());
            }
            return lines;
        }

        public static string Redact(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return "";
            }
            return SecretPattern.Replace(text, m => m.Value.Length <= ShownSecretLength ? m.Value : m.Value.Substring(0, ShownSecretLength) + "...");
        }

        public static DateTime ToLocal(DateTime utc)
        {
            try
            {
                return utc.Kind == DateTimeKind.Local ? utc : DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime();
            }
            catch (Exception)
            {
                return utc;
            }
        }
    }
}
