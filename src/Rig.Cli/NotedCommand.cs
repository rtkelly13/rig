using System.CommandLine;
using System.CommandLine.Help;
using System.CommandLine.Invocation;

namespace Rig.Cli;

/// <summary>
/// A command whose <c>--help</c> ends with <see cref="Notes"/>: what it decides, its exit codes and examples. The
/// description stays one line, so the list its parent prints stays short.
/// </summary>
internal sealed class NotedCommand(string name, string description, string notes) : Command(name, description)
{
    /// <summary>Printed after the command's own help, never in a parent's list.</summary>
    public string Notes { get; } = notes;

    /// <summary>Makes <paramref name="root"/>'s help print the notes of the command it was asked about.</summary>
    public static void AddToHelp(RootCommand root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var help = root.Options.OfType<HelpOption>().Single();
        help.Action = new HelpWithNotes((HelpAction)help.Action!);
    }

    private sealed class HelpWithNotes(HelpAction standard) : SynchronousCommandLineAction
    {
        /// <summary>As the standard help does: asking for help is never a parse error.</summary>
        public override bool ClearsParseErrors => true;

        public override int Invoke(ParseResult parseResult)
        {
            var code = standard.Invoke(parseResult);
            if (parseResult.CommandResult.Command is NotedCommand noted)
            {
                var output = parseResult.InvocationConfiguration.Output;
                output.WriteLine(noted.Notes);
                output.WriteLine();
            }

            return code;
        }
    }
}
