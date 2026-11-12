namespace Rig.Core.Workload;

/// <summary>The thresholds and caps a sample applies.</summary>
/// <param name="LargeMb">A process at or over this much private memory or working set is reported in <c>large</c>.</param>
/// <param name="BurstOver">A name with more live copies than this is reported in <c>bursts</c>.</param>
/// <param name="TopApps">Apps named in <c>apps</c>; the rest join <c>other</c>.</param>
/// <param name="MaxLarge">At most this many <c>large</c> entries.</param>
/// <param name="MaxBursts">At most this many <c>bursts</c>.</param>
/// <param name="MaxChildren">At most this many names in <c>claude.children</c>.</param>
/// <param name="CommandChars">Command lines are cut to this many characters.</param>
public sealed record SampleLimits(
    int LargeMb = 1024, int BurstOver = 8, int TopApps = 10, int MaxLarge = 10, int MaxBursts = 10, int MaxChildren = 10, int CommandChars = 160)
{
    /// <summary>The limits <c>workload sample</c> uses.</summary>
    public static SampleLimits Default { get; } = new();
}

/// <summary>
/// Turns a process snapshot into a <see cref="WorkloadSample"/>. Each process belongs to the app of its nearest ancestor,
/// or itself, that the app list matches, so a build started from a Claude session counts as Claude Code and a Chrome
/// renderer as Chrome. A parent that started after its child is not its parent (Windows reuses process ids), so the walk
/// stops there. Pure: the snapshot and the command lines come in, the sample goes out.
/// </summary>
public static class Attribution
{
    /// <summary>What a process no app owns is counted as.</summary>
    public const string Other = "other";

    private const long Mib = 1024 * 1024;
    private const int MaxDepth = 256;

    /// <summary>The pids a sample reports in <c>large</c>, whose command lines it needs.</summary>
    public static IReadOnlyList<int> LargePids(ProcessSnapshot snapshot, SampleLimits limits)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(limits);
        return [.. Largest(snapshot.Processes, limits).Select(process => process.Pid)];
    }

    /// <summary>The sample for <paramref name="snapshot"/>, taken at <paramref name="now"/> on <paramref name="host"/>.</summary>
    public static WorkloadSample Build(
        ProcessSnapshot snapshot, AppRules rules, IReadOnlyDictionary<int, string> commands, SampleLimits limits, DateTimeOffset now, string host, string os)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(limits);
        var tree = new Tree(snapshot.Processes, rules);
        var processes = snapshot.Processes;
        return new WorkloadSample(
            1,
            Seconds(now.UtcDateTime),
            host,
            os,
            new RamUse(Mb(snapshot.Memory.TotalBytes), Mb(snapshot.Memory.AvailableBytes)),
            new ProcessTotals(processes.Count, Mb(processes.Sum(p => p.PrivateBytes)), Mb(processes.Sum(p => p.WorkingSetBytes))),
            Apps(tree, limits),
            Sessions(tree, limits),
            [.. Largest(processes, limits).Select(p => new LargeProcess(
                p.Pid, p.Name, tree.App(p), Mb(p.PrivateBytes), Mb(p.WorkingSetBytes), p.Started is { } started ? Seconds(started) : null, CommandLines.Safe(commands.GetValueOrDefault(p.Pid, string.Empty), limits.CommandChars)))],
            Bursts(tree, rules, limits));
    }

    /// <summary>The middle of <paramref name="values"/> (the lower middle for an even count).</summary>
    public static Spread? SpreadOf(IReadOnlyCollection<long> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count == 0)
        {
            return null;
        }

        var sorted = values.Order().ToList();
        return new Spread(sorted[0], sorted[(sorted.Count - 1) / 2], sorted[^1]);
    }

    private static DateTime Seconds(DateTime utc) => new(utc.Ticks - (utc.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Utc);

    private static long Mb(long bytes) => (bytes + (Mib / 2)) / Mib;

    private static long Weight(ProcessRow process) => Math.Max(process.PrivateBytes, process.WorkingSetBytes);

    private static IEnumerable<ProcessRow> Largest(IEnumerable<ProcessRow> processes, SampleLimits limits) =>
        processes.Where(p => Weight(p) >= limits.LargeMb * Mib).OrderByDescending(Weight).ThenBy(p => p.Pid).Take(limits.MaxLarge);

    private static List<AppUse> Apps(Tree tree, SampleLimits limits)
    {
        var byApp = tree.Processes
            .GroupBy(tree.App)
            .Select(group => (Use: new AppUse(group.Key, group.Count(), Mb(group.Sum(p => p.PrivateBytes)), Mb(group.Sum(p => p.WorkingSetBytes))), Weight: group.Sum(Weight)))
            .OrderByDescending(app => app.Weight)
            .ThenBy(app => app.Use.App, StringComparer.Ordinal)
            .ToList();
        var named = byApp.Where(app => app.Use.App != Other).Take(limits.TopApps).Select(app => app.Use).ToList();
        var rest = byApp.Select(app => app.Use).Where(use => !named.Contains(use)).ToList();
        if (rest.Count > 0)
        {
            named.Add(new AppUse(Other, rest.Sum(use => use.Processes), rest.Sum(use => use.PrivateMb), rest.Sum(use => use.WorkingSetMb)));
        }

        return named;
    }

    private static SessionUse Sessions(Tree tree, SampleLimits limits)
    {
        var members = tree.Processes
            .Select(process => (Process: process, Root: tree.SessionRoot(process)))
            .Where(member => member.Root is not null)
            .GroupBy(member => member.Root!.Pid)
            .ToList();
        var children = members
            .SelectMany(group => group.Where(member => member.Process.Pid != group.Key).Select(member => member.Process.Name))
            .GroupBy(name => name, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .Take(limits.MaxChildren)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        return new SessionUse(
            members.Count,
            SpreadOf([.. members.Select(group => Mb(group.First(member => member.Process.Pid == group.Key).Process.PrivateBytes))]),
            SpreadOf([.. members.Select(group => Mb(group.Sum(member => member.Process.PrivateBytes)))]),
            children);
    }

    private static List<Burst> Bursts(Tree tree, AppRules rules, SampleLimits limits) =>
        [.. tree.Processes
            .Where(process => !rules.BurstIgnored(process.Name))
            .GroupBy(process => process.Name, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > limits.BurstOver)
            .Select(group => new Burst(
                group.Key,
                group.Count(),
                group.GroupBy(tree.App).OrderByDescending(owner => owner.Count()).ThenBy(owner => owner.Key, StringComparer.Ordinal).First().Key,
                Mb(group.Sum(process => process.PrivateBytes))))
            .OrderByDescending(burst => burst.Count)
            .ThenBy(burst => burst.Name, StringComparer.Ordinal)
            .Take(limits.MaxBursts)];

    // The process forest: each process's real parent, owning app and session.
    private sealed class Tree
    {
        private readonly Dictionary<int, ProcessRow> byPid;
        private readonly AppRules rules;
        private readonly Dictionary<int, string> owners = [];

        public Tree(IReadOnlyList<ProcessRow> processes, AppRules rules)
        {
            Processes = processes;
            this.rules = rules;
            byPid = processes.GroupBy(p => p.Pid).ToDictionary(group => group.Key, group => group.First());
        }

        public IReadOnlyList<ProcessRow> Processes { get; }

        // The app of the nearest ancestor-or-self the list matches, or Other.
        public string App(ProcessRow process)
        {
            if (owners.TryGetValue(process.Pid, out var known))
            {
                return known;
            }

            var chain = new List<int>();
            string app = Other;
            for (var current = process; current is not null && chain.Count < MaxDepth; current = Parent(current))
            {
                if (owners.TryGetValue(current.Pid, out var found))
                {
                    app = found;
                    break;
                }

                chain.Add(current.Pid);
                if (rules.Match(current) is { } rule)
                {
                    app = rule.App;
                    break;
                }
            }

            foreach (var pid in chain)
            {
                owners[pid] = app;
            }

            return app;
        }

        // The outermost sessions-app process at or above this one, or null when it runs in no session.
        public ProcessRow? SessionRoot(ProcessRow process)
        {
            ProcessRow? root = null;
            var depth = 0;
            for (var current = process; current is not null && depth < MaxDepth; current = Parent(current), depth++)
            {
                if (rules.Match(current) is { Sessions: true })
                {
                    root = current;
                }
            }

            return root;
        }

        private ProcessRow? Parent(ProcessRow child) =>
            child.ParentPid != child.Pid
            && byPid.TryGetValue(child.ParentPid, out var parent)
            && !(parent.Started > child.Started)
                ? parent
                : null;
    }
}
