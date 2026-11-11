using Rig.Core.Estate;
using Rig.Core.Processes;
using Rig.Core.Tests.Otel;

namespace Rig.Core.Tests.Estate;

/// <summary><see cref="RepoChecks"/> and <see cref="Exposure"/> over real git clones in a temp folder.</summary>
public sealed class RepoChecksTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_clean_ignored_clone_has_no_findings()
    {
        using var root = new TempDir();
        var repo = await Repos.PublishedAsync(root, "temp/\nTODO.md\n");
        _ = root.Folder("r/temp");
        _ = root.File("r/TODO.md");

        var report = await Checks(root).InspectAsync(repo);

        Assert.Empty(report.Findings);
        Assert.False(report.Blocked);
        Assert.Equal(0, report.WorktreeCount);
        Assert.Equal(1, report.BranchCount);
    }

    [Fact]
    public async Task Worktrees_report_dirty_unpushed_detached_locked_and_off_convention()
    {
        using var root = new TempDir();
        var repo = await Repos.PublishedAsync(root);
        var inTemp = Path.Join(repo, "temp", "feature");
        var outside = Path.Join(root.Path, "beside");
        var detached = Path.Join(repo, "temp", "loose");
        _ = await Repos.GitAsync(repo, "worktree", "add", "-q", "-b", "feature", inTemp);
        _ = await Repos.GitAsync(repo, "worktree", "add", "-q", "-b", "side", outside);
        _ = await Repos.GitAsync(repo, "worktree", "add", "-q", "--detach", detached);
        _ = await Repos.GitAsync(repo, "worktree", "lock", "--reason", "busy", outside);
        await Repos.CommitAsync(inTemp, "feature");
        await File.WriteAllTextAsync(Path.Join(inTemp, "wip.txt"), "wip", Token);

        var findings = await Checks(root).CheckWorktreesAsync(repo);

        var checks = findings.Select(f => (f.Check, Path.GetFileName(f.Subject))).ToList();
        Assert.Contains(("worktree-dirty", "feature"), checks);
        Assert.Contains(("worktree-unpushed", "feature"), checks);
        Assert.Contains(("worktree-locked", "beside"), checks);
        Assert.Contains(("worktree-off-convention", "beside"), checks);
        Assert.Contains(("worktree-detached", "loose"), checks);
        Assert.DoesNotContain(("worktree-off-convention", "feature"), checks);
        Assert.Equal("Locked: busy.", findings.Single(f => f.Check == "worktree-locked").Message);
        Assert.Equal("1 commit(s) on no remote — they exist only here.", findings.First(f => f.Check == "worktree-unpushed").Message);
    }

    [Fact]
    public async Task A_removed_worktree_folder_is_prunable_and_nothing_else()
    {
        using var root = new TempDir();
        var repo = await Repos.PublishedAsync(root);
        var tree = Path.Join(repo, "temp", "gone");
        _ = await Repos.GitAsync(repo, "worktree", "add", "-q", "-b", "gone", tree);
        Directory.Delete(tree, recursive: true);

        var finding = Assert.Single(await Checks(root).CheckWorktreesAsync(repo));

        Assert.Equal("worktree-prunable", finding.Check);
        Assert.True(finding.Actionable);
        Assert.Equal("git worktree prune", finding.Fix);
    }

    [Fact]
    public async Task An_unborn_worktree_names_its_last_commit_when_this_clone_still_has_it()
    {
        using var root = new TempDir();
        var repo = await Repos.PublishedAsync(root);
        var tree = Path.Join(repo, "temp", "lost");
        _ = await Repos.GitAsync(repo, "worktree", "add", "-q", "-b", "lost", tree);
        await Repos.CommitAsync(tree, "lost-work");
        var tip = await Repos.GitAsync(tree, "rev-parse", "HEAD");
        _ = await Repos.GitAsync(repo, "update-ref", "-d", "refs/heads/lost");

        var finding = Assert.Single(await Checks(root).CheckWorktreesAsync(repo), f => f.Check == "worktree-unborn-head");

        Assert.Equal(Severity.Error, finding.Severity);
        Assert.Contains($"Its last commit, {tip[..10]}, is still here", finding.Message, StringComparison.Ordinal);
        Assert.Equal($"cd {repo} && git branch lost {tip}", finding.Fix);
    }

    [Fact]
    public async Task A_worktree_whose_git_file_points_at_another_clone_is_foreign_and_left_alone()
    {
        using var root = new TempDir();
        var repo = await Repos.PublishedAsync(root);
        var tree = Path.Join(repo, "temp", "moved");
        _ = await Repos.GitAsync(repo, "worktree", "add", "-q", "-b", "moved", tree);
        var other = root.Folder("other/.git/worktrees/moved");
        await File.WriteAllTextAsync(Path.Join(tree, ".git"), $"gitdir: {other}\n", Token);

        var finding = Assert.Single(await Checks(root).CheckWorktreesAsync(repo));

        Assert.Equal("worktree-foreign-owner", finding.Check);
        Assert.Contains(other, finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_merged_pr_finishes_the_worktree_that_holds_its_branch()
    {
        using var root = new TempDir();
        var repo = await Repos.PublishedAsync(root);
        var tree = Path.Join(repo, "temp", "done");
        _ = await Repos.GitAsync(repo, "worktree", "add", "-q", "-b", "done", tree);
        await Repos.CommitAsync(tree, "shipped");
        var head = await Repos.GitAsync(tree, "rev-parse", "HEAD");
        var merged = new Dictionary<string, PullRequest> { ["done"] = new(42, "MERGED", "done", head) };

        var finding = Assert.Single(await Checks(root).CheckWorktreesAsync(repo, merged), f => f.Check == "worktree-merged");

        Assert.Equal("Holds `done`, merged in #42. The worktree is finished.", finding.Message);
        Assert.True(finding.Actionable);
    }

    [Fact]
    public async Task Branches_report_gone_merged_and_local_only_but_never_main_or_current()
    {
        using var root = new TempDir();
        var upstream = await Repos.InitAsync(root.Folder("up"));
        _ = await Repos.GitAsync(upstream, "branch", "doomed");
        var repo = root.Folder("r");
        _ = await Repos.GitAsync(root.Path, "clone", "-q", upstream, repo);
        _ = await Repos.GitAsync(repo, "branch", "--track", "doomed", "origin/doomed");
        _ = await Repos.GitAsync(upstream, "branch", "-D", "doomed");
        _ = await Repos.GitAsync(repo, "fetch", "-q", "--prune");
        _ = await Repos.GitAsync(repo, "branch", "landed");
        _ = await Repos.GitAsync(repo, "checkout", "-q", "-b", "mine");
        await Repos.CommitAsync(repo, "mine");
        _ = await Repos.GitAsync(repo, "checkout", "-q", "-b", "current");

        var findings = await Checks(root).CheckBranchesAsync(repo);

        Assert.Equal(
            [("branch-upstream-gone", "doomed"), ("branch-merged", "landed"), ("branch-local-only", "mine")],
            findings.Select(f => (f.Check, f.Subject)).ToList());
        Assert.Equal("Tracked `origin/doomed`, which no longer exists — the remote branch was deleted.", findings[0].Message);
        Assert.True(findings[0].Actionable);
    }

    [Fact]
    public async Task Temp_reports_unignored_and_stale_entries_by_age()
    {
        using var root = new TempDir();
        var repo = await Repos.InitAsync(root.Folder("r"));
        var stale = root.File("r/temp/old.log");
        await File.WriteAllTextAsync(stale, new string('x', 2048), Token);
        File.SetLastWriteTimeUtc(stale, Now.UtcDateTime.AddDays(-20));
        var fresh = root.File("r/temp/new.log");
        File.SetLastWriteTimeUtc(fresh, Now.UtcDateTime.AddDays(-1));
        var checks = Checks(root);

        var entries = await checks.ScanTempAsync(repo);
        var findings = await checks.CheckTempAsync(repo, entries, RepoChecks.DefaultStaleAfter);

        Assert.Equal(["new.log", "old.log"], entries.Select(e => Path.GetFileName(e.Path)));
        Assert.Equal(2048, entries[1].Bytes);
        Assert.Equal(["temp-not-ignored", "temp-stale"], findings.Select(f => f.Check));
        Assert.Equal("Scratch, untouched for 20 days (2.0KB).", findings[1].Message);
        Assert.Equal($"echo 'temp/' >> {Path.Join(repo, ".gitignore")}", findings[0].Fix);
    }

    [Fact]
    public async Task Repo_state_reports_stashes_detached_head_and_an_unignored_todo_once()
    {
        using var root = new TempDir();
        var repo = await Repos.InitAsync(root.Folder("r"));
        await File.WriteAllTextAsync(Path.Join(repo, "first.txt"), "changed", Token);
        _ = await Repos.GitAsync(repo, "stash", "push", "-q", "-m", "tag");
        _ = await Repos.GitAsync(repo, "checkout", "-q", "--detach");
        _ = root.File("r/TODO.md");

        var findings = await Checks(root).CheckRepoStateAsync(repo);

        Assert.Equal(["stash-entries", "detached-head", "todo-not-ignored"], findings.Select(f => f.Check));
        Assert.StartsWith("1 stash entry. Every worktree", findings[0].Message, StringComparison.Ordinal);
        Assert.Equal("TODO.md", findings[2].Subject);
    }

    [Fact]
    public async Task Exposure_counts_local_only_commits_dirt_stashes_and_carrying_branches()
    {
        using var root = new TempDir();
        var repo = await Repos.InitAsync(root.Folder("r"), "git@github.com:o/r.git");
        _ = await Repos.GitAsync(repo, "checkout", "-q", "-b", "side");
        await Repos.CommitAsync(repo, "side");
        await File.WriteAllTextAsync(Path.Join(repo, "loose.txt"), "x", Token);

        var exposure = await Exposure.InspectAsync(Git(root), repo, perBranch: true);

        Assert.Equal(2, exposure.LocalOnlyCommits);
        Assert.Equal(["main", "side"], exposure.Branches);
        Assert.Equal(1, exposure.DirtyFiles);
        Assert.Equal(0, exposure.Stashes);
        Assert.True(exposure.HasRemote);
        Assert.Equal("git@github.com:o/r.git", exposure.Origin);
    }

    [Fact]
    public async Task Exposure_of_a_clone_without_a_remote_says_so()
    {
        using var root = new TempDir();
        var repo = await Repos.InitAsync(root.Folder("r"));

        var exposure = await Exposure.InspectAsync(Git(root), repo, perBranch: false);

        Assert.False(exposure.HasRemote);
        Assert.Empty(exposure.Branches);
        Assert.Equal(string.Empty, exposure.Origin);
    }

    [Fact]
    public void Admin_dir_reflog_tip_and_foreign_owner_read_files_alone()
    {
        using var root = new TempDir();
        var tree = root.Folder("tree");
        var admin = root.Folder("own/worktrees/tree");
        File.WriteAllText(Path.Join(tree, ".git"), "gitdir: ../own/worktrees/tree\n");
        _ = root.File("own/worktrees/tree/logs/HEAD");
        File.WriteAllText(Path.Join(admin, "logs", "HEAD"), "0000 aaaa x\naaaa bbbb y\nbbbb 0000 z\n");

        Assert.Equal(Path.Join(tree, "../own/worktrees/tree"), RepoChecks.WorktreeAdminDir(tree));
        Assert.Null(RepoChecks.ForeignAdminDir(tree, Path.Join(root.Path, "own", "worktrees")));
        Assert.NotNull(RepoChecks.ForeignAdminDir(tree, Path.Join(root.Path, "else", "worktrees")));
        Assert.Equal("bbbb", RepoChecks.ReflogTip(admin));
        Assert.Equal(string.Empty, RepoChecks.ReflogTip(Path.Join(root.Path, "missing")));
        Assert.Null(RepoChecks.WorktreeAdminDir(root.Path));
    }

    private static Git Git(TempDir root) => new(Repos.Proc, root.Path);

    private static RepoChecks Checks(TempDir root) => new(Git(root), new ManualClock(Now));
}
