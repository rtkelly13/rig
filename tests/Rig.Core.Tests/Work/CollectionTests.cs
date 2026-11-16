using Rig.Core.WorkItems;

namespace Rig.Core.Tests.WorkItems;

public sealed class CollectionTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private const string Root = "/code";

    private static readonly string App = Path.Join(Root, "acme", "app");

    private static readonly string Lib = Path.Join(Root, "other", "lib");

    private static readonly string Feature = Path.Join(App, "temp", "feature");

    private const string Outside = "/elsewhere/app";

    [Fact]
    public async Task Collect_joins_every_source_and_records_every_phase()
    {
        var git = new FakeWorkGitFacts
        {
            Repos = Repos(),
            Slugs = new Dictionary<string, string> { [App] = "acme/app", [Lib] = "other/lib" },
        };
        var prs = new FakeWorkPrSource { Results = PrResults() };
        var sessions = new FakeWorkSessions(Sessions());
        var folders = Folders();
        var collector = new WorkCollector(git, prs, sessions, folders, new FixedClock(Now));

        var collected = await collector.CollectAsync(new WorkCollectOptions(Root) { Refresh = true }, Token);

        Assert.Equal(Now, collected.Report.GeneratedAt);
        Assert.Equal(Root, collected.Report.CodeRoot);
        Assert.Equal(["a warning"], collected.Report.Warnings);
        Assert.Equal(Root, git.Root);
        Assert.True(git.Refresh);
        Assert.Equal(1, git.EstateCalls);
        Assert.Equal(1, git.ProbeCalls);
        Assert.Equal(Now - TimeSpan.FromDays(30), sessions.Since);
        Assert.Equal([App, Lib], git.SlugRepos);
        Assert.Equal(Now, prs.Now);
        Assert.True(prs.Refresh);
        Assert.Equal(
            ["acme/app", "other/lib"],
            prs.Requests.Select(request => request.Slug));
        Assert.Equal(["feat/session-only", "feat/x"], prs.Requests[0].Branches);
        Assert.Equal(["feat/y"], prs.Requests[1].Branches);

        var phases = collected.Timings.Phases.Select(phase => phase.Key).ToList();
        Assert.Contains("sessions", phases);
        Assert.Contains("git refs", phases);
        Assert.Contains("attribute", phases);
        Assert.Contains("git status", phases);
        Assert.Contains("pr slugs", phases);
        Assert.Contains("github", phases);
        Assert.True(collected.Total >= 0);

        var keys = collected.Report.Items.Select(item => item.Key).ToList();
        Assert.Contains("acme/app:feat/x", keys);
        Assert.Contains("acme/app:feat/session-only", keys);
        Assert.Contains("other/lib:feat/y", keys);
        Assert.Contains("unattached", keys);
        var feature = collected.Report.Items.Single(item => item.Key == "acme/app:feat/x");
        Assert.Equal(WorkState.Merged, feature.State);
        Assert.True(feature.Dirty);
        Assert.Single(feature.RunningSessions);
    }

    [Fact]
    public async Task Collect_offline_asks_nothing_of_GitHub_and_skips_every_lookup()
    {
        var git = new FakeWorkGitFacts { Repos = Repos(), Slugs = Slugs() };
        var prs = new FakeWorkPrSource();
        var collector = new WorkCollector(git, prs, new FakeWorkSessions(), Folders(), new FixedClock(Now));

        var collected = await collector.CollectAsync(new WorkCollectOptions(Root) { Offline = true }, Token);

        Assert.Empty(prs.Requests);
        Assert.Empty(collected.Report.Warnings);
        Assert.All(
            collected.Report.Items.Where(item => item.State != WorkState.Trunk),
            item => Assert.Equal(PrLookup.Skipped, item.PrLookup));
    }

    [Fact]
    public async Task Collect_without_a_probe_leaves_dirty_unknown_and_skips_git_status()
    {
        var git = new FakeWorkGitFacts { Repos = Repos(), Slugs = Slugs() };
        var collector = new WorkCollector(git, new FakeWorkPrSource(), new FakeWorkSessions(), Folders(), new FixedClock(Now));

        var collected = await collector.CollectAsync(new WorkCollectOptions(Root) { Probe = false }, Token);

        Assert.Equal(0, git.ProbeCalls);
        Assert.All(
            collected.Report.Items.SelectMany(item => item.Checkouts).Where(checkout => !checkout.IsMainCheckout),
            checkout => Assert.Null(checkout.Dirty));
        Assert.DoesNotContain("git status", collected.Timings.Phases.Select(phase => phase.Key));
    }

    [Fact]
    public void PrBranches_includes_checked_out_and_session_branches_but_not_trunk_or_detached()
    {
        var wanted = WorkCollector.PrBranches(
            Repos(),
            new Dictionary<string, IReadOnlySet<string>>
            {
                [App] = new HashSet<string> { "feat/session-only", "main" },
                [Lib] = new HashSet<string> { "feat/y", "(detached)" },
            });

        Assert.Equal([App, Lib], wanted.Keys);
        Assert.Equal(["feat/session-only", "feat/x"], wanted[App]);
        Assert.Equal(["feat/y"], wanted[Lib]);
    }

    [Fact]
    public void FileSystemFolderProbe_checks_the_disk_and_delegates_the_resolver()
    {
        using var temp = new TempDir();
        var elsewhere = new Attribution("/other", "dev");
        var probe = new FileSystemFolderProbe(path => path == Outside ? elsewhere : null);

        Assert.True(probe.Exists(temp.Path));
        Assert.False(probe.Exists(Path.Join(temp.Path, "missing")));
        Assert.Equal(elsewhere, probe.Resolve(Outside));
        Assert.Null(probe.Resolve(Root));
    }

    private static IReadOnlyList<RepoFacts> Repos() =>
    [
        new RepoFacts(App, "acme/app", "main",
        [
            new Checkout(App, "main", IsMainCheckout: true, Dirty: false),
            new Checkout(Feature, "feat/x", IsMainCheckout: false, Dirty: null),
            new Checkout(Path.Join(App, "temp", "detached"), string.Empty, IsMainCheckout: false, Dirty: null),
        ])
        {
            Branches =
            [
                new BranchRef("main", Now - TimeSpan.FromDays(1), "origin/main", Ahead: 0, Behind: 0),
                new BranchRef("feat/x", Now - TimeSpan.FromDays(3), "origin/feat/x"),
            ],
        },
        new RepoFacts(Lib, "other/lib", "master",
        [
            new Checkout(Lib, "master", IsMainCheckout: true, Dirty: false),
            new Checkout(Path.Join(Lib, "temp", "y"), "feat/y", IsMainCheckout: false, Dirty: null),
        ])
        {
            Branches =
            [
                new BranchRef("master", Now - TimeSpan.FromDays(2), "origin/master", Ahead: 0, Behind: 0),
                new BranchRef("feat/y", Now - TimeSpan.FromHours(1), "origin/feat/y"),
            ],
        },
    ];

    private static IReadOnlyList<SessionFacts> Sessions() =>
    [
        new("live", "live", WorkSessionState.Live, Now - TimeSpan.FromMinutes(5), Feature, Feature, 2, 99),
        new("outside", "outside", WorkSessionState.EndedClean, Now - TimeSpan.FromHours(2), Outside, Outside, 1),
        new("stray", "stray", WorkSessionState.WaitingOnUser, Now - TimeSpan.FromMinutes(10), Root, Root, 3),
    ];

    private static FakeWorkFolders Folders() => new()
    {
        Existing = new HashSet<string>(StringComparer.Ordinal) { Feature, Outside, Root },
        Resolved = new Dictionary<string, Attribution?>(StringComparer.Ordinal)
        {
            [Outside] = new Attribution(App, "feat/session-only"),
        },
    };

    private static Dictionary<string, string> Slugs() => new Dictionary<string, string>
    {
        [App] = "acme/app",
        [Lib] = "other/lib",
    };

    private static PrResults PrResults() => new(
        new Dictionary<BranchKey, PrInfo>
        {
            [new BranchKey(App, "feat/x")] = new(12, PrStatus.Merged, "u12", Now - TimeSpan.FromDays(3)),
            [new BranchKey(Lib, "feat/y")] = new(34, PrStatus.Open, "u34", Now - TimeSpan.FromHours(1)),
        },
        new Dictionary<string, PrLookup> { [App] = PrLookup.Ok, [Lib] = PrLookup.Ok },
        ["a warning"]);
}
