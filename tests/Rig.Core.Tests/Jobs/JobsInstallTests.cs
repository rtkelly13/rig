using System.Text;
using System.Xml.Linq;
using Rig.Core.Jobs;
using Rig.Core.Processes;
using Rig.Core.Tests.Fakes;

namespace Rig.Core.Tests.Jobs;

public sealed class JobsInstallTests : IDisposable
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
    private readonly TempDir _temp = new();

    public void Dispose() => _temp.Dispose();

    private static readonly JobsInstallTarget Target = new("root", "plan.json", "rig.exe", "pythonw.exe");

    // A finished run is bounded so a hung one is killed; the guard watches for as long as the session lasts.
    [Theory]
    [InlineData(CoordinatorTasks.Logon, "LogonTrigger", "jobs run --trigger logon --scheduled", "PT2H")]
    [InlineData(CoordinatorTasks.Tick, "TimeTrigger", "jobs run --trigger tick --scheduled", "PT2H")]
    [InlineData(CoordinatorTasks.Guard, "LogonTrigger", "desktop workspace guard --config \"plan.json\"", "PT0S")]
    public void Each_task_runs_its_rig_command_least_privileged_on_its_trigger(string name, string trigger, string rig, string limit)
    {
        var task = Render(name);

        Assert.Equal(Target.Command, Value(task, "Actions", "Exec", "Command"));
        Assert.Equal(Target.Arguments(rig), Value(task, "Actions", "Exec", "Arguments"));
        Assert.Equal(Target.Root, Value(task, "Actions", "Exec", "WorkingDirectory"));
        Assert.NotNull(task.Element(Ns + "Triggers")!.Element(Ns + trigger));
        Assert.Equal(limit, Value(task, "Settings", "ExecutionTimeLimit"));
        Assert.Equal("LeastPrivilege", Value(task, "Principals", "Principal", "RunLevel"));
    }

    [Fact]
    public void The_tick_repeats_every_tick_interval() =>
        Assert.Equal($"PT{Job.TickMinutes}M", Value(Render(CoordinatorTasks.Tick), "Triggers", "TimeTrigger", "Repetition", "Interval"));

    [Fact]
    public void A_guard_that_dies_is_restarted()
    {
        var restart = Render(CoordinatorTasks.Guard).Element(Ns + "Settings")!.Element(Ns + "RestartOnFailure");

        Assert.Equal("PT1M", restart?.Element(Ns + "Interval")?.Value);
        Assert.Equal("10", restart?.Element(Ns + "Count")?.Value);
    }

    [Fact]
    public void An_unknown_task_is_refused() =>
        Assert.Throws<ArgumentException>(() => JobsTaskXml.Render("unknown", new("root", "plan", "rig"), "user", DateTimeOffset.UtcNow));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_main_checkout_or_unversioned_copy_uses_the_development_publish_and_user_plan(bool git)
    {
        var root = Path.Join(_temp.Path, "checkout");
        var plan = Path.Join(_temp.Path, "user-plan.json");
        var target = new JobsInstallTarget(root, plan, Path.Join(root, "temp", "jobs-runtime", "rig.exe"));
        Targets(target);
        var proc = new FakeProc().Answer(new Ran(git ? 0 : 1, Path.Join(root, ".git"), ""), "git");

        Assert.Equal(target, await JobsInstallTarget.ResolveAsync(root, Path.Join(_temp.Path, "app"), plan, proc, JobsPlatform.Windows, JobsLauncher.Direct, null, TestContext.Current.CancellationToken));
        Assert.Equal(root, Assert.Single(proc.Requests).WorkingDirectory);
    }

    [Theory]
    [InlineData("copy")]
    [InlineData("current")]
    public async Task Runtime_registration_always_uses_current_and_the_bundled_plan(string copy)
    {
        var app = Path.Join(_temp.Path, "app");
        var current = Path.Join(app, "runtime", "current");
        var target = new JobsInstallTarget(current, Path.Join(current, "desktop-manager", "workspace-plan.bundle.json"), Path.Join(current, "rig", "rig.exe"), Path.Join(_temp.Path, "py", "pythonw.exe"));
        Targets(target);
        var proc = new FakeProc();

        Assert.Equal(target, await JobsInstallTarget.ResolveAsync(Path.Join(app, "runtime", copy), app, "ignored", proc, JobsPlatform.Windows, JobsLauncher.Pythonw, target.Pythonw, TestContext.Current.CancellationToken));
        Assert.Empty(proc.Requests);
    }

    [Fact]
    public async Task Macos_resolves_the_published_rig_and_needs_no_plan_or_pythonw()
    {
        var root = Path.Join(_temp.Path, "checkout");
        var plan = Path.Join(_temp.Path, "user-plan.json");
        var target = new JobsInstallTarget(root, plan, Path.Join(root, "temp", "jobs-runtime", "rig"));
        Directory.CreateDirectory(Path.GetDirectoryName(target.Rig)!);
        await File.WriteAllTextAsync(target.Rig, "fixture", TestContext.Current.CancellationToken);
        var proc = new FakeProc().Answer(new Ran(0, Path.Join(root, ".git"), ""), "git");

        var resolved = await JobsInstallTarget.ResolveAsync(root, Path.Join(_temp.Path, "app"), plan, proc, JobsPlatform.MacOS, JobsLauncher.Pythonw, null, TestContext.Current.CancellationToken);

        Assert.Equal(target, resolved);
        Assert.Null(resolved.Pythonw);
        Assert.DoesNotContain(proc.Requests, request => request.Argv[0] == "uv");
    }

    [Fact]
    public async Task Macos_refuses_a_missing_published_rig_before_changing_agents()
    {
        var root = Path.Join(_temp.Path, "checkout");
        var target = new JobsInstallTarget(root, "plan", Path.Join(root, "temp", "jobs-runtime", "rig"));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => JobsInstallTarget.ResolveAsync(
            root, Path.Join(_temp.Path, "app"), "plan", new FakeProc(), JobsPlatform.MacOS, JobsLauncher.Direct, null, TestContext.Current.CancellationToken));

        Assert.Contains(target.Rig, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Linked_worktrees_are_refused_before_checking_or_changing_targets()
    {
        var proc = new FakeProc().Answer(new Ran(0, Path.Join(_temp.Path, "main", ".git"), ""), "git");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => JobsInstallTarget.ResolveAsync(_temp.Path, "app", "plan", proc, JobsPlatform.Windows, JobsLauncher.Direct, null, TestContext.Current.CancellationToken));
        Assert.Contains("not a worktree", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_git_failure_cannot_hide_a_linked_worktree()
    {
        await File.WriteAllTextAsync(Path.Join(_temp.Path, ".git"), "gitdir: elsewhere", TestContext.Current.CancellationToken);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => JobsInstallTarget.ResolveAsync(_temp.Path, "app", "plan", new FakeProc(), JobsPlatform.Windows, JobsLauncher.Direct, null, TestContext.Current.CancellationToken));
        Assert.Contains("Cannot verify the main checkout", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task A_missing_rig_plan_or_pythonw_refuses_installation(int missing)
    {
        var target = new JobsInstallTarget(_temp.Path, Path.Join(_temp.Path, "plan"), Path.Join(_temp.Path, "temp", "jobs-runtime", "rig.exe"), Path.Join(_temp.Path, "py", "pythonw.exe"));
        Targets(target);
        var path = new[] { target.Rig, target.Plan, target.Pythonw! }[missing];
        File.Delete(path);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => JobsInstallTarget.ResolveAsync(_temp.Path, "app", target.Plan, new FakeProc(), JobsPlatform.Windows, JobsLauncher.Pythonw, target.Pythonw, TestContext.Current.CancellationToken));
        Assert.Contains(path, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Register_writes_utf16_xml_and_cleans_it_up_on_success_or_failure(bool fail)
    {
        var proc = new FakeProc
        {
            BeforeRun = request =>
            {
                var path = request.Argv[5];
                Assert.Contains("<Task", File.ReadAllText(path, Encoding.Unicode), StringComparison.Ordinal);
                Assert.Equal(new byte[] { 255, 254 }, File.ReadAllBytes(path).Take(2));
            }
        };
        proc.Answer(new Ran(fail ? 1 : 0, "", "refused"), "schtasks");
        var tasks = new Schtasks(proc, _temp.Path);

        Task Register() => tasks.RegisterAsync(CoordinatorTasks.Logon, Target, "me", new DateTimeOffset(2026, 10, 10, 9, 0, 0, TimeSpan.Zero), TestContext.Current.CancellationToken);
        if (fail)
        {
            await Assert.ThrowsAsync<ProcException>(Register);
        }
        else
        {
            await Register();
        }

        Assert.Empty(Directory.GetFileSystemEntries(_temp.Path));
        Assert.Equal(["schtasks", "/Create", "/TN", CoordinatorTasks.Logon, "/XML", proc.Requests[0].Argv[5], "/F"], proc.Requests[0].Argv);
    }

    [Fact]
    public async Task Starting_the_guard_checks_the_task_scheduler_exit_code()
    {
        var proc = new FakeProc().Answer(new Ran(0, "", ""), "schtasks");
        var tasks = new Schtasks(proc, _temp.Path);
        await tasks.StartAsync(CoordinatorTasks.Guard, TestContext.Current.CancellationToken);
        Assert.Equal(["schtasks", "/Run", "/TN", CoordinatorTasks.Guard], Assert.Single(proc.Requests).Argv);
        proc.Answer(new Ran(1, "", "refused"), "schtasks");
        await Assert.ThrowsAsync<ProcException>(() => tasks.StartAsync(CoordinatorTasks.Guard, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Without_a_pythonw_uv_names_its_managed_python_from_outside_any_project()
    {
        var target = new JobsInstallTarget(_temp.Path, Path.Join(_temp.Path, "plan"), Path.Join(_temp.Path, "temp", "jobs-runtime", "rig.exe"), Path.Join(_temp.Path, "py", "pythonw.exe"));
        Targets(target);
        var app = Path.Join(_temp.Path, "app");
        var proc = new FakeProc().Answer(new Ran(0, "", ""), "git").Answer(new Ran(0, Path.Join(_temp.Path, "py", "python.exe") + "\n", ""), "uv");

        Assert.Equal(target, await JobsInstallTarget.ResolveAsync(_temp.Path, app, target.Plan, proc, JobsPlatform.Windows, JobsLauncher.Pythonw, null, TestContext.Current.CancellationToken));
        var uv = proc.Requests[^1];
        Assert.Equal(["uv", "python", "find", "--managed-python", "--no-project"], uv.Argv);
        Assert.Equal(app, uv.WorkingDirectory);
    }

    [Fact]
    public async Task Without_uv_python_the_error_names_both_ways_out()
    {
        var target = new JobsInstallTarget(_temp.Path, Path.Join(_temp.Path, "plan"), Path.Join(_temp.Path, "temp", "jobs-runtime", "rig.exe"));
        Targets(target);
        var proc = new FakeProc().Answer(new Ran(0, "", ""), "git").Answer(new Ran(2, "", "no python"), "uv");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => JobsInstallTarget.ResolveAsync(_temp.Path, "app", target.Plan, proc, JobsPlatform.Windows, JobsLauncher.Pythonw, null, TestContext.Current.CancellationToken));
        Assert.Contains("--pythonw", error.Message, StringComparison.Ordinal);
        Assert.Contains("--launcher direct", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_pythonw_action_hands_rig_its_parent_and_no_console() =>
        Assert.Equal(
            """-I -c "import os,subprocess,sys;os.environ['RIG_PARENT_PID']=str(os.getpid());sys.exit(subprocess.call(sys.argv[1:],creationflags=0x08000000))" "C:\r\rig.exe" jobs run""",
            new JobsInstallTarget("root", "plan", @"C:\r\rig.exe", "pythonw.exe").Arguments("jobs run"));

    private static XElement Render(string name) =>
        XElement.Parse(JobsTaskXml.Render(name, Target, "me", new DateTimeOffset(2026, 10, 10, 9, 0, 0, TimeSpan.Zero)));

    private static string? Value(XElement task, params string[] path) =>
        path.Aggregate<string, XElement?>(task, (element, name) => element?.Element(Ns + name))?.Value;

    private static void Targets(JobsInstallTarget target)
    {
        foreach (var path in new[] { target.Rig, target.Plan, target.Pythonw }.OfType<string>())
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "fixture");
        }
    }
}
