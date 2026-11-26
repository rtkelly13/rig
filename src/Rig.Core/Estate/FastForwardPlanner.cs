using Rig.Core.Reconciliation;

namespace Rig.Core.Estate;

/// <summary>Why a root checkout is not fast-forwarded.</summary>
public enum FastForwardRefusal
{
    /// <summary>It is safe to fast-forward.</summary>
    None,

    /// <summary>It is a linked worktree, and only root checkouts are candidates.</summary>
    LinkedWorktree,

    /// <summary>It sits under the code root's <c>external/</c> or <c>other/</c>.</summary>
    Untrusted,

    /// <summary>It has staged, unstaged or untracked changes.</summary>
    Dirty,

    /// <summary>A rebase, merge, cherry-pick, revert or bisect is in progress.</summary>
    OperationInProgress,

    /// <summary><c>HEAD</c> is detached.</summary>
    Detached,

    /// <summary><c>HEAD</c> is on a branch other than the remote default branch.</summary>
    NotDefaultBranch,

    /// <summary>There is no <c>origin</c>, or no <c>origin/&lt;default&gt;</c> to merge.</summary>
    NoRemote,

    /// <summary>The local default branch has commits <c>origin/&lt;default&gt;</c> does not.</summary>
    Ahead,

    /// <summary>The local and remote default branches have both moved.</summary>
    Diverged,

    /// <summary>Observed state could not be read, so safety cannot be proved.</summary>
    Unreadable,
}

/// <summary>What observation found about one candidate root checkout.</summary>
/// <param name="Repo">The checkout's folder.</param>
/// <param name="Exists">The folder is there.</param>
/// <param name="IsRootCheckout">It is a checkout's own working tree, not a linked worktree.</param>
/// <param name="IsUntrusted">It lies under the code root's <c>external/</c> or <c>other/</c>.</param>
/// <param name="IsDirty">It has staged, unstaged or untracked changes.</param>
/// <param name="IsDetached"><c>HEAD</c> is detached.</param>
/// <param name="Branch">The branch <c>HEAD</c> names, without <c>refs/heads/</c>; empty when detached.</param>
/// <param name="DefaultBranch">The remote default branch, without <c>origin/</c>.</param>
/// <param name="Target">The remote-tracking ref to merge, such as <c>origin/main</c>.</param>
/// <param name="HasRemote">The target ref exists and can be compared with.</param>
/// <param name="Ahead">Commits on <c>HEAD</c> the target does not have.</param>
/// <param name="Behind">Commits on the target <c>HEAD</c> does not have.</param>
/// <param name="Operation">The rebase, merge, cherry-pick, revert or bisect in progress; empty when none.</param>
/// <param name="Error">Why observed state could not be read; empty when it was.</param>
public sealed record RootCheckoutFacts(
    string Repo,
    bool Exists = true,
    bool IsRootCheckout = true,
    bool IsUntrusted = false,
    bool IsDirty = false,
    bool IsDetached = false,
    string Branch = "",
    string DefaultBranch = "",
    string Target = "",
    bool HasRemote = true,
    int Ahead = 0,
    int Behind = 0,
    string Operation = "",
    string Error = "");

/// <summary>A planner's verdict for one root checkout.</summary>
/// <param name="Kind">Aligned, would change, refused or error.</param>
/// <param name="Detail">What the verdict means, in a form a report can print.</param>
/// <param name="Refusal">The typed refusal when <paramref name="Kind"/> is <see cref="ChangeKind.Refused"/>.</param>
/// <param name="Target">The ref to merge, when <paramref name="Kind"/> is <see cref="ChangeKind.WouldChange"/>.</param>
public sealed record FastForwardDecision(
    ChangeKind Kind,
    string Detail,
    FastForwardRefusal Refusal = FastForwardRefusal.None,
    string Target = "");

/// <summary>
/// The fast-forward safety rules, as a pure function of one checkout's observed facts.
/// </summary>
/// <remarks>
/// Every refusal is deliberate and typed. The planner never repairs, resets or forces: anything other than a clean
/// root checkout on the remote default branch, behind it by fast-forwardable commits, is left exactly as it is.
/// </remarks>
public static class FastForwardPlanner
{
    /// <summary>Decides whether <paramref name="facts"/> may be fast-forwarded.</summary>
    /// <param name="facts">What observation found.</param>
    public static FastForwardDecision Decide(RootCheckoutFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        if (facts.Error.Length > 0)
        {
            return new FastForwardDecision(ChangeKind.Error, facts.Error, FastForwardRefusal.Unreadable);
        }

        if (!facts.Exists)
        {
            return new FastForwardDecision(ChangeKind.Error, "folder not found", FastForwardRefusal.Unreadable);
        }

        if (!facts.IsRootCheckout)
        {
            return Refuse(FastForwardRefusal.LinkedWorktree, "linked worktree — only root checkouts are fast-forwarded");
        }

        if (facts.IsUntrusted)
        {
            return Refuse(FastForwardRefusal.Untrusted, "under an untrusted folder");
        }

        if (facts.IsDirty)
        {
            return Refuse(FastForwardRefusal.Dirty, "working tree is dirty");
        }

        if (facts.Operation.Length > 0)
        {
            return Refuse(FastForwardRefusal.OperationInProgress, $"{facts.Operation} is in progress");
        }

        if (facts.IsDetached)
        {
            return Refuse(FastForwardRefusal.Detached, "HEAD is detached");
        }

        if (!string.Equals(facts.Branch, facts.DefaultBranch, StringComparison.Ordinal))
        {
            return Refuse(FastForwardRefusal.NotDefaultBranch, $"HEAD is on `{facts.Branch}`, not `{facts.DefaultBranch}`");
        }

        if (!facts.HasRemote)
        {
            return Refuse(FastForwardRefusal.NoRemote, $"no `{facts.Target}` to merge");
        }

        if (facts.Ahead > 0 && facts.Behind > 0)
        {
            return Refuse(FastForwardRefusal.Diverged, $"diverged: {facts.Ahead} ahead, {facts.Behind} behind `{facts.Target}`");
        }

        if (facts.Ahead > 0)
        {
            return Refuse(FastForwardRefusal.Ahead, $"{facts.Ahead} unpushed commit(s) on `{facts.Branch}`");
        }

        return facts.Behind == 0
            ? new FastForwardDecision(ChangeKind.Aligned, "up to date")
            : new FastForwardDecision(
                ChangeKind.WouldChange,
                $"fast-forward {facts.Behind} commit(s) to `{facts.Target}`",
                FastForwardRefusal.None,
                facts.Target);
    }

    private static FastForwardDecision Refuse(FastForwardRefusal refusal, string detail) =>
        new(ChangeKind.Refused, detail, refusal);
}
