using System.CommandLine;

namespace Rig.Cli.Forwarding;

/// <summary>
/// The commands still owned by the Python CLI (<c>src/rig/rig_cli.py</c>). Porting a command removes it from
/// <see cref="Commands"/> and registers it in C#; when the table is empty the forwarding goes too.
/// </summary>
internal static class Forwarded
{
    /// <summary>Each Python command and the one-line help it shows in <c>rig --help</c>.</summary>
    public static readonly IReadOnlyList<(string Name, string Description)> Commands =
    [
        ("align", "Reconcile desktop window layout and virtual desktop state."),
        ("workspace", "Virtual monorepo estate management for the personal folder."),
        ("estate", "Manage the code assets cloned on this machine."),
        ("triage", "Persistent PR and issue triage queue."),
        ("web", "Rig web dashboard & visual studio (static site + Python API)."),
        ("flags", "Feature flags shared across the rig stack (defaults in code, overrides in rig.db)."),
        ("c4", "C4 diagrams as an Open Knowledge Format bundle. Experimental."),
    ];

    private static readonly HashSet<string> Names = Commands.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Whether <paramref name="args"/> belong to the Python CLI: they start with a command it still owns, and, for a group
    /// partly ported to <paramref name="root"/> (<c>workspace doctor</c>), do not go on to name a subcommand C# runs. The
    /// whole line goes over unparsed, so its own help and flags work.
    /// </summary>
    public static bool Owns(IReadOnlyList<string> args, Command root)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(root);
        if (args.Count == 0 || !Names.Contains(args[0]))
        {
            return false;
        }

        var ported = root.Subcommands.FirstOrDefault(c => c.Name == args[0])?.Subcommands ?? [];
        return args.Count == 1 || !ported.Any(c => c.Name == args[1] || c.Aliases.Contains(args[1]));
    }
}
