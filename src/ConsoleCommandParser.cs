using System;

namespace SlmapsServerPlugin
{
    internal static class ConsoleCommandParser
    {
        public const string Usage = "Usage: slmaps claim <code> | slmaps status";

        internal enum Action
        {
            Usage,
            Claim,
            Status,
        }

        public static Action Parse(string[] args, out string code)
        {
            code = null;
            if (args == null || args.Length == 0 || string.IsNullOrWhiteSpace(args[0]))
            {
                return Action.Usage;
            }
            string sub = args[0].Trim();
            if (string.Equals(sub, "status", StringComparison.OrdinalIgnoreCase))
            {
                return Action.Status;
            }
            if (string.Equals(sub, "claim", StringComparison.OrdinalIgnoreCase))
            {
                if (args.Length < 2 || string.IsNullOrWhiteSpace(args[1]))
                {
                    return Action.Usage;
                }
                code = args[1].Trim();
                return Action.Claim;
            }
            return Action.Usage;
        }
    }
}
