using System;
using System.Globalization;

namespace SlmapsServerPlugin
{
    internal static class ConsoleCommandParser
    {
        public const int DefaultLogCount = 10;
        public const int MaxLogCount = 50;

        private const int MaxEchoedArgumentLength = 12;

        public const string Usage = "Usage: slmaps help | status | claim <code> | cancel | report | log [n] | version";
        public const string ClaimUsage = "Usage: slmaps claim <code>\nThe code starts with slclm_ and comes from /server claim in the slmaps Discord.";
        public static readonly string LogUsage = "Usage: slmaps log [n]\nn is 1 to " + MaxLogCount + ", default " + DefaultLogCount + ".";

        public static readonly string Help = "slmaps commands:"
            + "\n  slmaps help           List these commands."
            + "\n  slmaps status         Show registration, claim and last report state."
            + "\n  slmaps claim <code>   Claim this server with the slclm_... code from /server claim in the slmaps Discord."
            + "\n  slmaps cancel         Stop the claim that is in progress."
            + "\n  slmaps report         Report the current seed to slmaps now."
            + "\n  slmaps log [n]        Show the last n plugin events (default " + DefaultLogCount + ", max " + MaxLogCount + ")."
            + "\n  slmaps version        Show the installed, latest and minimum plugin versions and check slmaps for updates now.";

        internal enum Action
        {
            Error,
            Help,
            Status,
            Claim,
            Cancel,
            Report,
            Log,
            Version,
        }

        internal sealed class Parsed
        {
            public Action Action;
            public string Code;
            public int Count;
            public string Message;
        }

        public static Parsed Parse(string[] args)
        {
            if (args == null || args.Length == 0 || string.IsNullOrWhiteSpace(args[0]))
            {
                return new Parsed { Action = Action.Help };
            }
            string sub = args[0].Trim();
            int extra = args.Length - 1;
            if (Is(sub, "help"))
            {
                return new Parsed { Action = Action.Help };
            }
            if (Is(sub, "status"))
            {
                return extra == 0 ? new Parsed { Action = Action.Status } : NoArguments("status");
            }
            if (Is(sub, "cancel"))
            {
                return extra == 0 ? new Parsed { Action = Action.Cancel } : NoArguments("cancel");
            }
            if (Is(sub, "report"))
            {
                return extra == 0 ? new Parsed { Action = Action.Report } : NoArguments("report");
            }
            if (Is(sub, "version"))
            {
                return extra == 0 ? new Parsed { Action = Action.Version } : NoArguments("version");
            }
            if (Is(sub, "claim"))
            {
                if (extra != 1 || string.IsNullOrWhiteSpace(args[1]))
                {
                    return Error(ClaimUsage);
                }
                return new Parsed { Action = Action.Claim, Code = args[1].Trim() };
            }
            if (Is(sub, "log"))
            {
                if (extra == 0)
                {
                    return new Parsed { Action = Action.Log, Count = DefaultLogCount };
                }
                int n;
                if (extra != 1 || !int.TryParse((args[1] ?? "").Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out n) || n < 1)
                {
                    return Error(LogUsage);
                }
                return new Parsed { Action = Action.Log, Count = Math.Min(n, MaxLogCount) };
            }
            // A code may have been pasted in place of the subcommand, so only its beginning is echoed back.
            string shown = sub.Length <= MaxEchoedArgumentLength ? sub : sub.Substring(0, MaxEchoedArgumentLength) + "...";
            return Error("Unknown subcommand \"" + shown + "\".\nRun slmaps help for the list of commands.");
        }

        private static bool Is(string sub, string name)
        {
            return string.Equals(sub, name, StringComparison.OrdinalIgnoreCase);
        }

        private static Parsed NoArguments(string name)
        {
            return Error("slmaps " + name + " takes no arguments.\nRun slmaps help for the list of commands.");
        }

        private static Parsed Error(string message)
        {
            return new Parsed { Action = Action.Error, Message = message };
        }
    }
}
