namespace Rig.Core.Processes;

/// <summary>How a command ended. <see cref="Out"/> and <see cref="Err"/> are the captured text, unstripped.</summary>
/// <param name="Code">The exit code; 127 when the program was not found and 124 when it timed out, as a shell reports.</param>
/// <param name="Out">Captured standard output.</param>
/// <param name="Err">Captured standard error.</param>
public sealed record Ran(int Code, string Out, string Err)
{
    /// <summary>The exit code a shell gives a program that is not on PATH.</summary>
    public const int NotFound = 127;

    /// <summary>The exit code <c>timeout(1)</c> gives a program it stopped.</summary>
    public const int TimedOut = 124;

    /// <summary>Whether the command exited 0.</summary>
    public bool Ok => Code == 0;

    /// <summary>Standard output, stripped; empty when the command failed.</summary>
    public string OutOrEmpty => Ok ? Out.Trim() : string.Empty;

    /// <summary>The non-empty, stripped lines of standard output, or null when the command failed.</summary>
    public IReadOnlyList<string>? Lines =>
        Ok ? Out.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList() : null;

    /// <summary>This result, or a <see cref="ProcException"/> when it did not exit 0.</summary>
    public Ran EnsureOk(IReadOnlyList<string> argv) => Ok ? this : throw new ProcException(argv, this);
}
