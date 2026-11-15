using System.Text;
using System.Text.Json;
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

    [Theory]
    [InlineData(CoordinatorTasks.Logon, "_LOGON", "logon")]
    [InlineData(CoordinatorTasks.Tick, "_TICK", "tick")]
    [InlineData(CoordinatorTasks.Guard, "_GUARD", null)]
    public void Task_policy_matches_the_legacy_oracle_with_only_the_reviewed_action_cutover(string name, string key, string? trigger)
    {
        var target = new JobsInstallTarget(Path.Join(_temp.Path, "repo & tools"), Path.Join(_temp.Path, "plan & co.json"), Path.Join(_temp.Path, "rigw.exe"));
        using var oracle = JsonDocument.Parse(File.ReadAllText(Path.Join(AppContext.BaseDirectory, "Jobs", "task-xml-oracle.json")));
        var expected = XElement.Parse(oracle.RootElement.GetProperty(key).GetString()!);
        var action = expected.Element(Ns + "Actions")!.Element(Ns + "Exec")!;
        action.Element(Ns + "Command")!.Value = target.Rigw;
        action.Element(Ns + "Arguments")!.Value = trigger is null
            ? $"desktop workspace guard --config \"{target.Plan}\""
            : $"jobs run --trigger {trigger} --scheduled";
        action.SetElementValue(Ns + "WorkingDirectory", target.Root);

        var actual = JobsTaskXml.Render(name, target, "CORP\\me & you", new DateTimeOffset(2026, 10, 10, 9, 0, 0, TimeSpan.FromHours(2)));

        Assert.True(XNode.DeepEquals(expected, XElement.Parse(actual)), actual);
        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"UTF-16\"?>", actual, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_task_is_refused() =>
        Assert.Throws<ArgumentException>(() => JobsTaskXml.Render("unknown", new("root", "plan", "rigw"), "user", DateTimeOffset.UtcNow));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_main_checkout_or_unversioned_copy_uses_the_development_publish_and_user_plan(bool git)
    {
        var root = Path.Join(_temp.Path, "checkout");
        var plan = Path.Join(_temp.Path, "user-plan.json");
        var target = new JobsInstallTarget(root, plan, Path.Join(root, "temp", "jobs-runtime", "rigw.exe"));
        Targets(target);
        var proc = new FakeProc().Answer(new Ran(git ? 0 : 1, Path.Join(root, ".git"), ""), "git");

        Assert.Equal(target, await JobsInstallTarget.ResolveAsync(root, Path.Join(_temp.Path, "app"), plan, proc, TestContext.Current.CancellationToken));
        Assert.Equal(root, Assert.Single(proc.Requests).WorkingDirectory);
    }

    [Theory]
    [InlineData("copy")]
    [InlineData("current")]
    public async Task Runtime_registration_always_uses_current_and_the_bundled_plan(string copy)
    {
        var app = Path.Join(_temp.Path, "app");
        var current = Path.Join(app, "runtime", "current");
        var target = new JobsInstallTarget(current, Path.Join(current, "desktop-manager", "workspace-plan.bundle.json"), Path.Join(current, "rig", "rigw.exe"));
        Targets(target);
        var proc = new FakeProc();

        Assert.Equal(target, await JobsInstallTarget.ResolveAsync(Path.Join(app, "runtime", copy), app, "ignored", proc, TestContext.Current.CancellationToken));
        Assert.Empty(proc.Requests);
    }

    [Fact]
    public async Task Linked_worktrees_are_refused_before_checking_or_changing_targets()
    {
        var proc = new FakeProc().Answer(new Ran(0, Path.Join(_temp.Path, "main", ".git"), ""), "git");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => JobsInstallTarget.ResolveAsync(_temp.Path, "app", "plan", proc, TestContext.Current.CancellationToken));
        Assert.Contains("not a worktree", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_git_failure_cannot_hide_a_linked_worktree()
    {
        await File.WriteAllTextAsync(Path.Join(_temp.Path, ".git"), "gitdir: elsewhere", TestContext.Current.CancellationToken);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => JobsInstallTarget.ResolveAsync(_temp.Path, "app", "plan", new FakeProc(), TestContext.Current.CancellationToken));
        Assert.Contains("Cannot verify the main checkout", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task A_missing_launcher_or_plan_refuses_installation(int missing)
    {
        var target = new JobsInstallTarget(_temp.Path, Path.Join(_temp.Path, "plan"), Path.Join(_temp.Path, "temp", "jobs-runtime", "rigw.exe"));
        Targets(target);
        var path = new[] { target.Rigw, target.Plan }[missing];
        File.Delete(path);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => JobsInstallTarget.ResolveAsync(_temp.Path, "app", target.Plan, new FakeProc(), TestContext.Current.CancellationToken));
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

    private static void Targets(JobsInstallTarget target)
    {
        foreach (var path in new[] { target.Rigw, target.Plan })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "fixture");
        }
    }
}
