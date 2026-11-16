using Rig.Core.Estate;
using Rig.Core.Processes;
using Rig.Core.Roots;
using Rig.Core.Tests.Fakes;
using Rig.Core.WorkItems;

namespace Rig.Core.Tests.WorkItems;

public sealed class GitFactsTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    private readonly TempDir _temp = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void ParseTrack_reads_ahead_behind_gone_and_no_upstream()
    {
        Assert.Equal(new Track(null, null, false), WorkGitParsers.ParseTrack("", ""));
        Assert.Equal(new Track(null, null, true), WorkGitParsers.ParseTrack("origin/x", "[gone]"));
        Assert.Equal(new Track(2, 1, false), WorkGitParsers.ParseTrack("origin/x", "[ahead 2, behind 1]"));
        Assert.Equal(new Track(0, 0, false), WorkGitParsers.ParseTrack("origin/x", ""));
    }

    [Fact]
    public void ParseRefs_maps_branches_default_branch_and_unpushed_counts()
    {
        string[] lines =
        [
            "refs/heads/main\taaa\t2026-10-01T10:00:00+01:00\torigin/main\t\t",
            "refs/heads/feat\tbbb\t2026-10-02T10:00:00+00:00\torigin/feat\t[gone]\t",
            "refs/remotes/origin/HEAD\tccc\t2026-10-01T10:00:00+00:00\t\t\trefs/remotes/origin/main",
            "garbage",
        ];
        var output = string.Join("\n", lines);

        Assert.Equal(["aaa", "bbb", "ccc"], WorkGitParsers.RefTips(output));
        var scan = WorkGitParsers.ParseRefs(output, new Dictionary<string, int> { ["bbb"] = 4 });

        Assert.Equal("main", scan.DefaultBranch);
        Assert.Equal(
            [
                new BranchRef("main", new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero), "origin/main", Ahead: 0, Behind: 0),
                new BranchRef(
                    "feat",
                    new DateTimeOffset(2026, 10, 2, 10, 0, 0, TimeSpan.Zero),
                    "origin/feat",
                    UpstreamGone: true,
                    Unpushed: 4),
            ],
            scan.Branches);
    }

    [Fact]
    public void ParseRefs_tolerates_a_wrong_field_count_and_an_origin_head_without_a_branch()
    {
        var scan = WorkGitParsers.ParseRefs("refs/remotes/origin/HEAD\tccc\t2026-10-01T10:00:00+00:00\t\t\thuh\nonly-two\tfields");

        Assert.Equal("huh", scan.DefaultBranch);
        Assert.Empty(scan.Branches);
    }

    [Fact]
    public void UnpushedCounts_walks_only_inside_the_unpushed_set()
    {
        var revList = string.Join("\n", ["m c2 x1", "c3 c2", "c2 c1", "c1 pushed", "x1 pushed", ""]);

        var counts = WorkGitParsers.UnpushedCounts(revList, ["c3", "m", "c1", "pushed"]);

        Assert.Equal(3, counts["c3"]);
        Assert.Equal(4, counts["m"]);
        Assert.Equal(1, counts["c1"]);
        Assert.Equal(0, counts["pushed"]);
    }

    [Fact]
    public void RepoDisplayName_is_relative_under_the_root_and_the_folder_name_outside_it()
    {
        Assert.Equal("acme/app", WorkGitParsers.RepoDisplayName(Path.Join("/code", "acme", "app"), "/code"));
        Assert.Equal("app", WorkGitParsers.RepoDisplayName(Path.Join("/elsewhere", "app"), "/code"));
        Assert.Equal("acme/app", WorkGitParsers.RepoDisplayName(Path.Join("/code", "acme", "app") + Path.DirectorySeparatorChar, "/code"));
    }

    [Fact]
    public async Task Read_repo_reads_checkouts_refs_default_branch_and_name()
    {
        const string porcelain = """
            worktree /code/acme/app
            HEAD aaa
            branch refs/heads/main

            worktree /code/acme/app/temp/wt
            HEAD bbb
            branch refs/heads/feat/x

            worktree /code/acme/app/temp/gone
            HEAD ccc
            branch refs/heads/old
            prunable gitdir file points to non-existent location

            worktree /code/acme/app/bare
            HEAD ddd
            bare

            """;
        var refs = string.Join(
            "\n",
            "refs/heads/main\taaa\t2026-10-01T10:00:00+00:00\torigin/main\t\t",
            "refs/heads/feat/x\tbbb\t2026-10-02T10:00:00+00:00\t\t\t",
            "refs/remotes/origin/HEAD\tccc\t2026-10-01T10:00:00+00:00\t\t\trefs/remotes/origin/main");
        var proc = new FakeProc()
            .Answer(new Ran(0, porcelain, string.Empty), "git", "worktree", "list", "--porcelain")
            .Answer(new Ran(0, refs, string.Empty), "git", "for-each-ref")
            .Answer(new Ran(0, "bbb aaa\n", string.Empty), "git", "rev-list");
        var facts = Facts(proc);

        var repo = await facts.ReadRepoAsync("/code/acme/app", "/code", Token);

        Assert.Equal("/code/acme/app", repo.Path);
        Assert.Equal("acme/app", repo.Name);
        Assert.Equal("main", repo.DefaultBranch);
        Assert.Equal(2, repo.Checkouts.Count);
        Assert.True(repo.Checkouts[0].IsMainCheckout);
        Assert.Equal("feat/x", repo.Checkouts[1].Branch);
        Assert.Equal(1, repo.Branches.Single(branch => branch.Name == "feat/x").Unpushed);
    }

    [Fact]
    public async Task Read_repo_falls_back_to_master_and_lists_the_clone_when_git_fails()
    {
        var refs = "refs/heads/master\taaa\t2026-10-01T10:00:00+00:00\t\t\t";
        var proc = new FakeProc()
            .Answer(new Ran(1, string.Empty, "no worktrees"), "git", "worktree", "list", "--porcelain")
            .Answer(new Ran(0, refs, string.Empty), "git", "for-each-ref")
            .Answer(new Ran(0, string.Empty, string.Empty), "git", "rev-list");
        var facts = Facts(proc);

        var repo = await facts.ReadRepoAsync("/code/acme/app", "/code", Token);

        Assert.Equal("master", repo.DefaultBranch);
        var checkout = Assert.Single(repo.Checkouts);
        Assert.Equal("/code/acme/app", checkout.Path);
        Assert.Equal(string.Empty, checkout.Branch);
        Assert.True(checkout.IsMainCheckout);
    }

    [Fact]
    public async Task Read_repo_without_refs_has_no_default_branch()
    {
        var proc = new FakeProc()
            .Answer(new Ran(1, string.Empty, "no refs"), "git", "for-each-ref")
            .Answer(new Ran(1, string.Empty, "no rev-list"), "git", "rev-list");
        var facts = Facts(proc);

        var repo = await facts.ReadRepoAsync("/code/acme/app", "/code", Token);

        Assert.Equal(string.Empty, repo.DefaultBranch);
        Assert.Empty(repo.Branches);
    }

    [Theory]
    [InlineData("git@github.com:acme/app.git", "acme/app")]
    [InlineData("https://github.com/acme/app", "acme/app")]
    [InlineData("file:///srv/app", "")]
    [InlineData("", "")]
    public async Task Read_slug_parses_github_remotes_and_leaves_others_empty(string remote, string expected)
    {
        var proc = new FakeProc().Answer(new Ran(0, remote, string.Empty), "git", "remote", "get-url", "origin");
        var facts = Facts(proc);

        Assert.Equal(expected, await facts.ReadSlugAsync("/code/acme/app", Token));
    }

    [Fact]
    public async Task Read_slug_is_empty_when_git_fails()
    {
        var proc = new FakeProc().Answer(new Ran(1, string.Empty, "no origin"), "git", "remote", "get-url", "origin");

        Assert.Equal(string.Empty, await Facts(proc).ReadSlugAsync("/code/acme/app", Token));
    }

    [Fact]
    public async Task Probe_marks_dirty_reads_and_treats_failed_status_as_clean()
    {
        var clean = _temp.Folder("clean");
        var dirty = _temp.Folder("dirty");
        var broken = _temp.Folder("broken");
        var missing = Path.Join(_temp.Path, "missing");
        var proc = new FakeProc()
            .AnswerIn(clean, new Ran(0, string.Empty, string.Empty), "git", "status", "--porcelain")
            .AnswerIn(dirty, new Ran(0, " M file.txt\n", string.Empty), "git", "status", "--porcelain")
            .AnswerIn(broken, new Ran(1, string.Empty, "locked"), "git", "status", "--porcelain");
        var facts = Facts(proc);
        var repo = new RepoFacts("repo", "repo", "main",
        [
            new Checkout(clean, "main", IsMainCheckout: true),
            new Checkout(dirty, "dirty", IsMainCheckout: false),
            new Checkout(broken, "broken", IsMainCheckout: false),
            new Checkout(missing, "missing", IsMainCheckout: false),
        ]);

        var probed = await facts.ProbeCheckoutsAsync([repo], Token);

        Assert.False(probed[0].Checkouts[0].Dirty);
        Assert.True(probed[0].Checkouts[1].Dirty);
        Assert.False(probed[0].Checkouts[2].Dirty);
        Assert.Null(probed[0].Checkouts[3].Dirty);
        Assert.Equal(3, proc.Requests.Count);
    }

    [Fact]
    public async Task Read_estate_preserves_clone_discovery_order()
    {
        var root = _temp.Folder("root");
        _ = _temp.Folder("root/b/.git");
        _ = _temp.Folder("root/a/.git");
        var refs = "refs/heads/main\taaa\t2026-10-01T10:00:00+00:00\t\t\t";
        var proc = new FakeProc()
            .AnswerIn(Path.Join(root, "a"), new Ran(0, $"worktree {Path.Join(root, "a")}\nHEAD aaa\nbranch refs/heads/main\n", string.Empty), "git", "worktree", "list", "--porcelain")
            .AnswerIn(Path.Join(root, "b"), new Ran(0, $"worktree {Path.Join(root, "b")}\nHEAD bbb\nbranch refs/heads/main\n", string.Empty), "git", "worktree", "list", "--porcelain")
            .Answer(new Ran(0, refs, string.Empty), "git", "for-each-ref")
            .Answer(new Ran(0, string.Empty, string.Empty), "git", "rev-list");
        var facts = Facts(proc);

        var repos = await facts.ReadEstateAsync(root, refresh: true, Token);

        Assert.Equal([PathResolution.Resolve(Path.Join(root, "a")), PathResolution.Resolve(Path.Join(root, "b"))], repos.Select(repo => repo.Path));
        Assert.Equal(["a", "b"], repos.Select(repo => repo.Name));
    }

    [Fact]
    public async Task Read_estate_of_an_empty_root_returns_nothing()
    {
        var root = _temp.Folder("empty-root");
        var facts = Facts(new FakeProc());

        Assert.Empty(await facts.ReadEstateAsync(root, refresh: false, Token));
    }

    [Fact]
    public async Task Read_slugs_preserves_repo_order()
    {
        var proc = new FakeProc()
            .AnswerIn("/code/b", new Ran(0, "git@github.com:o/b.git", string.Empty), "git", "remote", "get-url", "origin")
            .AnswerIn("/code/a", new Ran(0, "git@github.com:o/a.git", string.Empty), "git", "remote", "get-url", "origin");

        var slugs = await Facts(proc).ReadSlugsAsync(["/code/b", "/code/a"], Token);

        Assert.Equal(["o/b", "o/a"], slugs.Select(pair => pair.Value));
        Assert.Equal(["/code/b", "/code/a"], slugs.Keys);
    }

    private WorkGitFacts Facts(FakeProc proc)
    {
        var discovery = new CloneDiscovery(Path.Join(_temp.Path, ".estate-repos.json"), new FixedClock(Now));
        return new WorkGitFacts(new Git(proc, _temp.Path), discovery, workers: 2);
    }
}
