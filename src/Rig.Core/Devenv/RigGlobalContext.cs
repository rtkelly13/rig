namespace Rig.Core.Devenv;

/// <summary>What writing a marker-managed rules file would do.</summary>
public enum ManagedRulesAction
{
    /// <summary>There was no file yet; one was created.</summary>
    Created,

    /// <summary>The managed block was added or refreshed.</summary>
    Updated,

    /// <summary>The managed block is already current.</summary>
    Unchanged,
}

/// <summary>The content to write, and what writing it would do.</summary>
/// <param name="Content">The whole file content.</param>
/// <param name="Action">Whether it is new, changed, or the same.</param>
public sealed record ManagedRulesMerge(string Content, ManagedRulesAction Action);

/// <summary>
/// Canonical definitions and merging logic for global rig context injected across agent harnesses.
/// Uses deterministic HTML comment markers so multiple external tools and human editors can interact
/// with context files in parallel without clobbering each other.
/// </summary>
public static class RigGlobalContext
{
    /// <summary>Opens the managed block.</summary>
    public const string Begin = "<!-- rig:global-context:begin -->";

    /// <summary>Closes the managed block.</summary>
    public const string End = "<!-- rig:global-context:end -->";

    /// <summary>The global rig availability context injected across agent harnesses.</summary>
    public const string ManagedBlock = """
        ### Rig Global Tooling
        `rig` CLI is installed globally on PATH across developer machines for estate and PR workflows:
        - **Landing a PR**: `rig gh pr land <PR>` queues squash auto-merge and waits until landed. Never poll `gh pr view` or `gh pr checks --watch`.
        - **Waiting on a PR**: `rig gh pr wait <PR>` waits for an already queued PR with backoff and settle time; detects review threads, merge queue stalls, failed checks.
        - **Review Threads**: `rig gh threads list <PR>` and `rig gh threads resolve <PR> <THREAD_ID> --sha <SHA>` to inspect and resolve review threads truthfully.
        - **Estate & Status**: `rig status` (health overview across estate repos), `rig estate orca-worktrees` (clean finished worktrees).
        - **Discovery**: `rig help` or `rig <group> --help` for on-demand details (adheres to progressive disclosure).
        """;

    /// <summary>The managed block, marker-wrapped with trailing newline.</summary>
    public static string Render() => $"{Begin}\n{ManagedBlock.ReplaceLineEndings("\n").TrimEnd('\n')}\n{End}\n";

    /// <summary>
    /// Splices the managed block into <paramref name="existing"/>, or starts a new file when null with optional <paramref name="header"/>.
    /// Preserves everything outside the markers.
    /// </summary>
    public static ManagedRulesMerge Merge(string? existing, string? header = null)
    {
        var block = Render();
        if (existing is null)
        {
            var content = string.IsNullOrWhiteSpace(header)
                ? block
                : $"{header.TrimEnd('\n')}\n\n{block}";
            return new ManagedRulesMerge(content, ManagedRulesAction.Created);
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

        return new ManagedRulesMerge(merged, merged == existing ? ManagedRulesAction.Unchanged : ManagedRulesAction.Updated);
    }
}
