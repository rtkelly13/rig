using Rig.Core.Jobs;
using Rig.Core.Processes;
using Rig.Core.Tests.Fakes;

namespace Rig.Core.Tests.Jobs;

/// <summary>
/// The systemd user units behind <c>rig jobs</c> on Linux: what each unit asks the user manager to do, and the
/// <c>systemctl --user</c> calls that install, query and remove them. Nothing here enables a real unit.
/// </summary>
public sealed class JobsSystemdTests : IDisposable
{
    private const string InstallerPath = "/usr/local/bin:/usr/bin";

    private static readonly JobsInstallTarget Target = new("/checkout", "/checkout/plan.json", "/checkout/temp/jobs-runtime/rig");

    private readonly TempDir temp = new();

    public void Dispose() => temp.Dispose();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static Ran Ok => new(0, string.Empty, string.Empty);

    [Theory]
    [InlineData(JobsSystemdUnit.Logon, "logon")]
    [InlineData(JobsSystemdUnit.Tick, "tick")]
    public void Each_service_is_a_one_shot_run_of_rig_on_its_trigger(string name, string trigger)
    {
        var unit = Lines(JobsSystemdUnit.RenderService(name, Target, InstallerPath));

        Assert.Contains("Type=oneshot", unit);
        Assert.Contains($"ExecStart={Target.Rig} jobs run --trigger {trigger} --scheduled", unit);
        Assert.Contains("WorkingDirectory=/checkout", unit);
        Assert.Contains($"Environment=PATH={InstallerPath}", unit);
        Assert.Contains("Nice=10", unit);
        Assert.Contains("IOSchedulingClass=idle", unit);
    }

    [Fact]
    public void Only_the_logon_service_starts_with_the_session_and_the_timer_starts_the_tick()
    {
        Assert.Contains("WantedBy=default.target", Lines(JobsSystemdUnit.RenderService(JobsSystemdUnit.Logon, Target, null)));
        Assert.DoesNotContain("[Install]", Lines(JobsSystemdUnit.RenderService(JobsSystemdUnit.Tick, Target, null)));

        var timer = Lines(JobsSystemdUnit.RenderTimer());
        Assert.Contains($"OnStartupSec={JobsSystemdUnit.FirstTickMinutes}min", timer);
        Assert.Contains($"OnUnitActiveSec={Job.TickMinutes}min", timer);
        Assert.Contains("Unit=rig-jobs-tick.service", timer);
        Assert.Contains("WantedBy=timers.target", timer);
    }

    [Fact]
    public void An_installer_without_a_path_leaves_the_environment_alone() =>
        Assert.DoesNotContain(Lines(JobsSystemdUnit.RenderService(JobsSystemdUnit.Tick, Target, null)), line => line.StartsWith("Environment=", StringComparison.Ordinal));

    [Fact]
    public void Paths_with_spaces_and_specifiers_survive_systemd_parsing()
    {
        var target = new JobsInstallTarget("/home/a b", "/plan", "/home/a b/rig");

        var unit = Lines(JobsSystemdUnit.RenderService(JobsSystemdUnit.Logon, target, "/opt/100%/bin:$HOME/bin"));

        Assert.Contains("ExecStart=\"/home/a b/rig\" jobs run --trigger logon --scheduled", unit);
        Assert.Contains("WorkingDirectory=\"/home/a b\"", unit);
        Assert.Contains("Environment=PATH=/opt/100%%/bin:$$HOME/bin", unit);
    }

    [Fact]
    public void A_unit_that_is_not_one_of_the_two_is_refused() =>
        Assert.Throws<ArgumentException>(() => JobsSystemdUnit.RenderService("Rig\\Display Guard", Target, null));

    [Fact]
    public void Linux_installs_two_units_and_retires_nothing()
    {
        var units = new SystemdTimers(new FakeProc(), "/units", null);

        Assert.Equal(JobsPlatform.Linux, units.Platform);
        Assert.Equal(new[] { JobsSystemdUnit.Logon, JobsSystemdUnit.Tick }, units.Names);
        Assert.Empty(units.Retired);
        Assert.Null(units.StartsAfterInstall);
    }

    [Fact]
    public async Task Install_writes_the_tick_service_and_timer_and_starts_the_timer()
    {
        var proc = new FakeProc().Answer(Ok, "systemctl");
        var dir = Path.Join(temp.Path, "systemd", "user");
        var units = new SystemdTimers(proc, dir, InstallerPath);

        await units.RegisterAsync(JobsSystemdUnit.Tick, Target, "me", DateTimeOffset.UtcNow, Token);

        Assert.Contains("--trigger tick", await File.ReadAllTextAsync(Path.Join(dir, "rig-jobs-tick.service"), Token), StringComparison.Ordinal);
        Assert.Equal(JobsSystemdUnit.RenderTimer(), await File.ReadAllTextAsync(Path.Join(dir, "rig-jobs-tick.timer"), Token));
        Assert.Equal(
            [["systemctl", "--user", "daemon-reload"], ["systemctl", "--user", "enable", "--now", "rig-jobs-tick.timer"]],
            proc.Requests.Select(r => r.Argv.ToArray()));
    }

    [Fact]
    public async Task Install_enables_the_logon_service_without_running_it_now()
    {
        var proc = new FakeProc().Answer(Ok, "systemctl");
        var units = new SystemdTimers(proc, temp.Path, InstallerPath);

        await units.RegisterAsync(JobsSystemdUnit.Logon, Target, "me", DateTimeOffset.UtcNow, Token);

        Assert.True(File.Exists(Path.Join(temp.Path, "rig-jobs-logon.service")));
        Assert.False(File.Exists(Path.Join(temp.Path, "rig-jobs-tick.timer")));
        Assert.Equal(["systemctl", "--user", "enable", "rig-jobs-logon.service"], proc.Requests[^1].Argv);
    }

    [Fact]
    public async Task Install_throws_when_systemctl_refuses()
    {
        var proc = new FakeProc().Answer(Ok, "systemctl").Answer(new Ran(1, string.Empty, "Failed to connect to bus"), "systemctl", "--user", "enable");
        var units = new SystemdTimers(proc, temp.Path, null);

        await Assert.ThrowsAsync<ProcException>(() => units.RegisterAsync(JobsSystemdUnit.Tick, Target, "me", DateTimeOffset.UtcNow, Token));
    }

    [Theory]
    [InlineData(JobsSystemdUnit.Logon, "rig-jobs-logon.service")]
    [InlineData(JobsSystemdUnit.Tick, "rig-jobs-tick.timer")]
    public async Task Status_asks_whether_the_enabled_unit_is_enabled(string name, string unit)
    {
        var proc = new FakeProc().Answer(Ok, "systemctl", "--user", "is-enabled");
        var units = new SystemdTimers(proc, temp.Path, null);

        Assert.True(await units.ExistsAsync(name, Token));
        Assert.Equal(["systemctl", "--user", "is-enabled", "--quiet", unit], Assert.Single(proc.Requests).Argv);
    }

    [Fact]
    public async Task Uninstall_disables_the_timer_and_removes_both_files()
    {
        var proc = new FakeProc().Answer(Ok, "systemctl");
        var units = new SystemdTimers(proc, temp.Path, null);
        await units.RegisterAsync(JobsSystemdUnit.Tick, Target, "me", DateTimeOffset.UtcNow, Token);
        proc.Requests.Clear();

        Assert.True(await units.DeleteAsync(JobsSystemdUnit.Tick, Token));

        Assert.Empty(Directory.GetFiles(temp.Path));
        Assert.Equal(
            [["systemctl", "--user", "disable", "--now", "rig-jobs-tick.timer"], ["systemctl", "--user", "daemon-reload"]],
            proc.Requests.Select(r => r.Argv.ToArray()));
    }

    [Fact]
    public async Task Uninstall_of_a_unit_never_installed_touches_nothing()
    {
        var proc = new FakeProc();
        var units = new SystemdTimers(proc, temp.Path, null);

        Assert.False(await units.DeleteAsync(JobsSystemdUnit.Logon, Token));
        Assert.Empty(proc.Requests);
    }

    private static string[] Lines(string unit) => unit.Split('\n');
}
