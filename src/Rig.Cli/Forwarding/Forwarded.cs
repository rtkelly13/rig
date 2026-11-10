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
        ("agent", "Launch preferred AI coding agent or selected flag (-agy, -ccc, -oc, -codex, -dng)."),
        ("status", "Report repository health, git status, and estate verification."),
        ("sync", "Synchronize estate index (AGENTS.md) and personal.code-workspace."),
        ("doctor", "Validate environment health across tools, runtimes, git, and AI assistants."),
        ("align", "Reconcile desktop window layout and virtual desktop state."),
        ("workspace", "Virtual monorepo estate management for the personal folder."),
        ("devenv", "Unified development environment manager (tools, runtimes, fonts, git, AI coding agents)."),
        ("estate", "Manage the code assets cloned on this machine."),
        ("models", "Enumerate AI models supported across coding agent CLIs in rig."),
        ("triage", "Persistent PR and issue triage queue."),
        ("web", "Rig web dashboard & visual studio (static site + Python API)."),
        ("flags", "Feature flags shared across the rig stack (defaults in code, overrides in rig.db)."),
        ("browser", "Set up the environment the upstream agent-browser CLI reads."),
        ("gh", "Fill gaps in the GitHub CLI: PR review threads. Temporary."),
        ("c4", "C4 diagrams as an Open Knowledge Format bundle. Experimental."),
    ];

    /// <summary>
    /// The agent launcher's shortcut flags. <c>rig -ccc</c> launches an agent straight away, as does a bare
    /// <c>rig</c>, so these go to Python before any parsing.
    /// </summary>
    public static readonly IReadOnlySet<string> AgentShortcutFlags = new HashSet<string>(StringComparer.Ordinal)
    {
        "-agy", "-ccc", "-agywsl", "-oc", "-opencode", "-codex", "-cpl", "-copilot", "-dng", "--dng",
        "--dangerously-skip-permissions", "-r", "--remote-control", "--print-config",
    };

    private static readonly HashSet<string> Names = Commands.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Whether <paramref name="args"/> belong to the Python CLI: no arguments (launch the preferred agent), an agent
    /// shortcut flag, or a command it still owns. The whole line goes over unparsed, so its own help and flags work.
    /// </summary>
    public static bool Owns(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return args.Count == 0 || AgentShortcutFlags.Contains(args[0]) || Names.Contains(args[0]);
    }
}
