using System;
using CommandSystem;

namespace SlmapsServerPlugin
{
    // Console input arrives on the main thread, so the reporter can be called directly.
    [CommandHandler(typeof(GameConsoleCommandHandler))]
    public sealed class SlmapsCommand : ICommand
    {
        public string Command { get { return "slmaps"; } }

        public string[] Aliases { get { return new string[0]; } }

        public string Description { get { return "slmaps server plugin. " + ConsoleCommandParser.Usage; } }

        public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
        {
            Reporter reporter = SlmapsPlugin.ActiveReporter;
            if (reporter == null)
            {
                response = "The slmaps plugin is not enabled.";
                return false;
            }
            string[] args = new string[arguments.Count];
            if (arguments.Array != null && arguments.Count > 0)
            {
                Array.Copy(arguments.Array, arguments.Offset, args, 0, arguments.Count);
            }
            string code;
            switch (ConsoleCommandParser.Parse(args, out code))
            {
                case ConsoleCommandParser.Action.Claim:
                    return reporter.StartClaim(code, out response);
                case ConsoleCommandParser.Action.Status:
                    response = reporter.DescribeStatus();
                    return true;
                default:
                    response = ConsoleCommandParser.Usage;
                    return false;
            }
        }
    }
}
