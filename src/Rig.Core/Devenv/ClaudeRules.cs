namespace Rig.Core.Devenv;

/// <summary>What writing the Claude Code global rules would do.</summary>
public enum ClaudeRulesAction
{
    /// <summary>There is no ~/.claude/rules/rig.md yet.</summary>
    Created,

    /// <summary>The managed block is added or refreshed.</summary>
    Updated,

    /// <summary>The managed block is already current.</summary>
    Unchanged,
}

/// <summary>The content to write, and what writing it would do.</summary>
/// <param name="Content">The whole file.</param>
/// <param name="Action">Whether it is new, changed or the same.</param>
public sealed record ClaudeRulesMerge(string Content, ClaudeRulesAction Action);

/// <summary>
/// Global agent rules for Claude Code in <c>~/.claude/rules/rig.md</c>.
/// Claude Code v2.1+ automatically discovers and loads all markdown files in <c>~/.claude/rules/</c>.
/// Only the block between the markers is managed: anything written outside it is preserved untouched
/// so multiple tools and developers can interact with rule files in parallel.
/// </summary>
public static class ClaudeRules
{
    /// <summary>Opens the managed block.</summary>
    public const string Begin = RigGlobalContext.Begin;

    /// <summary>Closes the managed block.</summary>
    public const string End = RigGlobalContext.End;

    /// <summary>The global rig availability context injected for Claude Code sessions.</summary>
    public const string ManagedBlock = RigGlobalContext.ManagedBlock;

    /// <summary>Default header for a newly created rules file.</summary>
    public const string Header = """
        # Rig Global Rules

        Managed by `rig devenv claude`.
        Edit outside the markers — content there is preserved across runs.
        """;

    /// <summary>Default file path under <paramref name="home"/>: <c>~/.claude/rules/rig.md</c>.</summary>
    public static string FilePath(string home) => Path.Join(home, ".claude", "rules", "rig.md");

    /// <summary>The managed block, marker-wrapped with trailing newline.</summary>
    public static string Render() => RigGlobalContext.Render();

    /// <summary>
    /// Splices the managed block into <paramref name="existing"/>, or starts a new file when null.
    /// Preserves everything outside the markers.
    /// </summary>
    public static ClaudeRulesMerge Merge(string? existing)
    {
        var result = RigGlobalContext.Merge(existing, Header);
        return new ClaudeRulesMerge(result.Content, (ClaudeRulesAction)result.Action);
    }

    /// <summary>Writes the rules content to <paramref name="path"/> using an atomic staging file.</summary>
    public static void Write(string path, string content, bool windows = false)
    {
        RigGlobalContext.Write(path, content, windows);
    }
}
