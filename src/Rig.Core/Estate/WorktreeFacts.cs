namespace Rig.Core.Estate;

/// <summary>
/// Everything the removal decision needs to know about one worktree, gathered once through
/// <see cref="IGitWorktreePort"/> and <see cref="ILivenessPort"/>.
/// </summary>
/// <remarks>
/// The record is data: <see cref="WorktreeRemovalJudge"/> reads it and never runs git again. A missing value is never
/// read as "none": <see cref="ReflogUnreadable"/> says the reflog could not be read, and <see cref="InUse"/> is the
/// answer of a liveness port that distinguishes a live folder from an unreadable snapshot.
/// </remarks>
/// <param name="Path">The worktree's folder.</param>
/// <param name="Branch">The branch checked out, without <c>refs/heads/</c>; empty when detached.</param>
/// <param name="Head">The commit HEAD names; empty or the null id when unborn.</param>
/// <param name="IsLocked">Locked against pruning and removal.</param>
/// <param name="LockReason">Why, when git was told.</param>
/// <param name="IsUnborn">HEAD names a branch whose ref does not exist.</param>
/// <param name="IsForeignClone">The worktree's <c>.git</c> resolves into another clone's admin folder.</param>
/// <param name="HasUncommittedChanges">Tracked edits or untracked files.</param>
/// <param name="HasIgnoredNotes">An ignored path that does not regenerate, such as the estate's <c>TODO.md</c>.</param>
/// <param name="InUse">A live terminal, agent or shell is working in it.</param>
/// <param name="ReflogOnlyCommitCount">Commits this worktree's or branch's reflog names and no ref does.</param>
/// <param name="UnpushedCommitCount">Commits on HEAD that no remote has.</param>
public sealed record WorktreeFacts(
    string Path,
    string Branch = "",
    string Head = "",
    bool IsLocked = false,
    string LockReason = "",
    bool IsUnborn = false,
    bool IsForeignClone = false,
    bool HasUncommittedChanges = false,
    bool HasIgnoredNotes = false,
    bool InUse = false,
    int ReflogOnlyCommitCount = 0,
    int UnpushedCommitCount = 0)
{
    /// <summary>The reflog could not be read, so the count is unknown rather than zero.</summary>
    public bool ReflogUnreadable { get; init; }

    /// <summary>The ignored paths could not be listed, so "no notes" cannot be claimed.</summary>
    public bool IgnoredUnreadable { get; init; }

    /// <summary>The clone's own git directory could not be read, so ownership cannot be proved.</summary>
    public bool OwnershipUnreadable { get; init; }

    /// <summary>The ignored, non-regenerable paths found, for a detail that names them.</summary>
    public IReadOnlyList<string> IgnoredNotes { get; init; } = [];
}

/// <summary>Reads the git-observable part of <see cref="WorktreeFacts"/> for one worktree.</summary>
public interface IGitWorktreePort
{
    /// <summary>
    /// Everything git knows about <paramref name="tree"/> in <paramref name="clone"/>, except whether a process is
    /// working in it, which <see cref="ILivenessPort"/> answers.
    /// </summary>
    /// <param name="clone">The clone the worktree is registered in.</param>
    /// <param name="tree">The worktree, as <c>git worktree list</c> reported it.</param>
    /// <param name="cancellationToken">Stops the reads.</param>
    Task<WorktreeFacts> ReadAsync(string clone, Worktree tree, CancellationToken cancellationToken = default);
}

/// <summary>Answers whether a live process is working in a folder.</summary>
public interface ILivenessPort
{
    /// <summary>
    /// Whether a shell, agent or terminal is working in <paramref name="path"/> or below it; null when that cannot be
    /// told, which is never read as idle.
    /// </summary>
    /// <param name="path">The worktree's folder.</param>
    bool? InUse(string path);
}
