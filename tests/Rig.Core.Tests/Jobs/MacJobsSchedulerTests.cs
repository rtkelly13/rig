using System.Runtime.InteropServices;
using System.Text;
using System.Xml.Linq;
using Rig.Core.Jobs;
using Rig.Core.Processes;
using Rig.Core.Tests.Fakes;

namespace Rig.Core.Tests.Jobs;

/// <summary>The finite Mac schedule and its installation boundaries, tested without launchd.</summary>
public sealed class MacJobsSchedulerTests : IDisposable
{
    private readonly TempDir _temp = new();

    public void Dispose() => _temp.Dispose();

    private MacJobsScheduler Scheduler(FakeProc proc) => new(proc, _temp.Path, Path.Join(_temp.Path, "jobs"), "/code", "/opt/homebrew/bin:/usr/bin:/bin", _ => Task.FromResult("gui/501"));

    private JobsInstallTarget Target() => new(_temp.Path, "unused-windows-plan", _temp.File("rig/rig"));

    private static XElement Value(XElement dict, string key) => dict.Elements("key").Single(x => x.Value == key).ElementsAfterSelf().First();

    [Fact]
    public void The_schedule_has_one_finite_tick_at_login_and_every_fifteen_minutes()
    {
        var target = Target();
        var xml = XDocument.Parse(Encoding.UTF8.GetString(Scheduler(new FakeProc()).Render(target))).Root!.Element("dict")!;

        Assert.Equal("true", Value(xml, "RunAtLoad").Name.LocalName);
        Assert.Equal("900", Value(xml, "StartInterval").Value);
        Assert.DoesNotContain(xml.Elements("key"), x => x.Value == "KeepAlive");
        Assert.Equal(new[] { target.Rig, "jobs", "run", "--trigger", "tick", "--scheduled" }, Value(xml, "ProgramArguments").Elements().Select(x => x.Value));
        Assert.Equal(target.Root, Value(xml, "WorkingDirectory").Value);
        Assert.Equal("/code", Value(Value(xml, "EnvironmentVariables"), "CODE_ROOT").Value);
        Assert.Contains("/opt/homebrew/bin", Value(Value(xml, "EnvironmentVariables"), "PATH").Value, StringComparison.Ordinal);
        Assert.Equal("10", Value(xml, "Nice").Value);
    }

    [Fact]
    public async Task Install_registers_only_the_mac_job_and_an_identical_reinstall_does_not_stop_it()
    {
        var proc = new FakeProc().Answer(new Ran(0, "", ""), "launchctl");
        var scheduler = Scheduler(proc);
        var target = Target();

        await scheduler.InstallAsync(target, TestContext.Current.CancellationToken);
        Assert.True(File.Exists(scheduler.PlistPath));
        Assert.Equal(["bootout", "bootstrap"], proc.Requests.Select(x => x.Argv[1]));
        Assert.All(proc.Requests, x => Assert.Equal("gui/501", x.Argv[2]));
        proc.Requests.Clear();

        await scheduler.InstallAsync(target, TestContext.Current.CancellationToken);
        Assert.Equal("print", Assert.Single(proc.Requests).Argv[1]);
        Assert.True(await scheduler.RegisteredAsync(TestContext.Current.CancellationToken));
        Assert.True(await scheduler.UninstallAsync(TestContext.Current.CancellationToken));
        Assert.False(File.Exists(scheduler.PlistPath));
        Assert.False(await scheduler.RegisteredAsync(TestContext.Current.CancellationToken));
        Assert.False(await scheduler.UninstallAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_missing_executable_preserves_the_existing_schedule()
    {
        var proc = new FakeProc();
        var scheduler = Scheduler(proc);
        Directory.CreateDirectory(Path.GetDirectoryName(scheduler.PlistPath)!);
        await File.WriteAllTextAsync(scheduler.PlistPath, "existing", TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(() => scheduler.InstallAsync(new(_temp.Path, "plan", "missing-rig"), TestContext.Current.CancellationToken));

        Assert.Empty(proc.Requests);
        Assert.Equal("existing", await File.ReadAllTextAsync(scheduler.PlistPath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task The_native_installer_selects_the_host_executable_without_platform_flags()
    {
        var app = _temp.Folder("app");
        var root = Path.Join(app, "runtime", "abc");
        var executable = OperatingSystem.IsWindows() ? "rig.exe" : "rig";
        var rig = _temp.File("app/runtime/current/rig/" + executable);
        _ = _temp.File("app/runtime/current/desktop-manager/workspace-plan.bundle.json");

        var target = await JobsInstallTarget.ResolveAsync(root, app, "unused-plan", new FakeProc(), JobsLauncher.Direct, null, TestContext.Current.CancellationToken);

        Assert.Equal(rig, target.Rig);
        Assert.Null(target.Pythonw);
    }

    [Fact]
    public async Task A_runtime_target_on_mac_needs_neither_a_windows_plan_nor_pythonw()
    {
        var app = _temp.Folder("app");
        var root = Path.Join(app, "runtime", "abc");
        _ = _temp.File("app/runtime/current/rig/rig");
        var target = await JobsInstallTarget.ResolveAsync(root, app, "absent-plan", new FakeProc(), JobsLauncher.Pythonw, null, OSPlatform.OSX, TestContext.Current.CancellationToken);

        Assert.Equal(Path.Join(app, "runtime", "current", "rig", "rig"), target.Rig);
        Assert.Null(target.Pythonw);
    }

    [Fact]
    public void A_mac_tick_prunes_old_traces_without_waiting_for_a_windows_shell()
    {
        var old = _temp.File("traces/old.ndjson");
        var fresh = _temp.File("traces/fresh.ndjson");
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-30));
        var seams = new ScheduledSeams(TimeProvider.System, _ => throw new InvalidOperationException("Windows shell must not be probed"), Path.Join(_temp.Path, "traces")) { PruneOnTick = true };

        Assert.Equal(0, ScheduledRun.Run(JobTrigger.Tick, Path.Join(_temp.Path, "jobs"), seams, (_, _) => 0));

        Assert.False(File.Exists(old));
        Assert.True(File.Exists(fresh));
    }

    [Fact]
    public void Mac_jobs_keep_the_complete_estate_and_never_schedule_windows_desktop_actions()
    {
        var env = new JobsEnvironment("/runtime/rig", "no-plan", "/code/work/ws", "/code/personal", [], "feed", "orca") { Platform = OSPlatform.OSX };
        var jobs = JobRegistry.Default(env);

        Assert.DoesNotContain(jobs, x => x.Name is "restore" or "align");
        Assert.Equal("web", jobs[0].Name);
        Assert.Equal("chrome-organise", jobs[^1].Name);
        Assert.Equal("0", jobs[^1].Argv[^1]);
        Assert.Contains(jobs, x => x.Name == "estate-decisions");
        Assert.Contains(jobs, x => x.Name == "triage-refresh");
        Assert.Contains(jobs, x => x.Name == "estate-orca-worktrees" && x.Argv.Contains("--apply", StringComparer.Ordinal));
        var tidy = jobs.Single(x => x.Name == "estate-tidy");
        Assert.Contains("--all", tidy.Argv);
        Assert.Contains("--merged", tidy.Argv);
        Assert.Contains("--apply", tidy.Argv);
        Assert.Contains("/code", tidy.Argv);
        Assert.DoesNotContain("--force", tidy.Argv);
        Assert.DoesNotContain("temp-stale", tidy.Argv);
        Assert.Equal(Path.Join("/code/work/ws", ".venv", "bin", "python"), env.WorkspacePython);
    }
}
