using System.Text.Json.Nodes;
using Rig.Core.Estate;
using Rig.Core.Processes;
using Rig.Core.Roots;
using Rig.Core.Tests.Otel;

namespace Rig.Core.Tests.Estate;

public sealed class DecisionsTests : IDisposable
{
    private const string Shelve =
        "Preview a shelve, then re-run it with --apply: the work goes to a shelved/ branch on origin, verified, before the worktree is removed";

    private const string HandWritten = "Some may be hand-written; git cannot restore them";

    private const string ListFirst = "List the files, keep anything hand-written, then delete the rest";

    private readonly TempDir _temp = new();

    // Eight days on, so every worktree made by the test is idle unless touched.
    private readonly ManualClock _clock = new(DateTimeOffset.UtcNow.AddDays(8));

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task Each_idle_worktree_lands_under_one_kind()
    {
        var clone = await CloneAsync();
        var unpushed = await WorktreeAsync(clone, "unpushed");
        await File.WriteAllTextAsync(Path.Join(unpushed, "new.txt"), string.Concat(Enumerable.Repeat("work\n", 2000)), Token);
        _ = await Repos.GitAsync(unpushed, "add", "new.txt");
        _ = await Repos.GitAsync(unpushed, "commit", "-qm", "local only");
        var dirty = await WorktreeAsync(clone, "dirty");
        await File.WriteAllTextAsync(Path.Join(dirty, "notes.md"), "hand-written\n", Token);
        var closed = await WorktreeAsync(clone, "closed");
        var noPr = await WorktreeAsync(clone, "no-pr");
        _ = await WorktreeAsync(clone, "merged");
        _ = await WorktreeAsync(clone, "open");
        var locked = await WorktreeAsync(clone, "locked");
        _ = await Repos.GitAsync(clone, "worktree", "lock", locked);
        await TouchAsync(await WorktreeAsync(clone, "fresh"), _clock.Now.UtcDateTime);
        var ghost = Directory.CreateDirectory(Path.Join(clone, ".worktrees", "ghost")).FullName;
        await File.WriteAllBytesAsync(Path.Join(ghost, "left.bin"), new byte[4096], Token);
        var stale = Directory.CreateDirectory(Path.Join(clone, "temp", "review-pr-1")).FullName;
        await File.WriteAllTextAsync(Path.Join(stale, ".git"), $"gitdir: {Path.Join(clone, ".git", "worktrees", "gone")}\n", Token);
        _ = Directory.CreateDirectory(Path.Join(clone, "temp", "scratch"));

        var heads = Answer(new PullRequest(4, "CLOSED", "closed"), new PullRequest(5, "MERGED", "merged"), new PullRequest(6, "OPEN", "open"));
        var found = await Subject((_, _) => heads).CollectAsync(Path.GetDirectoryName(clone)!, cancellationToken: Token);

        var remove = $"cd {clone} && git worktree remove";
        Assert.Equal(
            [
                new("unpushed-worktree", "app", unpushed, "1 commit(s) on no remote; untouched for 7+ days", Size(unpushed), Shelve, $"estate shelve \"{unpushed}\""),
                new("unpushed-worktree", "app", dirty, "uncommitted changes; untouched for 7+ days", Size(dirty), Shelve, $"estate shelve \"{dirty}\""),
                new(
                    "closed-pr-worktree",
                    "app",
                    closed,
                    "PR #4 for `closed` was closed without merging",
                    Size(closed),
                    "Remove the worktree if the work is abandoned, or reopen the PR",
                    $"{remove} {closed}"),
                new(
                    "no-pr-worktree",
                    "app",
                    noPr,
                    "`no-pr` has never had a PR; untouched for 7+ days",
                    Size(noPr),
                    "Open a PR if the work is wanted; otherwise remove the worktree",
                    $"{remove} {noPr}"),
                new("locked-path", "app", ghost, $"git no longer tracks it, but 1 file(s) remain: left.bin. {HandWritten}", 4096, ListFirst, $"cmd /c dir /s /b /a-d \"{ghost}\""),
                new("locked-path", "app", stale, $"git no longer tracks it, but 1 file(s) remain: .git. {HandWritten}", Size(stale), ListFirst, $"cmd /c dir /s /b /a-d \"{stale}\""),
            ],
            found);
    }

    [Fact]
    public async Task A_detached_worktree_with_no_pr_says_none_can_match()
    {
        var clone = await CloneAsync();
        var detached = Path.Join(clone, ".worktrees", "loose");
        _ = await Repos.GitAsync(clone, "worktree", "add", "-q", "--detach", detached);

        var found = await Subject((_, _) => Answer()).CloneDecisionsAsync(clone, cancellationToken: Token);

        Assert.Equal("detached HEAD, so no PR can match it; untouched for 7+ days", Assert.Single(found).Reason);
    }

    [Fact]
    public async Task Without_github_only_the_disk_decisions_are_made()
    {
        var clone = await CloneAsync();
        var dirty = await WorktreeAsync(clone, "dirty");
        await File.WriteAllTextAsync(Path.Join(dirty, "notes.md"), "hand-written\n", Token);
        _ = await WorktreeAsync(clone, "no-pr");
        var asked = new List<string>();

        var found = await Subject((repo, _) =>
        {
            asked.Add(repo);
            return Task.FromResult<IReadOnlyDictionary<string, PullRequest>?>(null);
        }).CollectAsync(clone, cancellationToken: Token);

        Assert.Equal([("unpushed-worktree", dirty)], found.Select(item => (item.Kind, item.Path)));
        Assert.Equal([clone], asked);
    }

    [Fact]
    public async Task Github_is_not_asked_when_no_worktree_needs_it()
    {
        var clone = await CloneAsync();
        await TouchAsync(await WorktreeAsync(clone, "busy"), _clock.Now.UtcDateTime);
        var asked = new List<string>();

        var found = await Subject((repo, _) =>
        {
            asked.Add(repo);
            return Answer();
        }).CollectAsync(clone, cancellationToken: Token);

        Assert.Empty(found);
        Assert.Empty(asked);
    }

    [Fact]
    public async Task A_clone_git_refuses_to_read_is_one_decision()
    {
        var clone = await CloneAsync();
        var git = new Git(new Refusing(clone), _temp.Path);

        var found = await new Decisions(git, _clock, (_, _) => Answer(), windows: false).CollectAsync(Path.GetDirectoryName(clone)!, cancellationToken: Token);

        Assert.Equal(
            [
                new(
                    "dubious-ownership",
                    "app",
                    clone,
                    "git refuses to read it: the directory is owned by another account",
                    Size(clone),
                    $"Trust it with safe.directory, or take ownership if it is yours: takeown /f \"{clone}\" /r /d y",
                    $"git config --global --add safe.directory {clone.Replace('\\', '/')}"),
            ],
            found);
    }

    [Fact]
    public async Task Collect_roots_skips_missing_roots_and_reports_a_path_once()
    {
        var clone = await CloneAsync();
        var ghost = Directory.CreateDirectory(Path.Join(clone, ".worktrees", "ghost")).FullName;
        var parent = Path.GetDirectoryName(clone)!;

        var found = await Subject((_, _) => Answer()).CollectRootsAsync([parent, clone, Path.Join(parent, "absent")], cancellationToken: Token);

        Assert.Equal([("locked-path", ghost)], found.Select(item => (item.Kind, item.Path)));
        Assert.Equal("git no longer tracks it, and only links and empty directories remain", found[0].Reason);
        Assert.Equal($"cmd /c rmdir /s /q \"{ghost}\"", found[0].Command);
    }

    [Fact]
    public void On_windows_paths_differing_only_in_case_are_one_decision()
    {
        // Two roots reporting the same path in different case: the later one stands, as os.path.normcase keys it.
        var first = new Decision("locked-path", "app", @"C:\Code\App\.worktrees\x", "a", 0, "s", "c");
        var second = first with { Path = "c:/code/app/.worktrees/x", Reason = "b" };

        Assert.Equal(["b"], Decisions.Ordered(Decisions.Unique([first, second], windows: true)).Select(item => item.Reason));
        Assert.Equal(2, Decisions.Unique([first, second], windows: false).Count());
    }

    [Fact]
    public void Ordering_is_by_kind_then_size_then_path()
    {
        Decision Make(string kind, long size, string path) => new(kind, "r", path, string.Empty, size, string.Empty, string.Empty);

        var ordered = Decisions.Ordered([Make("locked-path", 9, "/a"), Make("no-pr-worktree", 1, "/b"), Make("no-pr-worktree", 5, "/c"), Make("no-pr-worktree", 5, "/B")]);

        Assert.Equal(["/B", "/c", "/b", "/a"], ordered.Select(item => item.Path));
    }

    [Fact]
    public async Task A_leftover_names_a_few_of_its_files_and_counts_the_rest()
    {
        var clone = await CloneAsync();
        var ghost = Directory.CreateDirectory(Path.Join(clone, ".worktrees", "ghost")).FullName;
        foreach (var n in Enumerable.Range(0, 7))
        {
            await File.WriteAllTextAsync(Path.Join(ghost, $"f{n}.txt"), "x", Token);
        }

        var found = Decisions.LeftoverIn(ghost);
        var decision = Assert.Single(await Subject((_, _) => Answer()).CollectAsync(Path.GetDirectoryName(clone)!, cancellationToken: Token));

        Assert.Equal((7, 7L, Decisions.SampleFiles), (found.Files, found.Bytes, found.Sample.Count));
        Assert.StartsWith($"git no longer tracks it, but 7 file(s) remain: {string.Join(", ", found.Sample)} and 2 more.", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_leftover_walk_does_not_enter_links()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Directory symlinks need a privilege Windows CI does not grant.");
        var ghost = _temp.Folder("ghost");
        _ = Directory.CreateSymbolicLink(Path.Join(ghost, "link"), _temp.Folder("elsewhere"));
        File.WriteAllText(Path.Join(_temp.Path, "elsewhere", "big.bin"), "big");
        File.WriteAllText(Path.Join(_temp.Folder("ghost/sub"), "own.txt"), "own");

        var found = Decisions.LeftoverIn(ghost);

        Assert.Equal((1, 3L), (found.Files, found.Bytes));
        Assert.Equal(["sub/own.txt"], found.Sample);
    }

    [Fact]
    public async Task Only_a_marked_scratch_folder_or_a_dead_worktree_pointer_is_left_behind()
    {
        var clone = await CloneAsync();
        var live = await WorktreeAsync(clone, "live");
        var relative = Directory.CreateDirectory(Path.Join(clone, "temp", "relative")).FullName;
        await File.WriteAllTextAsync(Path.Join(relative, ".git"), "gitdir: ../../.git/worktrees/missing\n", Token);
        var garbled = Directory.CreateDirectory(Path.Join(clone, "temp", "garbled")).FullName;
        await File.WriteAllTextAsync(Path.Join(garbled, ".git"), "not a pointer\n", Token);
        var nested = Directory.CreateDirectory(Path.Join(clone, "temp", "nested", ".git")).FullName;
        var container = Directory.CreateDirectory(Path.Join(clone, "temp", "worktrees", "bare")).FullName;

        var found = Decisions.LeftBehind(clone);

        Assert.Equal([container, relative], found);
        Assert.DoesNotContain(live, found);
        Assert.DoesNotContain(Path.GetDirectoryName(nested), found);
    }

    [Fact]
    public void The_feed_round_trips_as_python_writes_it()
    {
        var feed = Path.Join(_temp.Path, "jobs", "decisions.json");
        var item = new Decision("no-pr-worktree", "app", @"C:\code\app\.worktrees\x+y", "why", 12, "do it", "git status");

        Decisions.WriteFeed(feed, [item]);

        Assert.Equal(
            "[\n  {\n    \"kind\": \"no-pr-worktree\",\n    \"repo\": \"app\",\n    \"path\": \"C:\\\\code\\\\app\\\\.worktrees\\\\x+y\",\n"
            + "    \"reason\": \"why\",\n    \"size_bytes\": 12,\n    \"suggested_action\": \"do it\",\n    \"command\": \"git status\"\n  }\n]\n",
            File.ReadAllText(feed).ReplaceLineEndings("\n"));
        Assert.Equal(item.ToJson().ToJsonString(), (JsonNode.Parse(File.ReadAllText(feed)) as JsonArray)![0]!.ToJsonString());
        Assert.False(File.Exists(Path.ChangeExtension(feed, ".tmp")));
        Assert.Equal("[]", Decisions.DumpFeed([]));
    }

    [Fact]
    public void The_feed_lives_beside_the_jobs_state()
    {
        Assert.Equal(Path.Join("L", "shared-utilities", "jobs", "decisions.json"), Decisions.DefaultFeedPath("L", "H"));
        Assert.Equal(Path.Join("H", "AppData", "Local", "shared-utilities", "jobs", "decisions.json"), Decisions.DefaultFeedPath(null, "H"));
    }

    [Fact]
    public async Task The_github_lookup_skips_a_clone_with_no_github_origin()
    {
        var clone = await CloneAsync();
        var lookup = Decisions.GitHub(new PullRequests(new Git(Repos.Proc, _temp.Path), new Fakes.FakeGitHub()));

        Assert.Null(await lookup(clone, Token));
    }

    private static Task<IReadOnlyDictionary<string, PullRequest>?> Answer(params PullRequest[] pulls) =>
        Task.FromResult<IReadOnlyDictionary<string, PullRequest>?>(pulls.ToDictionary(pull => pull.HeadRef));

    private static long Size(string path) => Footprint.Of(path).Bytes;

    private Decisions Subject(PullRequestLookup lookup) => new(new Git(Repos.Proc, _temp.Path), _clock, lookup, windows: false);

    private async Task<string> CloneAsync()
    {
        var origin = _temp.Folder("origin.git");
        _ = await Repos.GitAsync(origin, "init", "-q", "--bare", "-b", "main");
        var clone = PathResolution.Resolve(_temp.Folder(Path.Join("code", "app")));
        _ = await Repos.GitAsync(clone, "init", "-q", "-b", "main");
        await File.WriteAllTextAsync(Path.Join(clone, ".gitignore"), ".worktrees/\ntemp/\n", Token);
        _ = await Repos.GitAsync(clone, "add", ".");
        _ = await Repos.GitAsync(clone, "commit", "-qm", "first");
        _ = await Repos.GitAsync(clone, "remote", "add", "origin", origin);
        _ = await Repos.GitAsync(clone, "push", "-q", "-u", "origin", "main");
        return clone;
    }

    private static async Task<string> WorktreeAsync(string clone, string name)
    {
        var path = Path.GetFullPath(Path.Join(clone, ".worktrees", name));
        _ = await Repos.GitAsync(clone, "worktree", "add", "-q", "-b", name, path);
        return path;
    }

    private static async Task TouchAsync(string worktree, DateTime when)
    {
        var admin = await Repos.GitAsync(worktree, "rev-parse", "--absolute-git-dir");
        foreach (var name in new[] { "index", "HEAD", Path.Join("logs", "HEAD") }.Select(name => Path.Join(admin, name)).Where(File.Exists))
        {
            File.SetLastWriteTimeUtc(name, when);
        }
    }

    // Real git, except that reading the clone's git dir fails as it does for a folder another account owns.
    private sealed class Refusing(string clone) : IProc
    {
        public Ran Run(ProcRequest request) => Refused(request) ?? Repos.Proc.Run(request);

        public Task<Ran> RunAsync(ProcRequest request, CancellationToken cancellationToken = default) =>
            Refused(request) is { } refused ? Task.FromResult(refused) : Repos.Proc.RunAsync(request, cancellationToken);

        private Ran? Refused(ProcRequest request) =>
            request.Argv.Contains("rev-parse") && request.Argv.Contains("--git-dir") && request.WorkingDirectory == clone
                ? new Ran(128, string.Empty, $"fatal: detected dubious ownership in repository at '{clone.Replace('\\', '/')}'\n")
                : null;
    }
}
