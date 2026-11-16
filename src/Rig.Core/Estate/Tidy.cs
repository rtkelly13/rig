using System.Globalization;
using Rig.Core.Processes;
using Rig.Core.Reconciliation;
using Rig.Core.Roots;

namespace Rig.Core.Estate;

/// <summary>What happened to one finding.</summary>
public enum TidyOutcome
{
    /// <summary>Removed.</summary>
    Cleaned,

    /// <summary>A dry run found it safe to remove.</summary>
    WouldClean,

    /// <summary>Not provably safe, so left alone.</summary>
    Refused,

    /// <summary>Safe, but git or the disk would not remove it.</summary>
    Failed,
}

/// <summary>One cleanup decision and its result.</summary>
/// <param name="Check">The finding's check.</param>
/// <param name="Subject">The path or branch it is about.</param>
/// <param name="Outcome">What happened.</param>
/// <param name="Detail">Why, or what git said.</param>
public sealed record TidyAction(string Check, string Subject, TidyOutcome Outcome, string Detail = "") : IChange
{
    /// <summary>The outcome as reports spell it: <c>cleaned</c>, <c>would-clean</c>, <c>refused</c> or <c>failed</c>.</summary>
    public string OutcomeName => Name(Outcome);

    /// <summary>The standard reconciliation kind for <see cref="Outcome"/>.</summary>
    public ChangeKind Kind => Outcome switch
    {
        TidyOutcome.Cleaned => ChangeKind.Changed,
        TidyOutcome.WouldClean => ChangeKind.WouldChange,
        TidyOutcome.Refused => ChangeKind.Refused,
        _ => ChangeKind.Error,
    };

    /// <summary>How reports spell <paramref name="outcome"/>.</summary>
    public static string Name(TidyOutcome outcome) => outcome switch
    {
        TidyOutcome.Cleaned => "cleaned",
        TidyOutcome.WouldClean => "would-clean",
        TidyOutcome.Refused => "refused",
        _ => "failed",
    };
}

/// <summary>The finding checks <see cref="Tidy"/> acts on; anything else is reported, never touched.</summary>
public static class TidyChecks
{
    /// <summary>A branch git or GitHub says is finished.</summary>
    public static readonly IReadOnlyList<string> Branch = ["branch-upstream-gone", "branch-merged"];

    /// <summary>A worktree that is stale or finished.</summary>
    public static readonly IReadOnlyList<string> Worktree = ["temp-stale-worktree", "worktree-merged"];

    /// <summary>Every check, in the order <c>--check</c> lists them.</summary>
    public static readonly IReadOnlyList<string> All = ["worktree-prunable", .. Branch, .. Worktree, "temp-stale"];
}

/// <summary>What <see cref="Tidy"/> reconciles, and the policy that decides each row.</summary>
/// <param name="Report">From <see cref="RepoChecks.InspectAsync"/>.</param>
/// <param name="Force">Relax the soft refusals; never a hard one.</param>
/// <param name="MergedHeads">From <see cref="PullRequests.MergedHeadsAsync"/>; null when GitHub was not asked.</param>
/// <param name="Busy">The run's process snapshot; null skips the in-use check.</param>
/// <param name="Checks">Only these finding checks; null for all.</param>
public sealed record TidyTarget(
    RepoReport Report,
    bool Force = false,
    IReadOnlyDictionary<string, PullRequest>? MergedHeads = null,
    Busy? Busy = null,
    IReadOnlyCollection<string>? Checks = null);

/// <summary>The typed plan <see cref="Tidy.PlanAsync"/> produces and <see cref="Tidy.ApplyAsync"/> walks.</summary>
public sealed class TidyPlan : IPlan<TidyAction>
{
    private readonly List<TidyAction> _changes = [];

    /// <summary>The clone's findings the plan was built from.</summary>
    public required TidyTarget Target { get; init; }

    /// <summary>Every planned or completed action, in evaluation order.</summary>
    public IReadOnlyList<TidyAction> Changes => _changes;

    /// <summary>Failures the plan or its application reported.</summary>
    public IReadOnlyList<string> Errors =>
        [.. _changes.Where(change => change.Kind == ChangeKind.Error).Select(change => $"{change.Subject}: {change.Detail}")];

    internal void Add(TidyAction action) => _changes.Add(action);

    internal void Replace(int index, TidyAction action) => _changes[index] = action;
}

/// <summary>
/// The only estate code that deletes a branch or a worktree. Ported from <c>src/rig/estate/cleanup.py</c>.
/// </summary>
/// <remarks>
/// <para>
/// Everything is a dry run unless <c>apply</c>. Safety is per item: the first cut refused any clone holding a dirty file
/// or an unpushed worktree, and across this estate it refused 9 of 17 clones and hid 23 provably safe deletions. Each
/// handler proves its own target instead.
/// </para>
/// <list type="bullet">
/// <item>A branch goes only when it has no commits outside a remote, or a merged PR holds its tip.</item>
/// <item>A worktree goes only when clean and pushed; <c>force</c> relaxes that, never the refusals that read wrong
/// rather than cautious.</item>
/// <item>Anything resolving outside the clone is refused rather than followed.</item>
/// </list>
/// <para>
/// A registered worktree goes through <c>git worktree remove</c>, never a recursive delete, which leaves the clone's
/// admin files behind. A status or count git cannot give counts as dirty or as commits on no remote.
/// </para>
/// </remarks>
/// <param name="git">Runs git.</param>
/// <param name="removal">Deletes stale scratch.</param>
public sealed class Tidy(Git git, TreeRemoval removal) : IReconciler<TidyTarget, TidyPlan, TidyAction>
{
    // A worktree holding a large node_modules takes a while to delete.
    private static readonly TimeSpan RemoveTimeout = TimeSpan.FromMinutes(10);

    private readonly GitWorktreePort _worktrees = new(git);

    /// <summary>Acts on the actionable findings in <paramref name="report"/>.</summary>
    /// <param name="report">From <see cref="RepoChecks.InspectAsync"/>.</param>
    /// <param name="apply">Delete; otherwise say what would go.</param>
    /// <param name="force">Relax the dirty, unpushed and unreadable-lsof refusals on worktrees and scratch. No flag deletes a
    /// branch holding the last copy of a commit.</param>
    /// <param name="mergedHeads">From <see cref="PullRequests.MergedHeadsAsync"/>; null when GitHub was not asked.</param>
    /// <param name="busy">The run's process snapshot; null skips it, for a caller that has ruled it out.</param>
    /// <param name="checks">Only these finding checks; null for all.</param>
    /// <param name="cancellationToken">Stops a removal.</param>
    public async Task<IReadOnlyList<TidyAction>> CleanRepoAsync(
        RepoReport report,
        bool apply = false,
        bool force = false,
        IReadOnlyDictionary<string, PullRequest>? mergedHeads = null,
        Busy? busy = null,
        IReadOnlyCollection<string>? checks = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        var plan = await PlanAsync(new TidyTarget(report, force, mergedHeads, busy, checks), cancellationToken).ConfigureAwait(false);
        if (apply)
        {
            _ = await ApplyAsync(plan, cancellationToken).ConfigureAwait(false);
        }

        return plan.Changes;
    }

    /// <summary>Reads the report's actionable findings and decides each one. Never writes.</summary>
    /// <param name="target">The report and the policy that decides each row.</param>
    /// <param name="ct">Stops between findings.</param>
    public async Task<TidyPlan> PlanAsync(TidyTarget target, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        var report = target.Report;
        var plan = new TidyPlan { Target = target };
        var actionable = report.Findings
            .Where(finding => finding.Actionable && (target.Checks is null || target.Checks.Contains(finding.Check)))
            .ToList();
        if (actionable.Any(finding => finding.Check == "worktree-prunable"))
        {
            plan.Add(await PruneAsync(report.Repo, apply: false, ct).ConfigureAwait(false));
        }

        foreach (var finding in actionable.Where(finding => finding.Check != "worktree-prunable" && TidyChecks.All.Contains(finding.Check)))
        {
            ct.ThrowIfCancellationRequested();
            plan.Add(await DecideAsync(report.Repo, finding, target, apply: false, ct).ConfigureAwait(false));
        }

        return plan;
    }

    /// <summary>Walks the plan's would-change rows through the same handlers, recording each outcome.</summary>
    /// <param name="plan">The plan <see cref="PlanAsync"/> produced.</param>
    /// <param name="ct">Stops between findings.</param>
    public async Task<IReadOnlyList<string>> ApplyAsync(TidyPlan plan, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(plan);
        for (var index = 0; index < plan.Changes.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            var change = plan.Changes[index];
            if (change.Kind != ChangeKind.WouldChange)
            {
                continue;
            }

            var updated = change.Check == "worktree-prunable"
                ? await PruneAsync(plan.Target.Report.Repo, apply: true, ct).ConfigureAwait(false)
                : await DecideAsync(
                    plan.Target.Report.Repo,
                    new Finding(change.Check, Severity.Info, change.Subject, string.Empty, Actionable: true),
                    plan.Target,
                    apply: true,
                    ct).ConfigureAwait(false);
            plan.Replace(index, updated);
        }

        return plan.Errors;
    }

    private Task<TidyAction> DecideAsync(string repo, Finding finding, TidyTarget target, bool apply, CancellationToken cancellationToken) =>
        TidyChecks.Branch.Contains(finding.Check)
            ? BranchAsync(repo, finding, apply, target.MergedHeads, cancellationToken)
            : TidyChecks.Worktree.Contains(finding.Check)
                ? WorktreeAsync(repo, finding, apply, target.Force, target.MergedHeads, target.Busy, cancellationToken)
                : finding.Check == "temp-stale"
                    ? TempAsync(repo, finding, apply, target.Force, target.Busy, cancellationToken)
                    : Task.FromResult(new TidyAction(finding.Check, finding.Subject, TidyOutcome.Refused, "not a check tidy acts on"));

    private async Task<TidyAction> PruneAsync(string repo, bool apply, CancellationToken cancellationToken)
    {
        if (!apply)
        {
            return new TidyAction("worktree-prunable", repo, TidyOutcome.WouldClean, "git worktree prune");
        }

        var ran = await git.RunAsync(repo, [.. Trim.LongPaths, "worktree", "prune"], RemoveTimeout, cancellationToken: cancellationToken).ConfigureAwait(false);
        return new TidyAction("worktree-prunable", repo, ran.Ok ? TidyOutcome.Cleaned : TidyOutcome.Failed, Said(ran, "pruned"));
    }

    // Two signals, and needing both is the whole subtlety. A squash merge replaces a branch's commits with one new
    // commit on main, and deleting the remote branch strands the originals on no remote for good: measured on
    // design-system, all 12 branches whose upstream was deleted held 1-6 "local-only" commits, and all 12 had merged.
    // So a merged PR holding the tip authorises deletion when the commits look local-only.
    private async Task<TidyAction> BranchAsync(
        string repo,
        Finding finding,
        bool apply,
        IReadOnlyDictionary<string, PullRequest>? mergedHeads,
        CancellationToken cancellationToken)
    {
        var branch = finding.Subject;
        var trees = await Worktree.ListAsync(git, repo, cancellationToken).ConfigureAwait(false);
        if (trees.Any(tree => !tree.IsMain && tree.Branch == branch))
        {
            return new TidyAction(finding.Check, branch, TidyOutcome.Refused, "checked out in another worktree");
        }

        var orphaned = await OrphanedAsync(repo, $"refs/heads/{branch}").ConfigureAwait(false);
        var authority = "no commits outside a remote";
        if (orphaned is not 0)
        {
            if (await PullRequests.MergedPrAsync(git, repo, branch, mergedHeads).ConfigureAwait(false) is not { } pr)
            {
                return new TidyAction(finding.Check, branch, TidyOutcome.Refused, Stranded(orphaned, mergedHeads));
            }

            authority = $"squash-merged in #{pr}";
        }

        // -D rather than -d: under a squash policy git cannot see that the work landed, so -d refuses a branch just proved safe.
        if (!apply)
        {
            return new TidyAction(finding.Check, branch, TidyOutcome.WouldClean, $"{authority} · git branch -D {branch}");
        }

        var ran = await git.RunAsync(repo, [.. Trim.LongPaths, "branch", "-D", branch], cancellationToken: cancellationToken).ConfigureAwait(false);
        return new TidyAction(finding.Check, branch, ran.Ok ? TidyOutcome.Cleaned : TidyOutcome.Failed, Said(ran, authority));
    }

    // The same two signals as a branch. Unborn, foreign, nested and in-use refusals hold under force: each is a case where
    // the usual signals read wrong rather than merely cautious.
    private async Task<TidyAction> WorktreeAsync(
        string repo,
        Finding finding,
        bool apply,
        bool force,
        IReadOnlyDictionary<string, PullRequest>? mergedHeads,
        Busy? busy,
        CancellationToken cancellationToken)
    {
        var path = finding.Subject;
        var resolved = PathResolution.Resolve(path);
        var trees = await Worktree.ListAsync(git, repo, cancellationToken).ConfigureAwait(false);
        var tree = trees.FirstOrDefault(t => !t.IsMain && PathResolution.Same(PathResolution.Resolve(t.Path), resolved));
        if (tree is null && Guard(repo, path) is { Length: > 0 } outside)
        {
            return new TidyAction(finding.Check, path, TidyOutcome.Refused, outside);
        }

        var facts = tree is null ? null : await ReadFactsAsync(repo, tree, busy, cancellationToken).ConfigureAwait(false);
        if (Refusal(facts, force) is { Length: > 0 } refused)
        {
            return new TidyAction(finding.Check, path, TidyOutcome.Refused, refused);
        }

        if (busy?.Refusal(path, force) is { Length: > 0 } inUse)
        {
            return new TidyAction(finding.Check, path, TidyOutcome.Refused, inUse);
        }

        var dirty = facts is not null
            ? facts.HasUncommittedChanges
            : (await git.LinesAsync(path, "status", "--porcelain").ConfigureAwait(false)) is null or { Count: > 0 };
        if (!force && dirty)
        {
            return new TidyAction(finding.Check, path, TidyOutcome.Refused, "uncommitted changes");
        }

        var orphaned = force ? 0 : await OrphanedAsync(path, "HEAD").ConfigureAwait(false);
        if (orphaned is not 0
            && (tree is not { Branch.Length: > 0 } || await PullRequests.MergedPrAsync(git, repo, tree.Branch, mergedHeads).ConfigureAwait(false) is null))
        {
            return new TidyAction(finding.Check, path, TidyOutcome.Refused, Stranded(orphaned, mergedHeads));
        }

        if (Holds(path, resolved, trees) is { Length: > 0 } holds)
        {
            return new TidyAction(finding.Check, path, TidyOutcome.Refused, holds);
        }

        if (Unrecorded(facts) is { Length: > 0 } unrecorded)
        {
            return new TidyAction(finding.Check, path, TidyOutcome.Refused, unrecorded);
        }

        if (!apply)
        {
            return new TidyAction(finding.Check, path, TidyOutcome.WouldClean, $"git worktree remove {path}");
        }

        var ran = await git.RunAsync(repo, [.. Trim.LongPaths, "worktree", "remove", path], RemoveTimeout, cancellationToken: cancellationToken).ConfigureAwait(false);
        return new TidyAction(finding.Check, path, ran.Ok ? TidyOutcome.Cleaned : TidyOutcome.Failed, Said(ran, "removed"));
    }

    // The shared decision plus this command's policy: a worktree goes only when it holds nothing a flag did not agree
    // to lose. --force covers commits a coverage proof already placed on a remote; it never clears a hard reason.
    private static string Refusal(WorktreeFacts? facts, bool force)
    {
        if (facts is null)
        {
            return string.Empty;
        }

        var reasons = WorktreeRemovalJudge.Judge(facts, new RemovalPolicy(DiscardUnpushedCommits: force)).Reasons;
        return facts.OwnershipUnreadable ? "cannot read this clone's git dir"
            : reasons.Contains(RemovalRefusalReason.ForeignClone) ? "belongs to another clone — see worktree-foreign-owner"
            : reasons.Contains(RemovalRefusalReason.UnbornHead) ? "unborn HEAD — see worktree-unborn-head"
            : reasons.Contains(RemovalRefusalReason.Locked) ? (facts.LockReason.Length > 0 ? $"locked: {facts.LockReason}" : "locked")
            : reasons.Contains(RemovalRefusalReason.InUse) ? "a process is working in it (a shell, agent or Orca terminal)"
            : string.Empty;
    }

    // What only this worktree holds: ignored notes, then commits only a reflog names. Checked after a nested worktree or
    // checkout, so a clone inside its ignored temp/ is reported as what it is.
    private static string Unrecorded(WorktreeFacts? facts)
    {
        if (facts is null)
        {
            return string.Empty;
        }

        if (facts.HasIgnoredNotes)
        {
            var shown = string.Join(", ", facts.IgnoredNotes.Take(3));
            var more = facts.IgnoredNotes.Count > 3 ? " …" : string.Empty;
            return $"holds ignored files that do not regenerate: {shown}{more} — keep them or remove them yourself";
        }

        return facts.ReflogUnreadable ? "cannot read its reflog, so commits only it knows about may be lost"
            : facts.ReflogOnlyCommitCount > 0 ? $"{facts.ReflogOnlyCommitCount} commit(s) only a reflog names — push or keep the worktree"
            : string.Empty;
    }

    private async Task<WorktreeFacts> ReadFactsAsync(string repo, Worktree tree, Busy? busy, CancellationToken cancellationToken)
    {
        var facts = await _worktrees.ReadAsync(repo, tree, cancellationToken).ConfigureAwait(false);
        return facts with { InUse = busy?.WorkingIn(facts.Path) ?? false };
    }

    // git deletes a worktree's ignored files with it: a worktree, a clone or another repository's worktree in its temp/
    // would go too, whatever force says. A nested worktree carries a .git file, so it is named before the general test,
    // which walks every folder and so runs last.
    private static string Holds(string path, string resolved, IReadOnlyList<Worktree> trees) =>
        trees.Any(other => Trim.IsStrictlyBelow(PathResolution.Resolve(other.Path), resolved)) ? "holds a worktree"
        : TreeMeasure.HasCheckoutBelow(path, ownGit: true) ? "holds a git checkout"
        : string.Empty;

    private async Task<TidyAction> TempAsync(string repo, Finding finding, bool apply, bool force, Busy? busy, CancellationToken cancellationToken)
    {
        var path = finding.Subject;
        var link = new FileInfo(path).LinkTarget is not null;
        var refusal = Guard(repo, path) is { Length: > 0 } outside ? outside
            : !link && TreeMeasure.HasCheckoutBelow(path) ? "holds a git checkout"
            : busy?.Refusal(path, force) ?? string.Empty;
        if (refusal.Length > 0)
        {
            return new TidyAction(finding.Check, path, TidyOutcome.Refused, refusal);
        }

        if (!apply)
        {
            return new TidyAction(finding.Check, path, TidyOutcome.WouldClean, $"rm -rf {path}");
        }

        try
        {
            if (!link)
            {
                await removal.RemoveAsync(path, cancellationToken).ConfigureAwait(false);
            }
            else if (Directory.Exists(path))
            {
                Directory.Delete(path); // the link itself: never followed
            }
            else
            {
                File.Delete(path);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new TidyAction(finding.Check, path, TidyOutcome.Failed, e.Message);
        }

        return new TidyAction(finding.Check, path, TidyOutcome.Cleaned, "removed");
    }

    // Only paths inside the clone are touched: a temp entry resolving outside it, a link to somewhere real, is refused.
    private static string Guard(string repo, string path)
    {
        var resolved = PathResolution.Resolve(path);
        var root = PathResolution.Resolve(repo);
        return PathResolution.Same(resolved, root) ? "is the repo itself"
            : !PathResolution.IsUnder(resolved, root) ? "resolves outside the repo"
            : string.Empty;
    }

    // Commits reachable from `revision` on no remote; null when git cannot count them, which never reads as none.
    private async Task<int?> OrphanedAsync(string cwd, string revision)
    {
        var ran = await git.RunAsync(cwd, ["rev-list", "--count", revision, "--not", "--remotes"]).ConfigureAwait(false);
        return ran.Ok && int.TryParse(ran.Out.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var count) ? count : null;
    }

    private static string Stranded(int? orphaned, IReadOnlyDictionary<string, PullRequest>? mergedHeads)
    {
        var detail = orphaned is { } count
            ? string.Create(CultureInfo.InvariantCulture, $"{count} commit(s) on no remote")
            : "git cannot count its commits on no remote";
        return mergedHeads is null ? $"{detail} — pass --merged to ask GitHub" : detail;
    }

    private static string Said(Ran ran, string otherwise) =>
        (ran.Out + ran.Err).Trim() is { Length: > 0 } said ? said : otherwise;
}
