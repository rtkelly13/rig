using Rig.Core.Estate;
using Rig.Core.Processes;
using Rig.Core.Tests.Fakes;
using Rig.Core.Tests.Otel;

namespace Rig.Core.Tests.Estate;

public sealed class CachesTests : IDisposable
{
    private const string SessionId = "0a1b2c3d-4e5f-6a7b-8c9d-0e1f2a3b4c5d";

    private static readonly DateTimeOffset Now = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);

    private readonly TempDir _temp = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _temp.Dispose();

    // Every tool is "found" under its own name, so the fake answers by name; none of them is ever really run.
    private static Caches With(IProc proc, params string[] installed) =>
        new(proc, name => installed.Contains(name) ? name : null, new TreeRemoval(proc), new ManualClock(Now));

    // ---- tool caches ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_dry_run_locates_and_measures_each_cache_but_prunes_nothing()
    {
        var uv = Fill("uv", ("a.bin", 600), ("sub/b.bin", 400));
        var nuget = Fill("http-cache", ("x.dat", 300));
        var proc = new FakeProc()
            .Answer(new Ran(0, uv + "\n", string.Empty), "uv", "cache", "dir")
            .Answer(new Ran(0, $"info: something\nhttp-cache: {nuget}\n", string.Empty), "dotnet", "nuget", "locals", "http-cache", "--list");
        var caches = With(proc, "uv", "dotnet");

        var uvResult = await caches.PruneToolCacheAsync(Caches.Uv, apply: false, Token);
        var pnpm = await caches.PruneToolCacheAsync(Caches.Pnpm, apply: false, Token);
        var nugetResult = await caches.PruneToolCacheAsync(Caches.NuGet, apply: false, Token);

        Assert.Equal(new Pruned("uv cache", "uv cache prune", PruneStatus.WouldRun, uv, 1000), uvResult);
        Assert.Equal(new Pruned("pnpm store", "pnpm store prune", PruneStatus.Skipped, "pnpm not found on PATH"), pnpm);
        Assert.Equal(
            new Pruned("nuget http-cache", "dotnet nuget locals http-cache --clear", PruneStatus.WouldRun, nuget, 300, 300),
            nugetResult);
        Assert.Equal(2, proc.Requests.Count);
        Assert.All(proc.Requests, request => Assert.Equal(Caches.ProbeTimeout, request.Timeout));
    }

    [Fact]
    public async Task An_applied_prune_reports_what_the_folder_lost()
    {
        var store = Fill("store", ("keep.bin", 200), ("stale.bin", 800));
        var fake = new FakeProc()
            .Answer(new Ran(0, store, string.Empty), "pnpm", "store", "path")
            .Answer(new Ran(0, "Removed 1 package", string.Empty), "pnpm", "store", "prune");
        var proc = new ActingProc(fake, request =>
        {
            if (request.Argv.SequenceEqual(["pnpm", "store", "prune"]))
            {
                File.Delete(Path.Join(store, "stale.bin"));
            }
        });

        var result = await With(proc, "pnpm").PruneToolCacheAsync(Caches.Pnpm, apply: true, Token);

        Assert.Equal(new Pruned("pnpm store", "pnpm store prune", PruneStatus.Done, store, 1000, 800), result);
        Assert.Equal(Caches.PruneTimeout, fake.Requests[^1].Timeout);
    }

    [Fact]
    public async Task A_failed_prune_keeps_the_size_it_measured_and_says_why()
    {
        var cache = Fill("uv", ("a.bin", 10));
        var proc = new FakeProc()
            .Answer(new Ran(0, cache, string.Empty), "uv", "cache", "dir")
            .Answer(new Ran(2, "partial\n", "warning: retrying\nerror: cache is locked\n\n"), "uv", "cache", "prune");

        var result = await With(proc, "uv").PruneToolCacheAsync(Caches.Uv, apply: true, Token);

        Assert.Equal(new Pruned("uv cache", "uv cache prune", PruneStatus.Failed, "error: cache is locked", 10), result);
    }

    [Theory]
    [InlineData(1, "", "", "cannot locate the cache: exit 1")]
    [InlineData(1, "", "spawn ENOEXEC\n", "cannot locate the cache: spawn ENOEXEC")]
    [InlineData(0, "\n  \n", "", "cannot locate the cache: exit 0")]
    [InlineData(0, "http-cache:\n", "", "cannot locate the cache: http-cache:")]
    public async Task A_cache_that_cannot_be_located_fails_without_pruning(int code, string output, string error, string reason)
    {
        var proc = new FakeProc().Answer(new Ran(code, output, error), "dotnet");

        var result = await With(proc, "dotnet").PruneToolCacheAsync(Caches.NuGet, apply: true, Token);

        Assert.Equal(PruneStatus.Failed, result.Status);
        Assert.Equal(reason, result.Reason);
        Assert.Single(proc.Requests);
    }

    [Fact]
    public async Task A_located_folder_that_does_not_exist_measures_as_empty()
    {
        var missing = Path.Join(_temp.Path, "never-made");
        var proc = new FakeProc().Answer(new Ran(0, missing, string.Empty), "uv", "cache", "dir");

        var result = await With(proc, "uv").PruneToolCacheAsync(Caches.Uv, apply: false, Token);

        Assert.Equal(0, result.SizeBefore);
    }

    // ---- docker ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Docker_with_its_daemon_down_is_skipped_after_one_probe()
    {
        var proc = new FakeProc().Answer(new Ran(1, string.Empty, "Cannot connect to the Docker daemon"), "docker", "info");

        var result = await With(proc, "docker").PruneDockerAsync(apply: true, Token);

        Assert.Equal(PruneStatus.Skipped, result.Status);
        Assert.Equal("Docker daemon not running", result.Reason);
        Assert.Equal(["docker", "info", "--format", "{{.ServerVersion}}"], proc.Requests.Single().Argv);
    }

    [Fact]
    public async Task Docker_missing_is_skipped_without_running_anything()
    {
        var proc = new FakeProc();

        var result = await With(proc).PruneDockerAsync(apply: true, Token);

        Assert.Equal(new Pruned("docker build cache", "docker builder prune -f --filter until=168h", PruneStatus.Skipped, "docker not found on PATH"), result);
        Assert.Empty(proc.Requests);
    }

    [Fact]
    public async Task Docker_dry_run_cannot_know_the_size_and_an_applied_prune_reads_its_total()
    {
        var proc = new FakeProc()
            .Answer(new Ran(0, "27.3.1", string.Empty), "docker", "info")
            .Answer(new Ran(0, "Deleted build cache objects:\nabc\nTotal:\t1.5GB\n", string.Empty), "docker", "builder", "prune");
        var caches = With(proc, "docker");

        var dry = await caches.PruneDockerAsync(apply: false, Token);
        var done = await caches.PruneDockerAsync(apply: true, Token);

        Assert.Equal(PruneStatus.WouldRun, dry.Status);
        Assert.Equal("size is known only after the prune", dry.Reason);
        Assert.Null(dry.Freed);
        Assert.Equal(PruneStatus.Done, done.Status);
        Assert.Equal(1_500_000_000, done.Freed);
        Assert.Equal(["docker", "builder", "prune", "-f", "--filter", "until=168h"], proc.Requests[^1].Argv);
        Assert.Equal(Caches.PruneTimeout, proc.Requests[^1].Timeout);
    }

    [Fact]
    public async Task A_docker_prune_with_no_total_frees_zero_and_a_failed_one_says_why()
    {
        var proc = new FakeProc()
            .Answer(new Ran(0, "27.3.1", string.Empty), "docker", "info")
            .Answer(new Ran(0, "nothing to prune\n", string.Empty), "docker", "builder", "prune");
        var caches = With(proc, "docker");

        var nothing = await caches.PruneDockerAsync(apply: true, Token);
        _ = proc.Answer(new Ran(1, string.Empty, "error: permission denied"), "docker", "builder", "prune");
        var failed = await caches.PruneDockerAsync(apply: true, Token);

        Assert.Equal(0, nothing.Freed);
        Assert.Equal(new Pruned("docker build cache", "docker builder prune -f --filter until=168h", PruneStatus.Failed, "error: permission denied"), failed);
    }

    [Theory]
    [InlineData("Total:\t0B\n", 0L)]
    [InlineData("Total reclaimed space: 12.5MB", 12_500_000L)]
    [InlineData("Total:  3kB", 3000L)]
    [InlineData("Total:\t2GiB\r\n", 2_000_000_000L)]
    [InlineData("ID\nTotal: 7B\n", 7L)]
    public void Docker_totals_read_in_decimal_units(string output, long expected) =>
        Assert.Equal(expected, Caches.DockerReclaimed(output));

    [Theory]
    [InlineData("nothing to prune")]
    [InlineData("Total: 1.2.3GB")]
    [InlineData("  Total: 5MB")]
    public void Docker_output_without_a_readable_total_is_unknown(string output) =>
        Assert.Null(Caches.DockerReclaimed(output));

    // ---- claude temp ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Claude_temp_removes_only_idle_session_folders()
    {
        var root = _temp.Folder("claude");
        var idle = Session("project-a", SessionId, ageDays: 9, bytes: 500);
        var upper = Session("project-b", "AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE", ageDays: 1, bytes: 70);
        var notASession = Session("project-a", "scratch", ageDays: 30, bytes: 40);
        var proc = new FakeProc();
        var caches = With(proc);

        var dry = await caches.PruneClaudeTempAsync(root, apply: false, idleDays: 7, Token);
        var done = await caches.PruneClaudeTempAsync(root, apply: true, idleDays: 7, Token);

        Assert.Equal(
            new Pruned("claude temp", $"delete session dirs idle 7+ days under {root}", PruneStatus.WouldRun, "1 idle session dir(s)", 500, 500),
            dry);
        Assert.Equal(new Pruned("claude temp", dry.Command, PruneStatus.Done, "1 session dir(s) removed", 500, 500), done);
        Assert.False(Directory.Exists(idle));
        Assert.True(Directory.Exists(upper));
        Assert.True(Directory.Exists(notASession));
    }

    [Fact]
    public void An_uppercase_session_id_counts_and_a_recent_write_anywhere_inside_keeps_a_session()
    {
        var root = _temp.Folder("claude");
        var upper = Session("p", "AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE", ageDays: 10, bytes: 5);
        var busy = Session("p", SessionId, ageDays: 10, bytes: 5);
        var deep = Path.Join(busy, "a", "b", "fresh.txt");
        _ = Directory.CreateDirectory(Path.GetDirectoryName(deep)!);
        File.WriteAllText(deep, "x");
        File.SetLastWriteTimeUtc(deep, Now.UtcDateTime.AddHours(-1));
        Directory.SetLastWriteTimeUtc(busy, Now.UtcDateTime.AddDays(-10));

        var idle = With(new FakeProc()).IdleSessions(root, idleDays: 7);

        Assert.Equal([upper], idle.Select(s => s.Path));
        Assert.Equal(5, idle.Single().Bytes);
    }

    [Fact]
    public void A_freshly_made_empty_session_folder_is_not_idle()
    {
        var root = _temp.Folder("claude");
        var empty = _temp.Folder(Path.Join("claude", "p", SessionId));
        Directory.SetLastWriteTimeUtc(empty, Now.UtcDateTime.AddMinutes(-5));

        Assert.Empty(With(new FakeProc()).IdleSessions(root, idleDays: 7));
    }

    [Fact]
    public async Task A_negative_idle_period_throws_before_anything_is_pruned()
    {
        var root = _temp.Folder("claude");
        var fresh = Session("p", SessionId, ageDays: 0, bytes: 5);
        var proc = new FakeProc();
        var caches = With(proc, "uv");

        _ = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => caches.PruneAllAsync(apply: true, root, idleDays: -1, Token));
        _ = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => caches.PruneClaudeTempAsync(root, apply: true, idleDays: -1, Token));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => caches.IdleSessions(root, idleDays: -1));
        Assert.True(Directory.Exists(fresh));
        Assert.Empty(proc.Requests);
    }

    [Fact]
    public async Task A_missing_claude_temp_is_skipped()
    {
        var root = Path.Join(_temp.Path, "absent");

        var result = await With(new FakeProc()).PruneClaudeTempAsync(root, apply: true, idleDays: 3, Token);

        Assert.Equal(new Pruned("claude temp", $"delete session dirs idle 3+ days under {root}", PruneStatus.Skipped, $"{root} does not exist"), result);
    }

    // ---- everything -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Nothing_installed_and_no_claude_temp_skips_all_five_in_order()
    {
        var proc = new FakeProc();

        var results = await With(proc).PruneAllAsync(apply: true, Path.Join(_temp.Path, "absent"), idleDays: 7, Token);

        Assert.Equal(["uv cache", "pnpm store", "nuget http-cache", "claude temp", "docker build cache"], results.Select(r => r.Category));
        Assert.All(results, r => Assert.Equal(PruneStatus.Skipped, r.Status));
        Assert.Equal(["skipped", "skipped", "skipped", "skipped", "skipped"], results.Select(r => r.StatusName));
        Assert.Empty(proc.Requests);
    }

    [Fact]
    public void Status_names_print_as_python_wrote_them()
    {
        Assert.Equal("would run", new Pruned("c", "x", PruneStatus.WouldRun).StatusName);
        Assert.Equal("done", new Pruned("c", "x", PruneStatus.Done).StatusName);
        Assert.Equal("failed", new Pruned("c", "x", PruneStatus.Failed).StatusName);
    }

    [Fact]
    public void A_tool_found_nowhere_is_null_and_the_install_folders_include_the_user_bins()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        Assert.Null(Caches.FindTool("rig-no-such-tool-" + Guid.NewGuid().ToString("N")));
        Assert.Contains(Path.Join(home, ".local", "bin"), Caches.InstallDirs());
        Assert.Contains(Path.Join(home, ".cargo", "bin"), Caches.InstallDirs());
        Assert.Equal(7, Caches.InstallDirs().Count);
        Assert.EndsWith("claude", Caches.DefaultClaudeTemp(), StringComparison.Ordinal);
    }

    // ---- footprint ------------------------------------------------------------------------------------------------

    [Fact]
    public void A_footprint_sums_every_file_including_git_and_takes_the_newest_write()
    {
        var tree = Fill("tree", (".git/HEAD", 3), ("a/b/c.bin", 7));
        File.SetLastWriteTimeUtc(Path.Join(tree, ".git", "HEAD"), Now.UtcDateTime.AddDays(-1));
        File.SetLastWriteTimeUtc(Path.Join(tree, "a", "b", "c.bin"), Now.UtcDateTime.AddDays(-3));

        var footprint = Footprint.Of(tree);

        Assert.Equal(10, footprint.Bytes);
        Assert.Equal(Now.UtcDateTime.AddDays(-1), footprint.Newest);
        Assert.Equal(new Footprint(0, DateTime.MinValue), Footprint.Of(Path.Join(_temp.Path, "absent")));
    }

    [Fact]
    public void A_footprint_never_follows_a_link()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Creating a symlink needs developer mode on Windows.");
        var outside = Fill("outside", ("big.bin", 900));
        var tree = Fill("tree", ("own.bin", 4));
        _ = Directory.CreateSymbolicLink(Path.Join(tree, "linked"), outside);

        Assert.Equal(4, Footprint.Of(tree).Bytes);
    }

    // ---- helpers --------------------------------------------------------------------------------------------------

    private string Fill(string folder, params (string Name, int Bytes)[] files)
    {
        var root = _temp.Folder(folder);
        foreach (var (name, bytes) in files)
        {
            var path = Path.Join(root, name);
            _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, new byte[bytes]);
        }

        return root;
    }

    // A session folder under claude/<project>/ holding one file, it and the file last written ageDays ago.
    private string Session(string project, string name, int ageDays, int bytes)
    {
        var folder = _temp.Folder(Path.Join("claude", project, name));
        var file = Path.Join(folder, "out.txt");
        File.WriteAllBytes(file, new byte[bytes]);
        var when = Now.UtcDateTime.AddDays(-ageDays);
        File.SetLastWriteTimeUtc(file, when);
        Directory.SetLastWriteTimeUtc(folder, when);
        return folder;
    }

    // Runs a side effect before answering, as a real prune changes the folder it was pointed at.
    private sealed class ActingProc(IProc inner, Action<ProcRequest> act) : IProc
    {
        public Ran Run(ProcRequest request)
        {
            act(request);
            return inner.Run(request);
        }

        public Task<Ran> RunAsync(ProcRequest request, CancellationToken cancellationToken = default)
        {
            act(request);
            return inner.RunAsync(request, cancellationToken);
        }
    }
}
