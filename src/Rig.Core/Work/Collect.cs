using System.Diagnostics;

namespace Rig.Core.WorkItems;

/// <summary>What one collection should do.</summary>
/// <param name="Root">The code root holding every clone.</param>
public sealed record WorkCollectOptions(string Root)
{
    /// <summary>How far back to read Claude sessions.</summary>
    public TimeSpan SessionWindow { get; init; } = TimeSpan.FromDays(30);

    /// <summary>How long without activity before an item counts as stale.</summary>
    public TimeSpan StaleAfter { get; init; } = WorkJoin.DefaultStaleAfter;

    /// <summary>Skip GitHub entirely; every item's lookup is skipped.</summary>
    public bool Offline { get; init; }

    /// <summary>Ignore the PR cache and the clone-list cache.</summary>
    public bool Refresh { get; init; }

    /// <summary>Run <c>git status</c> per checkout for dirty state.</summary>
    public bool Probe { get; init; } = true;
}

/// <summary>Seconds per phase. Phases can overlap, so they need not add up to the wall clock.</summary>
public sealed class WorkTimings
{
    private readonly object _gate = new();
    private readonly List<KeyValuePair<string, double>> _phases = [];

    /// <summary>The phases recorded so far, in completion order.</summary>
    public IReadOnlyList<KeyValuePair<string, double>> Phases
    {
        get
        {
            lock (_gate)
            {
                return [.. _phases];
            }
        }
    }

    /// <summary>Runs <paramref name="work"/>, recording how long it took under <paramref name="name"/>.</summary>
    /// <param name="name">The phase name.</param>
    /// <param name="work">The work to measure.</param>
    public T Measure<T>(string name, Func<T> work)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(work);
        var started = Stopwatch.GetTimestamp();
        try
        {
            return work();
        }
        finally
        {
            Record(name, Stopwatch.GetElapsedTime(started).TotalSeconds);
        }
    }

    /// <summary>Runs <paramref name="work"/>, recording how long it took under <paramref name="name"/>.</summary>
    /// <param name="name">The phase name.</param>
    /// <param name="work">The work to measure.</param>
    public async Task<T> MeasureAsync<T>(string name, Func<Task<T>> work)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(work);
        var started = Stopwatch.GetTimestamp();
        try
        {
            return await work().ConfigureAwait(false);
        }
        finally
        {
            Record(name, Stopwatch.GetElapsedTime(started).TotalSeconds);
        }
    }

    private void Record(string name, double seconds)
    {
        lock (_gate)
        {
            _phases.Add(new KeyValuePair<string, double>(name, seconds));
        }
    }
}

/// <summary>A report and the timings of the collection that built it.</summary>
/// <param name="Report">The unfiltered report.</param>
/// <param name="Timings">Seconds per phase.</param>
/// <param name="Total">Wall-clock seconds for the whole collection.</param>
public sealed record Collected(WorkReport Report, WorkTimings Timings, double Total);

/// <summary>
/// Gathers every source and joins them into a <see cref="WorkReport"/>. Ported from
/// <c>desktop_manager/work/collect.py</c>; sessions and git refs overlap, and <c>git status</c> overlaps the GitHub
/// lookups.
/// </summary>
/// <param name="git">Reads the estate's git facts.</param>
/// <param name="prs">Looks up pull requests.</param>
/// <param name="sessions">Reads Claude Code sessions.</param>
/// <param name="folders">Answers whether a session's directory exists and where an outside folder lives.</param>
/// <param name="time">The clock the report and cache ages are read from.</param>
public sealed class WorkCollector(
    IWorkGitFacts git,
    IWorkPrSource prs,
    IWorkSessionSource sessions,
    IWorkFolderProbe folders,
    TimeProvider time)
{
    /// <summary>Non-trunk branches per estate repo: those checked out, plus those holding a session.</summary>
    /// <param name="repos">The estate's repos.</param>
    /// <param name="extra">Session branches per repo.</param>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> PrBranches(
        IReadOnlyList<RepoFacts> repos,
        IReadOnlyDictionary<string, IReadOnlySet<string>> extra)
    {
        ArgumentNullException.ThrowIfNull(repos);
        ArgumentNullException.ThrowIfNull(extra);
        var wanted = new Dictionary<string, IReadOnlyList<string>>(WorkPaths.Comparer);
        foreach (var repo in repos)
        {
            var branches = repo.Checkouts.Select(checkout => checkout.Branch).Where(branch => branch.Length > 0).ToHashSet(StringComparer.Ordinal);
            if (extra.TryGetValue(repo.Path, out var sessionBranches))
            {
                branches.UnionWith(sessionBranches);
            }

            branches.Remove(repo.DefaultBranch);
            branches.Remove(WorkJoin.DetachedBranch);
            if (branches.Count > 0)
            {
                wanted[repo.Path] = [.. branches.OrderBy(branch => branch, StringComparer.Ordinal)];
            }
        }

        return wanted;
    }

    /// <summary>Collects the whole estate.</summary>
    /// <param name="options">What to collect and how.</param>
    /// <param name="cancellationToken">Stops the collection.</param>
    public async Task<Collected> CollectAsync(WorkCollectOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var started = Stopwatch.GetTimestamp();
        var moment = time.GetUtcNow();
        var timings = new WorkTimings();
        var scanTask = timings.MeasureAsync("sessions", () => sessions.ReadAsync(moment - options.SessionWindow, cancellationToken));
        var repos = await timings.MeasureAsync("git refs", () => git.ReadEstateAsync(options.Root, options.Refresh, cancellationToken)).ConfigureAwait(false);
        var scan = await scanTask.ConfigureAwait(false);

        var index = new CheckoutIndex(repos);
        var existsCache = new Dictionary<string, bool>(StringComparer.Ordinal);
        var resolveCache = new Dictionary<string, Attribution?>(StringComparer.Ordinal);

        bool Exists(string cwd)
        {
            if (!existsCache.TryGetValue(cwd, out var known))
            {
                known = folders.Exists(cwd);
                existsCache[cwd] = known;
            }

            return known;
        }

        Attribution? Resolve(string cwd)
        {
            if (!resolveCache.TryGetValue(cwd, out var known))
            {
                known = folders.Resolve(cwd);
                resolveCache[cwd] = known;
            }

            return known;
        }

        var attributed = timings.Measure(
            "attribute",
            () => scan.Sessions.Select(session => new AttributedSession(session, WorkJoin.Attribute(session, index, Exists, Resolve))).ToList());
        var sessionBranches = new Dictionary<string, HashSet<string>>(WorkPaths.Comparer);
        foreach (var item in attributed)
        {
            if (item.Attribution.RepoPath is { } path && item.Attribution.Branch.Length > 0)
            {
                if (!sessionBranches.TryGetValue(path, out var branches))
                {
                    branches = new HashSet<string>(StringComparer.Ordinal);
                    sessionBranches[path] = branches;
                }

                branches.Add(item.Attribution.Branch);
            }
        }

        var probeTask = options.Probe
            ? timings.MeasureAsync("git status", () => git.ProbeCheckoutsAsync(repos, cancellationToken))
            : null;
        var extra = sessionBranches.ToDictionary(pair => pair.Key, pair => (IReadOnlySet<string>)pair.Value, WorkPaths.Comparer);
        var prResults = options.Offline
            ? PrResults.None()
            : await LookupPrsAsync(repos, extra, options, moment, timings, cancellationToken).ConfigureAwait(false);
        if (probeTask is not null)
        {
            repos = await probeTask.ConfigureAwait(false);
        }

        var items = WorkJoin.BuildItems(new JoinInputs(repos, attributed, prResults.Prs, prResults.Lookups), moment, options.StaleAfter);
        var report = new WorkReport(moment, options.Root, items) { Warnings = prResults.Warnings };
        return new Collected(report, timings, Stopwatch.GetElapsedTime(started).TotalSeconds);
    }

    private async Task<PrResults> LookupPrsAsync(
        IReadOnlyList<RepoFacts> repos,
        IReadOnlyDictionary<string, IReadOnlySet<string>> sessionBranches,
        WorkCollectOptions options,
        DateTimeOffset now,
        WorkTimings timings,
        CancellationToken cancellationToken)
    {
        var wanted = PrBranches(repos, sessionBranches);
        var slugs = await timings.MeasureAsync("pr slugs", () => git.ReadSlugsAsync([.. wanted.Keys], cancellationToken)).ConfigureAwait(false);
        var requests = wanted
            .Select(pair => new PrRequest(pair.Key, slugs.TryGetValue(pair.Key, out var slug) ? slug : string.Empty, pair.Value))
            .ToList();
        return await timings.MeasureAsync("github", () => prs.FetchAsync(requests, options.Refresh, now, cancellationToken)).ConfigureAwait(false);
    }
}
