using System.Text.Json.Nodes;
using Rig.Core.Estate;
using Rig.Core.Processes;
using Rig.Core.Tests.Fakes;

namespace Rig.Core.Tests.Estate;

/// <summary><see cref="PullRequests"/> over a scripted GraphQL seam, and <see cref="Reclaim"/> and <see cref="DiskUsage"/> over fakes.</summary>
public sealed class PullRequestsAndReclaimTests
{
    private const string Repo = "/r";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Heads_page_through_both_passes_and_keep_the_newest_pr_per_head()
    {
        var github = new Sequenced(
            Page(true, "c1", (1, "MERGED", "reused", "a"), (2, "OPEN", "live", "b")),
            Page(false, null, (5, "OPEN", "reused", "c")),
            Page(false, null, (3, "MERGED", "old-mine", "d"), (4, "MERGED", "reused", "e")));
        var prs = new PullRequests(Origin("git@github.com:o/r.git"), github);

        var heads = await prs.HeadsAsync(Repo, cancellationToken: Token);

        Assert.Equal(5, heads["reused"].Number);
        Assert.Equal("OPEN", heads["reused"].State);
        Assert.Equal(3, heads["old-mine"].Number);
        Assert.Equal(3, github.Calls.Count);
        var second = JsonNode.Parse(github.Calls[1].Body!)!["variables"]!;
        Assert.Equal("c1", second["after"]!.GetValue<string>());
        Assert.Equal("o", second["owner"]!.GetValue<string>());
        var mine = JsonNode.Parse(github.Calls[2].Body!)!["variables"]!;
        Assert.Equal("repo:o/r is:pr author:@me", mine["q"]!.GetValue<string>());
        Assert.Null(mine["after"]);
    }

    [Fact]
    public async Task Merged_heads_drop_a_head_whose_newest_pr_is_open()
    {
        var github = new Sequenced(Page(false, null, (1, "MERGED", "reused", "a"), (2, "OPEN", "reused", "b"), (3, "MERGED", "done", "c")), "{}");
        var prs = new PullRequests(Origin("https://github.com/o/r"), github);

        var merged = await prs.MergedHeadsAsync(Repo, cancellationToken: Token);

        Assert.Equal(["done"], merged.Keys);
    }

    [Fact]
    public async Task No_github_origin_asks_nothing_and_a_failed_page_ends_the_pass()
    {
        var none = new Sequenced();
        Assert.Empty(await new PullRequests(new Git(new FakeProc(), "/"), none).HeadsAsync(Repo, cancellationToken: Token));
        Assert.Empty(none.Calls);

        var failing = new Sequenced("not json", "{\"data\":{\"search\":[]}}");
        Assert.Empty(await new PullRequests(Origin("git@github.com:o/r.git"), failing).HeadsAsync(Repo, cancellationToken: Token));
        Assert.Equal(2, failing.Calls.Count);
    }

    [Fact]
    public async Task Merged_pr_matches_by_name_or_origin_upstream_and_needs_the_tip_inside_the_pr()
    {
        var proc = new FakeProc()
            .Answer(new Ran(0, "origin\trefs/heads/remote-name\n", string.Empty), "git", "for-each-ref")
            .Answer(new Ran(0, string.Empty, string.Empty), "git", "merge-base", "--is-ancestor", "refs/heads/local", "h1")
            .Answer(new Ran(1, string.Empty, string.Empty), "git", "merge-base", "--is-ancestor", "refs/heads/ahead", "h2");
        var git = new Git(proc, "/");
        var heads = new Dictionary<string, PullRequest>
        {
            ["remote-name"] = new(7, "MERGED", "remote-name", "h1"),
            ["ahead"] = new(8, "MERGED", "ahead", "h2"),
            ["no-oid"] = new(9, "MERGED", "no-oid"),
        };

        Assert.Equal(7, await PullRequests.MergedPrAsync(git, Repo, "local", heads));
        Assert.Null(await PullRequests.MergedPrAsync(git, Repo, "ahead", heads));
        Assert.Null(await PullRequests.MergedPrAsync(git, Repo, "no-oid", heads));
        Assert.Null(await PullRequests.MergedPrAsync(git, Repo, "local", null));
    }

    [Fact]
    public async Task Merged_pr_ignores_an_upstream_on_another_remote()
    {
        var proc = new FakeProc().Answer(new Ran(0, "fork\trefs/heads/x\n", string.Empty), "git", "for-each-ref");
        var heads = new Dictionary<string, PullRequest> { ["x"] = new(1, "MERGED", "x", "h") };

        Assert.Null(await PullRequests.MergedPrAsync(new Git(proc, "/"), Repo, "y", heads));
    }

    [Fact]
    public async Task Reclaim_explodes_temp_classifies_worktrees_and_keeps_entries_above_the_floor()
    {
        using var root = new TempDir();
        var repo = root.Folder("r");
        var external = root.Folder("estate-temp/ext");
        _ = root.Folder("r/temp/wt");
        _ = root.File("r/temp/scratch.bin");
        _ = root.Folder("r/temp/tiny");
        _ = root.Folder("r/node_modules");
        _ = root.Folder("r/web/node_modules");
        _ = root.Folder("r/.venv");
        _ = root.Folder("r/bin");
        var porcelain = $"worktree {repo}\nHEAD a\nbranch refs/heads/main\n\nworktree {Path.Join(repo, "temp", "wt")}\nHEAD b\nbranch refs/heads/wt\n\nworktree {external}\nHEAD c\nbranch refs/heads/ext\n";
        var proc = new FakeProc().Answer(new Ran(0, porcelain, string.Empty), "git", "worktree", "list");
        var disk = new Sizes(new()
        {
            ["temp/wt"] = 300,
            ["temp/scratch.bin"] = 200,
            ["temp/tiny"] = 1,
            ["node_modules"] = 500,
            ["web/node_modules"] = 100,
            [".venv"] = 50,
            ["bin"] = 5,
            ["ext"] = 400,
        });

        var scan = await new Reclaim(new Git(proc, root.Path), disk).ScanRepoAsync(repo, floor: 10, Token);

        Assert.Equal(
            ["node_modules", "ext", "wt", "scratch.bin", "node_modules", ".venv"],
            scan.Entries.Select(e => Path.GetFileName(e.Path)));
        var tree = scan.Entries.Single(e => e.Path.EndsWith("wt", StringComparison.Ordinal));
        Assert.True(tree.IsWorktree);
        Assert.Equal("git worktree add", tree.Regenerate);
        Assert.Equal("external worktree", scan.Entries[1].Category);
        Assert.Equal(1550, scan.Total);
        Assert.Equal(
            [new("node_modules", 600), new("temp", 500), new("external worktree", 400), new KeyValuePair<string, long>("python venv", 50)],
            scan.ByCategory());
    }

    [Fact]
    public async Task Disk_usage_reads_du_kilobytes_and_zero_on_failure()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "du is the POSIX path");
        var proc = new FakeProc()
            .Answer(new Ran(0, "12\t/a\n", string.Empty), "du", "-sk", "/a")
            .Answer(new Ran(1, string.Empty, "nope"), "du", "-sk", "/b");
        var disk = new DiskUsage(proc);

        Assert.Equal(12 * 1024, await disk.SizeAsync("/a", Token));
        Assert.Equal(0, await disk.SizeAsync("/b", Token));
        Assert.Equal(DiskUsage.Timeout, proc.Requests[0].Timeout);
    }

    [Fact]
    public void Walk_rounds_each_file_to_a_cluster()
    {
        using var root = new TempDir();
        File.WriteAllText(root.File("a/one.txt"), "x");
        File.WriteAllText(root.File("a/b/two.txt"), new string('y', 5000));

        Assert.Equal(4096 + 8192, DiskUsage.Walk(Path.Join(root.Path, "a")));
        Assert.Equal(4096, DiskUsage.Walk(Path.Join(root.Path, "a", "one.txt")));
        Assert.Equal(0, DiskUsage.Walk(Path.Join(root.Path, "missing")));
    }

    private static Git Origin(string url) =>
        new(new FakeProc().Answer(new Ran(0, url + "\n", string.Empty), "git", "remote", "get-url", "origin"), "/");

    private static string Page(bool hasNext, string? cursor, params (int Number, string State, string Head, string Oid)[] pulls)
    {
        var nodes = new JsonArray([.. pulls.Select(p => (JsonNode?)new JsonObject
        {
            ["number"] = p.Number,
            ["state"] = p.State,
            ["headRefName"] = p.Head,
            ["headRefOid"] = p.Oid,
        }), new JsonObject()]);
        var connection = new JsonObject { ["pageInfo"] = new JsonObject { ["hasNextPage"] = hasNext, ["endCursor"] = cursor }, ["nodes"] = nodes };
        return new JsonObject { ["data"] = new JsonObject { ["repository"] = new JsonObject { ["pullRequests"] = connection.DeepClone() }, ["search"] = connection } }.ToJsonString();
    }

    // Answers each call with the next scripted body, then an empty failure.
    private sealed class Sequenced(params string[] bodies) : IGitHubApi
    {
        public List<ApiRequest> Calls { get; } = [];

        public Task<Ran> CallAsync(ApiRequest request, CancellationToken cancellationToken = default)
        {
            Calls.Add(request);
            return Task.FromResult(Calls.Count <= bodies.Length ? new Ran(0, bodies[Calls.Count - 1], string.Empty) : new Ran(1, string.Empty, "HTTP 500"));
        }
    }

    // Sizes keyed by the path's tail, so the test reads as the folder layout.
    private sealed class Sizes(Dictionary<string, long> byTail) : IDiskUsage
    {
        public Task<long> SizeAsync(string path, CancellationToken cancellationToken = default) =>
            Task.FromResult(byTail.Where(pair => path.EndsWith(pair.Key, StringComparison.Ordinal)).OrderByDescending(pair => pair.Key.Length).Select(pair => pair.Value).FirstOrDefault());
    }
}
