using Rig.Core.Otel;
using Rig.Core.Processes;
using Rig.Core.Tests.Fakes;
using Rig.Core.Tests.Otel;
using Rig.Core.Tools;

namespace Rig.Core.Tests.Tools;

/// <summary><see cref="ServiceManager"/> against a temp home and fake service managers; nothing here touches the host.</summary>
public sealed class ServiceManagerTests : IDisposable
{
    private readonly TempDir temp = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Now = new(2026, 11, 16, 10, 0, 0, TimeSpan.Zero);

    public void Dispose() => temp.Dispose();

    private ServiceHost Host(ServicePlatform platform, IProc? proc = null, IPortProbe? probe = null, TimeProvider? clock = null, Func<TimeSpan, CancellationToken, Task>? delay = null, string? path = null) =>
        new(
            temp.Path,
            "/work",
            ["/rig"],
            platform,
            proc ?? new FakeProc().Answer(new Ran(0, string.Empty, string.Empty), "launchctl", "bootstrap").Answer(new Ran(0, string.Empty, string.Empty), "schtasks").Answer(new Ran(0, string.Empty, string.Empty), "systemctl"),
            probe ?? new FakeProbe(),
            clock ?? new ManualClock(Now),
            delay ?? ((_, _) => Task.CompletedTask),
            _ => Task.FromResult("gui/501"),
            path);

    [Fact]
    public async Task Mac_install_writes_a_long_lived_plist_then_bootstraps_it()
    {
        var proc = new FakeProc().Answer(new Ran(0, string.Empty, string.Empty), "launchctl");

        var result = await new ServiceManager(Host(ServicePlatform.MacOS, proc, path: "/bin:/usr/bin")).InstallAsync(ServiceTarget.Web, preview: false, port: null, Token);

        var path = Path.Join(temp.Path, "Library", "LaunchAgents", "com.rtkelly.rig.web.plist");
        var plist = await File.ReadAllTextAsync(path, Token);
        Assert.Equal($"Loaded macOS LaunchAgent [com.rtkelly.rig.web] running on port 8090 (PID supervised via launchd).", result.Message);
        Assert.Contains("<key>KeepAlive</key>\n\t<true/>", plist, StringComparison.Ordinal);
        Assert.Contains("<string>--port</string>\n\t\t<string>8090</string>", plist, StringComparison.Ordinal);
        Assert.Contains("<key>PATH</key>\n\t\t<string>/bin:/usr/bin</string>", plist, StringComparison.Ordinal);
        Assert.Equal(
            [["launchctl", "bootout", "gui/501", path], ["launchctl", "bootstrap", "gui/501", path]],
            proc.Requests.Select(r => r.Argv));
    }

    [Fact]
    public async Task Mac_preview_uses_the_preview_label_path_and_port()
    {
        var proc = new FakeProc().Answer(new Ran(0, string.Empty, string.Empty), "launchctl");

        var result = await new ServiceManager(Host(ServicePlatform.MacOS, proc)).InstallAsync(ServiceTarget.Web, preview: true, port: null, Token);

        var path = Path.Join(temp.Path, "Library", "LaunchAgents", "com.rtkelly.rig.web.preview.plist");
        Assert.Contains("com.rtkelly.rig.web.preview", result.Message, StringComparison.Ordinal);
        Assert.Contains("8091", result.Message, StringComparison.Ordinal);
        var plist = await File.ReadAllTextAsync(path, Token);
        Assert.Contains("<string>--preview</string>", plist, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_one_shot_target_is_run_at_load_without_keep_alive()
    {
        var proc = new FakeProc().Answer(new Ran(0, string.Empty, string.Empty), "launchctl");

        var result = await new ServiceManager(Host(ServicePlatform.MacOS, proc)).InstallAsync(ServiceTarget.Desktop, preview: false, port: null, Token);

        var plist = await File.ReadAllTextAsync(Path.Join(temp.Path, "Library", "LaunchAgents", "com.rtkelly.rig.desktop.plist"), Token);
        Assert.Equal("Loaded macOS LaunchAgent [com.rtkelly.rig.desktop].", result.Message);
        Assert.DoesNotContain("KeepAlive", plist, StringComparison.Ordinal);
        Assert.Contains("<string>desktop</string>", plist, StringComparison.Ordinal);
        Assert.Contains("<string>restore</string>", plist, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_port_override_is_used_and_reported()
    {
        var proc = new FakeProc().Answer(new Ran(0, string.Empty, string.Empty), "launchctl");

        var result = await new ServiceManager(Host(ServicePlatform.MacOS, proc)).InstallAsync(ServiceTarget.Web, preview: false, port: 8123, Token);

        Assert.Contains("port 8123", result.Message, StringComparison.Ordinal);
        var plist = await File.ReadAllTextAsync(Path.Join(temp.Path, "Library", "LaunchAgents", "com.rtkelly.rig.web.plist"), Token);
        Assert.Contains("<string>8123</string>", plist, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failed_bootstrap_is_reported()
    {
        var proc = new FakeProc().Answer(new Ran(1, string.Empty, "refused"), "launchctl", "bootstrap");

        var error = await Assert.ThrowsAsync<ProcException>(() =>
            new ServiceManager(Host(ServicePlatform.MacOS, proc)).InstallAsync(ServiceTarget.Web, preview: false, port: null, Token));

        Assert.Contains("refused", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Windows_install_registers_and_runs_a_logon_task()
    {
        var proc = new FakeProc().Answer(new Ran(0, string.Empty, string.Empty), "schtasks");

        var result = await new ServiceManager(Host(ServicePlatform.Windows, proc)).InstallAsync(ServiceTarget.Web, preview: true, port: null, Token);

        Assert.Equal("Registered and started Windows scheduled task [Shared Utilities\\Web Dashboard Preview] on port 8091.", result.Message);
        Assert.Equal(
            ["schtasks", "/Create", "/TN", "Shared Utilities\\Web Dashboard Preview", "/TR", "/rig web --port 8091 --no-open --preview", "/SC", "ONLOGON", "/F"],
            proc.Requests[0].Argv);
        Assert.Equal(["schtasks", "/Run", "/TN", "Shared Utilities\\Web Dashboard Preview"], proc.Requests[1].Argv);
    }

    [Fact]
    public async Task Windows_quotes_arguments_with_spaces_and_reports_a_portless_target()
    {
        var proc = new FakeProc().Answer(new Ran(0, string.Empty, string.Empty), "schtasks");
        var host = Host(ServicePlatform.Windows, proc) with { RigCommand = ["/rig folder/rig"] };

        var result = await new ServiceManager(host).InstallAsync(ServiceTarget.Desktop, preview: false, port: null, Token);

        Assert.Equal("Registered and started Windows scheduled task [Shared Utilities\\Desktop].", result.Message);
        Assert.Equal("\"/rig folder/rig\" desktop workspace restore", proc.Requests[0].Argv[5]);
    }

    [Fact]
    public async Task Windows_install_failure_is_reported()
    {
        var proc = new FakeProc().Answer(new Ran(1, string.Empty, "denied"), "schtasks");

        await Assert.ThrowsAsync<ProcException>(() =>
            new ServiceManager(Host(ServicePlatform.Windows, proc)).InstallAsync(ServiceTarget.Web, false, null, Token));
    }

    [Fact]
    public async Task Linux_install_writes_a_systemd_unit_and_enables_it()
    {
        var proc = new FakeProc().Answer(new Ran(0, string.Empty, string.Empty), "systemctl");

        var result = await new ServiceManager(Host(ServicePlatform.Linux, proc)).InstallAsync(ServiceTarget.Collector, preview: false, port: null, Token);

        var path = Path.Join(temp.Path, ".config", "systemd", "user", "rig-collector.service");
        var unit = await File.ReadAllTextAsync(path, Token);
        Assert.Equal("Loaded systemd user unit [rig-collector.service].", result.Message);
        Assert.Contains("[Unit]\nDescription=rig collector\n", unit, StringComparison.Ordinal);
        Assert.DoesNotContain("Restart=always", unit, StringComparison.Ordinal);
        Assert.Contains("ExecStart=/rig otel-stack collector ensure", unit, StringComparison.Ordinal);
        Assert.Equal(
            [["systemctl", "--user", "daemon-reload"], ["systemctl", "--user", "enable", "rig-collector.service"], ["systemctl", "--user", "reload-or-restart", "rig-collector.service"]],
            proc.Requests.Select(r => r.Argv));
    }

    [Fact]
    public async Task Linux_long_lived_targets_restart_and_report_their_port()
    {
        var proc = new FakeProc().Answer(new Ran(0, string.Empty, string.Empty), "systemctl");

        var result = await new ServiceManager(Host(ServicePlatform.Linux, proc)).InstallAsync(ServiceTarget.Web, preview: true, port: null, Token);

        var unit = await File.ReadAllTextAsync(Path.Join(temp.Path, ".config", "systemd", "user", "rig-web.preview.service"), Token);
        Assert.Contains("running on port 8091", result.Message, StringComparison.Ordinal);
        Assert.Contains("Restart=always", unit, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Linux_install_failure_is_reported()
    {
        var proc = new FakeProc().Answer(new Ran(1, string.Empty, "not permitted"), "systemctl", "enable");

        await Assert.ThrowsAsync<ProcException>(() =>
            new ServiceManager(Host(ServicePlatform.Linux, proc)).InstallAsync(ServiceTarget.Web, false, null, Token));
    }

    [Fact]
    public async Task Mac_uninstall_removes_a_plist_and_ignores_one_that_is_absent()
    {
        var proc = new FakeProc().Answer(new Ran(0, string.Empty, string.Empty), "launchctl");
        var manager = new ServiceManager(Host(ServicePlatform.MacOS, proc));

        Assert.Equal("No LaunchAgent found for [com.rtkelly.rig.web].", (await manager.UninstallAsync(ServiceTarget.Web, false, Token)).Message);

        var path = Path.Join(temp.Path, "Library", "LaunchAgents", "com.rtkelly.rig.web.plist");
        _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "plist", Token);

        Assert.Equal("Unloaded and removed macOS LaunchAgent [com.rtkelly.rig.web].", (await manager.UninstallAsync(ServiceTarget.Web, false, Token)).Message);
        Assert.False(File.Exists(path));
        Assert.Equal(["launchctl", "bootout", "gui/501", path], Assert.Single(proc.Requests).Argv);
    }

    [Fact]
    public async Task Windows_uninstall_deletes_the_task()
    {
        var proc = new FakeProc().Answer(new Ran(0, string.Empty, string.Empty), "schtasks");

        var result = await new ServiceManager(Host(ServicePlatform.Windows, proc)).UninstallAsync(ServiceTarget.Desktop, true, Token);

        Assert.Equal("Removed Windows Task Scheduler entry [Shared Utilities\\Desktop Preview].", result.Message);
        Assert.Equal(["schtasks", "/Delete", "/TN", "Shared Utilities\\Desktop Preview", "/F"], Assert.Single(proc.Requests).Argv);
    }

    [Fact]
    public async Task Linux_uninstall_removes_a_unit_and_ignores_one_that_is_absent()
    {
        var proc = new FakeProc().Answer(new Ran(0, string.Empty, string.Empty), "systemctl");
        var manager = new ServiceManager(Host(ServicePlatform.Linux, proc));

        Assert.Equal("No systemd unit found for [rig-web.service].", (await manager.UninstallAsync(ServiceTarget.Web, false, Token)).Message);

        var path = Path.Join(temp.Path, ".config", "systemd", "user", "rig-web.service");
        _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "unit", Token);

        Assert.Equal("Unloaded and removed systemd user unit [rig-web.service].", (await manager.UninstallAsync(ServiceTarget.Web, false, Token)).Message);
        Assert.False(File.Exists(path));
        Assert.Equal(
            [["systemctl", "--user", "disable", "--now", "rig-web.service"], ["systemctl", "--user", "daemon-reload"]],
            proc.Requests.Select(r => r.Argv));
    }

    [Fact]
    public async Task Mac_status_reports_install_load_and_port_separately()
    {
        var probe = new FakeProbe();
        probe.Listening.Add(8090);
        var manager = new ServiceManager(Host(ServicePlatform.MacOS, new FakeProc().Answer(new Ran(0, string.Empty, string.Empty), "launchctl", "print"), probe));

        Assert.Equal(new ServiceStatus(false, true, true, 8090), await manager.StatusAsync(ServiceTarget.Web, false, null, Token));

        var path = Path.Join(temp.Path, "Library", "LaunchAgents", "com.rtkelly.rig.web.plist");
        _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "plist", Token);

        Assert.Equal(new ServiceStatus(true, true, true, 8090), await manager.StatusAsync(ServiceTarget.Web, false, null, Token));
    }

    [Fact]
    public async Task Windows_status_uses_the_task_query_and_reports_the_port()
    {
        var probe = new FakeProbe();
        var manager = new ServiceManager(Host(ServicePlatform.Windows, new FakeProc().Answer(new Ran(1, string.Empty, "missing"), "schtasks"), probe));

        Assert.Equal(new ServiceStatus(false, false, false, 8090), await manager.StatusAsync(ServiceTarget.Web, false, null, Token));
    }

    [Fact]
    public async Task Linux_status_uses_is_active_and_a_portless_target_has_no_listening_answer()
    {
        var manager = new ServiceManager(Host(ServicePlatform.Linux, new FakeProc().Answer(new Ran(0, string.Empty, string.Empty), "systemctl")));

        Assert.Equal(new ServiceStatus(false, true, null, null), await manager.StatusAsync(ServiceTarget.Collector, false, null, Token));
    }

    [Fact]
    public async Task Ensure_leaves_an_answering_service_alone()
    {
        var probe = new FakeProbe();
        probe.Listening.Add(8090);
        var proc = new FakeProc();

        Assert.True(await new ServiceManager(Host(ServicePlatform.MacOS, proc, probe)).EnsureAsync(ServiceTarget.Web, null, TimeSpan.FromSeconds(5), Token));
        Assert.Empty(proc.Requests);
    }

    [Fact]
    public async Task Ensure_starts_a_service_and_waits_for_the_port()
    {
        var probe = new FakeProbe();
        var clock = new ManualClock(Now);
        var proc = new FakeProc().Answer(new Ran(0, string.Empty, string.Empty), "launchctl");
        async Task Delay(TimeSpan by, CancellationToken cancellationToken)
        {
            clock.Advance(by);
            probe.Listening.Add(8090);
            await Task.CompletedTask;
        }

        Assert.True(await new ServiceManager(Host(ServicePlatform.MacOS, proc, probe, clock, Delay)).EnsureAsync(ServiceTarget.Web, null, TimeSpan.FromSeconds(5), Token));
        Assert.Equal("bootstrap", proc.Requests[^1].Argv[1]);
    }

    [Fact]
    public async Task Ensure_fails_when_the_port_never_answers()
    {
        var probe = new FakeProbe();
        var clock = new ManualClock(Now);
        async Task Delay(TimeSpan by, CancellationToken cancellationToken)
        {
            clock.Advance(by);
            await Task.CompletedTask;
        }

        var proc = new FakeProc().Answer(new Ran(0, string.Empty, string.Empty), "launchctl");

        Assert.False(await new ServiceManager(Host(ServicePlatform.MacOS, proc, probe, clock, Delay)).EnsureAsync(ServiceTarget.Web, null, TimeSpan.FromSeconds(1), Token));
        Assert.Equal(Now + TimeSpan.FromSeconds(1), clock.Now);
    }

    [Fact]
    public async Task Ensure_without_a_port_installs_and_succeeds()
    {
        var proc = new FakeProc().Answer(new Ran(0, string.Empty, string.Empty), "launchctl");

        Assert.True(await new ServiceManager(Host(ServicePlatform.MacOS, proc)).EnsureAsync(ServiceTarget.Desktop, null, TimeSpan.FromSeconds(5), Token));
        Assert.Equal(2, proc.Requests.Count);
    }

    [Fact]
    public void Labels_and_paths_are_stable()
    {
        Assert.Equal("com.rtkelly.rig.web", ServiceManager.Label(ServiceTarget.Web, false));
        Assert.Equal("com.rtkelly.rig.web.preview", ServiceManager.Label(ServiceTarget.Web, true));
        Assert.Equal(Path.Join("/home", "Library", "LaunchAgents", "com.rtkelly.rig.mcp.plist"), ServiceManager.PlistPath("/home", ServiceTarget.Mcp, false));
        Assert.Equal("Shared Utilities\\Collector Preview", ServiceManager.TaskName(ServiceTarget.Collector, true));
        Assert.Equal("rig-desktop.service", ServiceManager.UnitName(ServiceTarget.Desktop, false));
        Assert.Equal(Path.Join("/home", ".config", "systemd", "user", "rig-desktop.service"), ServiceManager.UnitPath("/home", ServiceTarget.Desktop, false));
    }

    [Fact]
    public async Task An_unknown_platform_is_refused()
    {
        var host = Host((ServicePlatform)99);
        var manager = new ServiceManager(host);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => manager.InstallAsync(ServiceTarget.Web, false, null, Token));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => manager.UninstallAsync(ServiceTarget.Web, false, Token));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => manager.StatusAsync(ServiceTarget.Web, false, null, Token));
    }

    [Fact]
    public async Task Null_targets_are_refused()
    {
        var manager = new ServiceManager(Host(ServicePlatform.MacOS));

        await Assert.ThrowsAsync<ArgumentNullException>(() => manager.InstallAsync(null!, false, null, Token));
        await Assert.ThrowsAsync<ArgumentNullException>(() => manager.UninstallAsync(null!, false, Token));
        await Assert.ThrowsAsync<ArgumentNullException>(() => manager.StatusAsync(null!, false, null, Token));
        await Assert.ThrowsAsync<ArgumentNullException>(() => manager.EnsureAsync(null!, null, TimeSpan.Zero, Token));
    }
}
