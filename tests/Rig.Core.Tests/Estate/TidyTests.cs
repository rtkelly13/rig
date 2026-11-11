using Rig.Core.Estate;
using Rig.Core.Processes;
using Rig.Core.Roots;
using Rig.Core.Tests.Otel;

namespace Rig.Core.Tests.Estate;

/// <summary>
/// <see cref="Tidy"/> over real clones: the part of estate cleanup that must never be wrong. Ported from the
/// <c>clean_repo</c> cases of <c>tests/test_estate.py</c> and <c>tests/test_estate_worktree_safety.py</c>.
/// </summary>
public sealed class TidyTests : IDisposable
{
    private readonly TempDir _root = new();

    public void Dispose() => _root.Dispose();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private Git Git => new(Repos.Proc, _root.Path);

    private Tidy Tidy => new(Git, new TreeRemoval(Repos.Proc));

    private Task<RepoReport> InspectAsync(string repo, IReadOnlyDictionary<string, PullRequest>? heads = null, DateTimeOffset? now = null) =>
        new RepoChecks(Git, new ManualClock(now ?? DateTimeOffset.UtcNow)).InspectAsync(repo, heads);

    private static RepoReport Only(string repo, params Finding[] findings) => new(repo, findings, 0, 0, 0);

    private static Finding Merged(string path) => new("worktree-merged", Severity.Info, path, string.Empty, Actionable: true);

    private static Finding Stale(string path) => new("temp-stale", Severity.Info, path, string.Empty, Actionable: true);

    private static Dictionary<string, PullRequest> MergedSpike => new() { ["spike"] = new PullRequest(7, "MERGED", "spike") };

    // A clone of `up` whose main is on origin and whose temp/ is ignored.
    private async Task<string> RepoAsync() => PathResolution.Resolve(await Repos.PublishedAsync(_root));

    private static async Task<string> WorktreeAsync(string repo, string branch = "spike", string at = "temp/spike")
    {
        _ = await Repos.GitAsync(repo, "worktree", "add", "-q", "-b", branch, at);
        return Path.Join(repo, at);
    }

    // Commit on `local`, push it as `remoteBranch`, then delete that remote branch, as a merge does.
    private static async Task PushAsAsync(string repo, string local, string remoteBranch, string remote = "origin")
    {
        _ = await Repos.GitAsync(repo, "checkout", "-q", "-b", local);
        await Repos.CommitAsync(repo, "landed-by-squash");
        _ = await Repos.GitAsync(repo, "push", "-q", "-u", remote, $"{local}:{remoteBranch}");
        _ = await Repos.GitAsync(repo, "checkout", "-q", "main");
        _ = await Repos.GitAsync(repo, "push", "-q", remote, "--delete", remoteBranch);
        _ = await Repos.GitAsync(repo, "fetch", "-q", "--prune", remote);
    }

    // GitHub's view: PR `number` merged `headRef` with `tipOf`'s current commit as its head.
    private static async Task<Dictionary<string, PullRequest>> MergedAtAsync(string repo, int number, string headRef, string tipOf) =>
        new() { [headRef] = new PullRequest(number, "MERGED", headRef, await Repos.GitAsync(repo, "rev-parse", tipOf)) };

    private static async Task<bool> HasBranchAsync(string repo, string branch) =>
        (await Repos.GitAsync(repo, "branch", "--list", branch)).Length > 0;

    // ---- branches ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_branch_holding_local_only_commits_is_never_deleted_whatever_the_flags()
    {
        var repo = await RepoAsync();
        _ = await Repos.GitAsync(repo, "checkout", "-q", "-b", "work");
        await Repos.CommitAsync(repo, "only-here");
        _ = await Repos.GitAsync(repo, "checkout", "-q", "main");
        var report = Only(repo, new Finding("branch-merged", Severity.Warn, "work", "pretend git believes this landed", Actionable: true));

        var verdict = (await Tidy.CleanRepoAsync(report, apply: true, force: true, cancellationToken: Token)).Single();

        Assert.Equal(TidyOutcome.Refused, verdict.Outcome);
        Assert.Equal("1 commit(s) on no remote — pass --merged to ask GitHub", verdict.Detail);
        Assert.True(await HasBranchAsync(repo, "work"));
    }

    [Fact]
    public async Task A_branch_pushed_under_another_name_matches_its_merged_pr_and_goes()
    {
        var repo = await RepoAsync();
        await PushAsAsync(repo, "port/work", "30-work");
        var heads = await MergedAtAsync(repo, 68, "30-work", "port/work");
        var report = await InspectAsync(repo, heads);

        var dry = (await Tidy.CleanRepoAsync(report, mergedHeads: heads, cancellationToken: Token)).Single(a => a.Subject == "port/work");
        var applied = (await Tidy.CleanRepoAsync(report, apply: true, mergedHeads: heads, cancellationToken: Token)).Single(a => a.Subject == "port/work");

        Assert.Equal(TidyOutcome.WouldClean, dry.Outcome);
        Assert.Equal("squash-merged in #68 · git branch -D port/work", dry.Detail);
        Assert.Equal(TidyOutcome.Cleaned, applied.Outcome);
        Assert.False(await HasBranchAsync(repo, "port/work"));
    }

    [Theory]
    [InlineData("work", "work")]
    [InlineData("port/work", "30-work")]
    public async Task A_commit_after_the_merged_head_keeps_the_branch(string local, string remoteBranch)
    {
        var repo = await RepoAsync();
        await PushAsAsync(repo, local, remoteBranch);
        var heads = await MergedAtAsync(repo, 68, remoteBranch, local);
        _ = await Repos.GitAsync(repo, "checkout", "-q", local);
        await Repos.CommitAsync(repo, "after-the-push");
        _ = await Repos.GitAsync(repo, "checkout", "-q", "main");

        var actions = await Tidy.CleanRepoAsync(await InspectAsync(repo, heads), apply: true, mergedHeads: heads, cancellationToken: Token);

        var verdict = actions.Single(a => a.Subject == local);
        Assert.Equal(TidyOutcome.Refused, verdict.Outcome);
        Assert.Equal("2 commit(s) on no remote", verdict.Detail);
        Assert.True(await HasBranchAsync(repo, local));
    }

    [Fact]
    public async Task A_merged_pr_without_a_known_head_never_authorises_deletion()
    {
        var repo = await RepoAsync();
        await PushAsAsync(repo, "work", "work");
        var heads = new Dictionary<string, PullRequest> { ["work"] = new(68, "MERGED", "work") };

        var actions = await Tidy.CleanRepoAsync(await InspectAsync(repo, heads), apply: true, mergedHeads: heads, cancellationToken: Token);

        Assert.Equal(TidyOutcome.Refused, actions.Single(a => a.Subject == "work").Outcome);
    }

    [Fact]
    public async Task An_upstream_on_another_remote_never_matches_origins_prs()
    {
        var repo = await RepoAsync();
        var other = Path.Join(_root.Path, "other.git");
        _ = await Repos.GitAsync(_root.Path, "init", "-q", "--bare", "-b", "main", other);
        _ = await Repos.GitAsync(repo, "remote", "add", "other", other);
        _ = await Repos.GitAsync(repo, "push", "-q", "other", "main");
        await PushAsAsync(repo, "port/work", "30-work", remote: "other");
        var heads = await MergedAtAsync(repo, 68, "30-work", "port/work");
        var report = Only(repo, new Finding("branch-upstream-gone", Severity.Warn, "port/work", string.Empty, Actionable: true));

        var verdict = (await Tidy.CleanRepoAsync(report, apply: true, mergedHeads: heads, cancellationToken: Token)).Single();

        Assert.Equal(TidyOutcome.Refused, verdict.Outcome);
        Assert.Equal("1 commit(s) on no remote", verdict.Detail);
    }

    [Fact]
    public async Task A_branch_with_nothing_off_a_remote_goes_unless_another_worktree_has_it_checked_out()
    {
        var repo = await RepoAsync();
        _ = await Repos.GitAsync(repo, "branch", "done");
        _ = await WorktreeAsync(repo);
        var report = Only(
            repo,
            new Finding("branch-merged", Severity.Info, "done", string.Empty, Actionable: true),
            new Finding("branch-merged", Severity.Info, "spike", string.Empty, Actionable: true));

        var actions = await Tidy.CleanRepoAsync(report, cancellationToken: Token);

        Assert.Equal(
            [
                new TidyAction("branch-merged", "done", TidyOutcome.WouldClean, "no commits outside a remote · git branch -D done"),
                new TidyAction("branch-merged", "spike", TidyOutcome.Refused, "checked out in another worktree"),
            ],
            actions);
    }

    [Fact]
    public async Task A_branch_git_cannot_count_is_refused_not_read_as_empty()
    {
        var repo = await RepoAsync();
        var report = Only(repo, new Finding("branch-merged", Severity.Info, "gone", string.Empty, Actionable: true));

        var verdict = (await Tidy.CleanRepoAsync(report, apply: true, force: true, cancellationToken: Token)).Single();

        Assert.Equal(TidyOutcome.Refused, verdict.Outcome);
        Assert.Equal("git cannot count its commits on no remote — pass --merged to ask GitHub", verdict.Detail);
    }

    // ---- temp ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_dry_run_removes_nothing_and_apply_removes_stale_scratch()
    {
        var repo = await RepoAsync();
        var stale = Path.Join(repo, "temp", "old");
        _ = Directory.CreateDirectory(stale);
        await File.WriteAllTextAsync(Path.Join(stale, "f.txt"), "x", Token);
        var report = await InspectAsync(repo, now: DateTimeOffset.UtcNow.AddDays(30));

        var dry = await Tidy.CleanRepoAsync(report, cancellationToken: Token);
        var existedAfterDryRun = Directory.Exists(stale);
        var applied = await Tidy.CleanRepoAsync(report, apply: true, busy: new Busy([]), cancellationToken: Token);

        Assert.Equal([new TidyAction("temp-stale", stale, TidyOutcome.WouldClean, $"rm -rf {stale}")], dry);
        Assert.True(existedAfterDryRun);
        Assert.Equal([new TidyAction("temp-stale", stale, TidyOutcome.Cleaned, "removed")], applied);
        Assert.False(Directory.Exists(stale));
    }

    [Fact]
    public async Task A_path_outside_the_clone_or_the_clone_itself_is_refused()
    {
        var repo = await RepoAsync();
        var outside = _root.Folder("not-mine");

        var actions = await Tidy.CleanRepoAsync(Only(repo, Stale(outside), Stale(repo), Merged(outside)), apply: true, force: true, cancellationToken: Token);

        Assert.Equal(["resolves outside the repo", "is the repo itself", "resolves outside the repo"], actions.Select(a => a.Detail));
        Assert.All(actions, a => Assert.Equal(TidyOutcome.Refused, a.Outcome));
        Assert.True(Directory.Exists(outside));
    }

    [Fact]
    public async Task Scratch_holding_a_checkout_is_refused_even_forced()
    {
        var repo = await RepoAsync();
        var scratch = Path.Join(repo, "temp", "scratch");
        _ = await Repos.InitAsync(Path.Join(scratch, "nested"));

        var verdict = (await Tidy.CleanRepoAsync(Only(repo, Stale(scratch)), apply: true, force: true, cancellationToken: Token)).Single();

        Assert.Equal(new TidyAction("temp-stale", scratch, TidyOutcome.Refused, "holds a git checkout"), verdict);
        Assert.True(Directory.Exists(Path.Join(scratch, "nested", ".git")));
    }

    [Fact]
    public async Task Scratch_a_process_is_working_in_is_refused_even_forced()
    {
        var repo = await RepoAsync();
        var scratch = _root.Folder("r/temp/scratch");
        var busy = new Busy([PathResolution.Resolve(scratch)]);

        var verdict = (await Tidy.CleanRepoAsync(Only(repo, Stale(scratch)), apply: true, force: true, busy: busy, cancellationToken: Token)).Single();

        Assert.Equal(TidyOutcome.Refused, verdict.Outcome);
        Assert.True(Directory.Exists(scratch));
    }

    [Fact]
    public async Task A_linked_scratch_entry_is_removed_as_a_name_and_its_target_kept()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "creating a symbolic link needs developer mode on Windows");
        var repo = await RepoAsync();
        var target = _root.Folder("r/docs");
        await File.WriteAllTextAsync(Path.Join(target, "keep.md"), "keep", Token);
        _ = Directory.CreateDirectory(Path.Join(repo, "temp"));
        var folderLink = Path.Join(repo, "temp", "to-docs");
        var fileLink = Path.Join(repo, "temp", "to-keep");
        _ = Directory.CreateSymbolicLink(folderLink, target);
        _ = File.CreateSymbolicLink(fileLink, Path.Join(target, "keep.md"));

        var actions = await Tidy.CleanRepoAsync(Only(repo, Stale(folderLink), Stale(fileLink)), apply: true, force: true, cancellationToken: Token);

        Assert.All(actions, a => Assert.Equal(TidyOutcome.Cleaned, a.Outcome));
        Assert.False(Path.Exists(folderLink));
        Assert.False(Path.Exists(fileLink));
        Assert.True(File.Exists(Path.Join(target, "keep.md")));
    }

    // ---- worktrees -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_dirty_worktree_is_never_removed()
    {
        var repo = await RepoAsync();
        var tree = await WorktreeAsync(repo);
        await File.WriteAllTextAsync(Path.Join(tree, "first.txt"), "changed", Token);

        var verdict = (await Tidy.CleanRepoAsync(Only(repo, Merged(tree)), apply: true, mergedHeads: MergedSpike, cancellationToken: Token)).Single();

        Assert.Equal(new TidyAction("worktree-merged", tree, TidyOutcome.Refused, "uncommitted changes"), verdict);
        Assert.True(Directory.Exists(tree));
    }

    [Fact]
    public async Task An_untracked_file_also_blocks_removal()
    {
        var repo = await RepoAsync();
        var tree = await WorktreeAsync(repo);
        await File.WriteAllTextAsync(Path.Join(tree, "scratch.txt"), "notes I have not saved", Token);

        var verdict = (await Tidy.CleanRepoAsync(Only(repo, Merged(tree)), apply: true, mergedHeads: MergedSpike, cancellationToken: Token)).Single();

        Assert.Equal(TidyOutcome.Refused, verdict.Outcome);
        Assert.True(File.Exists(Path.Join(tree, "scratch.txt")));
    }

    [Fact]
    public async Task A_clean_worktree_is_removed_and_a_dry_run_names_the_command()
    {
        var repo = await RepoAsync();
        var tree = await WorktreeAsync(repo);

        var dry = (await Tidy.CleanRepoAsync(Only(repo, Merged(tree)), mergedHeads: MergedSpike, busy: new Busy([]), cancellationToken: Token)).Single();
        var applied = (await Tidy.CleanRepoAsync(Only(repo, Merged(tree)), apply: true, mergedHeads: MergedSpike, cancellationToken: Token)).Single();

        Assert.Equal(new TidyAction("worktree-merged", tree, TidyOutcome.WouldClean, $"git worktree remove {tree}"), dry);
        Assert.Equal(TidyOutcome.Cleaned, applied.Outcome);
        Assert.False(Directory.Exists(tree));
    }

    [Fact]
    public async Task Force_never_gets_past_git_on_a_dirty_worktree()
    {
        var repo = await RepoAsync();
        var tree = await WorktreeAsync(repo);
        await File.WriteAllTextAsync(Path.Join(tree, "first.txt"), "changed", Token);

        var verdict = (await Tidy.CleanRepoAsync(Only(repo, Merged(tree)), apply: true, force: true, mergedHeads: MergedSpike, cancellationToken: Token)).Single();

        Assert.Equal(TidyOutcome.Failed, verdict.Outcome);
        Assert.Contains("modified or untracked", verdict.Detail, StringComparison.Ordinal);
        Assert.True(Directory.Exists(tree));
    }

    [Fact]
    public async Task A_worktree_holding_the_only_copy_of_a_commit_goes_only_with_a_merged_pr_holding_it()
    {
        var repo = await RepoAsync();
        var tree = await WorktreeAsync(repo);
        await Repos.CommitAsync(tree, "spike-work");
        var heads = await MergedAtAsync(repo, 9, "spike", "spike");

        var local = (await Tidy.CleanRepoAsync(Only(repo, Merged(tree)), cancellationToken: Token)).Single();
        var other = (await Tidy.CleanRepoAsync(Only(repo, Merged(tree)), mergedHeads: new Dictionary<string, PullRequest>(), cancellationToken: Token)).Single();
        var merged = (await Tidy.CleanRepoAsync(Only(repo, Merged(tree)), mergedHeads: heads, cancellationToken: Token)).Single();

        Assert.Equal("1 commit(s) on no remote — pass --merged to ask GitHub", local.Detail);
        Assert.Equal("1 commit(s) on no remote", other.Detail);
        Assert.Equal(TidyOutcome.WouldClean, merged.Outcome);
    }

    [Fact]
    public async Task A_detached_worktree_with_its_own_commit_is_refused_even_with_merged_heads()
    {
        var repo = await RepoAsync();
        var tree = Path.Join(repo, "temp", "detached");
        _ = await Repos.GitAsync(repo, "worktree", "add", "-q", "--detach", tree);
        await Repos.CommitAsync(tree, "detached-work");

        var verdict = (await Tidy.CleanRepoAsync(Only(repo, Merged(tree)), mergedHeads: MergedSpike, cancellationToken: Token)).Single();

        Assert.Equal(new TidyAction("worktree-merged", tree, TidyOutcome.Refused, "1 commit(s) on no remote"), verdict);
    }

    [Fact]
    public async Task An_unborn_worktree_is_refused_even_forced()
    {
        var repo = await RepoAsync();
        var tree = await WorktreeAsync(repo);
        await Repos.CommitAsync(tree, "notes");
        _ = await Repos.GitAsync(repo, "update-ref", "-d", "refs/heads/spike");

        var verdict = (await Tidy.CleanRepoAsync(Only(repo, Merged(tree)), apply: true, force: true, mergedHeads: MergedSpike, cancellationToken: Token)).Single();

        Assert.Equal(new TidyAction("worktree-merged", tree, TidyOutcome.Refused, "unborn HEAD — see worktree-unborn-head"), verdict);
        Assert.True(File.Exists(Path.Join(tree, "notes.txt")));
    }

    [Fact]
    public async Task Another_clones_worktree_is_never_removed()
    {
        var repo = await RepoAsync();
        var tree = await WorktreeAsync(repo);
        var stale = PathResolution.Resolve(Path.Join(_root.Path, "stale"));
        _ = await Repos.GitAsync(_root.Path, "clone", "-q", Path.Join(_root.Path, "up"), stale);
        CopyFolder(Path.Join(repo, ".git", "worktrees", "spike"), Path.Join(stale, ".git", "worktrees", "spike"));

        var verdict = (await Tidy.CleanRepoAsync(Only(stale, Merged(tree)), apply: true, force: true, mergedHeads: MergedSpike, cancellationToken: Token)).Single();

        Assert.Equal(new TidyAction("worktree-merged", tree, TidyOutcome.Refused, "belongs to another clone — see worktree-foreign-owner"), verdict);
        Assert.True(Directory.Exists(tree));
    }

    [Fact]
    public async Task A_worktree_a_process_is_working_in_is_refused_even_forced_and_an_idle_one_still_goes()
    {
        var repo = await RepoAsync();
        var tree = await WorktreeAsync(repo);
        var report = Only(repo, Merged(tree));

        var below = (await Tidy.CleanRepoAsync(report, apply: true, force: true, mergedHeads: MergedSpike, busy: new Busy([Path.Join(PathResolution.Resolve(tree), "src")]), cancellationToken: Token)).Single();
        var unknown = (await Tidy.CleanRepoAsync(report, mergedHeads: MergedSpike, busy: Busy.Unknown, cancellationToken: Token)).Single();
        var forced = (await Tidy.CleanRepoAsync(report, force: true, mergedHeads: MergedSpike, busy: Busy.Unknown, cancellationToken: Token)).Single();
        var idle = (await Tidy.CleanRepoAsync(report, apply: true, mergedHeads: MergedSpike, busy: new Busy([Path.Join(repo, "docs")]), cancellationToken: Token)).Single();

        Assert.Equal("a process is working in it (a shell, agent or Orca terminal)", below.Detail);
        Assert.Equal(TidyOutcome.Refused, unknown.Outcome);
        Assert.Equal(TidyOutcome.WouldClean, forced.Outcome);
        Assert.Equal(TidyOutcome.Cleaned, idle.Outcome);
    }

    [Fact]
    public async Task A_worktree_with_another_nested_inside_it_is_refused_even_forced()
    {
        var repo = await RepoAsync();
        var outer = await WorktreeAsync(repo, "outer", "temp/outer");
        var inner = await WorktreeAsync(repo, "inner", "temp/outer/temp/inner");

        var verdict = (await Tidy.CleanRepoAsync(Only(repo, Merged(outer)), apply: true, force: true, cancellationToken: Token)).Single();

        Assert.Equal(new TidyAction("worktree-merged", outer, TidyOutcome.Refused, "holds a worktree"), verdict);
        Assert.True(Directory.Exists(inner));
    }

    [Fact]
    public async Task A_worktree_holding_a_separate_clone_in_its_ignored_temp_is_refused_even_forced()
    {
        var repo = await RepoAsync();
        var tree = await WorktreeAsync(repo);
        var nested = await Repos.InitAsync(Path.Join(tree, "temp", "nested"));

        var verdict = (await Tidy.CleanRepoAsync(Only(repo, Merged(tree)), apply: true, force: true, cancellationToken: Token)).Single();

        Assert.Equal(new TidyAction("worktree-merged", tree, TidyOutcome.Refused, "holds a git checkout"), verdict);
        Assert.True(Directory.Exists(Path.Join(nested, ".git")));
    }

    [Fact]
    public async Task A_folder_git_cannot_read_the_status_of_reads_as_uncommitted()
    {
        var repo = await RepoAsync();
        var broken = _root.Folder("r/temp/broken");
        await File.WriteAllTextAsync(Path.Join(broken, ".git"), "gitdir: /nowhere/at/all\n", Token);

        var verdict = (await Tidy.CleanRepoAsync(Only(repo, Merged(broken)), busy: new Busy([]), cancellationToken: Token)).Single();

        Assert.Equal(new TidyAction("worktree-merged", broken, TidyOutcome.Refused, "uncommitted changes"), verdict);
    }

    // ---- prune and filtering -------------------------------------------------------------------------------------

    [Fact]
    public async Task A_missing_worktree_is_pruned_once_and_checks_narrow_what_runs()
    {
        var repo = await RepoAsync();
        var tree = await WorktreeAsync(repo);
        Directory.Delete(tree, recursive: true);
        _ = await Repos.GitAsync(repo, "branch", "done");
        var report = await InspectAsync(repo);

        var dry = await Tidy.CleanRepoAsync(report, checks: ["worktree-prunable"], cancellationToken: Token);
        var applied = await Tidy.CleanRepoAsync(report, apply: true, checks: ["worktree-prunable"], cancellationToken: Token);

        Assert.Equal([new TidyAction("worktree-prunable", repo, TidyOutcome.WouldClean, "git worktree prune")], dry);
        Assert.Equal(TidyOutcome.Cleaned, applied.Single().Outcome);
        Assert.DoesNotContain("spike", await Repos.GitAsync(repo, "worktree", "list"), StringComparison.Ordinal);
        Assert.True(await HasBranchAsync(repo, "done"), "branch-merged was not in scope");
    }

    [Fact]
    public void Outcome_names_are_the_ones_the_table_and_summary_print()
    {
        Assert.Equal(
            ["cleaned", "would-clean", "refused", "failed"],
            Enum.GetValues<TidyOutcome>().Select(TidyAction.Name));
        Assert.Equal(
            ["worktree-prunable", "branch-upstream-gone", "branch-merged", "temp-stale-worktree", "worktree-merged", "temp-stale"],
            TidyChecks.All);
    }

    private static void CopyFolder(string from, string to)
    {
        _ = Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Join(to, Path.GetRelativePath(from, file));
            _ = Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }
}
