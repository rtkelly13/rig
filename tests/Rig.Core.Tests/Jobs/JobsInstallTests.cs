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

    /// <summary>
    /// Each task's XML as <see cref="JobsTaskXml.Render"/> writes it, kept in <c>Jobs/Tasks/&lt;task&gt;.xml</c> so a policy
    /// change shows up as a reviewable diff. Set <c>RIG_UPDATE_SNAPSHOTS=1</c> to rewrite the files from the renderer.
    /// </summary>
    [Theory]
    [InlineData(CoordinatorTasks.Logon, "logon", true)]
    [InlineData(CoordinatorTasks.Tick, "tick", true)]
    [InlineData(CoordinatorTasks.Guard, "display-guard", true)]
    [InlineData(CoordinatorTasks.Guard, "display-guard-direct", false)]
    public void Task_xml_matches_its_snapshot(string name, string file, bool python)
    {
        var target = new JobsInstallTarget(
            "C:\\rig & tools", "C:\\rig & tools\\plan & co.json", "C:\\rig & tools\\rig.exe", python ? "C:\\Python\\pythonw.exe" : null);
        var actual = JobsTaskXml.Render(name, target, "CORP\\me & you", new DateTimeOffset(2026, 10, 10, 9, 0, 0, TimeSpan.FromHours(2)));
        var path = Path.Join(Path.GetDirectoryName(RepoTemp.Root), "tests", "Rig.Core.Tests", "Jobs", "Tasks", file + ".xml");
        if (Environment.GetEnvironmentVariable("RIG_UPDATE_SNAPSHOTS") == "1")
        {
            _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, actual.ReplaceLineEndings("\n"));
        }

        Assert.Equal(File.ReadAllText(path).ReplaceLineEndings("\n"), actual.ReplaceLineEndings("\n"));
        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"UTF-16\"?>", actual, StringComparison.Ordinal);
        Assert.Equal("\\" + name, XElement.Parse(actual).Element(Ns + "RegistrationInfo")!.Element(Ns + "URI")!.Value);
    }

    [Fact]
    public void An_unknown_task_is_refused() =>
        Assert.Throws<ArgumentException>(() => JobsTaskXml.Render("unknown", new("root", "plan", "rig"), "user", TimeProvider.System.GetUtcNow()));

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

        Assert.Equal(target, await JobsInstallTarget.ResolveAsync(root, Path.Join(_temp.Path, "app"), plan, proc, JobsLauncher.Direct, null, TestContext.Current.CancellationToken));
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

        Assert.Equal(target, await JobsInstallTarget.ResolveAsync(Path.Join(app, "runtime", copy), app, "ignored", proc, JobsLauncher.Pythonw, target.Pythonw, TestContext.Current.CancellationToken));
        Assert.Empty(proc.Requests);
    }

    [Fact]
    public async Task Linked_worktrees_are_refused_before_checking_or_changing_targets()
    {
        var proc = new FakeProc().Answer(new Ran(0, Path.Join(_temp.Path, "main", ".git"), ""), "git");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => JobsInstallTarget.ResolveAsync(_temp.Path, "app", "plan", proc, JobsLauncher.Direct, null, TestContext.Current.CancellationToken));
        Assert.Contains("not a worktree", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_git_failure_cannot_hide_a_linked_worktree()
    {
        await File.WriteAllTextAsync(Path.Join(_temp.Path, ".git"), "gitdir: elsewhere", TestContext.Current.CancellationToken);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => JobsInstallTarget.ResolveAsync(_temp.Path, "app", "plan", new FakeProc(), JobsLauncher.Direct, null, TestContext.Current.CancellationToken));
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

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => JobsInstallTarget.ResolveAsync(_temp.Path, "app", target.Plan, new FakeProc(), JobsLauncher.Pythonw, target.Pythonw, TestContext.Current.CancellationToken));
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
                Assert.Equal("<?xml?> & data", File.ReadAllText(path, Encoding.Unicode));
                Assert.Equal(new byte[] { 255, 254 }, File.ReadAllBytes(path).Take(2));
            }
        };
        proc.Answer(new Ran(fail ? 1 : 0, "", "refused"), "schtasks");
        var tasks = new Schtasks(proc, _temp.Path);

        if (fail)
        {
            await Assert.ThrowsAsync<ProcException>(() => tasks.RegisterAsync(CoordinatorTasks.Logon, "<?xml?> & data", TestContext.Current.CancellationToken));
        }
        else
        {
            await tasks.RegisterAsync(CoordinatorTasks.Logon, "<?xml?> & data", TestContext.Current.CancellationToken);
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

        Assert.Equal(target, await JobsInstallTarget.ResolveAsync(_temp.Path, app, target.Plan, proc, JobsLauncher.Pythonw, null, TestContext.Current.CancellationToken));
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

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => JobsInstallTarget.ResolveAsync(_temp.Path, "app", target.Plan, proc, JobsLauncher.Pythonw, null, TestContext.Current.CancellationToken));
        Assert.Contains("--pythonw", error.Message, StringComparison.Ordinal);
        Assert.Contains("--launcher direct", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_pythonw_action_hands_rig_its_parent_and_no_console() =>
        Assert.Equal(
            """-I -c "import os,subprocess,sys;os.environ['RIG_PARENT_PID']=str(os.getpid());sys.exit(subprocess.call(sys.argv[1:],creationflags=0x08000000))" "C:\r\rig.exe" jobs run""",
            new JobsInstallTarget("root", "plan", @"C:\r\rig.exe", "pythonw.exe").Arguments("jobs run"));

    private static void Targets(JobsInstallTarget target)
    {
        foreach (var path in new[] { target.Rig, target.Plan, target.Pythonw }.OfType<string>())
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "fixture");
        }
    }
}
