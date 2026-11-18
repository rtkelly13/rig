namespace Rig.Core.Estate;

/// <summary>Why a worktree may not be removed, or why a command must keep what only it holds.</summary>
public enum RemovalRefusalReason
{
    /// <summary>A terminal, agent or shell is working in it.</summary>
    InUse,

    /// <summary>It holds ignored files that do not regenerate, such as the estate's <c>TODO.md</c>.</summary>
    IgnoredNotes,

    /// <summary>HEAD names a branch whose ref is gone; the commits are reachable only from its reflog.</summary>
    UnbornHead,

    /// <summary>It is registered in another clone; removing it here would delete that clone's work.</summary>
    ForeignClone,

    /// <summary>Git was told to keep it.</summary>
    Locked,

    /// <summary>Commits are reachable only from this worktree's or its branch's reflog.</summary>
    ReflogOnlyCommit,

    /// <summary>HEAD holds commits that no remote has.</summary>
    UnpushedCommits,
}

/// <summary>
/// What a command is willing to give up, and the only thing that makes one command's decision differ from another's.
/// </summary>
/// <remarks>
/// Hard reasons — in use, unborn, foreign, locked, reflog-only — refuse under every policy: the facts may be lying, or
/// the work exists nowhere else and no ordinary flag may drop it. <see cref="DiscardIgnoredNotes"/> is the explicit
/// <c>--discard-ignored</c> decision, and <see cref="DiscardUnpushedCommits"/> is the existing <c>--force</c> relaxation
/// for a command that has proved the commits landed (a merged PR covering the head).
/// </remarks>
/// <param name="DiscardIgnoredNotes">Let ignored, non-regenerable notes go with the worktree.</param>
/// <param name="DiscardUnpushedCommits">Let commits on no remote go with it.</param>
public sealed record RemovalPolicy(bool DiscardIgnoredNotes = false, bool DiscardUnpushedCommits = false)
{
    /// <summary>Refuse everything unless the worktree is clean, pushed and idle.</summary>
    public static RemovalPolicy Strict { get; } = new();
}

/// <summary>Whether a worktree may go, and every reason it may not.</summary>
/// <param name="Remove">No reason refuses it.</param>
/// <param name="Reasons">Every blocking reason, in the order <see cref="WorktreeRemovalJudge"/> checks them.</param>
public sealed record RemovalDecision(bool Remove, IReadOnlyList<RemovalRefusalReason> Reasons)
{
    /// <summary>The worktree may be removed.</summary>
    public static RemovalDecision Allowed { get; } = new(true, []);
}

/// <summary>
/// The one place that decides whether a worktree may be removed. Pure: the same facts and policy always give the same
/// answer, so every command can be tested from tables rather than from the disk.
/// </summary>
public static class WorktreeRemovalJudge
{
    /// <summary>Every reason <paramref name="facts"/> refuses removal under <paramref name="policy"/>.</summary>
    /// <param name="facts">What the ports observed; never re-read here.</param>
    /// <param name="policy">What the calling command allows.</param>
    public static RemovalDecision Judge(WorktreeFacts facts, RemovalPolicy policy)
    {
        var reasons = new List<RemovalRefusalReason>();
        if (facts.InUse)
        {
            reasons.Add(RemovalRefusalReason.InUse);
        }

        if ((facts.HasIgnoredNotes || facts.IgnoredUnreadable) && !policy.DiscardIgnoredNotes)
        {
            reasons.Add(RemovalRefusalReason.IgnoredNotes);
        }

        if (facts.IsUnborn)
        {
            reasons.Add(RemovalRefusalReason.UnbornHead);
        }

        if (facts.IsForeignClone || facts.OwnershipUnreadable)
        {
            reasons.Add(RemovalRefusalReason.ForeignClone);
        }

        if (facts.IsLocked)
        {
            reasons.Add(RemovalRefusalReason.Locked);
        }

        if (facts.ReflogOnlyCommitCount > 0 || facts.ReflogUnreadable)
        {
            reasons.Add(RemovalRefusalReason.ReflogOnlyCommit);
        }

        if (facts.UnpushedCommitCount > 0 && !policy.DiscardUnpushedCommits)
        {
            reasons.Add(RemovalRefusalReason.UnpushedCommits);
        }

        return reasons.Count == 0 ? RemovalDecision.Allowed : new RemovalDecision(false, reasons);
    }
}
