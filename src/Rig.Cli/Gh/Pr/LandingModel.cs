using Rig.Cli.Gh.Threads;

namespace Rig.Cli.Gh.Pr;

/// <summary>Where one check run or commit status stands.</summary>
internal enum CheckState
{
    Pending,
    Passed,
    Failed,
}

/// <summary>What a verdict means for the wait.</summary>
internal enum VerdictKind
{
    Merged,

    /// <summary>Still moving: keep polling.</summary>
    Waiting,

    /// <summary>Cannot land without someone acting: stop now.</summary>
    Stuck,

    /// <summary>Looks stuck, but GitHub may only be lagging: stop if it still looks this way after the settle time.</summary>
    Unsettled,
}

/// <summary>Why the wait is where it is, as <c>--json</c> prints it in <c>reason</c>.</summary>
internal static class Reason
{
    public const string Merged = "merged";

    // waiting
    public const string ChecksPending = "checks_pending";
    public const string InMergeQueue = "in_merge_queue";
    public const string MergeStateUnknown = "merge_state_unknown";
    public const string Ready = "ready";

    // stuck
    public const string Closed = "closed";
    public const string Draft = "draft";
    public const string CheckFailed = "check_failed";
    public const string ChangesRequested = "changes_requested";
    public const string ReviewRequired = "review_required";
    public const string UnresolvedThreads = "unresolved_threads";
    public const string Conflicts = "conflicts";

    // stuck once settled
    public const string RemovedFromMergeQueue = "removed_from_merge_queue";
    public const string AutoMergeOff = "auto_merge_off";
    public const string Behind = "behind";
    public const string Blocked = "blocked";

    /// <summary>GitHub could not be read at all.</summary>
    public const string GitHubError = "github_error";
}

/// <summary>One check run or commit status on the PR's head commit. <see cref="Result"/> is GitHub's own word for it.</summary>
internal sealed record Check(string Name, CheckState State, bool Required, string Result);

/// <summary>The PR's place in the merge queue. <see cref="Position"/> counts from 1 at the front.</summary>
internal sealed record QueueEntry(int Position, string State);

/// <summary>The pull request as one GraphQL read sees it.</summary>
internal sealed record Snapshot(
    string State,
    bool Draft,
    string Mergeable,
    string MergeState,
    string ReviewDecision,
    bool AutoMerge,
    QueueEntry? Queue,
    IReadOnlyList<Check> Checks,
    string Base)
{
    public string Url { get; init; } = string.Empty;

    /// <summary>Classic branch protection on the base branch requires resolved conversations.</summary>
    public bool ClassicThreadResolution { get; init; }

    /// <summary>Check names classic branch protection requires.</summary>
    public IReadOnlyList<string> ClassicRequiredChecks { get; init; } = [];
}

/// <summary>What the base branch's rulesets demand. <see cref="ThreadResolution"/> is null when they could not be read.</summary>
internal sealed record Rules(bool? ThreadResolution, IReadOnlyList<string> RequiredChecks);

/// <summary>Everything one poll saw. <see cref="Threads"/> holds the unresolved review threads only.</summary>
internal sealed record Observation(Snapshot Snapshot, Rules Rules, IReadOnlyList<ReviewThread> Threads)
{
    /// <summary>Whether unresolved threads block the merge; null when the rulesets could not be read.</summary>
    public bool? ThreadsRequired => Snapshot.ClassicThreadResolution ? true : Rules.ThreadResolution;

    /// <summary>Required checks that have not reported on the head commit yet, so the rollup does not list them.</summary>
    public IReadOnlyList<string> MissingChecks
    {
        get
        {
            var reported = Snapshot.Checks.Select(check => check.Name).ToHashSet(StringComparer.Ordinal);
            return [.. Rules.RequiredChecks.Concat(Snapshot.ClassicRequiredChecks).Distinct(StringComparer.Ordinal).Where(name => !reported.Contains(name))];
        }
    }

    /// <summary>Checks still running, then the required ones that have not reported.</summary>
    public IReadOnlyList<string> PendingChecks =>
        [.. Snapshot.Checks.Where(check => check.State is CheckState.Pending).Select(check => check.Name), .. MissingChecks];

    /// <summary>Required checks that failed or were cancelled.</summary>
    public IReadOnlyList<string> FailedChecks =>
        [.. Snapshot.Checks.Where(check => check.Required && check.State is CheckState.Failed).Select(check => check.Name)];
}

/// <summary>What one observation means: the kind, the <see cref="Pr.Reason"/> and a sentence for a person.</summary>
internal sealed record Verdict(VerdictKind Kind, string Reason, string Message);

/// <summary>The pure decision of whether a pull request can still land. No I/O.</summary>
internal static class LandingModel
{
    /// <summary>At most this many names are spelled out in a message; the rest are counted.</summary>
    private const int NamesShown = 5;

    /// <summary>Merge states in which GitHub would merge (or enqueue) the PR itself once auto-merge is on.</summary>
    private static readonly HashSet<string> ReadyStates = new(StringComparer.Ordinal) { "CLEAN", "UNSTABLE", "HAS_HOOKS" };

    /// <summary>
    /// Decides whether the PR has landed, is still moving, or cannot land without someone acting. A cause GitHub names
    /// outright (a failed required check, an open thread the ruleset wants resolved, a requested change, a conflict) is
    /// <see cref="VerdictKind.Stuck"/> at once. A state that is stuck only if it lasts (BLOCKED or BEHIND with nothing
    /// pending, auto-merge gone) is <see cref="VerdictKind.Unsettled"/>, because GitHub takes a moment between the last
    /// check passing and merging or enqueuing the PR. <paramref name="wasQueued"/> says an earlier poll saw the PR in the
    /// merge queue, which is how leaving the queue is told from never having joined it.
    /// </summary>
    public static Verdict Assess(Observation seen, bool wasQueued = false)
    {
        ArgumentNullException.ThrowIfNull(seen);
        var pr = seen.Snapshot;
        if (pr.State == "MERGED")
        {
            return new Verdict(VerdictKind.Merged, Reason.Merged, "merged");
        }

        if (pr.State != "OPEN")
        {
            return Stuck(Reason.Closed, "the pull request was closed without merging");
        }

        if (NamedBlocker(seen) is { } named)
        {
            return named;
        }

        if (pr.Queue is { } queue)
        {
            return Waiting(Reason.InMergeQueue, $"in the merge queue at position {queue.Position} ({queue.State})");
        }

        if (wasQueued)
        {
            return Unsettled(
                Reason.RemovedFromMergeQueue,
                "removed from the merge queue (its merge-group checks failed, or someone dequeued it); requeue it");
        }

        if (!pr.AutoMerge)
        {
            var ready = ReadyStates.Contains(pr.MergeState) ? " although it is ready to merge" : string.Empty;
            return Unsettled(Reason.AutoMergeOff, $"auto-merge is off{ready}, so nothing will merge it");
        }

        var pending = seen.PendingChecks;
        return pending.Count > 0
            ? Waiting(Reason.ChecksPending, $"{pending.Count} check(s) pending: {Names(pending)}")
            : SettledState(seen);
    }

    /// <summary>A cause that stops the PR whatever else is still running.</summary>
    private static Verdict? NamedBlocker(Observation seen)
    {
        var pr = seen.Snapshot;
        if (pr.Draft)
        {
            return Stuck(Reason.Draft, "the pull request is a draft; mark it ready for review");
        }

        var failed = seen.FailedChecks;
        if (failed.Count > 0)
        {
            return Stuck(Reason.CheckFailed, $"{failed.Count} required check(s) failed or were cancelled: {Names(failed)}");
        }

        if (pr.ReviewDecision == "CHANGES_REQUESTED")
        {
            return Stuck(Reason.ChangesRequested, "a reviewer requested changes");
        }

        if (seen.Threads.Count > 0 && seen.ThreadsRequired == true)
        {
            return Stuck(
                Reason.UnresolvedThreads,
                $"{seen.Threads.Count} unresolved review thread(s), and the ruleset requires every one resolved");
        }

        if (pr.Mergeable == "CONFLICTING" || pr.MergeState == "DIRTY")
        {
            return Stuck(Reason.Conflicts, $"merge conflicts with {pr.Base}; rebase the branch");
        }

        return pr.ReviewDecision == "REVIEW_REQUIRED"
            ? Stuck(Reason.ReviewRequired, "an approving review is required and has not been given")
            : null;
    }

    /// <summary>The verdict once auto-merge is on and every check has finished: only the merge state is left to read.</summary>
    private static Verdict SettledState(Observation seen)
    {
        var pr = seen.Snapshot;
        if (pr.MergeState == "BLOCKED" && seen.Threads.Count > 0 && seen.ThreadsRequired is null)
        {
            return Stuck(
                Reason.UnresolvedThreads,
                $"blocked with {seen.Threads.Count} unresolved review thread(s) and nothing pending "
                + "(the base branch's rules could not be read, so this is the likely cause)");
        }

        return pr.MergeState switch
        {
            "BEHIND" => Unsettled(Reason.Behind, $"the branch is behind {pr.Base} and no check is pending; update it"),
            "BLOCKED" => Unsettled(
                Reason.Blocked,
                "GitHub reports the merge BLOCKED with no check pending and no cause this command can name; "
                + "open the PR to see which requirement is unmet"),
            _ when ReadyStates.Contains(pr.MergeState) =>
                Waiting(Reason.Ready, "every requirement is met; waiting for GitHub to merge or enqueue it"),
            _ => Waiting(Reason.MergeStateUnknown, "GitHub is still working out whether the PR can merge"),
        };
    }

    private static string Names(IReadOnlyList<string> names)
    {
        var shown = string.Join(", ", names.Take(NamesShown));
        return names.Count > NamesShown ? $"{shown} and {names.Count - NamesShown} more" : shown;
    }

    private static Verdict Stuck(string reason, string message) => new(VerdictKind.Stuck, reason, message);

    private static Verdict Waiting(string reason, string message) => new(VerdictKind.Waiting, reason, message);

    private static Verdict Unsettled(string reason, string message) => new(VerdictKind.Unsettled, reason, message);
}
