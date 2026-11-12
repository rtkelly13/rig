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
    public const string Begin = "<!-- rig:global-context:begin -->";

    /// <summary>Closes the managed block.</summary>
    public const string End = "<!-- rig:global-context:end -->";

    /// <summary>The global rig availability context injected for AGY sessions.</summary>
    public const string ManagedBlock = """
        ### Rig Global Tooling
        `rig` CLI is installed globally on PATH across developer machines for estate and PR workflows:
        - **Landing a PR**: `rig gh pr land <PR>` queues squash auto-merge and waits until landed. Never poll `gh pr view` or `gh pr checks --watch`.
        - **Waiting on a PR**: `rig gh pr wait <PR>` waits for an already queued PR with backoff and settle time; detects review threads, merge queue stalls, failed checks.
        - **Review Threads**: `rig gh threads list <PR>` and `rig gh threads resolve <PR> <THREAD_ID> --sha <SHA>` to inspect and resolve review threads truthfully.
        - **Estate & Status**: `rig status` (health overview across estate repos), `rig estate orca-worktrees` (clean finished worktrees).
        - **Discovery**: `rig help` or `rig <group> --help` for on-demand details (adheres to progressive disclosure).
        """;

    /// <summary>Default file path under <paramref name="home"/>: <c>~/.gemini/GEMINI.md</c>.</summary>
    public static string FilePath(string home) => Path.Join(home, ".gemini", "GEMINI.md");

    /// <summary>The managed block, marker-wrapped with trailing newline.</summary>
    public static string Render() => $"{Begin}\n{ManagedBlock.ReplaceLineEndings("\n").TrimEnd('\n')}\n{End}\n";

    /// <summary>
    /// Splices the managed block into <paramref name="existing"/>, or starts a new file when null.
    /// Preserves everything outside the markers.
    /// </summary>
    public static AgyRulesMerge Merge(string? existing)
    {
        var block = Render();
        if (existing is null)
        {
            return new AgyRulesMerge(block, AgyRulesAction.Created);
        }

        var begin = existing.IndexOf(Begin, StringComparison.Ordinal);
        var end = existing.IndexOf(End, StringComparison.Ordinal);
        string merged;
        if (begin >= 0 && end >= 0)
        {
            var tail = existing[(end + End.Length)..].TrimStart('\n');
            merged = existing[..begin] + block + (string.IsNullOrWhiteSpace(tail) ? string.Empty : $"\n{tail}");
        }
        else
        {
            merged = existing.TrimEnd('\n') + $"\n\n{block}";
        }

        return new AgyRulesMerge(merged, merged == existing ? AgyRulesAction.Unchanged : AgyRulesAction.Updated);
    }
}
