using Rig.Core.Estate;
using Rig.Core.Processes;
using Rig.Core.Reconciliation;
using Rig.Core.Roots;
using Rig.Core.Tests.Otel;
using Rig.Core.Worktrees;

namespace Rig.Core.Tests.Estate;

public sealed class TrimTests : IDisposable
{
    private readonly TempDir _temp = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _temp.Dispose();

    // ---- HardLinks ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_hardlinked_file_reports_two_links_and_the_same_identity_from_either_name()
    {
        var first = Write("a/one.bin", 10);
        var second = await HardLinkAsync(first, Path.Join(_temp.Folder("b"), "two.bin"));

        var a = HardLinks.Identify(first);
        var b = HardLinks.Identify(second);

        Assert.NotNull(a);
        Assert.Equal(2UL, a.Value.Links);
        Assert.Equal(a, b);
        Assert.Equal(1UL, HardLinks.Count(Write("c/alone.bin", 1)));
    }

    [Fact]
    public void A_missing_file_has_no_identity_and_counts_as_its_own()
    {
        var missing = Path.Join(_temp.Path, "missing");

        Assert.Null(HardLinks.Identify(missing));
        Assert.Equal(1UL, HardLinks.Count(missing));
    }

    // ---- TreeMeasure and DiskUsage.WalkTree -----------------------------------------------------------------------

    [Fact]
    public async Task A_file_also_named_outside_the_tree_is_not_freeable()
    {
        var tree = _temp.Folder("tree");
        _ = Write("tree/own.bin", 100);
        _ = Write("tree/.hidden/dot.bin", 50);
        _ = await HardLinkAsync(Write("outside.bin", 200), Path.Join(tree, "shared.bin"));

        var measure = TreeMeasure.Measure(tree);
        var size = DiskUsage.WalkTree(tree);

        Assert.Equal(350, measure.Apparent);
        Assert.False(measure.HoldsCheckout);
        if (!measure.Estimated)
        {
            Assert.Equal(150, measure.Freeable);
        }

        Assert.Equal(new TreeSize(2 * DiskUsage.Cluster, DiskUsage.Cluster), size);
    }

    [Fact]
    public async Task Both_names_inside_the_tree_count_the_file_once_as_freeable()
    {
        var tree = _temp.Folder("tree");
        _ = await HardLinkAsync(Write("tree/a.bin", 10), Path.Join(tree, "b.bin"));

        Assert.Equal(new TreeSize(DiskUsage.Cluster, 0), DiskUsage.WalkTree(tree));
    }

    [Fact]
    public void A_git_file_anywhere_below_stops_the_measure()
    {
        var tree = _temp.Folder("node_modules");
        _ = Write("node_modules/pkg/big.bin", 1000);
        _ = Write("node_modules/pkg/deep/.git", 10);

        Assert.Equal(TreeMeasure.Unmeasured, TreeMeasure.Measure(tree));
    }

    [Fact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void A_folder_the_walk_cannot_list_counts_as_holding_a_checkout()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix file modes.");
        var tree = _temp.Folder("node_modules");
        _ = Write("node_modules/pkg/big.bin", 1000);
        var sealedFolder = _temp.Folder("node_modules/sealed");
        File.SetUnixFileMode(sealedFolder, UnixFileMode.None);
        try
        {
            Assert.Equal(TreeMeasure.Unmeasured, TreeMeasure.Measure(tree));
            Assert.True(TreeMeasure.HasCheckoutBelow(tree));
        }
        finally
        {
            File.SetUnixFileMode(sealedFolder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void A_symlink_inside_the_tree_is_not_followed_when_measuring()
    {
        var tree = _temp.Folder("tree");
        _ = Write("elsewhere/huge.bin", 5000);
        _ = Directory.CreateSymbolicLink(Path.Join(tree, "link"), Path.Join(_temp.Path, "elsewhere"));

        Assert.Equal(0, TreeMeasure.Measure(tree).Apparent);
        Assert.Equal(new TreeSize(0, 0), DiskUsage.WalkTree(tree));
    }

    // ---- TreeRemoval ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Removal_deletes_a_link_by_name_and_leaves_its_target()
    {
        var tree = _temp.Folder("tree");
        var target = Write("store/keep.txt", 5);
        _ = Directory.CreateSymbolicLink(Path.Join(tree, "pkg"), Path.GetDirectoryName(target)!);
        _ = File.CreateSymbolicLink(Path.Join(tree, "file-link"), target);

        await Removal().RemoveAsync(tree, Token);

        Assert.False(Directory.Exists(tree));
        Assert.True(File.Exists(target));
    }

    [Fact]
    public async Task Removal_deletes_read_only_files_and_folders()
    {
        var tree = _temp.Folder("tree");
        var file = Write("tree/locked/ro.txt", 5);
        File.SetAttributes(file, FileAttributes.ReadOnly);
        _ = Write("tree/sealed/inner.txt", 5);
        var locked = Path.Join(tree, "locked");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            File.SetUnixFileMode(Path.Join(tree, "sealed"), UnixFileMode.None);
        }

        await Removal().RemoveAsync(tree, Token);

        Assert.False(Directory.Exists(tree));
    }

    [Fact]
    public async Task Removal_refuses_a_root_that_is_a_link_or_missing()
    {
        var target = _temp.Folder("real");
        var link = Path.Join(_temp.Path, "link");
        _ = Directory.CreateSymbolicLink(link, target);

        _ = await Assert.ThrowsAsync<IOException>(() => Removal().RemoveAsync(link, Token));
        _ = await Assert.ThrowsAsync<DirectoryNotFoundException>(() => Removal().RemoveAsync(Path.Join(_temp.Path, "gone"), Token));
        Assert.True(Directory.Exists(target));
    }

    [Fact]
    public async Task Removing_a_trimmable_reports_refused_removed_or_failed()
    {
        var folder = _temp.Folder("wt/node_modules");
        var item = new Trimmable(Path.Join(_temp.Path, "wt"), folder, "node_modules", new TreeMeasure(0, 0));

        Assert.Equal("refused", await Trim.RemoveAsync(item with { Refused = "no" }, Removal(), Token));
        Assert.Equal("removed", await Trim.RemoveAsync(item, Removal(), Token));
        Assert.StartsWith("failed: ", await Trim.RemoveAsync(item, Removal(), Token), StringComparison.Ordinal);
    }

    // ---- Trim: categories and candidates --------------------------------------------------------------------------

    [Fact]
    public void Build_output_folders_count_only_beside_what_makes_them()
    {
        _ = _temp.File("app/App.csproj");
        _ = _temp.File("sln/All.sln");
        _ = _temp.File("crate/Cargo.toml");

        Assert.Equal("node_modules", Trim.CategoryOf(Path.Join(_temp.Path, "x", "node_modules")));
        Assert.Equal("python venv", Trim.CategoryOf(Path.Join(_temp.Path, "x", ".venv")));
        Assert.Equal("dotnet build output", Trim.CategoryOf(Path.Join(_temp.Path, "app", "obj")));
        Assert.Equal("dotnet build output", Trim.CategoryOf(Path.Join(_temp.Path, "sln", "packages")));
        Assert.Equal("rust build output", Trim.CategoryOf(Path.Join(_temp.Path, "crate", "target")));
        Assert.Null(Trim.CategoryOf(Path.Join(_temp.Path, "crate", "bin")));
        Assert.Null(Trim.CategoryOf(Path.Join(_temp.Path, "app", "target")));
    }

    [Fact]
    public void Candidates_stop_at_a_match_at_the_depth_limit_and_at_temp()
    {
        var root = _temp.Folder("wt");
        _ = _temp.Folder("wt/node_modules/inner/node_modules");
        _ = _temp.Folder("wt/a/b/.venv");
        _ = _temp.Folder("wt/a/b/c/dist");
        _ = _temp.Folder("wt/temp/node_modules");
        _ = _temp.Folder("wt/.worktrees/x/node_modules");

        var found = Trim.Candidates(root).Select(item => Path.GetRelativePath(root, item.Path).Replace('\\', '/')).Order(StringComparer.Ordinal);

        Assert.Equal(["a/b/.venv", "node_modules"], found);
    }

    // ---- Trim: idleness, scanning and rechecking ------------------------------------------------------------------

    [Fact]
    public void Unreadable_bookkeeping_counts_as_now()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow.AddDays(30));
        var trim = new Trim(new Git(Repos.Proc, _temp.Path), clock);

        Assert.Equal(clock.Now, trim.LastActivity(_temp.Folder("no-git")));
        _ = _temp.Folder("empty-admin/.git");
        Assert.Equal(clock.Now, trim.LastActivity(Path.Join(_temp.Path, "empty-admin")));
    }

    [Fact]
    public async Task A_scan_lists_ignored_output_in_idle_worktrees_and_refuses_the_rest()
    {
        var (clone, worktree) = await CloneWithWorktreeAsync();
        _ = Write("wt/node_modules/pkg.bin", 300);
        _ = Write("wt/dist/out.js", 200);
        _ = Write("wt/.venv/lib.py", 100);
        _ = await Repos.GitAsync(worktree, "add", "-f", ".venv/lib.py");

        var found = await Idle().ScanAsync(clone, floor: 1);

        Assert.Equal(
            [("node_modules", ""), ("dist", "not gitignored"), (".venv", "contains tracked files")],
            found.Select(item => (Path.GetFileName(item.Path), item.Refused)));
        Assert.All(found, item => Assert.True(PathResolution.Same(PathResolution.Resolve(worktree), PathResolution.Resolve(item.Worktree))));
    }

    [Fact]
    public async Task A_folder_below_the_floor_is_dropped_but_one_holding_a_checkout_is_listed_as_refused()
    {
        var (clone, _) = await CloneWithWorktreeAsync();
        _ = Write("wt/node_modules/small.bin", 10);
        _ = Write("wt/dist/sub/.git", 1);

        var found = await Idle().ScanAsync(clone, floor: 1000);

        var only = Assert.Single(found);
        Assert.Equal(("dist", "holds a git checkout"), (Path.GetFileName(only.Path), only.Refused));
    }

    [Fact]
    public async Task Recent_main_and_locked_worktrees_are_not_idle()
    {
        var (clone, worktree) = await CloneWithWorktreeAsync();

        Assert.Empty(await new Trim(new Git(Repos.Proc, _temp.Path), new ManualClock(DateTimeOffset.UtcNow)).IdleWorktreesAsync(clone));
        _ = Assert.Single(await Idle().IdleWorktreesAsync(clone));

        _ = await Repos.GitAsync(clone, "worktree", "lock", worktree);
        Assert.Empty(await Idle().IdleWorktreesAsync(clone));
    }

    [Fact]
    public async Task A_recheck_refuses_what_changed_since_the_scan()
    {
        var (clone, worktree) = await CloneWithWorktreeAsync();
        _ = Write("wt/node_modules/pkg.bin", 300);
        var item = Assert.Single(await Idle().ScanAsync(clone, floor: 1));

        Assert.Equal(string.Empty, await Idle().RecheckAsync(item));
        Assert.Equal("worktree is in use again", await new Trim(new Git(Repos.Proc, _temp.Path), new ManualClock(DateTimeOffset.UtcNow)).RecheckAsync(item));
        Assert.Equal("no longer regenerable output", await Idle().RecheckAsync(item with { Category = "tool caches" }));

        _ = Write("wt/node_modules/nested/.git", 1);
        Assert.Equal("holds a git checkout", await Idle().RecheckAsync(item));
        File.Delete(Path.Join(item.Path, "nested", ".git"));

        _ = await Repos.GitAsync(clone, "worktree", "lock", worktree);
        Assert.Equal("worktree is locked", await Idle().RecheckAsync(item));
        _ = await Repos.GitAsync(clone, "worktree", "unlock", worktree);

        Directory.Delete(item.Path, recursive: true);
        Assert.Equal("no longer exists", await Idle().RecheckAsync(item));

        _ = Directory.CreateSymbolicLink(item.Path, _temp.Folder("elsewhere"));
        Assert.Equal("is a link", await Idle().RecheckAsync(item));
    }

    [Fact]
    public async Task A_recheck_refuses_a_main_checkout()
    {
        var (_, worktree) = await CloneWithWorktreeAsync();
        var item = new Trimmable(worktree, _temp.Folder("wt/node_modules"), "node_modules", new TreeMeasure(1, 1));
        var stray = await Repos.InitAsync(_temp.Folder("stray"));
        var unregistered = item with { Worktree = stray, Path = _temp.Folder("stray/node_modules") };

        Assert.Equal("no longer a linked worktree", await Idle().RecheckAsync(unregistered));
    }

    [Fact]
    public async Task A_folder_outside_its_worktree_or_failing_git_is_refused()
    {
        var (_, worktree) = await CloneWithWorktreeAsync();
        var trim = Idle();

        Assert.Equal("resolves outside the worktree", await trim.RefusalAsync(worktree, _temp.Folder("other"), ignored: true));
        Assert.Equal("resolves outside the worktree", await trim.RefusalAsync(worktree, worktree, ignored: true));
        Assert.Equal("contains tracked files", await trim.RefusalAsync(_temp.Folder("plain"), _temp.Folder("plain/node_modules"), ignored: true));
    }

    [Fact]
    public async Task PlanAsync_and_ApplyAsync_execute_reconciler_pipeline()
    {
        var (clone, worktree) = await CloneWithWorktreeAsync();
        var candidate = Path.Join(worktree, "node_modules");
        Directory.CreateDirectory(candidate);
        File.WriteAllBytes(Path.Join(candidate, "dep.js"), [1, 2, 3]);

        var trim = Idle();
        var target = new TrimTarget(clone, IdleDays: 0, Floor: 0, Removal: Removal());
        var plan = await trim.PlanAsync(target, Token);

        Assert.NotNull(plan);
        Assert.Single(plan.Changes);
        Assert.Equal(ChangeKind.WouldChange, plan.Changes[0].Kind);

        var errors = await trim.ApplyAsync(plan, Token);
        Assert.Empty(errors);
        Assert.Equal(ChangeKind.Changed, plan.Changes[0].Kind);
        Assert.False(Directory.Exists(candidate));
    }

    [Fact]
    public async Task ApplyAsync_returns_error_when_removal_is_missing()
    {
        var trim = Idle();
        var target = new TrimTarget("nonexistent", Removal: null);
        var plan = new TrimPlan { Target = target, Items = [] };
        var errors = await trim.ApplyAsync(plan, Token);
        Assert.Contains("trim requires a removal port to apply", errors);
    }

    [Fact]
    public void CategoryOf_detects_all_build_outputs()
    {
        var dir = _temp.Folder("build-test");
        File.WriteAllText(Path.Join(dir, "test.csproj"), "<Project/>");
        File.WriteAllText(Path.Join(dir, "test.sln"), "");
        File.WriteAllText(Path.Join(dir, "Cargo.toml"), "");

        Assert.Equal("dotnet build output", Trim.CategoryOf(Path.Join(dir, "bin")));
        Assert.Equal("dotnet build output", Trim.CategoryOf(Path.Join(dir, "obj")));
        Assert.Equal("dotnet build output", Trim.CategoryOf(Path.Join(dir, "packages")));
        Assert.Equal("rust build output", Trim.CategoryOf(Path.Join(dir, "target")));
        Assert.Equal("js build output", Trim.CategoryOf(Path.Join(dir, "dist")));
        Assert.Equal("js build output", Trim.CategoryOf(Path.Join(dir, ".next")));
        Assert.Equal("js build output", Trim.CategoryOf(Path.Join(dir, ".turbo")));
        Assert.Equal("js build output", Trim.CategoryOf(Path.Join(dir, "storybook-static")));
        Assert.Equal("python venv", Trim.CategoryOf(Path.Join(dir, ".venv")));
        Assert.Equal("python venv", Trim.CategoryOf(Path.Join(dir, "venv")));
        Assert.Equal("tool caches", Trim.CategoryOf(Path.Join(dir, ".ruff_cache")));
        Assert.Equal("tool caches", Trim.CategoryOf(Path.Join(dir, ".pytest_cache")));
        Assert.Equal("tool caches", Trim.CategoryOf(Path.Join(dir, ".mypy_cache")));
        Assert.Equal("tool caches", Trim.CategoryOf(Path.Join(dir, ".tox")));
        Assert.Equal("tool caches", Trim.CategoryOf(Path.Join(dir, ".nox")));
        Assert.Null(Trim.CategoryOf(Path.Join(dir, "src")));

        var fsharpDir = _temp.Folder("fsharp-build");
        File.WriteAllText(Path.Join(fsharpDir, "test.fsproj"), "<Project/>");
        Assert.Equal("dotnet build output", Trim.CategoryOf(Path.Join(fsharpDir, "bin")));

        var vbDir = _temp.Folder("vb-build");
        File.WriteAllText(Path.Join(vbDir, "test.vbproj"), "<Project/>");
        Assert.Equal("dotnet build output", Trim.CategoryOf(Path.Join(vbDir, "obj")));

        var noProjectDir = _temp.Folder("no-project-build");
        Assert.Null(Trim.CategoryOf(Path.Join(noProjectDir, "bin")));
        Assert.Null(Trim.CategoryOf(Path.Join(noProjectDir, "obj")));
        Assert.Null(Trim.CategoryOf(Path.Join(noProjectDir, "packages")));
        Assert.Null(Trim.CategoryOf(Path.Join(noProjectDir, "target")));
    }

    [Fact]
    public void IsStrictlyBelow_verifies_hierarchy()
    {
        var root = _temp.Folder("root");
        var child = _temp.Folder("root/child");
        var grandChild = _temp.Folder("root/child/sub");
        var outside = _temp.Folder("outside");

        Assert.True(Trim.IsStrictlyBelow(child, root));
        Assert.True(Trim.IsStrictlyBelow(grandChild, root));
        Assert.False(Trim.IsStrictlyBelow(root, root));
        Assert.False(Trim.IsStrictlyBelow(outside, root));
        Assert.False(Trim.IsStrictlyBelow(root, child));
    }

    [Fact]
    public async Task RemoveAsync_handles_refused_success_and_failure()
    {
        var refused = new Trimmable("/wt", "/wt/ref", "cat", new TreeMeasure(1, 1), Refused: "locked");
        var removal = Removal();
        Assert.Equal("refused", await Trim.RemoveAsync(refused, removal, Token));

        var targetDir = _temp.Folder("target-to-remove");
        await File.WriteAllTextAsync(Path.Join(targetDir, "file.txt"), "hello", Token);
        var trimmable = new Trimmable(_temp.Path, targetDir, "cat", new TreeMeasure(1, 1));
        Assert.Equal("removed", await Trim.RemoveAsync(trimmable, removal, Token));
        Assert.False(Directory.Exists(targetDir));

        var nonExistentItem = new Trimmable(_temp.Path, Path.Join(_temp.Path, "does-not-exist"), "cat", new TreeMeasure(1, 1));
        var failure = await Trim.RemoveAsync(nonExistentItem, removal, Token);
        Assert.StartsWith("failed:", failure, StringComparison.Ordinal);

        await Assert.ThrowsAsync<ArgumentNullException>(() => Trim.RemoveAsync(null!, removal, Token));
        await Assert.ThrowsAsync<ArgumentNullException>(() => Trim.RemoveAsync(trimmable, null!, Token));
    }

    [Fact]
    public async Task RecheckAsync_validates_existence_and_category()
    {
        var trim = Idle();
        await Assert.ThrowsAsync<ArgumentNullException>(() => trim.RecheckAsync(null!));

        var missing = new Trimmable(_temp.Path, Path.Join(_temp.Path, "nonexistent-dir"), "node_modules", new TreeMeasure(1, 1));
        Assert.Equal("no longer exists", await trim.RecheckAsync(missing));

        var (clone, worktree) = await CloneWithWorktreeAsync();
        var candidateDir = Directory.CreateDirectory(Path.Join(worktree, "real-dir")).FullName;
        var mismatch = new Trimmable(worktree, candidateDir, "different-category", new TreeMeasure(1, 1));
        Assert.Equal("no longer regenerable output", await trim.RecheckAsync(mismatch));

        var outsideDir = Directory.CreateDirectory(Path.Join(_temp.Path, "outside-wt", "node_modules")).FullName;
        var outsideItem = new Trimmable(worktree, outsideDir, "node_modules", new TreeMeasure(1, 1));
        Assert.Equal("resolves outside the worktree", await trim.RecheckAsync(outsideItem));

        var checkoutDir = Directory.CreateDirectory(Path.Join(worktree, "sub-checkout", "node_modules")).FullName;
        Directory.CreateDirectory(Path.Join(checkoutDir, ".git"));
        var checkoutItem = new Trimmable(worktree, checkoutDir, "node_modules", new TreeMeasure(1, 1));
        Assert.Equal("holds a git checkout", await trim.RecheckAsync(checkoutItem));
    }

    [Fact]
    public async Task ApplyAsync_handles_recheck_refusal_and_removal_failure()
    {
        var (clone, worktree) = await CloneWithWorktreeAsync();
        var dir = Path.Join(worktree, "node_modules");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Join(dir, "temp.js"), "content", Token);

        var trim = Idle();
        var target = new TrimTarget(clone, IdleDays: 0, Floor: 0, Removal: Removal());
        var plan = await trim.PlanAsync(target, Token);

        // Delete directory before apply so recheck fails with "no longer exists"
        Directory.Delete(dir, recursive: true);
        _ = await trim.ApplyAsync(plan, Token);
        Assert.Single(plan.Changes);
        Assert.Equal(ChangeKind.Refused, plan.Changes[0].Kind);
        Assert.Equal("no longer exists", plan.Changes[0].Detail);
    }

    [Fact]
    public void TrimPlan_Errors_and_Trimmable_properties()
    {
        var target = new TrimTarget("/repo", 14, 5000, null);
        Assert.Equal("/repo", target.Repo);
        Assert.Equal(14, target.IdleDays);
        Assert.Equal(5000, target.Floor);
        Assert.Null(target.Removal);

        var measure = new TreeMeasure(100, 200);
        Assert.Equal(100, measure.Apparent);
        Assert.Equal(200, measure.Freeable);

        var item1 = new Trimmable("/wt", "/wt/node_modules", "node_modules", measure);
        Assert.Equal("/wt", item1.Worktree);
        Assert.Equal("/wt/node_modules", item1.Path);
        Assert.Equal("node_modules", item1.Category);
        Assert.Equal(measure, item1.Size);
        Assert.Equal(ChangeKind.WouldChange, item1.Kind);
        Assert.Equal(string.Empty, item1.Detail);
        Assert.Equal("/wt/node_modules", item1.Subject);

        var item2 = new Trimmable("/wt", "/wt/dist", "js build output", new TreeMeasure(200, 200), Refused: "in use");
        var item3 = new Trimmable("/wt", "/wt/bin", "dotnet build output", new TreeMeasure(300, 300))
        {
            Kind = ChangeKind.Error,
            Detail = "failed to delete",
        };

        var plan = new TrimPlan { Target = target, Items = [item1, item2, item3] };

        Assert.Equal(ChangeKind.Refused, item2.Kind);
        Assert.Equal("in use", item2.Detail);
        Assert.Equal(ChangeKind.Error, item3.Kind);
        Assert.Equal("failed to delete", item3.Detail);
        Assert.Equal(["/wt/bin: failed to delete"], plan.Errors);
        Assert.Equal(3, plan.Changes.Count);
    }

    // ---- helpers --------------------------------------------------------------------------------------------------

    private static TreeRemoval Removal() => new(Repos.Proc);

    private Trim Idle() => new(new Git(Repos.Proc, _temp.Path), new ManualClock(DateTimeOffset.UtcNow.AddDays(30)));

    private async Task<(string Clone, string Worktree)> CloneWithWorktreeAsync()
    {
        var clone = await Repos.PublishedAsync(_temp, "node_modules/\n");
        var worktree = Path.Join(_temp.Path, "wt");
        _ = await Repos.GitAsync(clone, "worktree", "add", "-q", "-b", "feat", worktree);
        return (clone, worktree);
    }

    [Fact]
    public void Trim_contracts_and_properties()
    {
        var item = new Trimmable("/wt", "/wt/bin", "bin", new TreeMeasure(10, 10));
        Assert.Equal("/wt/bin", item.Subject);
        Assert.Equal(ChangeKind.WouldChange, item.Kind);
        Assert.Equal(string.Empty, item.Detail);

        item.Kind = ChangeKind.Changed;
        item.Detail = "trimmed";
        Assert.Equal(ChangeKind.Changed, item.Kind);
        Assert.Equal("trimmed", item.Detail);

        var refusedItem = new Trimmable("/wt", "/wt/obj", "obj", new TreeMeasure(5, 5), Refused: "tracked file inside");
        Assert.Equal(ChangeKind.Refused, refusedItem.Kind);
        Assert.Equal("tracked file inside", refusedItem.Detail);

        var errItem = new Trimmable("/wt", "/wt/err", "err", new TreeMeasure(2, 2)) { Kind = ChangeKind.Error, Detail = "failed" };
        var target = new TrimTarget("/repo", 14, 50);
        var plan = new TrimPlan { Target = target, Items = [item, refusedItem, errItem] };

        Assert.Equal("/repo", plan.Target.Repo);
        Assert.Equal(14, plan.Target.IdleDays);
        Assert.Equal(50, plan.Target.Floor);
        Assert.Equal(3, plan.Changes.Count);
        Assert.Single(plan.Errors);
        Assert.Equal("/wt/err: failed", plan.Errors[0]);
    }

    private string Write(string relative, int bytes)
    {
        var path = _temp.File(relative);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    private static async Task<string> HardLinkAsync(string existing, string link)
    {
        string[] command = OperatingSystem.IsWindows() ? ["fsutil", "hardlink", "create", link, existing] : ["ln", existing, link];
        var ran = await Repos.Proc.RunAsync(new ProcRequest(command), Token);
        Assert.True(ran.Ok, ran.Err);
        return link;
    }
}
