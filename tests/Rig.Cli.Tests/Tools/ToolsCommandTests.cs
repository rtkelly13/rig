#pragma warning disable RS0030 // Legacy System.CommandLine awaiting DotMake migration (Issue #234)
using System.CommandLine;
using Rig.Cli.Tools;
using Rig.Cli.Web;
using Rig.Core.Otel;
using Rig.Core.Processes;
using Rig.Core.Tests;
using Rig.Core.Tests.Fakes;
using Rig.Core.Tools;
using Rig.Web;

namespace Rig.Cli.Tests.Tools;

/// <summary><c>rig tools</c> and the legacy <c>rig web</c> aliases, over temp folders and fake service managers.</summary>
public sealed class ToolsCommandTests : IDisposable
{
    private readonly TempDir _temp = new();
    private readonly StringWriter _out = new() { NewLine = "\n" };
    private readonly StringWriter _error = new() { NewLine = "\n" };

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _temp.Dispose();
        _out.Dispose();
        _error.Dispose();
    }

    private string Db => Path.Join(_temp.Path, "rig.db");

    private ServiceHost Host(IProc? proc = null, IPortProbe? probe = null, TimeProvider? clock = null, Func<TimeSpan, CancellationToken, Task>? delay = null, Func<CancellationToken, Task<string>>? domain = null) =>
        new(
            _temp.Path,
            "/work",
            ["/rig"],
            ServicePlatform.MacOS,
            proc ?? new FakeProc().Answer(new Ran(0, string.Empty, string.Empty), "launchctl"),
            probe ?? new Probe(),
            clock ?? new Clock(),
            delay ?? ((_, _) => Task.CompletedTask),
            domain ?? (_ => Task.FromResult("gui/501")));

    private DatabaseToolHost Database => new(Db, Path.Join(_temp.Path, "backups"), new Clock());

    private RootCommand ToolsRoot(ServiceHost? host = null) =>
        new() { new ToolsCommand(host ?? Host(), Database, _out, _error).Build() };

    private RootCommand WebRoot(ServiceHost? host = null)
    {
        var tools = new ToolsCommand(host ?? Host(), Database, _out, _error);
        var places = new WebPlaces(_temp.Path, Path.Join(_temp.Path, "managed.json"), _temp.Path, Db);
        return new()
        {
            new WebCommand(
                places,
                _ => throw new InvalidOperationException("nothing may start"),
                new Probe(),
                _ => throw new InvalidOperationException("nothing may open"),
                (_, _) => Task.CompletedTask,
                _out,
                _error,
                aliases: tools.BuildWebAliases()).Build(),
        };
    }

    private async Task<int> Run(RootCommand root, params string[] args) =>
        await root.Parse(args).InvokeAsync(new InvocationConfiguration { Output = _out, Error = _error }, Token);

    [Fact]
    public async Task Install_prints_what_the_service_manager_did()
    {
        var exit = await Run(ToolsRoot(), "tools", "install", "web");

        Assert.Equal(0, exit);
        Assert.Equal("Loaded macOS LaunchAgent [com.rtkelly.rig.web] running on port 8090 (PID supervised via launchd).\n", _out.ToString());
        Assert.True(File.Exists(Path.Join(_temp.Path, "Library", "LaunchAgents", "com.rtkelly.rig.web.plist")));
    }

    [Fact]
    public async Task Install_of_a_portless_target_reports_no_port()
    {
        var exit = await Run(ToolsRoot(), "tools", "install", "desktop");

        Assert.Equal(0, exit);
        Assert.Equal("Loaded macOS LaunchAgent [com.rtkelly.rig.desktop].\n", _out.ToString());
    }

    [Fact]
    public async Task An_unknown_target_is_a_usage_error()
    {
        var exit = await Run(ToolsRoot(), "tools", "install", "nope");

        Assert.Equal(ToolsCommand.UsageExit, exit);
        Assert.Equal("Unknown target 'nope'. Choose web, mcp, desktop, collector.\n", _error.ToString());
    }

    [Fact]
    public async Task A_service_manager_failure_is_exit_1()
    {
        var proc = new FakeProc().Answer(new Ran(1, string.Empty, "denied"), "launchctl", "bootstrap");

        var exit = await Run(ToolsRoot(Host(proc)), "tools", "install", "web");

        Assert.Equal(ToolsCommand.FailureExit, exit);
        Assert.StartsWith("rig tools install web: launchctl bootstrap", _error.ToString(), StringComparison.Ordinal);
        Assert.Contains("denied", _error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ensure_reports_an_answering_dashboard()
    {
        var probe = new Probe();
        probe.Listening.Add(8090);

        var exit = await Run(ToolsRoot(Host(probe: probe)), "tools", "ensure", "web");

        Assert.Equal(0, exit);
        Assert.Equal("Web answering on http://127.0.0.1:8090\n", _out.ToString());
    }

    [Fact]
    public async Task Ensure_reports_a_portless_service_as_started()
    {
        var exit = await Run(ToolsRoot(), "tools", "ensure", "desktop");

        Assert.Equal(0, exit);
        Assert.Equal("Desktop started.\n", _out.ToString());
    }

    [Fact]
    public async Task Ensure_times_out_with_exit_1()
    {
        var clock = new Clock();
        async Task Delay(TimeSpan by, CancellationToken cancellationToken)
        {
            clock.Advance(by);
            await Task.CompletedTask;
        }

        var exit = await Run(ToolsRoot(Host(clock: clock, delay: Delay)), "tools", "ensure", "web", "--timeout", "1");

        Assert.Equal(ToolsCommand.FailureExit, exit);
        Assert.Equal("Web did not answer on port 8090 within 1s.\n", _error.ToString());
    }

    [Fact]
    public async Task Ensure_reports_a_service_manager_failure()
    {
        var proc = new FakeProc().Answer(new Ran(1, string.Empty, "denied"), "launchctl", "bootstrap");

        var exit = await Run(ToolsRoot(Host(proc)), "tools", "ensure", "web");

        Assert.Equal(ToolsCommand.FailureExit, exit);
        Assert.StartsWith("rig tools ensure web: ", _error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Uninstall_reports_an_absent_and_a_removed_service()
    {
        var root = ToolsRoot();

        Assert.Equal(0, await Run(root, "tools", "uninstall", "web"));
        Assert.Equal("No LaunchAgent found for [com.rtkelly.rig.web].\n", _out.ToString());

        var path = Path.Join(_temp.Path, "Library", "LaunchAgents", "com.rtkelly.rig.web.plist");
        _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "plist", Token);
        _out.GetStringBuilder().Clear();

        Assert.Equal(0, await Run(root, "tools", "uninstall", "web"));
        Assert.Equal("Unloaded and removed macOS LaunchAgent [com.rtkelly.rig.web].\n", _out.ToString());
    }

    [Fact]
    public async Task Status_names_every_service_and_one_target()
    {
        var proc = new FakeProc().Answer(new Ran(0, string.Empty, string.Empty), "launchctl", "bootstrap");
        var root = ToolsRoot(Host(proc));

        Assert.Equal(0, await Run(root, "tools", "status"));
        Assert.Equal(
            "web: not installed, stopped, not listening on 8090\n"
            + "mcp: not installed, stopped, not listening on 8092\n"
            + "desktop: not installed, stopped\n"
            + "collector: not installed, stopped\n",
            _out.ToString());

        _out.GetStringBuilder().Clear();
        Assert.Equal(0, await Run(root, "tools", "status", "web"));
        Assert.Equal("web: not installed, stopped, not listening on 8090\n", _out.ToString());
    }

    [Fact]
    public async Task Status_with_an_unknown_target_is_a_usage_error()
    {
        var exit = await Run(ToolsRoot(), "tools", "status", "nope");

        Assert.Equal(ToolsCommand.UsageExit, exit);
        Assert.Contains("Unknown target 'nope'", _error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Status_reports_a_service_manager_failure()
    {
        var exit = await Run(ToolsRoot(Host(domain: _ => throw new InvalidOperationException("no uid"))), "tools", "status", "web");

        Assert.Equal(ToolsCommand.FailureExit, exit);
        Assert.Equal("rig tools status web: no uid\n", _error.ToString());
    }

    [Fact]
    public async Task Db_status_says_when_nothing_is_recorded_yet()
    {
        var exit = await Run(ToolsRoot(), "tools", "db", "status");

        Assert.Equal(0, exit);
        Assert.Equal($"Database Path: {Db}\nCurrent Schema Version: 0\nNo migrations have been recorded yet.\n", _out.ToString());
    }

    [Fact]
    public async Task Db_migrate_then_status_shows_the_applied_table()
    {
        var root = ToolsRoot();

        Assert.Equal(0, await Run(root, "tools", "db", "migrate"));
        Assert.Equal($"Migrations applied: 4 on {Db}\n", _out.ToString());

        _out.GetStringBuilder().Clear();
        Assert.Equal(0, await Run(root, "tools", "db", "status"));
        var text = _out.ToString();
        Assert.Contains($"Database Path: {Db}\nCurrent Schema Version: 4\nApplied Schema Migrations\n", text, StringComparison.Ordinal);
        Assert.Contains("Version  Name", text, StringComparison.Ordinal);
        Assert.Contains("Applied At", text, StringComparison.Ordinal);
        Assert.Contains("1        001_initial_schema", text, StringComparison.Ordinal);
        Assert.Contains("4        004_drop_optical_presets", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Db_backup_creates_a_file_under_backups()
    {
        var root = ToolsRoot();
        Assert.Equal(0, await Run(root, "tools", "db", "migrate"));
        _out.GetStringBuilder().Clear();

        Assert.Equal(0, await Run(root, "tools", "db", "backup"));

        var line = _out.ToString();
        Assert.StartsWith("Database backup created: ", line, StringComparison.Ordinal);
        var file = line["Database backup created: ".Length..].Trim();
        Assert.True(File.Exists(file));
        Assert.StartsWith(Path.Join(_temp.Path, "backups"), file, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Db_backup_of_a_missing_database_is_exit_1()
    {
        var exit = await Run(ToolsRoot(), "tools", "db", "backup");

        Assert.Equal(ToolsCommand.FailureExit, exit);
        Assert.Contains("Database file does not exist", _error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Db_migrate_and_status_report_an_unusable_database()
    {
        var directory = _temp.Folder("not-a-database");
        var host = new DatabaseToolHost(directory, Path.Join(_temp.Path, "backups"), new Clock());
        var root = new RootCommand { new ToolsCommand(Host(), host, _out, _error).Build() };

        Assert.Equal(ToolsCommand.FailureExit, await Run(root, "tools", "db", "migrate"));
        Assert.StartsWith("rig tools db migrate: ", _error.ToString(), StringComparison.Ordinal);

        _error.GetStringBuilder().Clear();
        Assert.Equal(ToolsCommand.FailureExit, await Run(root, "tools", "db", "status"));
        Assert.StartsWith("rig tools db status: ", _error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Web_install_and_uninstall_are_aliases_for_the_same_service()
    {
        var root = WebRoot();

        Assert.Equal(0, await Run(root, "web", "install", "--port", "8095"));
        Assert.Contains("port 8095", _out.ToString(), StringComparison.Ordinal);

        _out.GetStringBuilder().Clear();
        Assert.Equal(0, await Run(root, "web", "uninstall"));
        Assert.Equal("Unloaded and removed macOS LaunchAgent [com.rtkelly.rig.web].\n", _out.ToString());
    }

    [Fact]
    public async Task Web_ensure_is_an_alias()
    {
        var probe = new Probe();
        probe.Listening.Add(8090);
        var root = WebRoot(Host(probe: probe));

        Assert.Equal(0, await Run(root, "web", "ensure"));
        Assert.Equal("Web answering on http://127.0.0.1:8090\n", _out.ToString());
    }

    [Fact]
    public async Task Web_backup_migrate_and_db_status_are_aliases()
    {
        var root = WebRoot();

        Assert.Equal(0, await Run(root, "web", "db-status"));
        Assert.Contains("Current Schema Version: 0", _out.ToString(), StringComparison.Ordinal);

        _out.GetStringBuilder().Clear();
        Assert.Equal(0, await Run(root, "web", "migrate"));
        Assert.Equal($"Migrations applied: 4 on {Db}\n", _out.ToString());

        _out.GetStringBuilder().Clear();
        Assert.Equal(0, await Run(root, "web", "backup"));
        Assert.StartsWith("Database backup created: ", _out.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Web_install_failure_is_exit_1()
    {
        var proc = new FakeProc().Answer(new Ran(1, string.Empty, "denied"), "launchctl", "bootstrap");

        var exit = await Run(WebRoot(Host(proc)), "web", "install");

        Assert.Equal(ToolsCommand.FailureExit, exit);
        Assert.StartsWith("rig tools install web: ", _error.ToString(), StringComparison.Ordinal);
    }

    private sealed class Probe : IPortProbe
    {
        public HashSet<int> Listening { get; } = [];

        public Task<bool> ListeningAsync(int port, string host = "127.0.0.1", CancellationToken cancellationToken = default) =>
            Task.FromResult(Listening.Contains(port));
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 16, 10, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
