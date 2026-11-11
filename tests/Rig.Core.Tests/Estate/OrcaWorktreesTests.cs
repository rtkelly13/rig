using Rig.Core.Estate;
using Rig.Core.Governance;
using Rig.Core.Processes;
using Rig.Core.Roots;
using Rig.Core.Tests.Fakes;
using Rig.Core.Tests.Orca;

namespace Rig.Core.Tests.Estate;

/// <summary>Which Orca worktrees are finished, and removing only those. Ported from <c>tests/test_estate_orca_worktrees.py</c>.</summary>
public sealed class OrcaWorktreesTests : IDisposable
{
    private readonly TempDir _temp = new();
    private readonly string _root;

    public OrcaWorktreesTests() => _root = PathResolution.Resolve(_temp.Path);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _temp.Dispose();

    // ---------------------------------------------------------------- the rules

    private static readonly PullRequest MergedPr = new(7, "MERGED", "feature");

    public static TheoryData<string, string> Rules => new()
    {
        { "current", OrcaWorktrees.Current },
        { "busy", OrcaWorktrees.Busy },
        { "missing", OrcaWorktrees.Missing },
        { "leftover", OrcaWorktrees.Leftover },
        { "leftover-differs", OrcaWorktrees.LeftoverDiffers },
        { "foreign", OrcaWorktrees.Foreign },
        { "dirty", OrcaWorktrees.Dirty },
        { "pr-unknown", OrcaWorktrees.PrUnknown },
        { "merged", OrcaWorktrees.Merged },
        { "after-merge", OrcaWorktrees.AfterMerge },
        { "open-pr", OrcaWorktrees.OpenPr },
        { "closed-pr", OrcaWorktrees.ClosedPr },
        { "unmerged", OrcaWorktrees.Unmerged },
        { "recent", OrcaWorktrees.Recent },
        { "landed", OrcaWorktrees.Landed },
    };

    [Theory]
    [MemberData(nameof(Rules))]
    public void Each_rule_gives_its_verdict(string rule, string verdict)
    {
        var facts = rule switch
        {
            "current" => new WorktreeFacts(true, true, IsCurrent: true, Pr: MergedPr),
            "busy" => new WorktreeFacts(true, true, BusyAgents: 1, Pr: MergedPr),
            "missing" => new WorktreeFacts(false, false),
            "leftover" => new WorktreeFacts(true, false, LeftoverMatches: true),
            "leftover-differs" => new WorktreeFacts(true, false, LeftoverMatches: false),
            "foreign" => new WorktreeFacts(true, true, Foreign: true, Pr: MergedPr),
            "dirty" => new WorktreeFacts(true, true, Dirty: true, Pr: MergedPr),
            "pr-unknown" => new WorktreeFacts(true, true, PrKnown: false, InDefault: true, IdleDays: 30),
            "merged" => new WorktreeFacts(true, true, Pr: MergedPr),
            "after-merge" => new WorktreeFacts(true, true, Pr: MergedPr, BeyondMerge: 2),
            "open-pr" => new WorktreeFacts(true, true, Pr: MergedPr with { State = "OPEN" }),
            "closed-pr" => new WorktreeFacts(true, true, Pr: MergedPr with { State = "CLOSED" }),
            "unmerged" => new WorktreeFacts(true, true, InDefault: false, IdleDays: 30),
            "recent" => new WorktreeFacts(true, true, InDefault: true, IdleDays: 0.5),
            _ => new WorktreeFacts(true, true, InDefault: true, IdleDays: 3),
        };

        Assert.Equal(verdict, OrcaWorktrees.Decide(facts, idleDays: 2).Verdict);
    }

    [Fact]
    public void Only_finished_verdicts_are_removable() =>
        Assert.True(OrcaWorktrees.Removable.SetEquals([OrcaWorktrees.Merged, OrcaWorktrees.Landed, OrcaWorktrees.Missing, OrcaWorktrees.Leftover]));

    // ---------------------------------------------------------------- real git

    private async Task<string> RepoAsync()
    {
        var origin = Path.Join(_root, "origin.git");
        _ = await Repos.GitAsync(_root, "init", "-q", "--bare", "-b", "main", origin);
        var root = Directory.CreateDirectory(Path.Join(_root, "repo")).FullName;
        _ = await Repos.GitAsync(root, "init", "-q", "-b", "main");
        _ = await Repos.GitAsync(root, "config", "core.autocrlf", "false");
        _ = await Repos.GitAsync(root, "remote", "add", "origin", origin);
        await File.WriteAllTextAsync(Path.Join(root, ".gitignore"), ".venv/\n", Token);
        await File.WriteAllTextAsync(Path.Join(root, "README.md"), "hi\n", Token);
        _ = Directory.CreateDirectory(Path.Join(root, "src"));
        await File.WriteAllTextAsync(Path.Join(root, "src", "app.py"), "print(1)\n", Token);
        _ = await Repos.GitAsync(root, "add", ".");
        _ = await Repos.GitAsync(root, "commit", "-qm", "first");
        _ = await Repos.GitAsync(root, "push", "-q", "-u", "origin", "main");
        _ = await Repos.GitAsync(root, "remote", "set-head", "origin", "main");
        return root;
    }

    private static async Task<string> WorktreeAsync(string repo, string name, bool commit = true)
    {
        var path = Path.Join(Path.GetDirectoryName(repo), "orca", name);
        _ = await Repos.GitAsync(repo, "worktree", "add", "-q", "-b", name, path);
        if (commit)
        {
            await File.WriteAllTextAsync(Path.Join(path, "src", $"{name}.py"), "x = 1\n", Token);
            _ = await Repos.GitAsync(path, "add", ".");
            _ = await Repos.GitAsync(path, "commit", "-qm", name);
        }

        return path;
    }

    // What an interrupted delete leaves: no .git, no .gitignore, a locked .venv still there.
    private static void Break(string path)
    {
        File.Delete(Path.Join(path, ".git"));
        File.Delete(Path.Join(path, ".gitignore"));
        _ = Directory.CreateDirectory(Path.Join(path, ".venv"));
        File.WriteAllText(Path.Join(path, ".venv", "python.exe"), "binary");
    }

    private OrcaWorktrees Subject(IReadOnlyDictionary<string, PullRequest>? heads = null) =>
        new(new Git(Repos.Proc, _root), Repos.Proc, (_, branch, _) => Task.FromResult(new PrAnswer(true, heads?.GetValueOrDefault(branch))));

    [Fact]
    public async Task A_leftover_folder_matching_its_branch_is_safe()
    {
        var repo = await RepoAsync();
        var path = await WorktreeAsync(repo, "feature");
        Break(path);
        File.Delete(Path.Join(path, "README.md"));

        Assert.True((await Subject().LeftoverMatchesAsync(path, repo, "feature", Token)).Matches);
    }

    [Fact]
    public async Task A_leftover_folder_with_an_edit_or_a_new_file_is_kept()
    {
        var repo = await RepoAsync();
        var path = await WorktreeAsync(repo, "feature");
        Break(path);
        await File.WriteAllTextAsync(Path.Join(path, "src", "app.py"), "print(2)\n", Token);
        await File.WriteAllTextAsync(Path.Join(path, "notes.md"), "mine\n", Token);

        var compared = await Subject().LeftoverMatchesAsync(path, repo, "feature", Token);

        Assert.False(compared.Matches);
        Assert.Contains("2 file(s) differ", compared.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_leftover_folder_whose_branch_is_gone_is_kept()
    {
        var repo = await RepoAsync();
        var path = await WorktreeAsync(repo, "feature");
        Break(path);
        _ = await Repos.GitAsync(repo, "worktree", "prune");
        _ = await Repos.GitAsync(repo, "branch", "-D", "feature");

        var compared = await Subject().LeftoverMatchesAsync(path, repo, "feature", Token);

        Assert.False(compared.Matches);
        Assert.Contains("only copy", compared.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_leftover_comparison_leaves_no_scratch_index_behind()
    {
        var repo = await RepoAsync();
        var path = await WorktreeAsync(repo, "feature");
        Break(path);

        _ = await Subject().LeftoverMatchesAsync(path, repo, "feature", Token);

        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Join(repo, Gitignores.StateDir)));
    }

    // ---------------------------------------------------------------- through a fake Orca

    private static FakeOrca Orca(string repo)
    {
        var fake = new FakeOrca();
        fake.Repos.Add(new() { ["id"] = "r1", ["path"] = repo.Replace('\\', '/'), ["displayName"] = "repo" });
        return fake;
    }

    private Task<IReadOnlyList<WorktreeRow>> ReviewAsync(FakeOrca fake, string cwd, IReadOnlyDictionary<string, PullRequest>? heads = null) =>
        Subject(heads).ReviewAsync(fake.Runtime(), "personal", cwd, FakeOrca.NowMs, cancellationToken: Token);

    [Fact]
    public async Task Apply_removes_finished_worktrees_through_orca_and_keeps_the_rest()
    {
        var repo = await RepoAsync();
        var fake = Orca(repo);
        fake.Worktrees.Add(new() { ["id"] = "r1::main", ["repoId"] = "r1", ["path"] = repo, ["isMainWorktree"] = true });
        var mergedPath = await WorktreeAsync(repo, "merged");
        var merged = fake.AddWorktree(mergedPath, "merged");
        _ = fake.AddWorktree(await WorktreeAsync(repo, "open"), "open");
        var dirtyPath = await WorktreeAsync(repo, "dirty");
        await File.WriteAllTextAsync(Path.Join(dirtyPath, "scratch.txt"), "unsaved\n", Token);
        _ = fake.AddWorktree(dirtyPath, "dirty");
        var busy = fake.AddWorktree(await WorktreeAsync(repo, "busy"), "busy");
        fake.Activity.Add(new() { ["worktreeId"] = busy, ["liveTerminalCount"] = 1, ["agents"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject { ["state"] = "working" }) });
        var landed = fake.AddWorktree(await WorktreeAsync(repo, "landed", commit: false), "landed");
        _ = fake.AddWorktree(await WorktreeAsync(repo, "fresh", commit: false), "fresh", idleDays: 0.1);
        var gone = fake.AddWorktree(Path.Join(_root, "orca", "vanished"), "vanished");
        var heads = new Dictionary<string, PullRequest>
        {
            ["merged"] = new(1, "MERGED", "merged", await Repos.GitAsync(mergedPath, "rev-parse", "HEAD")),
            ["open"] = new(2, "OPEN", "open"),
            ["busy"] = new(3, "MERGED", "busy"),
        };

        var rows = await ReviewAsync(fake, repo, heads);
        var done = await Subject(heads).ApplyAsync(fake.Runtime(), rows, Token);

        Assert.Equal(
            new Dictionary<string, string>
            {
                ["merged"] = OrcaWorktrees.Merged,
                ["open"] = OrcaWorktrees.OpenPr,
                ["dirty"] = OrcaWorktrees.Dirty,
                ["busy"] = OrcaWorktrees.Busy,
                ["landed"] = OrcaWorktrees.Landed,
                ["fresh"] = OrcaWorktrees.Recent,
                ["vanished"] = OrcaWorktrees.Missing,
            },
            rows.ToDictionary(r => Path.GetFileName(r.Path), r => r.Verdict));
        Assert.Equal(new[] { merged, landed, gone }.Order(), fake.Removed.Order());
        Assert.Equal(["landed", "merged", "vanished"], done.Where(r => r.Outcome == "removed").Select(r => Path.GetFileName(r.Path)).Order());
    }

    [Fact]
    public async Task The_worktree_the_command_runs_in_is_never_judged_finished()
    {
        var repo = await RepoAsync();
        var fake = Orca(repo);
        var here = await WorktreeAsync(repo, "here");
        _ = fake.AddWorktree(here, "here");

        var rows = await ReviewAsync(fake, Path.Join(here, "src"), new Dictionary<string, PullRequest> { ["here"] = new(1, "MERGED", "here") });

        Assert.Equal([OrcaWorktrees.Current], rows.Select(r => r.Verdict));
    }

    [Fact]
    public async Task A_leftover_edited_after_review_is_not_deleted()
    {
        var repo = await RepoAsync();
        var fake = Orca(repo);
        var path = await WorktreeAsync(repo, "feature");
        Break(path);
        _ = fake.AddWorktree(path, "feature");
        var rows = await ReviewAsync(fake, repo);
        Assert.Equal([OrcaWorktrees.Leftover], rows.Select(r => r.Verdict));

        await File.WriteAllTextAsync(Path.Join(path, "notes.md"), "written after review\n", Token);
        var done = await Subject().ApplyAsync(fake.Runtime(), rows, Token);

        Assert.StartsWith("skipped: changed since review", done[0].Outcome, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Join(path, "notes.md")));
        Assert.Empty(fake.Removed);
    }

    [Fact]
    public async Task A_worktree_dirtied_after_review_is_kept()
    {
        var repo = await RepoAsync();
        var fake = Orca(repo);
        var path = await WorktreeAsync(repo, "landed", commit: false);
        _ = fake.AddWorktree(path, "landed");
        var rows = await ReviewAsync(fake, repo);
        Assert.Equal([OrcaWorktrees.Landed], rows.Select(r => r.Verdict));

        await File.WriteAllTextAsync(Path.Join(path, "scratch.txt"), "unsaved\n", Token);
        var done = await Subject().ApplyAsync(fake.Runtime(), rows, Token);

        Assert.Equal("skipped: changed since review: uncommitted or untracked files", done[0].Outcome);
        Assert.Empty(fake.Removed);
    }

    [Fact]
    public async Task A_worktree_an_agent_started_in_since_review_is_kept()
    {
        var repo = await RepoAsync();
        var fake = Orca(repo);
        var id = fake.AddWorktree(await WorktreeAsync(repo, "landed", commit: false), "landed");
        var rows = await ReviewAsync(fake, repo);
        Assert.Equal([OrcaWorktrees.Landed], rows.Select(r => r.Verdict));

        fake.Activity.Add(new() { ["worktreeId"] = id, ["agents"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject { ["state"] = "working" }) });
        var done = await Subject().ApplyAsync(fake.Runtime(), rows, Token);

        Assert.Equal("skipped: an agent started since review", done[0].Outcome);
        Assert.Empty(fake.Removed);
    }

    [Fact]
    public async Task A_removal_orca_does_not_confirm_is_a_failure()
    {
        var repo = await RepoAsync();
        var fake = Orca(repo);
        _ = fake.Stuck.Add(fake.AddWorktree(await WorktreeAsync(repo, "landed", commit: false), "landed"));

        var done = await Subject().ApplyAsync(fake.Runtime(), await ReviewAsync(fake, repo), Token);

        Assert.True(done[0].Failed);
        Assert.Contains("still lists", done[0].Outcome, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Apply_removes_only_the_worktree_it_reviewed()
    {
        var repo = await RepoAsync();
        var fake = Orca(repo);
        _ = fake.AddWorktree(await WorktreeAsync(repo, "landed", commit: false), "landed");
        var rows = await ReviewAsync(fake, repo);
        fake.Worktrees[0]["id"] = "r1::replacement";

        var done = await Subject().ApplyAsync(fake.Runtime(), rows, Token);

        Assert.Equal("skipped: Orca no longer lists the worktree reviewed", done[0].Outcome);
        Assert.Empty(fake.Removed);
    }

    [Fact]
    public async Task A_worktree_of_another_repository_is_foreign()
    {
        var repo = await RepoAsync();
        var other = await Repos.InitAsync(Path.Join(_root, "other"));
        var path = Path.Join(_root, "orca", "stranger");
        _ = await Repos.GitAsync(other, "worktree", "add", "-q", "-b", "stranger", path);
        var fake = Orca(repo);
        _ = fake.AddWorktree(path, "stranger");

        var rows = await ReviewAsync(fake, repo, new Dictionary<string, PullRequest> { ["stranger"] = new(1, "MERGED", "stranger") });

        Assert.Equal([OrcaWorktrees.Foreign], rows.Select(r => r.Verdict));
    }

    [Fact]
    public async Task A_failed_pr_lookup_keeps_the_worktree()
    {
        var repo = await RepoAsync();
        var fake = Orca(repo);
        _ = fake.AddWorktree(await WorktreeAsync(repo, "landed", commit: false), "landed");
        var subject = new OrcaWorktrees(new Git(Repos.Proc, _root), Repos.Proc, (_, _, _) => Task.FromResult(new PrAnswer(false)));

        var rows = await subject.ReviewAsync(fake.Runtime(), "personal", repo, FakeOrca.NowMs, cancellationToken: Token);

        Assert.Equal([OrcaWorktrees.PrUnknown], rows.Select(r => r.Verdict));
    }

    // ---------------------------------------------------------------- the PR lookup

    private async Task<string> GitHubCloneAsync()
    {
        var root = Directory.CreateDirectory(Path.Join(_root, "clone")).FullName;
        _ = await Repos.GitAsync(root, "init", "-q", "-b", "main");
        _ = await Repos.GitAsync(root, "remote", "add", "origin", "https://github.com/owner/name.git");
        return root;
    }

    [Fact]
    public async Task Branch_pr_asks_for_the_branch_and_ignores_fork_prs()
    {
        const string pulls = """
            [{"number": 9, "state": "OPEN", "headRefOid": "f", "isCrossRepository": true},
             {"number": 4, "state": "MERGED", "headRefOid": "a", "isCrossRepository": false},
             {"number": 6, "state": "CLOSED", "headRefOid": "b", "isCrossRepository": false}]
            """;
        var gh = new FakeProc().Answer(new Ran(0, pulls, string.Empty), "gh");

        var answer = await OrcaWorktrees.BranchPrAsync(new Git(Repos.Proc, _root), gh, await GitHubCloneAsync(), "feature", Token);

        Assert.Equal(new PrAnswer(true, new PullRequest(6, "CLOSED", "feature", "b")), answer);
        Assert.Equal(["gh", "pr", "list", "--repo", "owner/name", "--head", "feature"], gh.Requests[0].Argv.Take(7));
    }

    [Theory]
    [InlineData(0, "[]", true)]
    [InlineData(1, "", false)]
    [InlineData(0, "not json", false)]
    public async Task Branch_pr_says_when_it_does_not_know(int code, string output, bool known)
    {
        var gh = new FakeProc().Answer(new Ran(code, output, string.Empty), "gh");

        var answer = await OrcaWorktrees.BranchPrAsync(new Git(Repos.Proc, _root), gh, await GitHubCloneAsync(), "feature", Token);

        Assert.Equal(new PrAnswer(known), answer);
    }

    [Fact]
    public async Task Branch_pr_does_not_ask_github_without_a_github_origin()
    {
        var clone = await Repos.InitAsync(Path.Join(_root, "local"));
        var gh = new FakeProc();

        var answer = await OrcaWorktrees.BranchPrAsync(new Git(Repos.Proc, _root), gh, clone, "feature", Token);

        Assert.Equal(new PrAnswer(false), answer);
        Assert.Empty(gh.Requests);
    }

    // ---------------------------------------------------------------- scope

    [Fact]
    public async Task Select_names_a_project_by_folder_display_name_or_path()
    {
        var repo = await RepoAsync();
        var fake = Orca(repo);
        var inside = await WorktreeAsync(repo, "feature");
        _ = fake.AddWorktree(inside, "feature");
        var runtime = fake.Runtime();

        Assert.Equal(["r1"], await OrcaWorktrees.SelectAsync(runtime, "REPO", _root, Token));
        Assert.Equal(["r1"], await OrcaWorktrees.SelectAsync(runtime, Path.Join(repo, "src"), _root, Token));
        Assert.Equal(["r1"], await OrcaWorktrees.SelectAsync(runtime, inside, _root, Token));
        Assert.Empty(await OrcaWorktrees.SelectAsync(runtime, "elsewhere", _root, Token));
    }

    [Fact]
    public async Task Select_still_finds_a_worktree_whose_folder_is_gone()
    {
        var repo = await RepoAsync();
        var fake = Orca(repo);
        var vanished = Path.Join(_root, "orca", "vanished");
        _ = fake.AddWorktree(vanished, "vanished");

        Assert.Equal(["r1"], await OrcaWorktrees.SelectAsync(fake.Runtime(), vanished, _root, Token));
    }

    [Fact]
    public async Task Review_only_judges_the_chosen_projects()
    {
        var repo = await RepoAsync();
        var fake = Orca(repo);
        _ = fake.AddWorktree(await WorktreeAsync(repo, "feature"), "feature");

        var rows = await Subject().ReviewAsync(fake.Runtime(), "personal", repo, FakeOrca.NowMs, only: new HashSet<string> { "other" }, cancellationToken: Token);

        Assert.Empty(rows);
    }

    [Fact]
    public async Task Review_reads_nothing_more_when_no_project_is_chosen()
    {
        var fake = Orca(_root);

        _ = await Subject().ReviewAsync(fake.Runtime(), "personal", _root, FakeOrca.NowMs, only: new HashSet<string>(), cancellationToken: Token);

        Assert.Equal(["repo.list"], fake.Calls.Select(c => c.Method));
    }
}
