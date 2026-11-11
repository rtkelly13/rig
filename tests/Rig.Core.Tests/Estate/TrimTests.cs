using Rig.Core.Estate;
using Rig.Core.Processes;
using Rig.Core.Roots;
using Rig.Core.Tests.Otel;

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
