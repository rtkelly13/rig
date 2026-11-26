using Rig.Core.Processes;
using Rig.Core.Reconciliation;

namespace Rig.Core.Estate;

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
/// <see cref="RootCheckoutObserver"/> reads one checkout, and <see cref="FastForwardPlanner"/> applies the safety rules
/// without I/O. Planning fetches remote-tracking refs only; it never touches a working tree.
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
    /// <summary>How long one fast-forward merge may take.</summary>
    public static readonly TimeSpan MergeTimeout = TimeSpan.FromSeconds(60);

    private readonly RootCheckoutObserver _observer = new(git);

    /// <inheritdoc />
    public async Task<FastForwardPlan> PlanAsync(FastForwardTarget target, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        var changes = new List<FastForwardChange>(target.Repos.Count);
        var errors = new List<string>();
        foreach (var repo in target.Repos)
        {
            var change = await ObserveAsync(repo, target.RefreshRemote, ct).ConfigureAwait(false);
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
            var refreshed = await ObserveAsync(change.Repo, refreshRemote: true, ct).ConfigureAwait(false);
            if (refreshed.Kind == ChangeKind.Error)
            {
                change.Kind = ChangeKind.Error;
                change.Detail = refreshed.Detail;
                errors.Add($"{change.Repo}: {refreshed.Detail}");
                continue;
            }

            if (refreshed.Kind == ChangeKind.Refused)
            {
                change.Kind = ChangeKind.Refused;
                change.Detail = $"{refreshed.Detail} (state changed before apply)";
                continue;
            }

            if (refreshed.Kind == ChangeKind.Aligned)
            {
                change.Kind = ChangeKind.Aligned;
                change.Detail = "up to date";
                continue;
            }

            var merge = await git.RunAsync(
                change.Repo,
                ["merge", "--ff-only", refreshed.Target],
                MergeTimeout,
                cancellationToken: ct).ConfigureAwait(false);
            if (merge.Ok)
            {
                change.Kind = ChangeKind.Changed;
                change.Detail = $"fast-forwarded to `{refreshed.Target}`";
                continue;
            }

            change.Kind = ChangeKind.Error;
            change.Detail = LastLine(merge.Err) ?? $"git merge --ff-only {refreshed.Target} failed";
            errors.Add($"{change.Repo}: {change.Detail}");
        }

        return errors;
    }

    private async Task<FastForwardChange> ObserveAsync(string repo, bool refreshRemote, CancellationToken ct)
    {
        var facts = await _observer.ObserveAsync(repo, refreshRemote, ct).ConfigureAwait(false);
        return new FastForwardChange(repo, FastForwardPlanner.Decide(facts));
    }

    private static string? LastLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
}
