namespace Rig.Core.Processes;

/// <summary>A command that had to succeed did not exit 0.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1032", Justification = "Always carries the command and its result.")]
public sealed class ProcException : Exception
{
    /// <summary>Creates the exception for <paramref name="argv"/> ending as <paramref name="ran"/>.</summary>
    public ProcException(IReadOnlyList<string> argv, Ran ran)
        : base(Describe(argv, ran))
    {
        Argv = argv;
        Ran = ran;
    }

    /// <summary>The command that failed.</summary>
    public IReadOnlyList<string> Argv { get; }

    /// <summary>How it ended.</summary>
    public Ran Ran { get; }

    private static string Describe(IReadOnlyList<string> argv, Ran ran)
    {
        ArgumentNullException.ThrowIfNull(argv);
        ArgumentNullException.ThrowIfNull(ran);
        var detail = ran.Err.Trim() is { Length: > 0 } err ? err : ran.Out.Trim();
        var message = $"{string.Join(' ', argv)} exited {ran.Code}";
        return detail.Length > 0 ? $"{message}: {detail}" : message;
    }
}
