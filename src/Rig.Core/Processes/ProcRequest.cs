namespace Rig.Core.Processes;

/// <summary>One process to run.</summary>
/// <param name="Argv">The program and its arguments. Never passed through a shell.</param>
public sealed record ProcRequest(IReadOnlyList<string> Argv)
{
    /// <summary>The working directory, or null for the current one.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>How long to wait before stopping the process, or null to wait for it.</summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>Variables added to the inherited environment (never replacing it).</summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();

    /// <summary>Text written to standard input; when null, standard input is closed.</summary>
    public string? StdinText { get; init; }

    /// <summary>
    /// Whether the process shares this console: standard input, output and error are inherited rather than captured,
    /// so <see cref="Ran.Out"/> and <see cref="Ran.Err"/> stay empty. Used to hand a whole command to another program.
    /// </summary>
    public bool Attached { get; init; }
}
