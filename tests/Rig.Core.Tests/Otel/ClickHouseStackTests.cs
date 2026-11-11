using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Runtime.Versioning;
using Rig.Core.Otel;
using Rig.Core.Processes;
using Rig.Core.Tests.Fakes;

namespace Rig.Core.Tests.Otel;

/// <summary>
/// The ClickHouse client, the on-demand pair, process signals and file ids. Nothing here reaches ClickHouse's or a
/// collector's port, or signals a process it did not start: ClickHouse is an in-memory handler, the pair's processes
/// are fakes, and the real signals go to a <c>sleep</c> started in a temp folder.
/// </summary>
public sealed class ClickHouseStackTests : IDisposable
{
    private static readonly Uri Server = new("http://clickhouse.test:1");

    private const string Started = "Tue Nov 11 09:00:00 2026";

    private readonly TempDir temp = new();
    private readonly FakeClickHouse clickhouse = new();
    private readonly HttpClient http;
    private readonly FakeSignals signals = new();
    private readonly FakeProbe probe = new();
    private readonly FakeProc proc = new();
    private readonly ManualClock clock = new(new DateTimeOffset(2026, 11, 11, 9, 0, 0, TimeSpan.Zero));
    private readonly RecordingDetached detached;
    private readonly OtelPaths paths;
    private readonly ClickHousePair pair;

    public ClickHouseStackTests()
    {
        http = new HttpClient(clickhouse, disposeHandler: false);
        detached = new RecordingDetached(signals, proc);
        paths = new OtelPaths(temp.Path);
        pair = new ClickHousePair(paths, signals, detached, probe, new CollectorRuntime(paths, proc, detached, windows: false), clock, clock.SleepAsync);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ClickHouseClient Client => new(http, Server);

    public void Dispose()
    {
        http.Dispose();
        clickhouse.Dispose();
        temp.Dispose();
    }

    [Fact]
    public void The_local_server_is_loopback_on_the_http_port() =>
        Assert.Equal("http://127.0.0.1:18123/", ClickHouseClient.LocalUrl.ToString());

    [Fact]
    public async Task The_server_defaults_to_the_local_one()
    {
        _ = await new ClickHouseClient(http).TextAsync("SELECT 1", cancellationToken: Ct);

        Assert.Equal("127.0.0.1:18123", Assert.Single(clickhouse.Uris).Authority);
    }

    [Fact]
    public void The_stack_s_exceptions_carry_their_message_and_cause()
    {
        var cause = new IOException("disk");

        Assert.Equal("x", new ClickHouseException("x", cause).Message);
        Assert.Same(cause, new ClickHouseException("x", cause).InnerException);
        Assert.NotEmpty(new ClickHouseException().Message);
        Assert.Equal("y", new StartupException("y", cause).Message);
        Assert.Same(cause, new StartupException("y", cause).InnerException);
        Assert.NotEmpty(new StartupException().Message);
    }

    [Fact]
    public void A_quoted_string_escapes_backslashes_and_quotes() =>
        Assert.Equal(@"'a\'b\\c'", ClickHouseClient.Quote(@"a'b\c"));

    [Fact]
    public async Task A_query_binds_its_parameters_and_authenticates_as_default()
    {
        var body = await Client.TextAsync("SELECT {name:String}", new Dictionary<string, string>(StringComparer.Ordinal) { ["name"] = "a b&c=d" }, Ct);

        var (sql, query, auth) = Assert.Single(clickhouse.Requests);
        Assert.Equal("ok\n", body);
        Assert.Equal("SELECT {name:String}", sql);
        Assert.Equal("a b&c=d", query["param_name"]);
        Assert.Equal("0", query["output_format_json_quote_64bit_integers"]);
        Assert.Equal("iso", query["date_time_output_format"]);
        Assert.Equal("Basic ZGVmYXVsdDo=", auth);
    }

    [Fact]
    public async Task An_error_answer_is_raised_with_its_text()
    {
        clickhouse.Error = "  Code: 62. Syntax error\n";

        var error = await Assert.ThrowsAsync<ClickHouseException>(() => Client.TextAsync("SELEC", cancellationToken: Ct));

        Assert.Equal("Code: 62. Syntax error", error.Message);
    }

    [Fact]
    public async Task An_empty_error_answer_is_raised_as_its_status()
    {
        clickhouse.Status = HttpStatusCode.ServiceUnavailable;

        var error = await Assert.ThrowsAsync<ClickHouseException>(() => Client.TextAsync("SELECT 1", cancellationToken: Ct));

        Assert.Equal("HTTP 503", error.Message);
    }

    [Fact]
    public async Task A_refused_connection_is_unreachable()
    {
        clickhouse.Throws = new HttpRequestException("connection refused");

        var error = await Assert.ThrowsAsync<ClickHouseException>(() => Client.TextAsync("SELECT 1", cancellationToken: Ct));

        Assert.Equal("ClickHouse unreachable at http://clickhouse.test:1/: connection refused", error.Message);
        Assert.IsType<HttpRequestException>(error.InnerException);
    }

    [Fact]
    public async Task A_timeout_is_unreachable()
    {
        clickhouse.Throws = new TaskCanceledException("timed out");

        var error = await Assert.ThrowsAsync<ClickHouseException>(() => Client.TextAsync("SELECT 1", cancellationToken: Ct));

        Assert.StartsWith("ClickHouse unreachable at", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_cancelled_query_is_cancelled_not_unreachable()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Client.TextAsync("SELECT 1", cancellationToken: cancelled.Token));
    }

    [Fact]
    public async Task A_ping_is_whether_the_server_answers_one()
    {
        Assert.True(await Client.PingAsync(Ct));

        clickhouse.Throws = new HttpRequestException("refused");
        Assert.False(await Client.PingAsync(Ct));
    }

    [Fact]
    public async Task A_table_exists_only_when_clickhouse_says_so()
    {
        Assert.True(await Client.HasTableAsync("otel_traces", Ct));
        Assert.False(await Client.HasTableAsync("otel_logs", Ct));
        Assert.Equal("otel", clickhouse.Requests[0].Query["param_database"]);

        clickhouse.Error = "boom";
        Assert.False(await Client.HasTableAsync("otel_traces", Ct));
    }

    [Fact]
    public async Task Table_stats_are_read_from_each_row()
    {
        clickhouse.Stats = """
            {"table":"otel_traces","rows":12,"bytes_on_disk":4096}

            {"table":"otel_logs","rows":0,"bytes_on_disk":0}
            """;

        var stats = await Client.TableStatsAsync(Ct);

        Assert.Equal([new TableStats("otel_traces", 12, 4096), new TableStats("otel_logs", 0, 0)], stats);
        Assert.EndsWith("ORDER BY bytes_on_disk DESC, table\nFORMAT JSONEachRow", clickhouse.Requests[0].Sql, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{\"table\":\"t\"}")]
    [InlineData("not json")]
    [InlineData("{\"table\":\"t\",\"rows\":\"many\",\"bytes_on_disk\":0}")]
    public async Task Rows_that_cannot_be_read_are_a_clickhouse_error(string stats)
    {
        clickhouse.Stats = stats;

        var error = await Assert.ThrowsAsync<ClickHouseException>(() => Client.TableStatsAsync(Ct));

        Assert.StartsWith("ClickHouse returned rows that could not be read: ", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Existing_spans_are_looked_up_in_sorted_distinct_batches()
    {
        _ = clickhouse.Spans.Add(new SpanKey("t1", "s1"));
        _ = clickhouse.Spans.Add(new SpanKey("t3", "s3"));
        _ = clickhouse.Spans.Add(new SpanKey("t9", "s9"));

        var found = await Client.ExistingSpansAsync(["t3", "t1", "t2", "t1"], chunk: 2, Ct);

        Assert.Equal(new HashSet<SpanKey> { new("t1", "s1"), new("t3", "s3") }, found);
        Assert.Equal(["['t1', 't2']", "['t3']"], clickhouse.Requests.Skip(1).Select(request => request.Query["param_ids"]));
    }

    [Fact]
    public async Task No_trace_ids_ask_nothing()
    {
        Assert.Empty(await Client.ExistingSpansAsync([], cancellationToken: Ct));
        Assert.Empty(clickhouse.Requests);
    }

    [Fact]
    public async Task No_spans_exist_before_the_traces_table_does()
    {
        clickhouse.HasTraces = false;

        Assert.Empty(await Client.ExistingSpansAsync(["t1"], cancellationToken: Ct));
        Assert.Single(clickhouse.Requests);
    }

    [Fact]
    public async Task A_batch_size_must_be_positive() =>
        _ = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Client.ExistingSpansAsync(["t"], chunk: 0, Ct));

    [Fact]
    public void Configs_are_written_beside_the_data_folder()
    {
        var configs = pair.WriteConfigs();

        Assert.True(Directory.Exists(paths.ClickHouseData));
        Assert.Equal(ClickHouseConfig.ServerXml(paths.ClickHouseData, configs.Users), File.ReadAllText(configs.Server));
        Assert.Equal(ClickHouseConfig.UsersXml(), File.ReadAllText(configs.Users));
        Assert.Equal(ClickHouseConfig.CollectorYaml(), File.ReadAllText(configs.Collector));
        Assert.Equal(paths.ClickHouseHome, Path.GetDirectoryName(configs.Collector));
    }

    [Fact]
    public async Task Up_starts_clickhouse_then_the_collector_and_records_their_pids()
    {
        probe.Listening.Add(ClickHouseConfig.CollectorPort);

        var result = await pair.UpAsync("/bin/clickhouse", "/bin/otelcol-contrib", _ => Task.FromResult(true), Ct);

        Assert.Equal(["clickhouse", "otelcol-contrib"], result.Started);
        Assert.Empty(result.Already);
        var (serverArgv, serverLog, serverDir, serverEnv) = detached.Starts[0];
        Assert.Equal(["/bin/clickhouse", "server", $"--config-file={Path.Join(paths.ClickHouseHome, "server.xml")}"], serverArgv);
        Assert.Equal(paths.ClickHouseLog, serverLog);
        Assert.Equal(paths.ClickHouseData, serverDir);
        Assert.Equal("0", serverEnv!["CLICKHOUSE_WATCHDOG_ENABLE"]);
        var (collectorArgv, collectorLog, collectorDir, collectorEnv) = detached.Starts[1];
        Assert.Equal(["/bin/otelcol-contrib", "--config", Path.Join(paths.ClickHouseHome, "otelcol-contrib.yaml")], collectorArgv);
        Assert.Equal(paths.ContribLog, collectorLog);
        Assert.Equal(paths.ClickHouseHome, collectorDir);
        Assert.Null(collectorEnv);
        Assert.Equal("100\t/bin/clickhouse\t\n", await File.ReadAllTextAsync(pair.Server.PidFile, Ct));
        Assert.Equal("101\t/bin/otelcol-contrib\t\n", await File.ReadAllTextAsync(pair.Collector.PidFile, Ct));
        Assert.True(Directory.Exists(Path.GetDirectoryName(paths.ContribLog)));
    }

    [Fact]
    public async Task Up_leaves_running_parts_alone()
    {
        probe.Listening.Add(ClickHouseConfig.CollectorPort);
        Running(pair.Server, 7);
        Running(pair.Collector, 8);

        var result = await pair.UpAsync("/bin/clickhouse", "/bin/otelcol-contrib", _ => Task.FromResult(true), Ct);

        Assert.Empty(result.Started);
        Assert.Equal(["clickhouse", "otelcol-contrib"], result.Already);
        Assert.Empty(detached.Starts);
    }

    [Fact]
    public async Task Up_polls_until_clickhouse_answers()
    {
        probe.Listening.Add(ClickHouseConfig.CollectorPort);
        var pings = 0;

        _ = await pair.UpAsync("/bin/clickhouse", "/bin/otelcol-contrib", _ => Task.FromResult(++pings > 3), Ct);

        Assert.Equal([TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(0.5)], clock.Slept);
    }

    [Fact]
    public async Task A_server_that_dies_during_startup_is_reported_with_its_log()
    {
        detached.DieAtOnce = true;

        var error = await Assert.ThrowsAsync<StartupException>(() => pair.UpAsync("/bin/clickhouse", "/bin/otelcol-contrib", _ => Task.FromResult(false), Ct));

        Assert.Equal($"clickhouse exited during startup; see {paths.ClickHouseLog}", error.Message);
        Assert.False(File.Exists(pair.Server.PidFile));
    }

    [Fact]
    public async Task A_server_that_never_answers_times_out()
    {
        var error = await Assert.ThrowsAsync<StartupException>(() => pair.UpAsync("/bin/clickhouse", "/bin/otelcol-contrib", _ => Task.FromResult(false), Ct));

        Assert.Equal($"clickhouse did not come up within 60s; see {paths.ClickHouseLog}", error.Message);
        Assert.Equal(120, clock.Slept.Count);
    }

    [Fact]
    public async Task A_collector_that_never_listens_times_out()
    {
        var error = await Assert.ThrowsAsync<StartupException>(() => pair.UpAsync("/bin/clickhouse", "/bin/otelcol-contrib", _ => Task.FromResult(true), Ct));

        Assert.Equal($"otelcol-contrib did not come up within 60s; see {paths.ContribLog}", error.Message);
    }

    [Fact]
    public async Task A_running_pid_needs_a_live_process()
    {
        Assert.Null(await pair.RunningPidAsync(pair.Server, Ct));

        Running(pair.Server, 7);
        Assert.Equal(7, await pair.RunningPidAsync(pair.Server, Ct));

        _ = signals.Living.Remove(7);
        Assert.Null(await pair.RunningPidAsync(pair.Server, Ct));
        Assert.False(File.Exists(pair.Server.PidFile));
    }

    [Theory]
    [InlineData("/usr/sbin/nginx")]
    [InlineData("/bin/clickhouse server", "Mon Nov 10 08:00:00 2026")]
    public async Task A_pid_reused_by_another_process_is_not_signalled(string command, string started = Started)
    {
        Running(pair.Server, 7);
        _ = proc.Answer(new Ran(0, command + "\n", string.Empty), "ps", "-o", "command=", "-p", "7");
        _ = proc.Answer(new Ran(0, started + "\n", string.Empty), "ps", "-o", "lstart=", "-p", "7");

        Assert.Empty(await pair.DownAsync(Ct));

        Assert.Empty(signals.Sent);
        Assert.False(File.Exists(pair.Server.PidFile));
    }

    [Theory]
    [InlineData("")]
    [InlineData("-7")]
    [InlineData("seven")]
    [InlineData("99999999999")]
    [InlineData("7\n")]
    public async Task A_pid_file_that_is_not_a_launch_record_is_removed(string text)
    {
        _ = Directory.CreateDirectory(paths.ClickHouseHome);
        _ = signals.Living.Add(7);
        await File.WriteAllTextAsync(pair.Server.PidFile, text, Ct);

        Assert.Null(await pair.RunningPidAsync(pair.Server, Ct));
        Assert.False(File.Exists(pair.Server.PidFile));
    }

    [Fact]
    public async Task Down_stops_the_collector_then_clickhouse()
    {
        Running(pair.Server, 7);
        Running(pair.Collector, 8);

        var stopped = await pair.DownAsync(Ct);

        Assert.Equal(["otelcol-contrib", "clickhouse"], stopped);
        Assert.Equal([(8, false), (7, false)], signals.Sent);
        Assert.False(File.Exists(pair.Server.PidFile));
        Assert.False(File.Exists(pair.Collector.PidFile));
        Assert.Empty(clock.Slept);
    }

    [Fact]
    public async Task Down_kills_a_part_still_running_after_its_wait()
    {
        Running(pair.Collector, 8);
        _ = signals.Stubborn.Add(8);

        var stopped = await pair.DownAsync(Ct);

        Assert.Equal(["otelcol-contrib"], stopped);
        Assert.Equal([(8, false), (8, true)], signals.Sent);
        Assert.Equal(60, clock.Slept.Count);
    }

    [Fact]
    public async Task Down_with_nothing_running_stops_nothing()
    {
        Assert.Empty(await pair.DownAsync(Ct));
        Assert.Empty(signals.Sent);
    }

    [Fact]
    public async Task Status_reads_each_running_part_s_memory()
    {
        Running(pair.Server, 7);
        _ = proc.Answer(new Ran(0, "  204800\n", string.Empty), "ps", "-o", "rss=", "-p", "7");

        var statuses = await pair.StatusAsync(Ct);

        Assert.Equal([new PartStatus("clickhouse", 7, 200.0), new PartStatus("otelcol-contrib", null, null)], statuses);
    }

    [Fact]
    public void Data_bytes_count_regular_files_and_skip_links()
    {
        Assert.Equal(0, pair.DataBytes());

        var data = Directory.CreateDirectory(paths.ClickHouseData).FullName;
        File.WriteAllText(Path.Join(data, "a.bin"), "1234");
        _ = Directory.CreateDirectory(Path.Join(data, "store"));
        File.WriteAllText(Path.Join(data, "store", "b.bin"), "56");
        if (!OperatingSystem.IsWindows())
        {
            var outside = temp.File("outside.bin");
            File.WriteAllText(outside, new string('x', 1000));
            _ = File.CreateSymbolicLink(Path.Join(data, "link.bin"), outside);
        }

        Assert.Equal(6, pair.DataBytes());
    }

    [Fact]
    public void No_pid_at_or_below_zero_is_alive()
    {
        var real = new ProcessSignals();

        Assert.False(real.Alive(0));
        Assert.False(real.Alive(-1));
        Assert.False(real.Alive(int.MaxValue));
    }

    [Fact]
    public void This_process_is_alive_where_signals_exist() =>
        Assert.Equal(!OperatingSystem.IsWindows(), new ProcessSignals().Alive(Environment.ProcessId));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [UnsupportedOSPlatform("windows")]
    public async Task A_signal_stops_a_process_this_test_started(bool kill)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix only: Windows has no signals.");
        var real = new ProcessSignals();
        var pid = await new UnixDetachedStart(new SystemProc()).StartAsync(["/bin/sleep", "30"], Path.Join(temp.Path, "sleep.log"), temp.Path, cancellationToken: Ct);
        try
        {
            Assert.True(real.Alive(pid));

            real.Signal(pid, kill);

            var watch = Stopwatch.StartNew();
            while (real.Alive(pid) && watch.Elapsed < TimeSpan.FromSeconds(10))
            {
                await Task.Delay(50, Ct);
            }

            Assert.False(real.Alive(pid));
        }
        finally
        {
            if (real.Alive(pid))
            {
                using var started = Process.GetProcessById(pid);
                started.Kill();
            }
        }
    }

    [Fact]
    public async Task An_inode_is_what_ls_reports()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix only: Windows has file ids, not ls -i.");
        var file = temp.File("a.jsonl");

        var listed = await new SystemProc().RunAsync(new ProcRequest(["ls", "-di", file]), Ct);

        Assert.Equal(ulong.Parse(listed.Out.Trim().Split(' ')[0], CultureInfo.InvariantCulture), FileIds.Inode(file));
    }

    [Fact]
    public void A_file_keeps_its_id_through_a_rename_and_differs_from_another()
    {
        var file = temp.File("logs.jsonl");
        var other = temp.File("metrics.jsonl");
        var before = FileIds.Inode(file);

        var rotated = Path.Join(temp.Path, "logs-2026.jsonl");
        File.Move(file, rotated);

        Assert.NotNull(before);
        Assert.Equal(before, FileIds.Inode(rotated));
        Assert.NotEqual(before, FileIds.Inode(other));
    }

    [Fact]
    public void A_missing_file_has_no_id() =>
        Assert.Null(FileIds.Inode(Path.Join(temp.Path, "gone.jsonl")));

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
}
