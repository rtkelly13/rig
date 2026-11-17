using System.Text.RegularExpressions;

namespace Rig.Core.WorkItems;

/// <summary>A state and the facts that decided it, most important first.</summary>
/// <param name="State">The derived state.</param>
/// <param name="Reasons">The facts behind it.</param>
public sealed record StateDecision(WorkState State, IReadOnlyList<string> Reasons);

/// <summary>One session and the (repo, branch) it was attributed to.</summary>
/// <param name="Session">The session.</param>
/// <param name="Attribution">Where it belongs.</param>
public sealed record AttributedSession(SessionFacts Session, Attribution Attribution);

/// <summary>Everything the join needs: repos, attributed sessions, PRs and PR lookup outcomes.</summary>
/// <param name="Repos">The estate's repos.</param>
/// <param name="Sessions">The sessions, already attributed.</param>
/// <param name="Prs">The newest PR per (repo, branch); absent means none.</param>
/// <param name="PrLookups">Per repo path; a repo missing here was not asked about.</param>
public sealed record JoinInputs(
    IReadOnlyList<RepoFacts> Repos,
    IReadOnlyList<AttributedSession> Sessions,
    IReadOnlyDictionary<BranchKey, PrInfo> Prs,
    IReadOnlyDictionary<string, PrLookup> PrLookups);

/// <summary>What the text view keeps. The JSON path applies the same filter.</summary>
public sealed record ItemFilter
{
    /// <summary>Only repos whose display name contains this text, case-insensitively.</summary>
    public string? Repo { get; init; }

    /// <summary>Only these states; empty means all.</summary>
    public IReadOnlySet<WorkState> States { get; init; } = new HashSet<WorkState>();

    /// <summary>Only items whose last activity is at or after this moment.</summary>
    public DateTimeOffset? Since { get; init; }

    /// <summary>Include trunk items with nothing going on.</summary>
    public bool IncludeQuietTrunk { get; init; }
}

/// <summary>
/// The pure joining logic: attribute sessions, build one item per (repo, branch), derive its state, sort and filter.
/// No subprocess, filesystem or network lives here.
/// </summary>
public static partial class WorkJoin
{
    /// <summary>How long without activity before a branch counts as stale.</summary>
    public static readonly TimeSpan DefaultStaleAfter = TimeSpan.FromDays(14);

    /// <summary>The key of the bucket holding sessions outside every known checkout.</summary>
    public const string UnattachedKey = "unattached";

    /// <summary>How a detached HEAD's branch is spelled.</summary>
    public const string DetachedBranch = "(detached)";

    /// <summary>The display order of states, most deserving of attention first.</summary>
    public static IReadOnlyList<WorkState> StateOrder { get; } =
    [
        WorkState.Active,
        WorkState.Merged,
        WorkState.Closed,
        WorkState.Stale,
        WorkState.Trunk,
        WorkState.Unattached,
    ];

    private static readonly HashSet<string> NotAProject = new(StringComparer.Ordinal)
    {
        "pr", "clone", "issue", "fix", "step", "part", "rc", "stack", "layer", "phase", "wave", "round", "deploy", "wt",
    };

    /// <summary>
    /// The first ticket id in a branch name, upper-cased: <c>feat/PROJ-1234-thing</c> and <c>proj-1234-thing</c> both give
    /// <c>PROJ-1234</c>. An upper-case id wins wherever it appears; a common word such as <c>pr</c> is never a project key.
    /// </summary>
    /// <param name="branch">The branch name, or null.</param>
    public static string? ParseTicket(string? branch)
    {
        if (string.IsNullOrEmpty(branch))
        {
            return null;
        }

        return new[] { Ticket(), LowerTicket() }
            .SelectMany(pattern => pattern.Matches(branch).Select(match => match.Groups[1].Value.ToUpperInvariant()))
            .FirstOrDefault(ticket => !NotAProject.Contains(ticket[..ticket.IndexOf('-', StringComparison.Ordinal)].ToLowerInvariant()));
    }

    /// <summary>
    /// One session's state, from the live registry, the end ledger and the transcript tail. A live registry entry wins:
    /// an idle one is waiting at the prompt, any other live status is working.
    /// </summary>
    /// <param name="liveStatus">The live registry's status, when it has one.</param>
    /// <param name="isLive">A live registry entry exists.</param>
    /// <param name="cleared">The end ledger records <c>/clear</c> with nothing after it.</param>
    /// <param name="exitedCleanly">The transcript ends with a clean exit.</param>
    /// <param name="turnState">The last turn: <c>answered</c>, <c>mid-tool</c> or <c>unanswered</c>.</param>
    public static WorkSessionState ClassifySession(string? liveStatus, bool isLive, bool cleared, bool exitedCleanly, string turnState)
    {
        ArgumentNullException.ThrowIfNull(turnState);
        return isLive ? (liveStatus == "idle" ? WorkSessionState.WaitingOnUser : WorkSessionState.Live)
            : cleared ? WorkSessionState.Cleared
            : exitedCleanly ? WorkSessionState.EndedClean
            : turnState is "mid-tool" or "unanswered" ? WorkSessionState.DiedMidTask
            : WorkSessionState.DiedIdle;
    }

    /// <summary>Which (repo, branch) a session belongs to, judged by its last working directory.</summary>
    /// <param name="session">The session.</param>
    /// <param name="index">Every known checkout.</param>
    /// <param name="exists">Whether a directory still exists.</param>
    /// <param name="resolveUnknown">The repo of a directory outside the estate, or null.</param>
    public static Attribution Attribute(
        SessionFacts session,
        CheckoutIndex index,
        Func<string, bool> exists,
        Func<string, Attribution?> resolveUnknown)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(exists);
        ArgumentNullException.ThrowIfNull(resolveUnknown);
        var cwd = string.IsNullOrEmpty(session.LastCwd) ? session.StartCwd : session.LastCwd;
        if (string.IsNullOrEmpty(cwd))
        {
            return new Attribution(null, Reason: "no working directory recorded");
        }

        return !exists(cwd) ? new Attribution(null, Reason: $"directory gone: {cwd}")
            : index.Find(cwd) is { } found ? new Attribution(found.RepoPath, found.Branch)
            : resolveUnknown(cwd) ?? new Attribution(null, Reason: $"not in a git checkout: {cwd}");
    }

    /// <summary>
    /// The lifecycle state, first rule that matches: trunk, merged, active signals, closed, recent activity, stale.
    /// </summary>
    /// <param name="isTrunk">The branch is the repo's default branch.</param>
    /// <param name="pr">The newest PR, or null.</param>
    /// <param name="checkouts">The checkouts holding the branch.</param>
    /// <param name="sessions">The sessions attributed to it, newest first.</param>
    /// <param name="branchRef">The local branch ref, or null.</param>
    /// <param name="lastActivity">The latest activity, or null.</param>
    /// <param name="now">The moment activity is measured from.</param>
    /// <param name="staleAfter">How old activity may be before the item is stale.</param>
    public static StateDecision DeriveState(
        bool isTrunk,
        PrInfo? pr,
        IReadOnlyList<Checkout> checkouts,
        IReadOnlyList<SessionFacts> sessions,
        BranchRef? branchRef,
        DateTimeOffset? lastActivity,
        DateTimeOffset now,
        TimeSpan staleAfter)
    {
        ArgumentNullException.ThrowIfNull(checkouts);
        ArgumentNullException.ThrowIfNull(sessions);
        var signals = SignalsOf(pr, checkouts, sessions, branchRef);
        if (isTrunk)
        {
            return new StateDecision(WorkState.Trunk, ["default branch", .. signals.All()]);
        }

        if (pr is not null && pr.Status == PrStatus.Merged)
        {
            return new StateDecision(WorkState.Merged, [$"PR #{pr.Number} merged", .. signals.CleanupBlockers()]);
        }

        if (signals.All() is { Count: > 0 } active)
        {
            return new StateDecision(WorkState.Active, active);
        }

        IReadOnlyList<string> gone = branchRef is { UpstreamGone: true } ? ["upstream gone"] : [];
        if (pr is not null && pr.Status == PrStatus.Closed)
        {
            return new StateDecision(WorkState.Closed, [$"PR #{pr.Number} closed unmerged", .. gone]);
        }

        if (lastActivity is { } activity && now - activity <= staleAfter)
        {
            return new StateDecision(WorkState.Active, [$"activity {Ago(now - activity)} ago", .. gone]);
        }

        return new StateDecision(WorkState.Stale, [lastActivity is null ? "no activity recorded" : $"no activity for {Ago(now - lastActivity.Value)}", .. gone]);
    }

    /// <summary>One item per (repo, branch) checked out anywhere or holding a session, plus the unattached bucket.</summary>
    /// <param name="inputs">The facts to join.</param>
    /// <param name="now">The moment activity is measured from.</param>
    /// <param name="staleAfter">How old activity may be before an item is stale.</param>
    public static IReadOnlyList<WorkItem> BuildItems(JoinInputs inputs, DateTimeOffset now, TimeSpan staleAfter)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        var views = Views(inputs);
        var byKey = inputs.Sessions
            .Where(attributed => attributed.Attribution.RepoPath is not null)
            .GroupBy(attributed => new BranchKey(attributed.Attribution.RepoPath!, BranchOrDetached(attributed.Attribution.Branch)), BranchKeyComparer.Instance)
            .ToDictionary(group => group.Key, group => group.Select(attributed => attributed.Session).ToList(), BranchKeyComparer.Instance);
        var unattached = inputs.Sessions.Where(attributed => attributed.Attribution.RepoPath is null).Select(attributed => attributed.Session).ToList();
        var items = Keys(inputs)
            .Select(key => Item(key, views[key.RepoPath], inputs, byKey.TryGetValue(key, out var sessions) ? sessions : [], now, staleAfter))
            .ToList();
        if (unattached.Count > 0)
        {
            items.Add(UnattachedItem(unattached));
        }

        return SortItems(items);
    }

    /// <summary>State order first, then most recent activity first, then key for stability.</summary>
    /// <param name="items">The items to order.</param>
    public static IReadOnlyList<WorkItem> SortItems(IEnumerable<WorkItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        return
        [
            .. items
                .OrderBy(item => (int)item.State)
                .ThenByDescending(item => item.LastActivity ?? DateTimeOffset.MinValue)
                .ThenBy(item => item.Key, StringComparer.Ordinal),
        ];
    }

    /// <summary>Whether <paramref name="item"/> is a default branch with no sessions and no uncommitted changes.</summary>
    /// <param name="item">The item.</param>
    public static bool IsQuietTrunk(WorkItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return item.State == WorkState.Trunk && item.Sessions.Count == 0 && !item.Dirty;
    }

    /// <summary>Keeps the items matching <paramref name="spec"/>, hiding quiet trunk items unless asked.</summary>
    /// <param name="items">The items to filter.</param>
    /// <param name="spec">What to keep.</param>
    public static IReadOnlyList<WorkItem> FilterItems(IEnumerable<WorkItem> items, ItemFilter spec)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(spec);
        return
        [
            .. items.Where(item =>
                (string.IsNullOrEmpty(spec.Repo) || (item.Repo is not null && item.Repo.Contains(spec.Repo, StringComparison.OrdinalIgnoreCase)))
                && (spec.States.Count == 0 || spec.States.Contains(item.State))
                && (spec.Since is not { } since || (item.LastActivity is not null && item.LastActivity >= since))
                && (spec.IncludeQuietTrunk || spec.States.Count > 0 || !IsQuietTrunk(item))),
        ];
    }

    /// <summary>How many items are in each state, in display order, including the states with none.</summary>
    /// <param name="items">The items to count.</param>
    public static IReadOnlyDictionary<WorkState, int> CountStates(IEnumerable<WorkItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var counts = items.CountBy(item => item.State).ToDictionary();
        return StateOrder.ToDictionary(state => state, state => counts.GetValueOrDefault(state));
    }

    private static Dictionary<string, RepoView> Views(JoinInputs inputs)
    {
        var views = new Dictionary<string, RepoView>(WorkPaths.Comparer);
        foreach (var repo in inputs.Repos)
        {
            views[repo.Path] = new RepoView(
                repo.Path,
                repo.Name,
                repo.DefaultBranch,
                repo.Checkouts,
                repo.Branches.ToDictionary(branch => branch.Name, StringComparer.Ordinal));
        }

        // A session can sit in a checkout outside the code root; give its repo a minimal view.
        foreach (var path in inputs.Sessions.Select(attributed => attributed.Attribution.RepoPath).OfType<string>().Where(path => !views.ContainsKey(path)))
        {
            views[path] = new RepoView(
                path,
                Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)),
                string.Empty,
                [],
                new Dictionary<string, BranchRef>(StringComparer.Ordinal));
        }

        return views;
    }

    private static List<BranchKey> Keys(JoinInputs inputs)
    {
        var located = inputs.Repos.SelectMany(repo => repo.Checkouts.Select(checkout => new BranchKey(repo.Path, checkout.Branch)));
        var sessions = inputs.Sessions
            .Where(attributed => attributed.Attribution.RepoPath is not null)
            .Select(attributed => new BranchKey(attributed.Attribution.RepoPath!, attributed.Attribution.Branch));
        return [.. located.Concat(sessions).Select(key => key with { Branch = BranchOrDetached(key.Branch) }).Distinct(BranchKeyComparer.Instance)];
    }

    private static WorkItem Item(
        BranchKey key,
        RepoView view,
        JoinInputs inputs,
        IReadOnlyList<SessionFacts> sessions,
        DateTimeOffset now,
        TimeSpan staleAfter)
    {
        var branch = key.Branch;
        var detached = branch == DetachedBranch;
        var checkouts = view.Checkouts.Where(checkout => BranchOrDetached(checkout.Branch) == branch).ToList();
        var ordered = OrderSessions(sessions);
        var branchRef = detached ? null : view.Branches.GetValueOrDefault(branch);
        var isTrunk = !detached && (view.DefaultBranch.Length > 0 ? branch == view.DefaultBranch : branch is "main" or "master");
        var pr = inputs.Prs.GetValueOrDefault(key);
        var lookup = isTrunk || detached ? PrLookup.Skipped : inputs.PrLookups.GetValueOrDefault(view.Path, PrLookup.Skipped);
        var lastActivity = Latest(branchRef?.LastCommit, ordered.Count > 0 ? ordered[0].LastActivity : null, pr?.UpdatedAt);
        var decision = DeriveState(isTrunk, pr, checkouts, ordered, branchRef, lastActivity, now, staleAfter);
        return new WorkItem(
            $"{view.Name}:{branch}",
            view.Name,
            view.Path,
            branch,
            ParseTicket(branch),
            isTrunk,
            decision.State,
            decision.Reasons,
            checkouts,
            branchRef,
            pr,
            lookup,
            ordered,
            lastActivity);
    }

    private static WorkItem UnattachedItem(IReadOnlyList<SessionFacts> sessions)
    {
        var ordered = OrderSessions(sessions);
        return new WorkItem(
            UnattachedKey,
            null,
            null,
            null,
            null,
            false,
            WorkState.Unattached,
            ["sessions outside any known checkout"],
            [],
            null,
            null,
            PrLookup.Skipped,
            ordered,
            ordered.Count > 0 ? ordered[0].LastActivity : null);
    }

    private static IReadOnlyList<SessionFacts> OrderSessions(IReadOnlyList<SessionFacts> sessions) =>
        [.. sessions.OrderByDescending(session => session.LastActivity ?? DateTimeOffset.MinValue)];

    private static DateTimeOffset? Latest(DateTimeOffset? first, DateTimeOffset? second, DateTimeOffset? third) => new[] { first, second, third }.Max();

    internal static string BranchOrDetached(string branch) => branch.Length > 0 ? branch : DetachedBranch;

    private static Signals SignalsOf(PrInfo? pr, IReadOnlyList<Checkout> checkouts, IReadOnlyList<SessionFacts> sessions, BranchRef? branchRef)
    {
        var running = sessions.Count(session => session.State.IsRunning());
        var unpushed = branchRef?.Unpushed ?? 0;
        return new Signals(
            pr is not null && pr.Status is PrStatus.Open or PrStatus.Draft ? $"{pr.Status.Wire()} PR #{pr.Number}" : null,
            running > 0 ? Plural(running, "running session") : null,
            checkouts.Any(checkout => checkout.Dirty == true) ? "uncommitted changes" : null,
            unpushed > 0 ? Plural(unpushed, "unpushed commit") : null);
    }

    private static string Plural(int count, string noun) => $"{count} {noun}{(count == 1 ? string.Empty : "s")}";

    internal static string Ago(TimeSpan elapsed)
    {
        var days = (int)elapsed.TotalDays;
        if (days >= 1)
        {
            return $"{days}d";
        }

        var hours = (int)(elapsed.TotalSeconds / 3600);
        return hours >= 1 ? $"{hours}h" : $"{(int)(elapsed.TotalSeconds / 60)}m";
    }

    private sealed record RepoView(
        string Path,
        string Name,
        string DefaultBranch,
        IReadOnlyList<Checkout> Checkouts,
        IReadOnlyDictionary<string, BranchRef> Branches);

    private sealed record Signals(string? OpenPr, string? Running, string? Dirty, string? Unpushed)
    {
        public IReadOnlyList<string> All() =>
            [.. new[] { OpenPr, Running, Dirty, Unpushed }.Where(signal => signal is not null).Select(signal => signal!)];

        public IReadOnlyList<string> CleanupBlockers() =>
            [.. new[] { Running, Dirty }.Where(signal => signal is not null).Select(signal => signal!)];
    }

    [GeneratedRegex(@"(?<![A-Za-z0-9])([A-Z][A-Z0-9]+-\d+)(?![0-9])")]
    private static partial Regex Ticket();

    [GeneratedRegex(@"(?<![A-Za-z0-9])([A-Za-z]{2,6}-\d{2,})(?![0-9])")]
    private static partial Regex LowerTicket();
}

/// <summary>Every known checkout path, longest first, for longest-prefix lookup of a session's working directory.</summary>
public sealed class CheckoutIndex
{
    private readonly List<LocatedCheckout> _located;

    /// <summary>Indexes every checkout of every repo.</summary>
    /// <param name="repos">The repos and their checkouts.</param>
    public CheckoutIndex(IEnumerable<RepoFacts> repos)
    {
        ArgumentNullException.ThrowIfNull(repos);
        // Longest first, so a worktree at <repo>/temp/<name> wins over the clone that contains it.
        _located =
        [
            .. repos
                .SelectMany(repo => repo.Checkouts.Select(checkout => new LocatedCheckout(repo.Path, WorkJoin.BranchOrDetached(checkout.Branch), WorkPaths.Normalise(checkout.Path))))
                .OrderByDescending(entry => entry.Normalised.Length),
        ];
    }

    /// <summary>The checkout holding <paramref name="cwd"/>, or null when none does.</summary>
    /// <param name="cwd">A session's working directory.</param>
    internal LocatedCheckout? Find(string cwd)
    {
        ArgumentNullException.ThrowIfNull(cwd);
        var target = WorkPaths.Normalise(cwd);
        return _located.Find(entry => WorkPaths.IsUnder(target, entry.Normalised));
    }
}

/// <summary>One indexed checkout: its repo, the branch it holds now, and its normalised path.</summary>
/// <param name="RepoPath">The clone's folder.</param>
/// <param name="Branch">The branch checked out there now.</param>
/// <param name="Normalised">The path in the form used for prefix comparison.</param>
internal sealed record LocatedCheckout(string RepoPath, string Branch, string Normalised);
