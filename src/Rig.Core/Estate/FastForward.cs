using Rig.Core.Processes;
using Rig.Core.Reconciliation;
using Rig.Core.Roots;

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

/// <summary>One root checkout's row in the plan.</summary>
public sealed class FastForwardChange : IChange
{
    private ChangeKind? _kind;
    private string? _detail;

    /// <summary>Builds the row from a repo and its verdict.</summary>
    /// <param name="repo">The checkout's folder.</param>
    /// <param name="decision">The planner's verdict.</param>
    public FastForwardChange(string repo, FastForwardDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        Repo = repo;
        Decision = decision;
    }

    /// <summary>The checkout's folder.</summary>
    public string Repo { get; }

    /// <summary>The planner's verdict, before apply updates the row.</summary>
    public FastForwardDecision Decision { get; }

    /// <inheritdoc />
    public string Subject => Repo;

    /// <inheritdoc />
    public ChangeKind Kind
    {
        get => _kind ?? Decision.Kind;
        set => _kind = value;
    }

    /// <inheritdoc />
    public string Detail
    {
        get => _detail ?? Decision.Detail;
        set => _detail = value;
    }

    /// <summary>The ref to merge, when the row would change.</summary>
    public string Target => Decision.Target;
}

/// <summary>What to fast-forward: the root checkouts, in report order.</summary>
/// <param name="Repos">Each candidate root checkout.</param>
/// <param name="RefreshRemote">Fetch each origin first, so behind/ahead are read from the current remote.</param>
public sealed record FastForwardTarget(IReadOnlyList<string> Repos, bool RefreshRemote = true);

/// <summary>The typed plan <see cref="FastForward.PlanAsync"/> produces and <see cref="FastForward.ApplyAsync"/> walks.</summary>
public sealed class FastForwardPlan : IPlan<FastForwardChange>
{
    /// <summary>The checkouts the plan was built for.</summary>
    public required IReadOnlyList<string> Repos { get; init; }

    /// <summary>One row per checkout, in the order they were named.</summary>
    public required IReadOnlyList<FastForwardChange> Changes { get; init; }

    /// <summary>Checkouts whose state could not be read, or whose origin could not be fetched.</summary>
    public IReadOnlyList<string> Errors { get; init; } = [];
}

/// <summary>
/// Fast-forwards clean root checkouts to their remote default branch.
/// </summary>
/// <remarks>
/// <para>
/// Observation reads one checkout through <see cref="Git"/>: whether it is a root checkout, dirty, mid-operation,
/// detached, on the default branch, and how far it is ahead or behind <c>origin/&lt;default&gt;</c>. The fetch updates
/// remote-tracking refs only; planning never touches a working tree. <see cref="FastForwardPlanner"/> then applies the
/// safety rules without I/O, so every refusal is a table test.
/// </para>
/// <para>
/// Apply fetches and re-observes each checkout immediately before merging it with <c>git merge --ff-only</c>, so a
/// checkout that became dirty or diverged between plan and apply is refused rather than forced. The merge is
/// idempotent: a second run sees the checkout aligned and does nothing.
/// </para>
/// </remarks>
/// <param name="git">Runs every git call, hardened under untrusted folders.</param>
public sealed class FastForward(Git git) : IReconciler<FastForwardTarget, FastForwardPlan, FastForwardChange>
{
    /// <summary>How long one fetch may take.</summary>
    public static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(120);

    /// <summary>How long one fast-forward merge may take.</summary>
    public static readonly TimeSpan MergeTimeout = TimeSpan.FromSeconds(60);

    /// <inheritdoc />
    public async Task<FastForwardPlan> PlanAsync(FastForwardTarget target, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        var changes = new List<FastForwardChange>(target.Repos.Count);
        var errors = new List<string>();
        foreach (var repo in target.Repos)
        {
            var facts = await ObserveAsync(repo, target.RefreshRemote, ct).ConfigureAwait(false);
            var change = new FastForwardChange(repo, FastForwardPlanner.Decide(facts));
            changes.Add(change);
            if (change.Kind == ChangeKind.Error)
            {
                errors.Add($"{repo}: {change.Detail}");
            }
        }

        return new FastForwardPlan { Repos = target.Repos, Changes = changes, Errors = errors };
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ApplyAsync(FastForwardPlan plan, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var errors = new List<string>();
        foreach (var change in plan.Changes.Where(row => row.Kind == ChangeKind.WouldChange))
        {
            // Re-read, and fetch again, so the merge is decided on state as it is now rather than when the plan was built.
            var facts = await ObserveAsync(change.Repo, refreshRemote: true, ct).ConfigureAwait(false);
            var decision = FastForwardPlanner.Decide(facts);
            if (decision.Kind == ChangeKind.Error)
            {
                change.Kind = ChangeKind.Error;
                change.Detail = decision.Detail;
                errors.Add($"{change.Repo}: {decision.Detail}");
                continue;
            }

            if (decision.Kind == ChangeKind.Refused)
            {
                change.Kind = ChangeKind.Refused;
                change.Detail = $"{decision.Detail} (state changed before apply)";
                continue;
            }

            if (decision.Kind == ChangeKind.Aligned)
            {
                change.Kind = ChangeKind.Aligned;
                change.Detail = "up to date";
                continue;
            }

            var merge = await git.RunAsync(change.Repo, ["merge", "--ff-only", decision.Target], MergeTimeout, cancellationToken: ct).ConfigureAwait(false);
            if (merge.Ok)
            {
                change.Kind = ChangeKind.Changed;
                change.Detail = $"fast-forwarded to `{decision.Target}`";
                continue;
            }

            change.Kind = ChangeKind.Error;
            change.Detail = LastLine(merge.Err) ?? $"git merge --ff-only {decision.Target} failed";
            errors.Add($"{change.Repo}: {change.Detail}");
        }

        return errors;
    }

    private async Task<RootCheckoutFacts> ObserveAsync(string repo, bool refreshRemote, CancellationToken ct)
    {
        if (!Directory.Exists(repo))
        {
            return new RootCheckoutFacts(repo, Exists: false, Error: "folder not found");
        }

        if (git.IsUntrusted(repo))
        {
            return new RootCheckoutFacts(repo, IsUntrusted: true);
        }

        var resolved = PathResolution.Resolve(repo);
        var paths = await git.RunAsync(
            repo,
            ["rev-parse", "--path-format=absolute", "--git-dir", "--git-common-dir", "--show-toplevel"],
            cancellationToken: ct).ConfigureAwait(false);
        if (!paths.Ok)
        {
            return new RootCheckoutFacts(resolved, IsRootCheckout: false, Error: LastLine(paths.Err) ?? "not a git checkout");
        }

        var lines = paths.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length < 3)
        {
            return new RootCheckoutFacts(resolved, IsRootCheckout: false, Error: "git rev-parse did not name the checkout");
        }

        var gitDir = PathResolution.Resolve(lines[0]);
        var commonDir = PathResolution.Resolve(lines[1]);
        var top = PathResolution.Resolve(lines[2]);
        var isRoot = PathResolution.Same(gitDir, commonDir) && PathResolution.Same(top, resolved);

        var status = await git.RunAsync(repo, ["status", "--porcelain", "--untracked-files=normal"], cancellationToken: ct).ConfigureAwait(false);
        if (!status.Ok)
        {
            return new RootCheckoutFacts(resolved, IsRootCheckout: isRoot, Error: LastLine(status.Err) ?? "git status failed");
        }

        var operation = OperationInProgress(gitDir);
        var head = await git.RunAsync(repo, ["symbolic-ref", "--quiet", "--short", "HEAD"], cancellationToken: ct).ConfigureAwait(false);
        var detached = !head.Ok;
        var branch = head.OutOrEmpty;

        if (!isRoot || detached)
        {
            return new RootCheckoutFacts(
                resolved,
                IsRootCheckout: isRoot,
                IsDirty: status.Out.Length > 0,
                IsDetached: detached,
                Branch: branch,
                Operation: operation);
        }

        var origin = await git.RunAsync(repo, ["remote", "get-url", "origin"], cancellationToken: ct).ConfigureAwait(false);
        var hasOrigin = origin.Ok;
        if (hasOrigin && refreshRemote)
        {
            var fetch = await git.RunAsync(repo, ["fetch", "--prune", "--quiet", "origin"], FetchTimeout, cancellationToken: ct).ConfigureAwait(false);
            if (!fetch.Ok)
            {
                return new RootCheckoutFacts(
                    resolved,
                    IsDirty: status.Out.Length > 0,
                    Branch: branch,
                    Operation: operation,
                    Error: $"git fetch --prune origin failed: {LastLine(fetch.Err) ?? "no output"}");
            }
        }

        var defaultBranch = "main";
        var originHead = await git.RunAsync(repo, ["symbolic-ref", "--quiet", "--short", "refs/remotes/origin/HEAD"], cancellationToken: ct).ConfigureAwait(false);
        if (originHead.OutOrEmpty is { Length: > 0 } symbolic && symbolic.StartsWith("origin/", StringComparison.Ordinal))
        {
            defaultBranch = symbolic["origin/".Length..];
        }

        var target = $"origin/{defaultBranch}";
        var hasTarget = false;
        if (hasOrigin)
        {
            var verify = await git.RunAsync(repo, ["rev-parse", "--verify", "--quiet", $"refs/remotes/{target}"], cancellationToken: ct).ConfigureAwait(false);
            hasTarget = verify.Ok && verify.OutOrEmpty.Length > 0;
        }

        var ahead = 0;
        var behind = 0;
        if (hasTarget)
        {
            var counts = await git.RunAsync(repo, ["rev-list", "--left-right", "--count", $"HEAD...{target}"], cancellationToken: ct).ConfigureAwait(false);
            if (!counts.Ok)
            {
                return new RootCheckoutFacts(
                    resolved,
                    IsDirty: status.Out.Length > 0,
                    Branch: branch,
                    DefaultBranch: defaultBranch,
                    Target: target,
                    HasRemote: true,
                    Operation: operation,
                    Error: LastLine(counts.Err) ?? $"cannot compare HEAD with {target}");
            }

            var split = counts.OutOrEmpty.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            _ = int.TryParse(split.ElementAtOrDefault(0), out ahead);
            _ = int.TryParse(split.ElementAtOrDefault(1), out behind);
        }

        return new RootCheckoutFacts(
            resolved,
            IsRootCheckout: true,
            IsDirty: status.Out.Length > 0,
            Branch: branch,
            DefaultBranch: defaultBranch,
            Target: target,
            HasRemote: hasTarget,
            Ahead: ahead,
            Behind: behind,
            Operation: operation);
    }

    private static string OperationInProgress(string gitDir)
    {
        if (Directory.Exists(Path.Join(gitDir, "rebase-merge")))
        {
            return "rebase";
        }

        if (Directory.Exists(Path.Join(gitDir, "rebase-apply")))
        {
            return "rebase";
        }

        if (File.Exists(Path.Join(gitDir, "MERGE_HEAD")))
        {
            return "merge";
        }

        if (File.Exists(Path.Join(gitDir, "CHERRY_PICK_HEAD")))
        {
            return "cherry-pick";
        }

        if (File.Exists(Path.Join(gitDir, "REVERT_HEAD")))
        {
            return "revert";
        }

        return File.Exists(Path.Join(gitDir, "BISECT_LOG")) ? "bisect" : string.Empty;
    }

    private static string? LastLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
}
