namespace Rig.Core.Devenv;

/// <summary>What writing the AGY (Antigravity) global context would do.</summary>
public enum AgyRulesAction
{
    /// <summary>There is no ~/.gemini/GEMINI.md yet.</summary>
    Created,

    /// <summary>The managed block is added or refreshed.</summary>
    Updated,

    /// <summary>The managed block is already current.</summary>
    Unchanged,
}

/// <summary>The content to write, and what writing it would do.</summary>
/// <param name="Content">The whole file.</param>
/// <param name="Action">Whether it is new, changed or the same.</param>
public sealed record AgyRulesMerge(string Content, AgyRulesAction Action);

/// <summary>
/// Global agent context for Google Antigravity (AGY) in <c>~/.gemini/GEMINI.md</c>.
/// Only the block between the markers is managed: anything written outside it (e.g. memories, Keep notes, user rules)
/// is preserved untouched so multiple tools can interact with the file in parallel.
/// </summary>
public static class AgyRules
{
    /// <summary>Opens the managed block.</summary>
    public const string Begin = RigGlobalContext.Begin;

    /// <summary>Closes the managed block.</summary>
    public const string End = RigGlobalContext.End;

    /// <summary>The global rig availability context injected for AGY sessions.</summary>
    public const string ManagedBlock = RigGlobalContext.ManagedBlock;

    /// <summary>Default file path under <paramref name="home"/>: <c>~/.gemini/GEMINI.md</c>.</summary>
    public static string FilePath(string home) => Path.Join(home, ".gemini", "GEMINI.md");

    /// <summary>The managed block, marker-wrapped with trailing newline.</summary>
    public static string Render() => RigGlobalContext.Render();

    /// <summary>
    /// Splices the managed block into <paramref name="existing"/>, or starts a new file when null.
    /// Preserves everything outside the markers.
    /// </summary>
    public static AgyRulesMerge Merge(string? existing)
    {
        var result = RigGlobalContext.Merge(existing);
        return new AgyRulesMerge(result.Content, (AgyRulesAction)result.Action);
    }
}
