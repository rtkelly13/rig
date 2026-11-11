using System.CommandLine;
using System.Text.Json.Nodes;
using Rig.Cli.Estate;
using Rig.Core.Estate;
using Rig.Core.Processes;
using Rig.Core.Tests.Estate;
using Rig.Core.Tests.Fakes;
using Rig.Core.Tests.Otel;

namespace Rig.Cli.Tests.Estate;

/// <summary>
/// <c>rig estate shelve|unshelve|shelved</c> end to end against a real clone and a bare origin in a temp folder: option
/// parsing, scope, exit codes and the <c>--json</c> payloads. The ledger lands in the temp home. Ported from
/// <c>tests/test_estate_shelve_cli.py</c>.
/// </summary>
public sealed class ShelveCommandTests : IDisposable
{
    private readonly ShelveFixture _fx = new();
    private TimeProvider _clock = new ManualClock(ShelveFixture.Now);
    private readonly FakeGitHub _github = new();
    private Func<ProcRequest, Ran?> _answer = _ => null;

    public void Dispose() => _fx.Dispose();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private string Clone => _fx.Clone;

    private string Home => Path.Join(_fx.Root, "home");

    private async Task<(int Code, string Out, string Err)> RunInAsync(string cwd, params string[] args)
    {
        _ = Directory.CreateDirectory(Home);
        using var output = new StringWriter();
        using var error = new StringWriter();
        var command = new EstateCommand(new EstateHost(_fx.Root, Home, cwd), NoOrca.Host, new AnsweringProc(_answer), _github, _clock, _ => null, output, error);
        var code = await new RootCommand { command.Build() }.Parse(["estate", .. args]).InvokeAsync(new InvocationConfiguration { Output = output, Error = error }, Token);
        return (code, output.ToString().ReplaceLineEndings("\n"), error.ToString().ReplaceLineEndings("\n"));
    }

    private Task<(int Code, string Out, string Err)> RunAsync(params string[] args) => RunInAsync(Clone, args);

    private async Task<(int Code, JsonArray Rows)> ShelveJsonAsync(params string[] args)
    {
        var (code, output, _) = await RunAsync(["shelve", "--json", "--idle", "0", .. args]);
        return (code, JsonNode.Parse(output)!.AsArray());
    }

    private async Task<IReadOnlyList<string>> RemoteRefsAsync() => [.. (await _fx.Store.RemoteShelvesAsync(Clone)).Keys];

    // A dirty worktree, shelved and removed. Returns where it was.
    private async Task<string> ShelvedDirtyAsync()
    {
        var dirty = await _fx.AddWorktreeAsync("dirty");
        await ShelveFixture.MakeDirtyAsync(dirty);
        Assert.Equal(0, (await ShelveJsonAsync("--repo", Clone, "--apply")).Code);
        return dirty;
    }

    // ------------------------------------------------------------------ --idle

    [Theory]
    [InlineData("0", 0)]
    [InlineData("", 0)]
    [InlineData("30m", 30)]
    [InlineData("2h", 120)]
    [InlineData(" 2H ", 120)]
    [InlineData("1.5d", 2160)]
    public void Idle_accepts_minutes_hours_and_days(string text, int minutes) =>
        Assert.Equal(TimeSpan.FromMinutes(minutes), ShelveCommand.ParseDuration(text));

    [Theory]
    [InlineData("2")]
    [InlineData("h")]
    [InlineData("2w")]
    [InlineData("-1h")]
    [InlineData("1.2.3h")]
    [InlineData("two hours")]
    public void Idle_rejects_anything_else(string text) => Assert.Null(ShelveCommand.ParseDuration(text));

    [Fact]
    public async Task A_bad_idle_is_refused_with_the_expected_forms()
    {
        _ = await _fx.InitAsync();

        var (code, output, error) = await RunAsync("shelve", "--repo", Clone, "--idle", "2w");

        Assert.Equal(2, code);
        Assert.Empty(output);
        Assert.Equal("--idle: expected e.g. 30m, 2h, 1d — got '2w'\n", error);
    }

    // ------------------------------------------------------------------ estate shelve

    [Fact]
    public async Task Opposite_ignored_flags_are_refused()
    {
        _ = await _fx.InitAsync();

        var (code, output, error) = await RunAsync("shelve", "--repo", Clone, "--include-ignored", "--discard-ignored");

        Assert.Equal(2, code);
        Assert.Empty(output);
        Assert.Contains("are opposites", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_clone_with_no_linked_worktrees_plans_nothing()
    {
        _ = await _fx.InitAsync();

        var (code, rows) = await ShelveJsonAsync("--repo", Clone);

        Assert.Equal(0, code);
        Assert.Empty(rows);
    }

    [Fact]
    public async Task Dry_run_plans_every_linked_worktree_and_touches_nothing()
    {
        _ = await _fx.InitAsync();
        var clean = await _fx.AddWorktreeAsync("clean");
        var ahead = await _fx.AddWorktreeAsync("ahead");
        await ShelveFixture.CommitAsync(ahead, "local.txt", "only here\n");
        var dirty = await _fx.AddWorktreeAsync("dirty");
        await ShelveFixture.MakeDirtyAsync(dirty);

        var (code, rows) = await ShelveJsonAsync("--repo", Clone);

        Assert.Equal(0, code);
        Assert.Equal(
            [("ahead", "ahead", Shelver.Push), ("clean", "clean", Shelver.Remove), ("dirty", "dirty", Shelver.Snapshot)],
            rows.Select(p => (p!["name"]!.GetValue<string>(), p["branch"]!.GetValue<string>(), p["action"]!.GetValue<string>())));
        Assert.Equal([ahead, clean, dirty], rows.Select(p => p!["path"]!.GetValue<string>()));
        Assert.All(rows, p => Assert.Equal(Clone, p!["clone"]!.GetValue<string>()));
        Assert.All([clean, ahead, dirty], path => Assert.True(Directory.Exists(path)));
        Assert.Empty(await RemoteRefsAsync());
    }

    [Fact]
    public async Task Apply_shelves_and_removes_each_worktree_and_appends_to_the_ledger()
    {
        _ = await _fx.InitAsync();
        var dirty = await _fx.AddWorktreeAsync("dirty");
        await ShelveFixture.MakeDirtyAsync(dirty);

        var (code, rows) = await ShelveJsonAsync("--repo", Clone, "--apply");

        Assert.Equal(0, code);
        var row = Assert.Single(rows)!;
        Assert.Equal(("dirty", "shelved"), (row["name"]!.GetValue<string>(), row["outcome"]!.GetValue<string>()));
        Assert.False(Directory.Exists(dirty));
        var shelves = await _fx.Store.RemoteShelvesAsync(Clone);
        Assert.Equal(["refs/heads/shelved/t.user/dirty"], shelves.Keys);
        Assert.Equal(row["sha"]!.GetValue<string>(), shelves["refs/heads/shelved/t.user/dirty"]);
        var ledger = Assert.Single(await File.ReadAllLinesAsync(Path.Join(Home, ShelveCommand.LedgerName), Token));
        Assert.Equal("shelved", JsonNode.Parse(ledger)!["outcome"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_worktree_needing_a_decision_exits_2_and_says_why()
    {
        _ = await _fx.InitAsync();
        var secret = await _fx.AddWorktreeAsync("secret");
        await File.WriteAllTextAsync(Path.Join(secret, ".env"), "TOKEN=x\n", Token);

        var (code, rows) = await ShelveJsonAsync("--repo", Clone);
        var (discardCode, discarded) = await ShelveJsonAsync("--repo", Clone, "--discard-ignored");

        Assert.Equal(2, code);
        var plan = Assert.Single(rows)!;
        Assert.Equal("secret", plan["name"]!.GetValue<string>());
        Assert.Equal([".env"], plan["keep_ignored"]!.AsArray().Select(p => p!.GetValue<string>()));
        Assert.NotEmpty(plan["decision_needed"]!.AsArray());
        Assert.Equal(0, discardCode);
        Assert.Empty(discarded[0]!["decision_needed"]!.AsArray());
    }

    [Fact]
    public async Task A_recently_touched_worktree_is_held_by_the_default_idle_guard()
    {
        _ = await _fx.InitAsync();
        var live = await _fx.AddWorktreeAsync("live");
        await ShelveFixture.MakeDirtyAsync(live);
        _clock = TimeProvider.System; // The files were written moments ago.

        var (code, output, _) = await RunAsync("shelve", "--json", "--repo", Clone);

        Assert.Equal(2, code);
        Assert.NotEmpty(JsonNode.Parse(output)![0]!["decision_needed"]!.AsArray());
    }

    [Fact]
    public async Task Paths_scope_to_the_worktree_holding_each_path()
    {
        _ = await _fx.InitAsync();
        var one = await _fx.AddWorktreeAsync("one");
        _ = await _fx.AddWorktreeAsync("two");
        var sub = Directory.CreateDirectory(Path.Join(one, "sub")).FullName;

        var (code, rows) = await ShelveJsonAsync(sub);

        Assert.Equal(0, code);
        Assert.Equal(["one"], rows.Select(p => p!["name"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Two_paths_inside_one_worktree_plan_it_once()
    {
        _ = await _fx.InitAsync();
        var one = await _fx.AddWorktreeAsync("one");
        var sub = Directory.CreateDirectory(Path.Join(one, "sub")).FullName;

        var (code, rows) = await ShelveJsonAsync(one, sub);

        Assert.Equal(0, code);
        Assert.Equal(["one"], rows.Select(p => p!["name"]!.GetValue<string>()));
    }

    [Fact]
    public async Task An_origin_that_cannot_list_shelves_fails_rather_than_acting_blind()
    {
        _ = await _fx.InitAsync();
        _ = await _fx.AddWorktreeAsync("one");
        _answer = request => request.Argv.Contains("ls-remote") ? new Ran(128, string.Empty, "no route") : null;

        var shelve = await RunAsync("shelve", "--repo", Clone, "--idle", "0", "--apply");
        var shelved = await RunAsync("shelved", "--repo", Clone);

        Assert.Equal((1, string.Empty), (shelve.Code, shelve.Out));
        Assert.Contains("✗ could not list shelves on origin for ", shelve.Err, StringComparison.Ordinal);
        Assert.Equal(1, shelved.Code);
        Assert.True(Directory.Exists(Path.Join(Clone, ".worktrees", "one")));
    }

    [Fact]
    public async Task With_no_path_the_worktree_holding_the_cwd_is_the_target()
    {
        _ = await _fx.InitAsync();
        var one = await _fx.AddWorktreeAsync("one");

        var (code, output, _) = await RunInAsync(one, "shelve", "--json", "--idle", "0");

        Assert.Equal(0, code);
        Assert.Equal(["one"], JsonNode.Parse(output)!.AsArray().Select(p => p!["name"]!.GetValue<string>()));
    }

    [Fact]
    public async Task The_main_checkout_is_never_a_target()
    {
        _ = await _fx.InitAsync();

        var (code, output, error) = await RunAsync("shelve", "--idle", "0", Clone);

        Assert.Equal(2, code);
        Assert.Empty(output);
        Assert.NotEmpty(error);
    }

    [Fact]
    public async Task A_path_outside_any_repository_is_refused()
    {
        _ = await _fx.InitAsync();
        var outside = Directory.CreateDirectory(Path.Join(_fx.Root, "plain")).FullName;

        var (code, _, error) = await RunAsync("shelve", outside);

        Assert.Equal(2, code);
        Assert.NotEmpty(error);
    }

    [Fact]
    public async Task The_table_reports_each_worktree_and_tallies_the_plans()
    {
        _ = await _fx.InitAsync();
        _ = await _fx.AddWorktreeAsync("clean");
        await ShelveFixture.MakeDirtyAsync(await _fx.AddWorktreeAsync("dirty"));

        var (code, output, error) = await RunAsync("shelve", "--idle", "0", "--repo", Clone);

        Assert.Equal(0, code);
        Assert.Contains("planning clone: 2 worktree(s)", error, StringComparison.Ordinal);
        Assert.Contains("dry run", output, StringComparison.Ordinal);
        Assert.Contains("1 remove", output, StringComparison.Ordinal);
        Assert.Contains("1 snapshot+remove", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_applied_table_reports_outcomes()
    {
        _ = await _fx.InitAsync();
        _ = await _fx.AddWorktreeAsync("clean");

        var (code, output, _) = await RunAsync("shelve", "--idle", "0", "--repo", Clone, "--apply");

        Assert.Equal(0, code);
        Assert.DoesNotContain("dry run", output, StringComparison.Ordinal);
        Assert.Contains("1 removed", output, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ estate shelved and estate unshelve

    [Fact]
    public async Task Nothing_shelved_says_so()
    {
        _ = await _fx.InitAsync();

        Assert.Equal((0, "✓ Nothing shelved.\n", ""), await RunAsync("shelved", "--repo", Clone));
        Assert.Equal((0, "[]\n", ""), await RunAsync("shelved", "--repo", Clone, "--json"));
    }

    [Fact]
    public async Task Shelved_lists_each_shelf_with_its_trailers()
    {
        _ = await _fx.InitAsync();
        _ = await ShelvedDirtyAsync();

        var (code, output, _) = await RunAsync("shelved", "--repo", Clone, "--json");

        Assert.Equal(0, code);
        var row = Assert.Single(JsonNode.Parse(output)!.AsArray())!;
        Assert.Equal(["clone", "ref", "sha", "name", "subject", "snapshot", "branch", "base", "path", "at", "by"], row.AsObject().Select(p => p.Key));
        Assert.Equal("refs/heads/shelved/t.user/dirty", row["ref"]!.GetValue<string>());
        Assert.Equal(("dirty", true), (row["name"]!.GetValue<string>(), row["snapshot"]!.GetValue<bool>()));
        Assert.Equal(("dirty", ".worktrees/dirty", "estate shelve"), (row["branch"]!.GetValue<string>(), row["path"]!.GetValue<string>(), row["by"]!.GetValue<string>()));
        Assert.Equal((await _fx.Store.RemoteShelvesAsync(Clone))["refs/heads/shelved/t.user/dirty"], row["sha"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_snapshot_is_never_pruned()
    {
        _ = await _fx.InitAsync();
        _ = await ShelvedDirtyAsync();

        var (code, output, _) = await RunAsync("shelved", "--repo", Clone, "--prune", "--apply", "--json");

        Assert.Equal(0, code);
        var row = Assert.Single(JsonNode.Parse(output)!.AsArray())!;
        Assert.Equal(string.Empty, row["prune"]!.GetValue<string>());
        Assert.False(row.AsObject().ContainsKey("pruned"));
        Assert.Equal(["refs/heads/shelved/t.user/dirty"], await RemoteRefsAsync());
    }

    [Fact]
    public async Task Prune_drops_a_shelf_whose_base_landed_only_with_apply()
    {
        _ = await _fx.InitAsync();
        var ahead = await _fx.AddWorktreeAsync("ahead");
        await ShelveFixture.CommitAsync(ahead, "local.txt", "only here\n");
        Assert.Equal(0, (await ShelveJsonAsync("--repo", Clone, "--apply")).Code);
        var shelf = Assert.Single(await _fx.Store.ListAsync(Clone));
        _ = await Repos.GitAsync(Clone, "push", "-q", "origin", $"{shelf.Trailers.Base}:main");
        _ = await Repos.GitAsync(Clone, "fetch", "-q", "origin");

        var dry = JsonNode.Parse((await RunAsync("shelved", "--repo", Clone, "--prune", "--json")).Out)!.AsArray();
        var applied = JsonNode.Parse((await RunAsync("shelved", "--repo", Clone, "--prune", "--apply", "--json")).Out)!.AsArray();

        Assert.Equal([("base is in main", false)], dry.Select(r => (r!["prune"]!.GetValue<string>(), r.AsObject().ContainsKey("pruned"))));
        Assert.Equal([("base is in main", true)], applied.Select(r => (r!["prune"]!.GetValue<string>(), r["pruned"]!.GetValue<bool>())));
        Assert.Empty(await RemoteRefsAsync());
    }

    [Fact]
    public async Task Prune_drops_a_shelf_whose_pull_request_merged_its_commits()
    {
        _ = await _fx.InitAsync();
        var path = await _fx.AddWorktreeAsync("squashed", push: false);
        await ShelveFixture.CommitAsync(path, "a.txt", "a\n");
        var plan = await _fx.PlanAsync(path, prs: new Dictionary<string, PullRequest> { ["squashed"] = new(5, "OPEN", "squashed") });
        Assert.Equal("shelved", (await _fx.Shelver().ApplyAsync(plan)).Outcome);
        _answer = request => request.Argv.Contains("get-url") ? new Ran(0, "https://github.com/o/r.git\n", string.Empty) : null;
        var node = $$"""{"number": 5, "state": "MERGED", "headRefName": "squashed", "headRefOid": "{{plan.Head}}"}""";
        var page = $$"""{"pageInfo": {"hasNextPage": false}, "nodes": [{{node}}]}""";
        _ = _github.On("POST", "graphql", $$$"""{"data": {"repository": {"pullRequests": {{{page}}}}, "search": {{{page}}}}}""");

        var (code, output, _) = await RunAsync("shelved", "--repo", Clone, "--prune", "--json");

        Assert.Equal(0, code);
        Assert.Equal("PR #5 merged", Assert.Single(JsonNode.Parse(output)!.AsArray())!["prune"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_path_that_does_not_exist_is_refused()
    {
        _ = await _fx.InitAsync();

        var (code, _, error) = await RunAsync("shelve", Path.Join(_fx.Root, "missing"));

        Assert.Equal(2, code);
        Assert.NotEmpty(error);
    }

    [Fact]
    public async Task The_shelved_table_marks_snapshots_and_prune_verdicts()
    {
        _ = await _fx.InitAsync();
        _ = await ShelvedDirtyAsync();

        var (code, output, _) = await RunAsync("shelved", "--repo", Clone, "--prune");

        Assert.Equal(0, code);
        Assert.Contains("prune dry run", output, StringComparison.Ordinal);
        Assert.Contains("shelved/t.user/dirty", output, StringComparison.Ordinal);
        Assert.Contains("✓", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unshelve_restores_the_worktree_and_its_uncommitted_work()
    {
        _ = await _fx.InitAsync();
        var dirty = await ShelvedDirtyAsync();

        var (code, output, _) = await RunAsync("unshelve", "dirty", "--repo", Clone);

        Assert.Equal(0, code);
        Assert.StartsWith("✓ shelved/t.user/dirty → ", output, StringComparison.Ordinal);
        Assert.Equal("untracked\n", await File.ReadAllTextAsync(Path.Join(dirty, "new.txt"), Token));
        Assert.False(File.Exists(Path.Join(dirty, "gone.txt")));
        Assert.Equal(["refs/heads/shelved/t.user/dirty"], await RemoteRefsAsync());
    }

    [Fact]
    public async Task Unshelve_drop_deletes_the_shelf_once_restored()
    {
        _ = await _fx.InitAsync();
        _ = await ShelvedDirtyAsync();

        var (code, output, _) = await RunAsync("unshelve", "shelved/t.user/dirty", "--repo", Clone, "--drop");

        Assert.Equal(0, code);
        Assert.Contains("shelf dropped from origin", output, StringComparison.Ordinal);
        Assert.Empty(await RemoteRefsAsync());
    }

    [Fact]
    public async Task Unshelve_from_inside_another_worktree_restores_beside_the_main_checkout()
    {
        _ = await _fx.InitAsync();
        var dirty = await ShelvedDirtyAsync();
        var other = await _fx.AddWorktreeAsync("other");

        var (code, _, error) = await RunInAsync(other, "unshelve", "dirty");

        Assert.True(code == 0, error);
        Assert.Equal("untracked\n", await File.ReadAllTextAsync(Path.Join(dirty, "new.txt"), Token));
        Assert.False(Directory.Exists(Path.Join(other, ".worktrees")));
    }

    [Fact]
    public async Task A_drop_origin_refuses_fails_the_unshelve()
    {
        _ = await _fx.InitAsync();
        _ = await ShelvedDirtyAsync();
        _answer = RefuseDrops;

        var (code, output, _) = await RunAsync("unshelve", "dirty", "--repo", Clone, "--drop");

        Assert.Equal(1, code);
        Assert.Contains("drop failed: rejected", output, StringComparison.Ordinal);
        Assert.Equal(["refs/heads/shelved/t.user/dirty"], await RemoteRefsAsync());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_prune_origin_refuses_fails_the_listing(bool json)
    {
        _ = await _fx.InitAsync();
        var ahead = await _fx.AddWorktreeAsync("ahead");
        await ShelveFixture.CommitAsync(ahead, "local.txt", "only here\n");
        Assert.Equal(0, (await ShelveJsonAsync("--repo", Clone, "--apply")).Code);
        var shelf = Assert.Single(await _fx.Store.ListAsync(Clone));
        _ = await Repos.GitAsync(Clone, "push", "-q", "origin", $"{shelf.Trailers.Base}:main");
        _ = await Repos.GitAsync(Clone, "fetch", "-q", "origin");
        _answer = RefuseDrops;

        var (code, output, _) = json
            ? await RunAsync("shelved", "--repo", Clone, "--prune", "--apply", "--json")
            : await RunAsync("shelved", "--repo", Clone, "--prune", "--apply");

        Assert.Equal(1, code);
        Assert.Contains("failed: rejected", output, StringComparison.Ordinal);
        Assert.Single(await RemoteRefsAsync());
    }

    private static Ran? RefuseDrops(ProcRequest request) =>
        request.Argv.Any(arg => arg.StartsWith("--force-with-lease=", StringComparison.Ordinal)) ? new Ran(1, string.Empty, "rejected") : null;

    [Fact]
    public async Task Unshelve_to_an_occupied_path_fails_and_keeps_the_shelf()
    {
        _ = await _fx.InitAsync();
        var dirty = await ShelvedDirtyAsync();
        _ = Directory.CreateDirectory(dirty);

        var (code, output, _) = await RunAsync("unshelve", "dirty", "--repo", Clone, "--drop");

        Assert.Equal(1, code);
        Assert.StartsWith("✗ ", output, StringComparison.Ordinal);
        Assert.Equal(["refs/heads/shelved/t.user/dirty"], await RemoteRefsAsync());
    }

    [Fact]
    public async Task Unshelve_of_an_unknown_name_is_refused()
    {
        _ = await _fx.InitAsync();
        _ = await ShelvedDirtyAsync();

        var (code, _, error) = await RunAsync("unshelve", "nope", "--repo", Clone);

        Assert.Equal(2, code);
        Assert.Equal("'nope' matches 0 shelves in clone (none)\n", error);
    }

    [Fact]
    public async Task A_bare_name_shared_by_two_users_is_ambiguous_unless_the_user_is_named()
    {
        _ = await _fx.InitAsync();
        _ = await ShelvedDirtyAsync();
        var sha = (await _fx.Store.RemoteShelvesAsync(Clone))["refs/heads/shelved/t.user/dirty"];
        _ = await Repos.GitAsync(Clone, "push", "-q", "origin", $"{sha}:refs/heads/shelved/someone/dirty");

        var (code, _, error) = await RunAsync("unshelve", "dirty", "--repo", Clone, "--user", "nobody");
        var (named, output, _) = await RunAsync("unshelve", "dirty", "--repo", Clone, "--user", "someone");

        Assert.Equal(2, code);
        Assert.Contains("matches 2 shelves", error, StringComparison.Ordinal);
        Assert.Equal(0, named);
        Assert.StartsWith("✓ shelved/someone/dirty → ", output, StringComparison.Ordinal);
    }

    // Real processes, unless the test answers a request itself.
    private sealed class AnsweringProc(Func<ProcRequest, Ran?> answer) : IProc
    {
        public async Task<Ran> RunAsync(ProcRequest request, CancellationToken cancellationToken = default) =>
            answer(request) ?? await Repos.Proc.RunAsync(request, cancellationToken);
    }
}
