using System.Text.Json.Nodes;
using Rig.Core.Estate;
using Rig.Core.Processes;

namespace Rig.Core.Tests.Estate;

/// <summary>
/// <see cref="Shelver"/> and <see cref="ShelfStore"/> against a real clone and a throwaway bare origin. Ported from
/// <c>tests/test_estate_shelve.py</c> and the rules in <c>tests/features/estate_shelve.feature</c>.
/// </summary>
public sealed class ShelveTests : IDisposable
{
    private readonly ShelveFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private string Clone => _fx.Clone;

    private Task<ShelveFixture> InitAsync() => _fx.InitAsync();

    // ------------------------------------------------------------------ pure helpers

    [Fact]
    public void Parse_status_skips_a_renames_source_field()
    {
        var parsed = Shelver.ParseStatus("R  new.py\0old.py\0 M a.txt\0?? b.txt\0");

        Assert.Equal(["new.py", "a.txt"], parsed.Changed);
        Assert.Equal(["b.txt"], parsed.Untracked);
    }

    [Fact]
    public void Message_trailers_round_trip()
    {
        var text = Shelver.Message("data-platform", "feat", "feat", "abc123", ".worktrees/feat", 42, ShelveFixture.Now);

        Assert.StartsWith($"{ShelfStore.Prefix} data-platform/feat (feat)\n", text, StringComparison.Ordinal);
        Assert.Equal(new ShelfTrailers("feat", "abc123", ".worktrees/feat", "#42", "2026-10-09T12:00:00+00:00", "estate shelve"), ShelfStore.ParseTrailers(text));
    }

    [Theory]
    [InlineData(".worktrees/PROJ-1", "PROJ-1")]
    [InlineData("temp/x", "temp/x")]
    [InlineData(".claude/worktrees/a b", "claude/worktrees/a-b")]
    public void Shelf_name_drops_the_convention_dir_only(string relative, string expected)
    {
        using var temp = new TempDir();

        Assert.Equal(expected, Shelver.ShelfName(temp.Path, Path.Join(temp.Path, relative)));
    }

    [Theory]
    [InlineData("a b", "a-b")]
    [InlineData("x..y", "x-y")]
    [InlineData(".hidden.lock", "hidden")]
    [InlineData("...", "-")]
    public void Ref_component_makes_a_legal_ref_segment(string part, string expected) => Assert.Equal(expected, Shelver.RefComponent(part));

    [Theory]
    [InlineData("node_modules/", true)]
    [InlineData("pkg/.venv/", true)]
    [InlineData("x/__pycache__/", true)]
    [InlineData("node_modules/a/Secret.js", true)]
    [InlineData("packages/", true)]
    [InlineData(".DS_Store", true)]
    [InlineData(".husky/_/", true)]
    [InlineData("js/pkg/.wireit/", true)]
    [InlineData("paket-files/", true)]
    [InlineData("logs/run.log", true)]
    [InlineData("batch/Specs/Advances.feature.cs", true)]
    [InlineData(".env", false)]
    [InlineData("temp/", false)]
    [InlineData("packages/api/.env", false)]
    [InlineData("packages/web/.env.local", false)]
    [InlineData("build/notes.md", false)]
    [InlineData("tools/bin/deploy.sh", false)]
    [InlineData("src/target/x.secret", false)]
    [InlineData("docs/.husky-notes.md", false)]
    public void Regenerable_paths_are_known_tool_output(string path, bool regenerable) => Assert.Equal(regenerable, Shelver.IsRegenerable(path));

    [Fact]
    public void A_shelf_prints_its_fixed_fields_then_the_trailers_that_are_set()
    {
        var shelf = new Shelf("/c/r", "refs/heads/shelved/t.user/a/b", "abc", "TEMP-SHELVED: r/a/b (b)", new ShelfTrailers(Branch: "b", Pr: "#3"), true);

        Assert.Equal("a/b", shelf.Name);
        Assert.Equal(["clone", "ref", "sha", "name", "subject", "snapshot", "branch", "pr"], shelf.ToJson().Select(pair => pair.Key));
    }

    // ------------------------------------------------------------------ planning: the least action that loses nothing

    [Theory]
    [InlineData("clean", Shelver.Remove)]
    [InlineData("ahead", Shelver.Push)]
    [InlineData("dirty", Shelver.Snapshot)]
    public async Task Shelving_picks_the_least_action_that_loses_nothing(string state, string action)
    {
        _ = await InitAsync();
        var path = await _fx.AddWorktreeAsync(state);
        if (state == "ahead")
        {
            await ShelveFixture.CommitAsync(path, "a.txt", "a\n");
        }
        else if (state == "dirty")
        {
            await ShelveFixture.MakeDirtyAsync(path);
        }

        var plan = await _fx.PlanAsync(path);

        Assert.Equal(action, plan.Action);
        Assert.Empty(plan.DecisionNeeded);
    }

    [Fact]
    public async Task A_merged_pull_request_authorises_removing_commits_a_squash_merge_stranded()
    {
        _ = await InitAsync();
        var path = await _fx.AddWorktreeAsync("landed");
        await ShelveFixture.CommitAsync(path, "a.txt", "a\n");

        var plan = await _fx.PlanAsync(path, prs: new Dictionary<string, PullRequest> { ["landed"] = new(7, "MERGED", "landed") });

        Assert.Equal(Shelver.Remove, plan.Action);
        Assert.Contains("squash-merged in #7", plan.Reasons);
    }

    // ------------------------------------------------------------------ decisions a person must make

    [Fact]
    public async Task An_ignored_secret_needs_a_decision_and_is_not_shelved()
    {
        _ = await InitAsync();
        var path = await _fx.AddWorktreeAsync("secret");
        await File.WriteAllTextAsync(Path.Join(path, ".env"), "TOKEN=1\n", Token);

        await AssertRefusedAsync(path, await _fx.PlanAsync(path), ".env");
    }

    [Fact]
    public async Task A_recently_touched_worktree_is_a_live_session()
    {
        _ = await InitAsync();
        var path = await _fx.AddWorktreeAsync("live");
        await ShelveFixture.MakeDirtyAsync(path);
        var shelver = _fx.Shelver(clock: TimeProvider.System); // The files were written moments ago.

        await AssertRefusedAsync(path, await _fx.PlanAsync(path, ShelveOptions.Default, shelver: shelver), "live session");
    }

    [Fact]
    public async Task A_shelf_on_origin_holding_other_content_is_not_overwritten()
    {
        _ = await InitAsync();
        var path = await _fx.AddWorktreeAsync("clash");
        await ShelveFixture.MakeDirtyAsync(path);
        var other = await Repos.GitAsync(Clone, "rev-parse", "HEAD");
        _ = await Repos.GitAsync(Clone, "push", "-q", "origin", $"{other}:refs/heads/shelved/t.user/clash");
        await ShelveFixture.CommitAsync(path, "x.txt", "x\n");

        await AssertRefusedAsync(path, await _fx.PlanAsync(path), "already on origin");
    }

    [Fact]
    public async Task Ignored_build_output_is_regenerable_so_it_is_no_reason_to_stop()
    {
        _ = await InitAsync();
        var path = await _fx.AddWorktreeAsync("built");
        _ = Directory.CreateDirectory(Path.Join(path, "node_modules"));
        await File.WriteAllTextAsync(Path.Join(path, "node_modules", "x.js"), "x", Token);

        Assert.Empty((await _fx.PlanAsync(path)).DecisionNeeded);
    }

    [Fact]
    public async Task The_owner_can_carry_or_discard_an_ignored_secret()
    {
        _ = await InitAsync();
        var path = await _fx.AddWorktreeAsync("secret");
        await File.WriteAllTextAsync(Path.Join(path, ".env"), "TOKEN=1\n", Token);

        var carried = await _fx.PlanAsync(path, ShelveFixture.NoIdle with { IncludeIgnored = true });
        var discarded = await _fx.PlanAsync(path, ShelveFixture.NoIdle with { DiscardIgnored = true });

        Assert.Equal(Shelver.Snapshot, carried.Action);
        Assert.Equal([".env"], carried.CarryIgnored);
        Assert.Empty(discarded.DecisionNeeded);
    }

    [Fact]
    public async Task The_decision_reports_how_much_the_ignored_paths_weigh()
    {
        _ = await InitAsync();
        var path = await _fx.AddWorktreeAsync("weighed");
        await File.WriteAllBytesAsync(Path.Join(path, ".env"), new byte[3 * 1024 * 1024], Token);

        var plan = await _fx.PlanAsync(path);

        Assert.Equal([".env"], plan.KeepIgnored);
        Assert.Equal(3L * 1024 * 1024, plan.KeepIgnoredBytes);
        Assert.Contains("3.0 MB", plan.DecisionNeeded[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_worktree_holding_another_registered_worktree_is_refused()
    {
        _ = await InitAsync();
        var outer = await _fx.AddWorktreeAsync("outer");
        _ = await Repos.GitAsync(Clone, "worktree", "add", "-q", "-b", "inner", Path.Join(outer, "node_modules", "inner"));

        var plan = await _fx.PlanAsync(outer, ShelveFixture.NoIdle with { DiscardIgnored = true });

        Assert.Contains(plan.DecisionNeeded, d => d.Contains("holds another repository", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_worktree_holding_a_nested_clone_is_refused()
    {
        _ = await InitAsync();
        var outer = await _fx.AddWorktreeAsync("outer");
        var nested = Directory.CreateDirectory(Path.Join(outer, "scratch")).FullName;
        _ = await Repos.GitAsync(nested, "init", "-q");

        var plan = await _fx.PlanAsync(outer);

        Assert.Contains(plan.DecisionNeeded, d => d.Contains("holds another repository", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_branch_with_no_history_in_common_with_origin_is_refused()
    {
        _ = await InitAsync();
        _ = await Repos.GitAsync(Clone, "fetch", "-q", "origin");
        var path = await _fx.AddWorktreeAsync("before-the-restart", push: false);
        _ = await Repos.GitAsync(path, "checkout", "-q", "--orphan", "old-history");
        await ShelveFixture.CommitAsync(path, "old.txt", "history origin never had\n");

        var plan = await _fx.PlanAsync(path);

        Assert.Equal(Shelver.Push, plan.Action);
        Assert.Contains(plan.DecisionNeeded, d => d.Contains("shares no history", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Stale_tracking_refs_do_not_prove_shared_history()
    {
        _ = await InitAsync();
        var path = await _fx.AddWorktreeAsync("stale-view", push: false);
        _ = await Repos.GitAsync(path, "checkout", "-q", "--orphan", "old-history");
        await ShelveFixture.CommitAsync(path, "old.txt", "history origin never had\n");
        // A clone from before the restart: its tracking refs still name the old history.
        _ = await Repos.GitAsync(path, "update-ref", "refs/remotes/origin/main", "HEAD");
        _ = await Repos.GitAsync(path, "symbolic-ref", "refs/remotes/origin/HEAD", "refs/remotes/origin/main");
        await ShelveFixture.MakeDirtyAsync(path);

        var plan = await _fx.PlanAsync(path);

        Assert.Equal(Shelver.Snapshot, plan.Action);
        Assert.Contains(plan.DecisionNeeded, d => d.Contains("shares no history", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Carrying_ignored_files_does_not_skip_the_history_check()
    {
        _ = await InitAsync();
        var path = await _fx.AddWorktreeAsync("ignored-only", push: false);
        _ = await Repos.GitAsync(path, "checkout", "-q", "--orphan", "old-history");
        await ShelveFixture.CommitAsync(path, ".gitignore", ".env\n");
        _ = await Repos.GitAsync(path, "push", "-q", "origin", "old-history:refs/heads/elsewhere"); // Every commit on a remote.
        await File.WriteAllTextAsync(Path.Join(path, ".env"), "TOKEN=x\n", Token);

        var plan = await _fx.PlanAsync(path, ShelveFixture.NoIdle with { IncludeIgnored = true });

        Assert.Equal(Shelver.Snapshot, plan.Action);
        Assert.Contains(plan.DecisionNeeded, d => d.Contains("shares no history", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_branch_built_on_origin_is_not_refused_for_its_history()
    {
        _ = await InitAsync();
        _ = await Repos.GitAsync(Clone, "fetch", "-q", "origin");
        var path = await _fx.AddWorktreeAsync("after-the-restart", push: false);
        await ShelveFixture.CommitAsync(path, "new.txt", "new\n");

        var plan = await _fx.PlanAsync(path);

        Assert.DoesNotContain(plan.DecisionNeeded, d => d.Contains("shares no history", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ applying: nothing removed until origin holds it

    [Fact]
    public async Task A_rejected_push_leaves_the_worktree_exactly_as_it_was()
    {
        _ = await InitAsync();
        var hook = Path.Join(_fx.Origin, "hooks", "pre-receive");
        await File.WriteAllTextAsync(hook, "#!/bin/sh\necho rejected by ruleset >&2\nexit 1\n", Token);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var path = await _fx.AddWorktreeAsync("blocked", push: false);
        await ShelveFixture.MakeDirtyAsync(path);

        var result = await _fx.Shelver().ApplyAsync(await _fx.PlanAsync(path));

        Assert.Equal("failed", result.Outcome);
        await AssertStillThereAsync(path);
        Assert.Equal("untracked\n", await File.ReadAllTextAsync(Path.Join(path, "new.txt"), Token));
        Assert.Equal(
            [" D gone.txt", " M README.md", "?? new.txt", "A  staged.txt"],
            (await StatusLinesAsync(path)).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Uncommitted_work_survives_a_shelve_and_unshelve_round_trip()
    {
        _ = await InitAsync();
        var path = await _fx.AddWorktreeAsync("wip");
        await ShelveFixture.CommitAsync(path, "local.txt", "local\n");
        var head = await Repos.GitAsync(path, "rev-parse", "HEAD");
        var pushed = await Repos.GitAsync(Clone, "rev-parse", "origin/wip");
        await ShelveFixture.MakeDirtyAsync(path);

        var result = await _fx.Shelver().ApplyAsync(await _fx.PlanAsync(path));

        Assert.Equal("shelved", result.Outcome);
        Assert.False(Directory.Exists(path));
        Assert.Equal(result.Sha, (await Repos.GitAsync(Clone, "ls-remote", "origin", "refs/heads/shelved/t.user/wip")).Split('\t')[0]);
        Assert.StartsWith(ShelfStore.Prefix, await Repos.GitAsync(Clone, "log", "-1", "--format=%s", result.Sha), StringComparison.Ordinal);
        Assert.Equal(head, await Repos.GitAsync(Clone, "rev-parse", $"{result.Sha}^"));
        var shelf = Assert.Single(await _fx.Store.ListAsync(Clone));
        Assert.Equal(("wip", true, "wip", head), (shelf.Name, shelf.Snapshot, shelf.Trailers.Branch, shelf.Trailers.Base));
        Assert.Equal(pushed, (await Repos.GitAsync(Clone, "ls-remote", "origin", "refs/heads/wip")).Split('\t')[0]);

        var restored = await _fx.Store.UnshelveAsync(shelf);

        Assert.True(restored.Ok, restored.Detail);
        Assert.Equal(head, await Repos.GitAsync(path, "rev-parse", "HEAD"));
        Assert.Equal("wip", await Repos.GitAsync(path, "branch", "--show-current"));
        Assert.Equal("edited\n", await File.ReadAllTextAsync(Path.Join(path, "README.md"), Token));
        Assert.Equal("untracked\n", await File.ReadAllTextAsync(Path.Join(path, "new.txt"), Token));
        Assert.False(File.Exists(Path.Join(path, "gone.txt")));
        Assert.Equal(
            [" D gone.txt", " M README.md", "?? new.txt", "?? staged.txt"],
            (await StatusLinesAsync(path)).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Local_commits_are_shelved_and_the_branch_is_recreated()
    {
        _ = await InitAsync();
        var path = await _fx.AddWorktreeAsync("never-pushed", push: false);
        await ShelveFixture.CommitAsync(path, "a.txt", "a\n");
        var head = await Repos.GitAsync(path, "rev-parse", "HEAD");

        var result = await _fx.Shelver().ApplyAsync(await _fx.PlanAsync(path));

        Assert.Equal("shelved", result.Outcome);
        Assert.Contains("branch never-pushed deleted", result.Detail, StringComparison.Ordinal);
        var shelf = Assert.Single(await _fx.Store.ListAsync(Clone));
        Assert.False(shelf.Snapshot);

        var restored = await _fx.Store.UnshelveAsync(shelf);

        Assert.True(restored.Ok, restored.Detail);
        Assert.Equal(head, await Repos.GitAsync(Clone, "rev-parse", "never-pushed"));
        Assert.Empty(await Repos.GitAsync(path, "status", "--porcelain"));
    }

    [Fact]
    public async Task A_clean_pushed_worktree_is_removed_and_the_ledger_records_it()
    {
        _ = await InitAsync();
        var path = await _fx.AddWorktreeAsync("clean");
        var ledger = Path.Join(_fx.Root, "shelved.jsonl");

        var result = await _fx.Shelver(ledger: ledger).ApplyAsync(await _fx.PlanAsync(path));

        Assert.Equal("removed", result.Outcome);
        Assert.False(Directory.Exists(path));
        var line = Assert.Single(await File.ReadAllLinesAsync(ledger, Token));
        Assert.StartsWith("{\"at\": \"2026-10-09T12:00:00+00:00\", ", line, StringComparison.Ordinal);
        var entry = JsonNode.Parse(line)!;
        Assert.Equal("removed", entry["outcome"]!.GetValue<string>());
        Assert.Equal(result.ToJson().Select(pair => pair.Key), entry.AsObject().Skip(1).Select(pair => pair.Key));
    }

    [Fact]
    public async Task A_rerun_after_a_failed_removal_is_idempotent()
    {
        _ = await InitAsync();
        var path = await _fx.AddWorktreeAsync("again");
        await ShelveFixture.MakeDirtyAsync(path);
        var shelver = _fx.Shelver();
        var plan = await _fx.PlanAsync(path);
        var marker = await shelver.MarkerAsync(plan);
        Assert.True(marker.Ok, marker.Detail);
        _ = await Repos.GitAsync(Clone, "push", "-q", "origin", $"{marker.Detail}:{plan.Ref}");

        var rerun = await _fx.PlanAsync(path);

        Assert.Empty(rerun.DecisionNeeded);
        Assert.Equal(marker.Detail, rerun.Existing);
        Assert.Equal("shelved", (await shelver.ApplyAsync(rerun)).Outcome);
    }

    [Fact]
    public async Task A_tree_edited_after_planning_is_kept()
    {
        _ = await InitAsync();
        var path = await _fx.AddWorktreeAsync("late-edit");
        await ShelveFixture.MakeDirtyAsync(path);
        var plan = await _fx.PlanAsync(path);
        var shelver = _fx.Shelver(BeforeLsRemote(() => File.WriteAllText(Path.Join(path, "late.txt"), "late\n")));

        var result = await shelver.ApplyAsync(plan);

        Assert.Equal("failed", result.Outcome);
        Assert.Contains("changed since planning", result.Detail, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Join(path, "late.txt")));
    }

    [Fact]
    public async Task An_ignored_file_written_after_planning_is_kept()
    {
        _ = await InitAsync();
        var path = await _fx.AddWorktreeAsync("late-ignored", push: false);
        await ShelveFixture.CommitAsync(path, "a.txt", "a\n");
        var plan = await _fx.PlanAsync(path);
        Assert.Equal(Shelver.Push, plan.Action);
        var shelver = _fx.Shelver(BeforeLsRemote(() => File.WriteAllText(Path.Join(path, ".env"), "TOKEN=1\n")));

        var result = await shelver.ApplyAsync(plan);

        Assert.Equal("failed", result.Outcome);
        Assert.Contains(".env", result.Detail, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Join(path, ".env")));
    }

    [Fact]
    public async Task An_unreadable_worktree_list_never_finishes_a_delete()
    {
        _ = await InitAsync();
        var plan = await _fx.PlanAsync(await _fx.AddWorktreeAsync("unlisted"));
        var unreadable = _fx.Shelver(new HookedProc(request =>
            request.Argv.Contains("worktree") && request.Argv.Contains("list") ? new Ran(128, string.Empty, "fatal: unreadable") : null));

        Assert.False(await unreadable.AbandonedPartWayAsync(plan));
    }

    // ------------------------------------------------------------------ pruning

    [Fact]
    public async Task Only_a_marker_without_a_snapshot_is_prunable_and_a_pruned_shelf_can_be_dropped()
    {
        _ = await InitAsync();
        var shelver = _fx.Shelver();
        var landed = await _fx.AddWorktreeAsync("landed", push: false);
        await ShelveFixture.CommitAsync(landed, "a.txt", "a\n");
        Assert.Equal("shelved", (await shelver.ApplyAsync(await _fx.PlanAsync(landed))).Outcome);
        var dirty = await _fx.AddWorktreeAsync("dirty");
        await ShelveFixture.MakeDirtyAsync(dirty);
        Assert.Equal("shelved", (await shelver.ApplyAsync(await _fx.PlanAsync(dirty))).Outcome);
        var shelves = (await _fx.Store.ListAsync(Clone)).ToDictionary(shelf => shelf.Name);
        var none = new Dictionary<int, string>();

        Assert.Empty(await _fx.Store.PrunableAsync(shelves["landed"], none));
        _ = await Repos.GitAsync(Clone, "fetch", "-q", "origin");
        _ = await Repos.GitAsync(Clone, "push", "-q", "origin", $"{shelves["landed"].Sha}^:refs/heads/main");
        _ = await Repos.GitAsync(Clone, "fetch", "-q", "origin");
        Assert.Equal("base is in main", await _fx.Store.PrunableAsync(shelves["landed"], none));
        Assert.Empty(await _fx.Store.PrunableAsync(shelves["dirty"], none)); // A snapshot is never pruned.

        var dropped = await _fx.Store.DropAsync(shelves["landed"]);

        Assert.True(dropped.Ok, dropped.Detail);
        Assert.Equal(["dirty"], (await _fx.Store.ListAsync(Clone)).Select(shelf => shelf.Name));
    }

    [Fact]
    public async Task A_merged_pr_prunes_a_shelf_only_when_it_merged_the_shelved_commits()
    {
        _ = await InitAsync();
        var path = await _fx.AddWorktreeAsync("squashed");
        await ShelveFixture.CommitAsync(path, "a.txt", "a\n");
        var pushed = await Repos.GitAsync(path, "rev-parse", "HEAD");
        _ = await Repos.GitAsync(path, "push", "-q", "origin", "squashed");
        await ShelveFixture.CommitAsync(path, "b.txt", "local only\n");
        var plan = await _fx.PlanAsync(path, prs: new Dictionary<string, PullRequest> { ["squashed"] = new(5, "OPEN", "squashed") });
        Assert.Equal("shelved", (await _fx.Shelver().ApplyAsync(plan)).Outcome);
        var shelf = (await _fx.Store.ListAsync(Clone)).Single(s => s.Name == "squashed");

        Assert.Empty(await _fx.Store.PrunableAsync(shelf, new Dictionary<int, string> { [5] = pushed })); // The local-only commit was never in #5.
        Assert.Equal("PR #5 merged", await _fx.Store.PrunableAsync(shelf, new Dictionary<int, string> { [5] = plan.Head }));
    }

    // ------------------------------------------------------------------ the Windows link skeleton

    [Fact]
    public void A_link_skeleton_is_swept_without_following_its_links()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "pnpm makes junctions there, which need the Win32 API to create");
        using var temp = new TempDir();
        var store = temp.Folder("store");
        File.WriteAllText(Path.Join(store, "keep.js"), "x");
        var skeleton = Path.Join(temp.Path, "wt");
        var scope = temp.Folder(Path.Join("wt", "js", "node_modules", "@scope"));
        _ = Directory.CreateSymbolicLink(Path.Join(scope, "pkg"), store);

        var detail = Shelver.SweepLinkSkeleton(skeleton);

        Assert.Contains("1 leftover link(s) cleared", detail, StringComparison.Ordinal);
        Assert.False(Directory.Exists(skeleton));
        Assert.Equal("x", File.ReadAllText(Path.Join(store, "keep.js")));
    }

    [Fact]
    public void A_skeleton_holding_a_file_is_left_alone()
    {
        using var temp = new TempDir();
        _ = temp.Folder(Path.Join("wt", "node_modules"));
        var notes = Path.Join(temp.Path, "wt", "notes.md");
        File.WriteAllText(notes, "mine");

        Assert.Contains("still holds files", Shelver.SweepLinkSkeleton(Path.Join(temp.Path, "wt")), StringComparison.Ordinal);
        Assert.True(File.Exists(notes));
    }

    // ------------------------------------------------------------------ helpers

    private static HookedProc BeforeLsRemote(Action action) => new(request =>
    {
        if (request.Argv.Contains("ls-remote"))
        {
            action();
        }

        return null;
    });

    private static async Task<string[]> StatusLinesAsync(string path)
    {
        var ran = await Repos.Proc.RunAsync(new ProcRequest(["git", "status", "--porcelain"]) { WorkingDirectory = path }, Token);
        return ran.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    private async Task AssertStillThereAsync(string path)
    {
        Assert.True(Directory.Exists(path));
        _ = await _fx.TreeAsync(path); // Still registered with git.
    }

    private async Task AssertRefusedAsync(string path, ShelvePlan plan, string concern)
    {
        Assert.Contains(plan.DecisionNeeded, d => d.Contains(concern, StringComparison.Ordinal));

        var result = await _fx.Shelver().ApplyAsync(plan);

        Assert.Equal("needs-decision", result.Outcome);
        await AssertStillThereAsync(path);
    }

    // Real processes, with a hook that may run something first or answer a request itself.
    private sealed class HookedProc(Func<ProcRequest, Ran?> hook) : IProc
    {
        public async Task<Ran> RunAsync(ProcRequest request, CancellationToken cancellationToken = default) =>
            hook(request) ?? await Repos.Proc.RunAsync(request, cancellationToken);
    }
}
