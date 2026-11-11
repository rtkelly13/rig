using System.CommandLine;
using System.Text;
using Rig.Cli.Otel;
using Rig.Core.Otel;
using Rig.Core.Processes;
using Rig.Core.Services;
using Rig.Core.Tests;
using Rig.Core.Tests.Fakes;
using Rig.Core.Tests.Otel;

namespace Rig.Cli.Tests.Otel;

/// <summary>
/// <c>rig otel-stack</c> through its command line, ported from the Python <c>tests/test_otel_stack.py</c>. Every path is
/// a temp folder, binaries are seeded as already verified so nothing downloads, and launchctl, ps, lsof, the ports,
/// ClickHouse and the OTLP sender are all fakes: nothing here reaches the live stack.
/// </summary>
public sealed class OtelStackCommandTests : IDisposable
{
    private const string Key = "darwin_arm64";

    private const string Started = "Tue Nov 11 09:00:00 2026";

    private readonly TempDir temp = new();
    private readonly FakeProc proc = new();
    private readonly FakeSignals signals = new();
    private readonly RecordingDetached detached;
    private readonly FakeProbe probe = new();
    private readonly ManualClock clock = new(new DateTimeOffset(2026, 11, 11, 9, 0, 0, TimeSpan.Zero));
    private readonly FakeClickHouse clickhouse = new();
    private readonly HttpClient clickhouseHttp;
    private readonly StubHandler downloads;
    private readonly HttpClient downloadHttp;
    private readonly FakeSender sender = new();
    private readonly StringWriter output = new();
    private readonly StringWriter error = new();
    private readonly OtelPaths paths;
    private readonly CollectorRuntime runtime;
    private readonly ClickHousePair pair;
    private string key = Key;
    private bool macOs = true;
    private int launchdBuilt;

    public OtelStackCommandTests()
    {
        detached = new RecordingDetached(signals, proc);
        clickhouseHttp = new HttpClient(clickhouse, disposeHandler: false);
        downloads = new StubHandler(_ => throw new InvalidOperationException("nothing may download"));
        downloadHttp = new HttpClient(downloads, disposeHandler: false);
        paths = new OtelPaths(temp.Folder("base"));
        runtime = new CollectorRuntime(paths, proc, detached, windows: false);
        pair = new ClickHousePair(paths, signals, detached, probe, runtime, clock, clock.SleepAsync);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Agents => Path.Join(temp.Path, "agents");

    private string Plist => Path.Join(Agents, OtelService.PlistName);

    private string Binary => OtelPins.BinaryPath(paths.Home, Key);

    private string Output => output.ToString().ReplaceLineEndings("\n");

    private string Error => error.ToString().ReplaceLineEndings("\n");

    public void Dispose()
    {
        clickhouseHttp.Dispose();
        clickhouse.Dispose();
        downloadHttp.Dispose();
        downloads.Dispose();
        output.Dispose();
        error.Dispose();
        temp.Dispose();
    }

    [Fact]
    public async Task Install_reports_the_verified_collector_without_downloading_again()
    {
        SeedCollector();

        Assert.Equal(0, await Run("collector", "install"));

        Assert.Equal($"otelcol {OtelPins.Version} verified at {Binary}\n", Output);
    }

    [Fact]
    public async Task Config_writes_the_collector_config()
    {
        Assert.Equal(0, await Run("collector", "config"));

        Assert.Equal($"wrote {paths.Config}\n", Output);
        Assert.True(File.Exists(paths.Config));
    }

    [Fact]
    public async Task Config_print_also_shows_it()
    {
        Assert.Equal(0, await Run("collector", "config", "--print"));

        Assert.Equal($"wrote {paths.Config}\n" + await File.ReadAllTextAsync(paths.Config, Ct), Output);
    }

    [Fact]
    public async Task Start_leaves_our_listening_collector_alone()
    {
        probe.Listening.Add(CollectorConfig.HttpPort);
        Holder(55, "/opt/otel/otelcol --config c");

        Assert.Equal(0, await Run("collector", "start"));

        Assert.Equal("already listening on 4318\n", Output);
        Assert.Empty(detached.Starts);
    }

    [Theory]
    [InlineData("nginx", "nginx")]
    [InlineData("", "an unknown process")]
    public async Task Start_refuses_a_port_held_by_another_process(string command, string named)
    {
        probe.Listening.Add(CollectorConfig.HttpPort);
        Holder(9, command);

        Assert.Equal(OtelStackCommand.ForeignListenerExit, await Run("collector", "start"));

        Assert.Equal(
            $"port 4318 is held by {named} (pid 9), not otelcol: free the port (for a Docker collector, publish it on another port "
            + "and `otel-stack collector forward set` to it), then run this again\n",
            Output);
    }

    [Fact]
    public async Task Start_kicks_a_loaded_launch_agent()
    {
        Launchctl(loaded: true);

        Assert.Equal(0, await Run("collector", "start"));

        Assert.Equal("kicked the LaunchAgent\n", Output);
        Assert.Contains(proc.Requests, request => request.Argv.SequenceEqual(["launchctl", "kickstart", "-k", $"gui/501/{OtelService.Label}"]));
        Assert.True(File.Exists(paths.Config));
    }

    [Fact]
    public async Task Start_needs_the_collector_installed()
    {
        macOs = false;

        Assert.Equal(OtelStackCommand.FailedExit, await Run("collector", "start"));

        Assert.Equal("not installed: run `otel-stack collector install`\n", Output);
        Assert.Equal(0, launchdBuilt);
    }

    [Fact]
    public async Task Start_launches_the_collector_detached()
    {
        SeedCollector();

        Assert.Equal(0, await Run("collector", "start"));

        Assert.Equal($"started pid 100; log {paths.Log}\n", Output);
        Assert.Equal([Binary, "--config", paths.Config], Assert.Single(detached.Starts).Argv);
    }

    [Fact]
    public async Task Stop_unloads_a_loaded_launch_agent()
    {
        Launchctl(loaded: true);

        Assert.Equal(0, await Run("collector", "stop"));

        Assert.Equal("unloaded the LaunchAgent\n", Output);
        Assert.Contains(proc.Requests, request => request.Argv.SequenceEqual(["launchctl", "bootout", "gui/501", Plist]));
    }

    [Fact]
    public async Task Stop_says_when_nothing_is_recorded()
    {
        macOs = false;

        Assert.Equal(0, await Run("collector", "stop"));

        Assert.Equal("no collector started by `otel-stack` is recorded\n", Output);
    }

    [Fact]
    public async Task Stop_ends_the_recorded_collector()
    {
        macOs = false;
        _ = proc.Answer(new Ran(0, "/bin/otelcol --config c\n", string.Empty), "ps", "-o", "command=");
        _ = await runtime.StartAsync("/bin/otelcol", Ct);

        Assert.Equal(0, await Run("collector", "stop"));

        Assert.Equal("stopped\n", Output);
        Assert.Contains(proc.Requests, request => request.Argv.SequenceEqual(["kill", "-TERM", "100"]));
    }

    [Fact]
    public async Task Ensure_prunes_old_rotated_files_and_starts_the_collector()
    {
        macOs = false;
        SeedCollector();
        var old = DataFile("traces-2026-11-01.jsonl", days: 8);
        var recent = DataFile("traces-2026-11-10.jsonl", days: 1);

        Assert.Equal(0, await Run("collector", "ensure"));

        Assert.Equal("pruned 1 rotated files older than 7 days\nstarted pid 100\n", Output);
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(recent));
    }

    [Fact]
    public async Task Ensure_leaves_a_listening_collector_alone()
    {
        macOs = false;
        SeedCollector();
        probe.Listening.Add(CollectorConfig.HttpPort);

        Assert.Equal(0, await Run("collector", "ensure"));

        Assert.Equal("listening on 4318\n", Output);
        Assert.Empty(detached.Starts);
    }

    [Fact]
    public async Task Ensure_reports_a_foreign_listener()
    {
        macOs = false;
        SeedCollector();
        probe.Listening.Add(CollectorConfig.HttpPort);
        Holder(9, "nginx");

        Assert.Equal(OtelStackCommand.ForeignListenerExit, await Run("collector", "ensure"));

        Assert.StartsWith("port 4318 is held by nginx (pid 9)", Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ensure_loads_an_installed_agent_that_is_not_loaded()
    {
        SeedCollector();
        SeedPlist();
        Launchctl(loaded: false);

        Assert.Equal(0, await Run("collector", "ensure"));

        Assert.Equal("loaded the LaunchAgent\n", Output);
        Assert.Contains(proc.Requests, request => request.Argv.SequenceEqual(["launchctl", "bootstrap", "gui/501", Plist]));
        Assert.Contains(paths.Config, await File.ReadAllTextAsync(Plist, Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ensure_restarts_a_loaded_agent_that_is_not_listening()
    {
        SeedCollector();
        SeedPlist();
        Launchctl(loaded: true);

        Assert.Equal(0, await Run("collector", "ensure"));

        Assert.Equal("restarted the LaunchAgent\n", Output);
        Assert.Contains(proc.Requests, request => request.Argv.SequenceEqual(["launchctl", "kickstart", "-k", $"gui/501/{OtelService.Label}"]));
    }

    [Fact]
    public async Task Ensure_leaves_a_loaded_listening_agent_alone()
    {
        SeedCollector();
        SeedPlist();
        Launchctl(loaded: true);
        probe.Listening.Add(CollectorConfig.HttpPort);

        Assert.Equal(0, await Run("collector", "ensure"));

        Assert.Equal("listening on 4318\n", Output);
    }

    [Fact]
    public async Task Ensure_without_an_agent_starts_the_collector_itself()
    {
        SeedCollector();

        Assert.Equal(0, await Run("collector", "ensure"));

        Assert.Equal("started pid 100\n", Output);
    }

    [Fact]
    public async Task Status_off_macos_shows_the_binary_ports_forward_and_empty_data()
    {
        macOs = false;

        Assert.Equal(0, await Run("collector", "status"));

        Assert.Equal(
            $"binary: missing otelcol {OtelPins.Version} (not verified) {Binary}\n"
            + "port 4317: closed\nport 4318: closed\nforward: off\n"
            + $"data: empty ({paths.Data})\n",
            Output);
        Assert.Equal(0, launchdBuilt);
    }

    [Fact]
    public async Task Status_shows_the_service_process_and_data_in_the_local_zone()
    {
        SeedCollector();
        SeedPlist();
        probe.Listening.Add(CollectorConfig.GrpcPort);
        probe.Listening.Add(CollectorConfig.HttpPort);
        Holder(55, "/opt/otel/otelcol --config c");
        _ = proc.Answer(new Ran(0, "2048\n", string.Empty), "ps", "-o", "rss=");
        _ = runtime.SetForward("http://127.0.0.1:24318");
        var first = DataFile("traces.jsonl", days: 2, bytes: 1_048_576);
        File.SetLastWriteTimeUtc(first, new DateTime(2026, 11, 9, 8, 30, 0, DateTimeKind.Utc));
        var second = DataFile("logs.jsonl", days: 1, bytes: 524_288);
        File.SetLastWriteTimeUtc(second, new DateTime(2026, 11, 10, 23, 15, 0, DateTimeKind.Utc));

        Assert.Equal(0, await Run("collector", "status"));

        Assert.Equal(
            $"binary: installed otelcol {OtelPins.Version} (sha256 verified) {Binary}\n"
            + $"service: installed, not loaded ({Plist})\n"
            + "port 4317: listening (otelcol, pid 55)\nport 4318: listening (otelcol, pid 55)\n"
            + "forward: http://127.0.0.1:24318\n"
            + "process: pid 55, 2.0 MB resident\n"
            + $"data: 2 files, 1.5 MB, 2026-11-09 09:30 to 2026-11-11 00:15 ({paths.Data})\n",
            Output);
    }

    [Theory]
    [InlineData(true, false, "loaded")]
    [InlineData(false, false, "not installed")]
    public async Task Status_names_the_service_state(bool loaded, bool installed, string state)
    {
        if (installed)
        {
            SeedPlist();
        }

        Launchctl(loaded);

        Assert.Equal(0, await Run("collector", "status"));

        Assert.Contains($"service: {state} ({Plist})\n", Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Status_names_a_port_held_by_another_process_and_shows_no_process()
    {
        macOs = false;
        probe.Listening.Add(CollectorConfig.HttpPort);
        Holder(9, string.Empty);

        Assert.Equal(0, await Run("collector", "status"));

        Assert.Contains("port 4318: held by unknown process (pid 9), not otelcol\n", Output, StringComparison.Ordinal);
        Assert.DoesNotContain("process:", Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Status_names_another_process_s_command_and_a_listener_it_cannot_place()
    {
        macOs = false;
        probe.Listening.Add(CollectorConfig.GrpcPort);

        Assert.Equal(0, await Run("collector", "status"));
        probe.Listening.Add(CollectorConfig.HttpPort);
        Holder(9, "nginx");
        Assert.Equal(0, await Run("collector", "status"));

        Assert.Contains("port 4317: listening\n", Output, StringComparison.Ordinal);
        Assert.Contains("port 4318: held by nginx (pid 9), not otelcol\n", Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Status_shows_a_listener_nobody_can_name_and_a_process_without_memory()
    {
        macOs = false;
        probe.Listening.Add(CollectorConfig.HttpPort);
        _ = proc.Answer(new Ran(0, "55\n", string.Empty), "lsof");
        _ = proc.Answer(new Ran(0, "/opt/otel/otelcol --config c\n", string.Empty), "ps", "-o", "command=");
        _ = proc.Answer(new Ran(0, "n/a\n", string.Empty), "ps", "-o", "rss=");

        Assert.Equal(0, await Run("collector", "status"));

        Assert.Contains("process: pid 55\n", Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Service_install_is_macos_only()
    {
        macOs = false;

        Assert.Equal(OtelStackCommand.FailedExit, await Run("collector", "service", "install"));

        Assert.Equal("Only on macOS; on Windows the jobs coordinator runs `collector ensure`.\n", Output);
    }

    [Fact]
    public async Task Service_install_loads_the_agent()
    {
        SeedCollector();
        Launchctl(loaded: false);

        Assert.Equal(0, await Run("collector", "service", "install"));

        Assert.Equal($"loaded {Plist}\n", Output);
        Assert.True(File.Exists(Plist));
        Assert.True(Directory.Exists(Path.GetDirectoryName(paths.Log)));
    }

    [Fact]
    public async Task Service_uninstall_removes_the_agent()
    {
        SeedPlist();

        Assert.Equal(0, await Run("collector", "service", "uninstall"));

        Assert.Equal("removed\n", Output);
        Assert.False(File.Exists(Plist));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Service_uninstall_says_when_there_is_nothing_to_remove(bool onMac)
    {
        macOs = onMac;

        Assert.Equal(0, await Run("collector", "service", "uninstall"));

        Assert.Equal("Not installed.\n", Output);
    }

    [Fact]
    public async Task Forward_set_off_and_show()
    {
        Assert.Equal(0, await Run("collector", "forward", "show"));
        Assert.Equal(0, await Run("collector", "forward", "set", "http://127.0.0.1:24318"));
        Assert.Equal(0, await Run("collector", "forward", "show"));
        Assert.Equal(0, await Run("collector", "forward", "off"));
        Assert.Equal(0, await Run("collector", "forward", "show"));

        Assert.Equal(
            "off\n"
            + "forwarding to http://127.0.0.1:24318; restart the collector to apply (`collector stop`, then `collector start`)\n"
            + "http://127.0.0.1:24318\n"
            + "forwarding off; restart the collector to apply (`collector stop`, then `collector start`)\n"
            + "off\n",
            Output);
    }

    [Fact]
    public async Task Forward_set_refuses_what_is_not_an_endpoint()
    {
        Assert.Equal(OtelStackCommand.UsageExit, await Run("collector", "forward", "set", "not a url"));

        Assert.NotEmpty(Output);
        Assert.Null(runtime.Forward());
    }

    [Fact]
    public async Task ClickHouse_install_verifies_both_pins()
    {
        SeedPins();

        Assert.Equal(0, await Run("clickhouse", "install"));

        Assert.Equal(
            $"verified {OtelPins.PinBinary(OtelPins.ClickHousePin(Key), paths.Home)}\n"
            + $"verified {OtelPins.PinBinary(OtelPins.ContribPin(Key), paths.Home)}\n",
            Output);
    }

    [Theory]
    [InlineData("install")]
    [InlineData("up")]
    public async Task ClickHouse_has_no_windows_build(string command)
    {
        key = "windows_amd64";

        Assert.Equal(OtelStackCommand.FailedExit, await Run("clickhouse", command));

        Assert.Equal("ClickHouse has no native build for windows_amd64; on Windows run it under WSL or Docker\n", Error);
        Assert.Empty(Output);
    }

    [Fact]
    public async Task Up_starts_both_parts_and_says_where_they_answer()
    {
        SeedPins();
        probe.Listening.Add(ClickHouseConfig.CollectorPort);

        Assert.Equal(0, await Run("clickhouse", "up"));

        Assert.Equal(
            "started clickhouse\nstarted otelcol-contrib\n"
            + "ClickHouse http://127.0.0.1:18123 (database otel); OTLP for backfill http://127.0.0.1:14318. `otel-stack clickhouse down` stops both.\n",
            Output);
    }

    [Fact]
    public async Task Up_names_what_was_already_running()
    {
        SeedPins();
        probe.Listening.Add(ClickHouseConfig.CollectorPort);
        Running(pair.Server, 7);
        Running(pair.Collector, 8);

        Assert.Equal(0, await Run("clickhouse", "up"));

        Assert.StartsWith("already running: clickhouse\nalready running: otelcol-contrib\n", Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Up_reports_a_part_that_does_not_start()
    {
        SeedPins();
        clickhouse.Error = "down";

        Assert.Equal(OtelStackCommand.FailedExit, await Run("clickhouse", "up"));

        Assert.NotEmpty(Error);
        Assert.Empty(Output);
    }

    [Fact]
    public async Task Down_stops_what_runs()
    {
        Running(pair.Server, 7);
        Running(pair.Collector, 8);

        Assert.Equal(0, await Run("clickhouse", "down"));

        Assert.Equal("stopped: otelcol-contrib, clickhouse\n", Output);
    }

    [Fact]
    public async Task Down_says_when_nothing_ran()
    {
        Assert.Equal(0, await Run("clickhouse", "down"));

        Assert.Equal("nothing was running\n", Output);
    }

    [Fact]
    public async Task ClickHouse_status_without_a_server_shows_the_parts_and_data()
    {
        clickhouse.Error = "down";

        Assert.Equal(0, await Run("clickhouse", "status"));

        Assert.Equal(
            $"clickhouse: not running\notelcol-contrib: not running\ndata: 0.0 MB in {paths.ClickHouseData}\n",
            Output);
    }

    [Fact]
    public async Task ClickHouse_status_shows_memory_and_the_tables()
    {
        Running(pair.Server, 7);
        Running(pair.Collector, 8);
        _ = proc.Answer(new Ran(0, "2048\n", string.Empty), "ps", "-o", "rss=", "-p", "7");
        clickhouse.Stats = """{"table":"otel_traces","rows":3,"bytes_on_disk":2097152}""" + "\n";

        Assert.Equal(0, await Run("clickhouse", "status"));

        Assert.StartsWith("clickhouse: pid 7, 2.0 MB resident\notelcol-contrib: pid 8\n", Output, StringComparison.Ordinal);
        Assert.Contains("otel_traces", Output, StringComparison.Ordinal);
        Assert.Contains("2.0 MB", Output.Split('\n')[3..].First(line => line.Contains("otel_traces", StringComparison.Ordinal)), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("SELECT count() FROM t;", "SELECT count() FROM t\nFORMAT PrettyCompactMonoBlock")]
    [InlineData("SELECT 2 FORMAT CSV", "SELECT 2 FORMAT CSV")]
    public async Task Query_adds_a_format_unless_the_sql_names_one(string sql, string sent)
    {
        Assert.Equal(0, await Run("clickhouse", "query", sql));

        Assert.Equal(sent, clickhouse.Requests[^1].Sql);
        Assert.Equal("ok\n", Output);
    }

    [Fact]
    public async Task Query_takes_the_format_option()
    {
        Assert.Equal(0, await Run("clickhouse", "query", "SELECT 2", "--format", "TSV"));

        Assert.Equal("SELECT 2\nFORMAT TSV", clickhouse.Requests[^1].Sql);
    }

    [Fact]
    public async Task Query_errors_go_to_stderr()
    {
        clickhouse.Error = "Code: 62. Syntax error";

        Assert.Equal(OtelStackCommand.FailedExit, await Run("clickhouse", "query", "SELEC"));

        Assert.Contains("Syntax error", Error, StringComparison.Ordinal);
        Assert.Empty(Output);
    }

    [Fact]
    public async Task Backfill_needs_clickhouse_answering()
    {
        clickhouse.Error = "down";

        Assert.Equal(OtelStackCommand.FailedExit, await Run("clickhouse", "backfill"));

        Assert.Equal("ClickHouse is not answering; run `otel-stack clickhouse up` first. Nothing sent.\n", Error);
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task Backfill_with_nothing_recorded_sends_nothing()
    {
        Assert.Equal(0, await Run("clickhouse", "backfill"));

        Assert.Equal("traces: 0 files, 0 unchanged since done, 0 checked, 0 spans sent\nlogs and metrics: 0 lines sent from 0 files\n", Output);
    }

    [Fact]
    public async Task Backfill_dry_run_counts_an_extra_trace_file()
    {
        var file = Path.Join(temp.Folder("extra"), "spans.jsonl");
        var ended = TraceLines.Ns(clock.GetUtcNow().AddHours(-1));
        await File.WriteAllTextAsync(file, TraceLines.Span("t1", "s1", ended) + TraceLines.Span("t1", "s2", ended), Ct);

        Assert.Equal(0, await Run("clickhouse", "backfill", "--path", file, "--dry-run"));

        Assert.Equal("traces: 1 files, 0 unchanged since done, 1 checked, 2 spans would send\nlogs and metrics: 0 lines would send from 0 files\n", Output);
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task Backfill_replays_recorded_logs()
    {
        _ = DataFile("logs.jsonl", days: 0, text: "{\"resourceLogs\": []}\n");

        Assert.Equal(0, await Run("clickhouse", "backfill"));

        Assert.EndsWith("logs and metrics: 1 lines sent from 1 files\n", Output, StringComparison.Ordinal);
        Assert.Empty(Error);
    }

    [Fact]
    public async Task Backfill_reports_a_rejected_replay_on_stderr()
    {
        _ = DataFile("logs.jsonl", days: 0, text: "{\"resourceLogs\": []}\n");
        sender.RejectFrom = 0;

        Assert.Equal(OtelStackCommand.FailedExit, await Run("clickhouse", "backfill"));

        Assert.Contains("collector rejected a batch: HTTP 400 no", Error, StringComparison.Ordinal);
    }

    private Task<int> Run(params string[] args)
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("test+1", TimeSpan.FromHours(1), "test+1", "test+1");
        var host = new OtelHost(paths, () => key, macOs, Path.Join(temp.Path, "checkout-traces"), _ => null, clock, zone);
        var parts = new OtelParts(
            runtime,
            probe,
            new OtelInstaller(downloadHttp),
            BuildLaunchd,
            pair,
            new ClickHouseClient(clickhouseHttp, new Uri("http://clickhouse.test:1")),
            sender,
            clock.SleepAsync);
        var command = new OtelStackCommand(host, parts, output, error);
        return command.Build().Parse(args).InvokeAsync(new InvocationConfiguration { Output = output, Error = error }, Ct);
    }

    private Task<Launchd> BuildLaunchd(CancellationToken cancellationToken)
    {
        launchdBuilt++;
        return Task.FromResult(new Launchd(proc, Agents, "gui/501"));
    }

    // launchctl answers everything; `print` says whether the agent is loaded.
    private void Launchctl(bool loaded)
    {
        _ = proc.Answer(new Ran(0, string.Empty, string.Empty), "launchctl");
        _ = proc.Answer(new Ran(loaded ? 0 : 113, string.Empty, string.Empty), "launchctl", "print");
    }

    // `pid` listens on the collector's port, running `command`.
    private void Holder(int pid, string command)
    {
        _ = proc.Answer(new Ran(0, $"{pid}\n", string.Empty), "lsof");
        _ = proc.Answer(new Ran(0, command + "\n", string.Empty), "ps", "-o", "command=");
    }

    // `pid` is alive and is the part's binary, started when its pid file says.
    private void Running(StackPart part, int pid)
    {
        var binary = $"/bin/{part.Name}";
        _ = Directory.CreateDirectory(Path.GetDirectoryName(part.PidFile)!);
        File.WriteAllText(part.PidFile, new Launched(pid, binary, Started).Serialise());
        _ = signals.Living.Add(pid);
        _ = proc.Answer(new Ran(0, $"{binary} --flag\n", string.Empty), "ps", "-o", "command=", "-p", $"{pid}");
        _ = proc.Answer(new Ran(0, $"{Started}\n", string.Empty), "ps", "-o", "lstart=", "-p", $"{pid}");
    }

    private void SeedCollector() =>
        Seed(Binary, OtelPins.InstallDir(paths.Home), OtelPins.Releases[Key].Sha256);

    private void SeedPins()
    {
        foreach (var pin in new[] { OtelPins.ClickHousePin(Key), OtelPins.ContribPin(Key) })
        {
            Seed(OtelPins.PinBinary(pin, paths.Home), OtelPins.PinDir(pin, paths.Home), pin.Sha256);
        }
    }

    private static void Seed(string binary, string folder, string sha256)
    {
        _ = Directory.CreateDirectory(folder);
        _ = Directory.CreateDirectory(Path.GetDirectoryName(binary)!);
        File.WriteAllText(binary, "binary");
        File.WriteAllText(Path.Join(folder, ".sha256"), sha256 + "\n");
    }

    private void SeedPlist()
    {
        _ = Directory.CreateDirectory(Agents);
        File.WriteAllText(Plist, "<plist/>");
    }

    private string DataFile(string name, int days, int bytes = 0, string? text = null)
    {
        _ = Directory.CreateDirectory(paths.Data);
        var path = Path.Join(paths.Data, name);
        File.WriteAllText(path, text ?? new string('x', bytes), new UTF8Encoding(false));
        File.SetLastWriteTimeUtc(path, clock.GetUtcNow().UtcDateTime.AddDays(-days));
        return path;
    }
}
