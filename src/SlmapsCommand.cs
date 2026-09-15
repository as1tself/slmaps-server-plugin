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
            try
            {
                string[] args = new string[arguments.Count];
                if (arguments.Array != null && arguments.Count > 0)
                {
                    Array.Copy(arguments.Array, arguments.Offset, args, 0, arguments.Count);
                }
                ConsoleCommandParser.Parsed command = ConsoleCommandParser.Parse(args);
                if (command.Action == ConsoleCommandParser.Action.Help)
                {
                    response = ConsoleCommandParser.Help;
                    return true;
                }
                if (command.Action == ConsoleCommandParser.Action.Error)
                {
                    response = command.Message;
                    return false;
                }
                Reporter reporter = SlmapsPlugin.ActiveReporter;
                if (reporter == null)
                {
                    response = "The slmaps plugin is not enabled.";
                    return false;
                }
                switch (command.Action)
                {
                    case ConsoleCommandParser.Action.Status:
                        response = reporter.DescribeStatus();
                        return true;
                    case ConsoleCommandParser.Action.Claim:
                        return reporter.StartClaim(command.Code, out response);
                    case ConsoleCommandParser.Action.Cancel:
                        return reporter.CancelClaim(out response);
                    case ConsoleCommandParser.Action.Report:
                        return reporter.QueueReportNow(out response);
                    case ConsoleCommandParser.Action.Log:
                        response = reporter.DescribeEvents(command.Count);
                        return true;
                    case ConsoleCommandParser.Action.Version:
                        return reporter.CheckVersionNow(out response);
                    default:
                        response = ConsoleCommandParser.Usage;
                        return false;
                }
            }
            catch (Exception ex)
            {
                response = "slmaps command failed: " + ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }
    }
}
