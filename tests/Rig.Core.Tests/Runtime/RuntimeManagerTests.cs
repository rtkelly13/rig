using System.Text.Json.Nodes;
using Rig.Core.Processes;
using Rig.Core.Roots;
using Rig.Core.Runtime;
using Rig.Core.Tests.Fakes;

namespace Rig.Core.Tests.Runtime;

/// <summary><c>RuntimeManager</c> over a throwaway git checkout, with uv, dotnet and the copy's checks answered by a fake.</summary>
public sealed class RuntimeManagerTests : IDisposable
{
    private readonly TempDir _temp = new();
    private readonly SystemProc _system = new();

    public void Dispose() => _temp.Dispose();

    private string Root => Path.Join(_temp.Path, "runtime");

    private string NewRepo()
    {
        var repo = _temp.Folder("repo");
        Git(repo, "init", "-q");
        return repo;
    }

    private string Commit(string repo, IReadOnlyDictionary<string, string> files)
    {
        foreach (var (name, text) in files)
        {
            var path = Path.Join(repo, name);
            _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }

        Git(repo, "add", "-A");
        Git(repo, "commit", "-q", "-m", "c");
        return Git(repo, "rev-parse", "HEAD").Trim();
    }

    private string Git(string workingDirectory, params string[] args)
    {
        var ran = _system.Run(new ProcRequest(
            ["git", "-c", "user.name=t", "-c", "user.email=t@example.com", "-c", "core.autocrlf=false", "-C", workingDirectory, .. args])
        {
            Timeout = TimeSpan.FromMinutes(2),
        });
        Assert.True(ran.Ok, ran.Err);
        return ran.Out;
    }

    private RuntimeManager Manager(Tools tools) => new(tools, Root, TimeProvider.System);

    [Fact]
    public void Install_copies_the_commit_not_the_working_tree()
    {
        var repo = NewRepo();
        var sha = Commit(repo, new Dictionary<string, string> { ["app.txt"] = "committed\n" });
        File.WriteAllText(Path.Join(repo, "app.txt"), "uncommitted edit\n");
        var tools = new Tools(_system);
        var manager = Manager(tools);

        var result = manager.Install(repo, "HEAD", fetch: false);

        var copy = Path.Join(Root, sha[..RuntimeManager.ShortShaLength]);
        Assert.True(result.Changed);
        Assert.Equal(copy, result.Copy);
        Assert.Equal("committed\n", File.ReadAllText(Path.Join(RuntimeManager.CurrentLink(Root), "app.txt")));
        Assert.Equal(sha, JsonNode.Parse(File.ReadAllText(Path.Join(copy, RuntimeManager.InstalledName)))!["sha"]!.GetValue<string>());
        var state = manager.ReadState();
        Assert.Equal(Path.GetFileName(copy), state.Current);
        Assert.Null(state.Previous);
        Assert.Equal(PathResolution.Resolve(repo), state.Repo);
        Assert.Contains(tools.Calls, call => call.Argv.SequenceEqual(
            ["uv", "sync", "--project", copy, "--frozen", "--no-dev", "--link-mode", "copy"]));
        Assert.Contains(tools.Calls, call => call.Argv.SequenceEqual(
            ["dotnet", "publish", Path.Join(copy, "src", "Rig.Launcher"), "-c", "Release", "-o", Path.Join(copy, "rig"), "-m:2", "-v", "q"]));
        var windows = OperatingSystem.IsWindows();
        Assert.Contains(tools.Calls, call => call.Argv.SequenceEqual([RuntimeManager.Published(copy, "rig", windows), "jobs", "run", "--help"]));
        Assert.Contains(tools.Calls, call => call.Argv.SequenceEqual([RuntimeManager.Published(copy, "rig", windows), "jobs", "install", "--help"]));
    }

    [Fact]
    public void A_copy_whose_rig_does_not_publish_is_removed_and_never_current()
    {
        var repo = NewRepo();
        _ = Commit(repo, new Dictionary<string, string> { ["app.txt"] = "1\n" });
        var tools = new Tools(_system) { Fails = request => request.Argv[0] == "dotnet" };

        var error = Assert.Throws<RuntimeInstallException>(() => Manager(tools).Install(repo, "HEAD", fetch: false));

        Assert.Contains("dotnet publish", error.Message, StringComparison.Ordinal);
        Assert.Null(Manager(tools).ReadState().Current);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Root));
    }

    [Fact]
    public void A_new_install_keeps_one_previous_copy_and_removes_older_ones()
    {
        var repo = NewRepo();
        var first = Commit(repo, new Dictionary<string, string> { ["app.txt"] = "1\n" })[..RuntimeManager.ShortShaLength];
        _ = Manager(new Tools(_system)).Install(repo, "HEAD", fetch: false);
        var second = Commit(repo, new Dictionary<string, string> { ["app.txt"] = "2\n" })[..RuntimeManager.ShortShaLength];
        _ = Manager(new Tools(_system)).Install(repo, "HEAD", fetch: false);
        var third = Commit(repo, new Dictionary<string, string> { ["app.txt"] = "3\n" })[..RuntimeManager.ShortShaLength];

        var result = Manager(new Tools(_system)).Install(repo, "HEAD", fetch: false);

        Assert.Equal(second, result.Previous);
        Assert.Equal([first], result.Removed);
        Assert.Equal(
            new[] { second, third }.Order(StringComparer.Ordinal),
            Directory.EnumerateDirectories(Root).Select(Path.GetFileName).Where(name => name != RuntimeManager.LinkName).Order(StringComparer.Ordinal)!);
        Assert.Equal("3\n", File.ReadAllText(Path.Join(RuntimeManager.CurrentLink(Root), "app.txt")));
    }

    [Fact]
    public void A_copy_that_fails_its_checks_never_becomes_current()
    {
        var repo = NewRepo();
        var good = Commit(repo, new Dictionary<string, string>
        {
            ["app.txt"] = "good\n",
            ["desktop-manager/workspace-plan.bundle.json"] = "{}",
        });
        _ = Manager(new Tools(_system)).Install(repo, "HEAD", fetch: false);
        var bad = Commit(repo, new Dictionary<string, string> { ["app.txt"] = "bad\n" });
        var tools = new Tools(_system)
        {
            Fails = request => request.Argv.Contains("chrome-tabs", StringComparer.Ordinal) && request.Argv.Contains("--help", StringComparer.Ordinal),
        };

        var error = Assert.Throws<RuntimeInstallException>(() => Manager(tools).Install(repo, "HEAD", fetch: false));

        Assert.Contains("current is unchanged", error.Message, StringComparison.Ordinal);
        Assert.Equal(good[..RuntimeManager.ShortShaLength], Manager(tools).ReadState().Current);
        Assert.Equal("good\n", File.ReadAllText(Path.Join(RuntimeManager.CurrentLink(Root), "app.txt")));
        Assert.False(Directory.Exists(Path.Join(Root, bad[..RuntimeManager.ShortShaLength])));
    }

    [Fact]
    public void The_check_validates_the_copys_workspace_plan()
    {
        var repo = NewRepo();
        _ = Commit(repo, new Dictionary<string, string> { ["desktop-manager/workspace-plan.bundle.json"] = "{}" });
        var tools = new Tools(_system);

        _ = Manager(tools).Install(repo, "HEAD", fetch: false);

        Assert.Contains(tools.Calls, call => call.Argv.Skip(1).Take(2).SequenceEqual(["workspace", "validate-plan"]));
    }

    [Fact]
    public void Installing_the_current_commit_again_changes_nothing()
    {
        var repo = NewRepo();
        _ = Commit(repo, new Dictionary<string, string> { ["app.txt"] = "1\n" });
        _ = Manager(new Tools(_system)).Install(repo, "HEAD", fetch: false);
        var tools = new Tools(_system);

        var result = Manager(tools).Install(repo, "HEAD", fetch: false);

        Assert.False(result.Changed);
        Assert.Empty(tools.Calls);
    }

    [Fact]
    public void Rollback_swaps_current_and_previous()
    {
        var repo = NewRepo();
        var first = Commit(repo, new Dictionary<string, string> { ["app.txt"] = "1\n" })[..RuntimeManager.ShortShaLength];
        _ = Manager(new Tools(_system)).Install(repo, "HEAD", fetch: false);
        var second = Commit(repo, new Dictionary<string, string> { ["app.txt"] = "2\n" })[..RuntimeManager.ShortShaLength];
        var manager = Manager(new Tools(_system));
        _ = manager.Install(repo, "HEAD", fetch: false);

        var state = manager.Rollback();

        Assert.Equal((first, second), (state.Current, state.Previous));
        Assert.Equal("1\n", File.ReadAllText(Path.Join(RuntimeManager.CurrentLink(Root), "app.txt")));
    }

    [Fact]
    public void Rollback_without_a_previous_copy_fails()
    {
        var error = Assert.Throws<RuntimeInstallException>(() => Manager(new Tools(_system)).Rollback());
        Assert.Contains("no previous copy", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Status_reports_the_copies_and_the_distance_to_the_compared_ref()
    {
        var repo = NewRepo();
        _ = Commit(repo, new Dictionary<string, string> { ["app.txt"] = "1\n" });
        var manager = Manager(new Tools(_system));
        var installed = manager.Install(repo, "HEAD", fetch: false);
        _ = Commit(repo, new Dictionary<string, string> { ["app.txt"] = "2\n" });

        var status = manager.Status("HEAD");

        Assert.Equal(installed.Sha, status.Current?.Sha);
        Assert.Equal("HEAD", status.Current?.Ref);
        Assert.NotNull(status.Current?.InstalledAt);
        Assert.Equal(1, status.Behind);
        Assert.Equal(RuntimeManager.CurrentLink(Root), status.Link);
    }

    [Fact]
    public void Status_without_an_install_has_no_current_copy()
    {
        var status = Manager(new Tools(_system)).Status("origin/main");

        Assert.Null(status.State.Current);
        Assert.Null(status.Current);
        Assert.Null(status.Previous);
        Assert.Null(status.Behind);
    }

    [Fact]
    public void Point_current_replaces_the_link_and_refuses_a_real_folder_it_did_not_write()
    {
        var first = _temp.Folder("first");
        var second = _temp.Folder("second");
        var manager = Manager(new Tools(_system));
        _ = Directory.CreateDirectory(Root);
        manager.PointCurrent(first);
        var target = new DirectoryInfo(RuntimeManager.CurrentLink(Root)).LinkTarget;
        Assert.Equal(PathResolution.Resolve(first), PathResolution.Resolve(target!));

        manager.PointCurrent(second);
        target = new DirectoryInfo(RuntimeManager.CurrentLink(Root)).LinkTarget;
        Assert.Equal(PathResolution.Resolve(second), PathResolution.Resolve(target!));

        var occupied = _temp.Folder("occupied");
        var occupiedRoot = Path.Join(occupied, "runtime");
        _ = Directory.CreateDirectory(Path.Join(occupiedRoot, RuntimeManager.LinkName));
        var error = Assert.Throws<RuntimeInstallException>(() =>
            new RuntimeManager(new Tools(_system), occupiedRoot, TimeProvider.System).PointCurrent(first));
        Assert.Contains("is not a link", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void State_is_written_in_the_python_shape_and_read_back()
    {
        var manager = Manager(new Tools(_system));

        manager.WriteState(new RuntimeState("abc", null, "/repo"));

        Assert.Equal(
            "{\n  \"current\": \"abc\",\n  \"previous\": null,\n  \"repo\": \"/repo\"\n}\n",
            File.ReadAllText(Path.Join(Root, RuntimeManager.StateName)));
        Assert.Equal(new RuntimeState("abc", null, "/repo"), manager.ReadState());
        Assert.Equal(new InstalledInfo(), RuntimeManager.ReadInstalled(Path.Join(Root, "missing")));
    }

    [Fact]
    public void Running_copy_is_true_only_under_the_runtime_root()
    {
        Assert.True(RuntimeManager.RunningCopy(Path.Join(Root, "abc", "src", "rig", "cli.py"), Root));
        Assert.False(RuntimeManager.RunningCopy(Path.Join(_temp.Path, "repo", "src", "rig", "cli.py"), Root));
    }

    [Fact]
    public void RuntimeInstallException_constructors_are_covered()
    {
        var e1 = new RuntimeInstallException();
        Assert.NotNull(e1);
        var inner = new InvalidOperationException("boom");
        var e2 = new RuntimeInstallException("msg", inner);
        Assert.Equal("msg", e2.Message);
        Assert.Same(inner, e2.InnerException);
    }

    [Fact]
    public void ReadState_and_ReadInstalled_throw_on_invalid_shapes()
    {
        var manager = Manager(new Tools(_system));
        _ = Directory.CreateDirectory(Root);
        File.WriteAllText(Path.Join(Root, RuntimeManager.StateName), "[1, 2, 3]");
        Assert.Throws<InvalidDataException>(() => manager.ReadState());

        var copy = Path.Join(Root, "bad-copy");
        _ = Directory.CreateDirectory(copy);
        File.WriteAllText(Path.Join(copy, RuntimeManager.InstalledName), "[]");
        Assert.Throws<InvalidDataException>(() => RuntimeManager.ReadInstalled(copy));
    }

    [Fact]
    public void Install_fails_when_repo_does_not_exist()
    {
        var manager = Manager(new Tools(_system));
        var error = Assert.Throws<RuntimeInstallException>(() => manager.Install(Path.Join(_temp.Path, "no-such-repo"), "HEAD", fetch: false));
        Assert.Contains("not a checkout: no such folder", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Rollback_fails_when_previous_folder_has_no_installed_json()
    {
        var manager = Manager(new Tools(_system));
        _ = Directory.CreateDirectory(Root);
        var emptyPrev = Path.Join(Root, "emptyprev");
        _ = Directory.CreateDirectory(emptyPrev);
        manager.WriteState(new RuntimeState(Current: "curr", Previous: "emptyprev", Repo: "/repo"));

        var error = Assert.Throws<RuntimeInstallException>(() => manager.Rollback());
        Assert.Contains("no previous copy", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PointCurrent_replaces_fallback_directory_when_installed_marker_exists()
    {
        var first = _temp.Folder("first");
        var manager = Manager(new Tools(_system));
        _ = Directory.CreateDirectory(Root);

        var currentDir = RuntimeManager.CurrentLink(Root);
        _ = Directory.CreateDirectory(currentDir);
        File.WriteAllText(Path.Join(currentDir, RuntimeManager.InstalledName), "{}");

        manager.PointCurrent(first);
        var target = new DirectoryInfo(currentDir).LinkTarget;
        Assert.Equal(PathResolution.Resolve(first), PathResolution.Resolve(target!));
    }

    [Fact]
    public void Executable_and_Published_return_correct_names_per_os()
    {
        Assert.EndsWith(Path.Join("bin", "rig"), RuntimeManager.Executable("/rt/curr", "rig", windows: false), StringComparison.Ordinal);
        Assert.EndsWith(Path.Join("Scripts", "rig.exe"), RuntimeManager.Executable(@"C:\rt\curr", "rig", windows: true), StringComparison.Ordinal);
        Assert.EndsWith(Path.Join("rig", "rig"), RuntimeManager.Published("/rt/curr", "rig", windows: false), StringComparison.Ordinal);
        Assert.EndsWith(Path.Join("rig", "rig.exe"), RuntimeManager.Published(@"C:\rt\curr", "rig", windows: true), StringComparison.Ordinal);
    }

    [Fact]
    public void CommitsBehind_reports_distance_or_null_when_git_fails()
    {
        var repo = NewRepo();
        var sha = Commit(repo, new Dictionary<string, string> { ["app.txt"] = "1\n" });
        var manager = Manager(new Tools(_system));

        Assert.Equal(0, manager.CommitsBehind(repo, sha, "HEAD"));
        Assert.Null(manager.CommitsBehind(repo, "non-existent-sha", "HEAD"));
    }

    [Fact]
    public void RunningCopy_detects_whether_path_is_under_runtime_root()
    {
        var root = _temp.Folder("runtime");
        var copy = Path.Join(root, "current", "bin", "rig");
        var external = _temp.Folder("other");

        Assert.True(RuntimeManager.RunningCopy(copy, root));
        Assert.False(RuntimeManager.RunningCopy(external, root));
    }

    [Fact]
    public void ReadInstalled_returns_empty_when_missing()
    {
        var empty = _temp.Folder("empty-copy");
        var info = RuntimeManager.ReadInstalled(empty);

        Assert.Null(info.Sha);
        Assert.Null(info.Ref);
        Assert.Null(info.Repo);
        Assert.Null(info.InstalledAt);
    }

    [Fact]
    public void Rollback_fails_when_state_has_no_previous()
    {
        var manager = Manager(new Tools(_system));
        _ = Directory.CreateDirectory(Root);
        manager.WriteState(new RuntimeState(Current: "curr", Previous: null, Repo: "/repo"));

        var error = Assert.Throws<RuntimeInstallException>(() => manager.Rollback());
        Assert.Contains("no previous copy", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PointCurrent_fails_when_target_is_file_or_directory_without_marker()
    {
        var manager = Manager(new Tools(_system));
        _ = Directory.CreateDirectory(Root);
        var link = RuntimeManager.CurrentLink(Root);
        File.WriteAllText(link, "not a directory or link");

        var error = Assert.Throws<RuntimeInstallException>(() => manager.PointCurrent(_temp.Folder("target")));
        Assert.Contains("exists and is not a link", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PointCurrent_backs_up_and_replaces_existing_real_directory()
    {
        var manager = Manager(new Tools(_system));
        _ = Directory.CreateDirectory(Root);
        var link = RuntimeManager.CurrentLink(Root);
        _ = Directory.CreateDirectory(link);
        File.WriteAllText(Path.Join(link, RuntimeManager.InstalledName), "{}");
        var target = _temp.Folder("target");

        manager.PointCurrent(target);

        Assert.True(Directory.Exists(link) || Path.Exists(link));
    }

    [Fact]
    public void Rollback_throws_when_no_previous_copy_recorded()
    {
        var manager = Manager(new Tools(_system));
        _ = Directory.CreateDirectory(Root);
        File.WriteAllText(Path.Join(Root, RuntimeManager.StateName), """{"current": "one", "previous": null, "repo": "r"}""");

        var error = Assert.Throws<RuntimeInstallException>(() => manager.Rollback());
        Assert.Equal("no previous copy to roll back to", error.Message);
    }

    [Fact]
    public void Rollback_throws_when_previous_folder_missing_installed_json()
    {
        var manager = Manager(new Tools(_system));
        _ = Directory.CreateDirectory(Root);
        _ = Directory.CreateDirectory(Path.Join(Root, "prev"));
        File.WriteAllText(Path.Join(Root, RuntimeManager.StateName), """{"current": "one", "previous": "prev", "repo": "r"}""");

        var error = Assert.Throws<RuntimeInstallException>(() => manager.Rollback());
        Assert.Equal("no previous copy to roll back to", error.Message);
    }

    [Fact]
    public void RunningCopy_detects_subpaths_under_root()
    {
        Assert.True(RuntimeManager.RunningCopy(Path.Join(Root, "abc", "rig"), Root));
        Assert.False(RuntimeManager.RunningCopy(_temp.Folder("other"), Root));
    }

    [Fact]
    public void CommitsBehind_returns_null_when_git_cannot_resolve()
    {
        var repo = NewRepo();
        var manager = Manager(new Tools(_system));
        Assert.Null(manager.CommitsBehind(repo, "nonexistent", "HEAD"));
    }

    [Fact]
    public void PointCurrent_when_link_is_not_a_link_throws()
    {
        var manager = Manager(new Tools(_system));
        var link = RuntimeManager.CurrentLink(Root);
        Directory.CreateDirectory(Root);
        File.WriteAllText(link, "not a directory or link");
        Assert.Throws<RuntimeInstallException>(() => manager.PointCurrent("any"));
    }

    [Fact]
    public void ResolveRef_throws_when_git_fails()
    {
        var proc = new FakeProc().Answer(new(1, "", "fatal: not a valid ref"), "git");
        var manager = new RuntimeManager(proc, Root, TimeProvider.System);
        var repo = _temp.Folder("repo-git-fail");
        Assert.Throws<RuntimeInstallException>(() => manager.Install(repo, "invalid-ref", fetch: false));
    }

    [Fact]
    public void RuntimeInstallException_and_models_constructors()
    {
        var ex1 = new RuntimeInstallException();
        Assert.NotNull(ex1);
        var inner = new InvalidOperationException("inner");
        var ex2 = new RuntimeInstallException("msg", inner);
        Assert.Equal("msg", ex2.Message);
        Assert.Same(inner, ex2.InnerException);

        var info = new InstalledInfo();
        Assert.Null(info.Sha);
        Assert.Null(info.Ref);
        Assert.Null(info.Repo);
        Assert.Null(info.InstalledAt);

        var status = new RuntimeStatus("root", "link", new RuntimeState(), null, null, null);
        Assert.Equal("root", status.Root);
        Assert.Equal("link", status.Link);
    }

    [Fact]
    public void ReadState_and_ReadInstalled_throw_on_empty_or_non_object_json()
    {
        var manager = Manager(new Tools(_system));
        _ = Directory.CreateDirectory(Root);
        var statePath = Path.Join(Root, RuntimeManager.StateName);

        File.WriteAllText(statePath, "null");
        Assert.Throws<InvalidDataException>(() => manager.ReadState());

        File.WriteAllText(statePath, "[]");
        Assert.Throws<InvalidDataException>(() => manager.ReadState());

        var copyDir = Path.Join(Root, "copy1");
        _ = Directory.CreateDirectory(copyDir);
        var installedPath = Path.Join(copyDir, RuntimeManager.InstalledName);

        File.WriteAllText(installedPath, "null");
        Assert.Throws<InvalidDataException>(() => RuntimeManager.ReadInstalled(copyDir));

        File.WriteAllText(installedPath, "[\"not an object\"]");
        Assert.Throws<InvalidDataException>(() => RuntimeManager.ReadInstalled(copyDir));
    }

    [Fact]
    public void Check_validates_workspace_plan_when_bundle_exists()
    {
        var repo = NewRepo();
        var sha = Commit(repo, new Dictionary<string, string>
        {
            ["desktop-manager/workspace-plan.bundle.json"] = "{}",
        });
        var tools = new Tools(_system);
        var manager = Manager(tools);

        var result = manager.Install(repo, "HEAD", fetch: false);

        var copy = Path.Join(Root, sha[..RuntimeManager.ShortShaLength]);
        Assert.True(result.Changed);
        Assert.Contains(tools.Calls, call => call.Argv.Contains("validate-plan"));
    }

    [Fact]
    public void Prune_cleans_partial_folders_and_untracked_copies()
    {
        var repo = NewRepo();
        var sha = Commit(repo, new Dictionary<string, string> { ["a.txt"] = "1\n" });
        var tools = new Tools(_system);
        var manager = Manager(tools);

        _ = manager.Install(repo, "HEAD", fetch: false);

        // Add a partial folder and another untracked installed copy
        var partial = Path.Join(Root, "deadbeef.partial");
        Directory.CreateDirectory(partial);
        File.WriteAllText(Path.Join(partial, "stale.tmp"), "stale");

        var untracked = Path.Join(Root, "oldcopy12345");
        Directory.CreateDirectory(untracked);
        File.WriteAllText(Path.Join(untracked, RuntimeManager.InstalledName), "{}");

        var sha2 = Commit(repo, new Dictionary<string, string> { ["a.txt"] = "2\n" });
        var res2 = manager.Install(repo, "HEAD", fetch: false);

        Assert.Contains("deadbeef.partial", res2.Removed);
        Assert.Contains("oldcopy12345", res2.Removed);
        Assert.False(Directory.Exists(partial));
        Assert.False(Directory.Exists(untracked));
    }

    /// <summary>Real git; uv, dotnet and the copy's commands are recorded and answered here.</summary>
    private sealed class Tools(IProc system) : IProc
    {
        public List<ProcRequest> Calls { get; } = [];

        public Func<ProcRequest, bool>? Fails { get; init; }

        public Ran Run(ProcRequest request)
        {
            if (request.Argv[0] == "git" || (OperatingSystem.IsWindows() && request.Argv[0] == "cmd"))
            {
                return system.Run(request);
            }

            Calls.Add(request);
            return Fails?.Invoke(request) == true ? new Ran(1, "Traceback: boom", "") : new Ran(0, "", "");
        }

        public Task<Ran> RunAsync(ProcRequest request, CancellationToken cancellationToken = default) => Task.FromResult(Run(request));
    }
}
