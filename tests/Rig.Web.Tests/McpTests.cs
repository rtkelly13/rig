using System.Globalization;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Rig.Core.Estate;
using Rig.Core.Jobs;
using Rig.Core.Layout;
using Rig.Core.Otel;
using Rig.Core.Roots;
using Rig.Core.Tests;
using Rig.Web.Mcp;
using Rig.Web.Ops;

namespace Rig.Web.Tests;

/// <summary>The ops MCP server: its tools, their schemas, and the answers the ops pages would give.</summary>
public sealed class McpTests
{
    [Fact]
    public async Task Server_lists_every_tool_with_its_arguments_and_annotations()
    {
        await using var harness = await Harness.StartAsync();
        await using var client = await harness.ConnectAsync();

        var tools = (await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken))
            .ToDictionary(tool => tool.Name, StringComparer.Ordinal);

        Assert.Equal(
            "apply_trim, attention, decisions, disk_usage, list_jobs, list_runs, list_traces, run_output, start_run, telemetry_status, trim_candidates",
            string.Join(", ", tools.Keys.Order(StringComparer.Ordinal)));

        foreach (var name in tools.Keys.Where(name => name is not ("start_run" or "apply_trim")))
        {
            Assert.True(tools[name].ProtocolTool.Annotations?.ReadOnlyHint, name);
        }

        var start = tools["start_run"].ProtocolTool.Annotations;
        Assert.False(start?.ReadOnlyHint);
        Assert.False(start?.DestructiveHint);
        Assert.False(start?.IdempotentHint);
        var trim = tools["apply_trim"].ProtocolTool.Annotations;
        Assert.True(trim?.DestructiveHint);
        Assert.True(trim?.IdempotentHint);
        Assert.Equal("job, run_id", string.Join(", ", Required(tools["run_output"].ProtocolTool)));
        Assert.Equal("paths", string.Join(", ", Required(tools["apply_trim"].ProtocolTool)));
        Assert.Empty(Required(tools["list_jobs"].ProtocolTool));
    }

    [Fact]
    public async Task Attention_lists_failing_jobs_and_says_why_there_are_no_decisions()
    {
        await using var harness = await Harness.StartAsync();
        await using var client = await harness.ConnectAsync();

        var answer = JsonNode.Parse(await CallAsync(client, "attention"))!;

        Assert.Equal("align", Assert.Single(answer["failing_jobs"]!.AsArray())!["name"]!.ToString());
        Assert.Empty(answer["decisions"]!["decisions"]!.AsArray());
        Assert.Contains("No decisions feed yet", (string?)answer["decisions"]!["note"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Jobs_and_runs_match_the_routes()
    {
        await using var harness = await Harness.StartAsync();
        await using var client = await harness.ConnectAsync();

        var jobs = JsonNode.Parse(await CallAsync(client, "list_jobs"))!.AsArray();
        Assert.Equal("align, sync", string.Join(", ", jobs.Select(job => job!["name"]!.ToString())));
        Assert.Equal("every run", (string?)jobs[0]!["cadence"]);
        Assert.Equal("tick", (string?)jobs[0]!["triggers"]![0]);
        Assert.Equal(5, (int?)jobs[0]!["timeout_minutes"]);

        var runs = JsonNode.Parse(await CallAsync(client, "list_runs", ("job", "align"), ("limit", 0)))!.AsArray();
        Assert.Equal("tick-20261010-090000", Assert.Single(runs)!["run_id"]!.ToString());
        Assert.False((bool?)runs[0]!["ok"]);

        var output = JsonNode.Parse(await CallAsync(client, "run_output", ("job", "align"), ("run_id", "tick-20261010-090000")))!;
        Assert.Null(output["output"]);
    }

    [Fact]
    public async Task Run_output_refuses_an_unknown_job_or_run_as_a_tool_error()
    {
        await using var harness = await Harness.StartAsync();
        await using var client = await harness.ConnectAsync();

        var unknownJob = await client.CallToolAsync("run_output", new Dictionary<string, object?> { ["job"] = "nope", ["run_id"] = "tick-20261010-090000" }, cancellationToken: TestContext.Current.CancellationToken);
        var unknownRun = await client.CallToolAsync("run_output", new Dictionary<string, object?> { ["job"] = "align", ["run_id"] = "../../etc" }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(unknownJob.IsError);
        Assert.Contains("No job named 'nope'", Text(unknownJob), StringComparison.Ordinal);
        Assert.True(unknownRun.IsError);
        Assert.Contains("No run '../../etc' of align", Text(unknownRun), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Start_run_runs_one_job_or_every_due_one_and_refuses_an_unknown_job()
    {
        await using var harness = await Harness.StartAsync();
        await using var client = await harness.ConnectAsync();

        Assert.Equal(
            """{"job":"align","pid":4242}""",
            await CallAsync(client, "start_run", ("job", "align")));
        Assert.Equal("""{"job":null,"pid":4242}""", await CallAsync(client, "start_run"));
        Assert.Equal(["rig", "jobs", "run", "--trigger", "tick", "--only", "align"], harness.Started[0]);
        Assert.Equal(["rig", "jobs", "run", "--trigger", "tick"], harness.Started[1]);

        var refused = await client.CallToolAsync("start_run", new Dictionary<string, object?> { ["job"] = "nope" }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(refused.IsError);
        Assert.Contains("No job named 'nope'", Text(refused), StringComparison.Ordinal);
        Assert.Equal(2, harness.Started.Count);
    }

    [Fact]
    public async Task Decisions_and_disk_usage_answer_the_routes_shapes()
    {
        await using var harness = await Harness.StartAsync();
        await using var client = await harness.ConnectAsync();

        var feed = JsonNode.Parse(await CallAsync(client, "decisions"))!;
        Assert.Contains("No decisions feed yet", (string?)feed["note"], StringComparison.Ordinal);

        var disk = JsonNode.Parse(await CallAsync(client, "disk_usage"))!;
        Assert.Equal(harness.Root, (string?)disk["path"]);
        Assert.True((long?)disk["total"] > 0);
        Assert.True((long?)disk["free"] >= 0);
        Assert.Equal((long?)disk["total"]! - (long?)disk["free"]!, (long?)disk["used"]);
    }

    [Fact]
    public async Task Trim_candidates_scan_and_apply_only_offered_paths()
    {
        var offered = Path.Join(Path.GetTempPath(), "rig-tests", "node_modules");
        await using var harness = await Harness.StartAsync([new Trimmable("/wt", offered, "node_modules", new TreeMeasure(4096, 4096))]);
        await using var client = await harness.ConnectAsync();
        _ = await CallAsync(client, "trim_candidates");
        await harness.Trims.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        var scan = JsonNode.Parse(await CallAsync(client, "trim_candidates"))!;
        Assert.False((bool?)scan["scanning"]);
        Assert.Equal(offered, (string?)scan["candidates"]![0]!["path"]);
        Assert.Equal(4096, (long?)scan["candidates"]![0]!["freeable"]);

        var applied = JsonNode.Parse(await CallAsync(client, "apply_trim", ("paths", new[] { offered, Path.Join(Path.GetTempPath(), "stray") })))!;
        Assert.Equal("removed", (string?)applied[0]!["status"]);
        Assert.Equal("not-in-scan", (string?)applied[1]!["status"]);
    }

    [Fact]
    public async Task Telemetry_status_and_traces_answer_the_test_source()
    {
        await using var harness = await Harness.StartAsync();
        await using var client = await harness.ConnectAsync();

        var status = JsonNode.Parse(await CallAsync(client, "telemetry_status"))!;
        Assert.Equal("clickhouse", (string?)status["store"]!["source"]);
        Assert.True((bool?)status["store"]!["clickhouse_reachable"]);
        Assert.Equal("http://hyperdx.test", (string?)status["explorers"]!["hyperdx_url"]);

        var traces = JsonNode.Parse(await CallAsync(client, "list_traces", ("days", 99), ("limit", 0), ("service", " rig.proc ")))!;
        Assert.Equal("clickhouse", (string?)traces["source"]);
        var trace = traces["traces"]![0]!;
        Assert.Equal("trace-1", (string?)trace["trace_id"]);
        Assert.Equal("rig.proc", (string?)trace["service"]);
        Assert.Equal(12.5, (double?)trace["duration_ms"]);
    }

    [Fact]
    public void Decision_feed_reads_the_written_feed_and_says_why_it_is_empty()
    {
        using var dir = new TempDir();
        var path = Path.Join(dir.Path, "decisions.json");
        Assert.Contains("has not written", DecisionFeed.Read(path).Note, StringComparison.Ordinal);

        File.WriteAllText(path, "[]");
        var empty = DecisionFeed.Read(path);
        Assert.Equal("Nothing needs deciding.", empty.Note);
        Assert.NotEmpty(empty.Written);

        File.WriteAllText(path, """[{"kind":"no-pr-worktree","repo":"rig","path":"/code/rig","reason":"idle","size_bytes":10,"suggested_action":"remove","command":"git worktree remove /code/rig"}]""");
        var one = DecisionFeed.Read(path);
        Assert.Equal("/code/rig", Assert.Single(one.Decisions).Path);
        Assert.Empty(one.Note);
        Assert.EndsWith("+00:00", one.Written, StringComparison.Ordinal);

        File.WriteAllText(path, "{not json");
        Assert.Contains("expected shape", DecisionFeed.Read(path).Note, StringComparison.Ordinal);
    }

    [Fact]
    public void Run_history_skips_unreadable_lines_and_reads_output_only_inside_the_runs_folder()
    {
        using var dir = new TempDir();
        RunHistory.Append(dir.Path, new JobRun("align", "tick-20261010-090000", "2026-10-10T09:00:00+00:00", 2.0, 3, "tick", null, "runs/align/tick-20261010-090000.log"));
        File.AppendAllText(Path.Join(dir.Path, RunHistory.HistoryName), "{broken\n");
        var output = Path.Join(dir.Path, "runs", "align", "tick-20261010-090000.log");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output, "it failed\n");

        var record = Assert.Single(RunHistory.ReadRuns(dir.Path));
        Assert.Equal("it failed\n", RunHistory.ReadOutput(dir.Path, record));
        Assert.Equal(record.RunId, RunHistory.FindRun(dir.Path, "align", record.RunId)?.RunId);
        Assert.True(RunHistory.IsRunId(record.RunId));
        Assert.False(RunHistory.IsRunId("../../etc"));
        Assert.Null(RunHistory.ReadOutput(dir.Path, record with { Output = "runs/../../outside.log" }));

        var views = JobViews.Runs(dir.Path, limit: 1);
        Assert.Equal(record.RunId, Assert.Single(views).RunId);
    }

    [Fact]
    public void Run_views_add_a_last_outcome_the_history_does_not_hold()
    {
        using var dir = new TempDir();
        var outcome = new Outcome("2026-10-10T09:00:00+00:00", 2.0, 0, "tick") { RunId = "tick-20261010-090000" };
        JobState.WriteState(Path.Join(dir.Path, JobState.StateName), new Dictionary<string, Outcome> { ["sync"] = outcome });
        Directory.CreateDirectory(JobState.LogsDir(dir.Path));
        File.WriteAllText(Path.Join(JobState.LogsDir(dir.Path), "sync.log"), "--- 2026-10-10 09:00:00 [tick-20261010-090000] sync\nok\n--- exit 0\n");

        var view = Assert.Single(JobViews.Runs(dir.Path));
        Assert.Equal("sync", view.Job);
        Assert.True(view.HasOutput);
        Assert.Equal("ok\n", JobViews.Output([new Job("sync", "", ["sync"], new HashSet<JobTrigger> { JobTrigger.Tick }, JobCadence.EveryRun, 5)], dir.Path, "sync", "tick-20261010-090000").Output);
    }

    [Fact]
    public async Task Telemetry_prefers_clickhouse_then_falls_back_and_remembers_a_failure()
    {
        var clock = 0.0;
        var probes = 0;
        var queries = 0;
        var files = 0;
        using var source = new Telemetry(
            () =>
            {
                probes++;
                return Task.FromResult(true);
            },
            query =>
            {
                queries++;
                return Task.FromResult<IReadOnlyList<TraceView>>([new TraceView("trace-1", query.Service ?? "", "root", 10, 2, 1, 0)]);
            },
            _ =>
            {
                files++;
                return [new TraceView("file-1", "rig", "root", 5, 1, 1, 0)];
            },
            _ => Task.FromResult(new ExplorerStatusView("http://hyperdx.test", true, "http://hyperdx.test/{trace_id}", "", false, "")),
            () => clock);

        var status = await source.StatusAsync(TestContext.Current.CancellationToken);
        Assert.Equal("clickhouse", status.Store.Source);
        Assert.True(status.Store.ClickhouseReachable);
        Assert.Equal(1, probes);
        _ = await source.StatusAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, probes);
        clock = 31;
        _ = await source.StatusAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, probes);

        var recent = await source.RecentAsync(new TraceQuery(2, 5, "rig.proc"));
        Assert.Equal("clickhouse", recent.Source);
        Assert.Empty(recent.Note);
        Assert.Equal("trace-1", Assert.Single(recent.Traces).TraceId);
        Assert.Equal(1, queries);

        using var failing = new Telemetry(
            () => Task.FromResult(true),
            _ => throw new ClickHouseException(new string('x', 400)),
            _ =>
            {
                files++;
                return [new TraceView("file-1", "rig", "root", 5, 1, 1, 0)];
            },
            _ => Task.FromResult(new ExplorerStatusView("", false, "", "", false, "")),
            () => 100);
        var fellBack = await failing.RecentAsync(new TraceQuery());
        Assert.Equal("ndjson", fellBack.Source);
        Assert.Contains("ClickHouse query failed", fellBack.Note, StringComparison.Ordinal);
        Assert.True(fellBack.Note.Length < 400);
        Assert.Equal("file-1", Assert.Single(fellBack.Traces).TraceId);
        _ = await failing.StatusAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, files);
    }

    [Fact]
    public async Task Trim_cache_reports_a_scan_failure_and_refuses_a_changed_candidate()
    {
        var item = new Trimmable("/wt", "/wt/node_modules", "node_modules", new TreeMeasure(10, 10));
        using var failingScan = new TrimCache(
            _ => throw new IOException("disk gone"),
            (_, _) => Task.FromResult(string.Empty),
            (_, _) => Task.FromResult("removed"),
            () => "now");
        _ = failingScan.Current();
        await failingScan.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal("disk gone", failingScan.Current().Error);

        using var refusing = new TrimCache(
            _ => Task.FromResult<IReadOnlyList<Trimmable>>([item with { Refused = "tracked files" }]),
            (_, _) => Task.FromResult(string.Empty),
            (_, _) => Task.FromResult("removed"),
            () => "now");
        _ = refusing.Current();
        await refusing.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var refused = Assert.Single(await refusing.ApplyAsync([item.Path], TestContext.Current.CancellationToken));
        Assert.Equal("refused: tracked files", refused.Status);
        Assert.Equal(10, refused.Freeable);

        var missing = await refusing.ApplyAsync([Path.Join(Path.GetTempPath(), "elsewhere")], TestContext.Current.CancellationToken);
        Assert.Equal("not-in-scan", Assert.Single(missing).Status);
    }

    [Fact]
    public void The_real_sources_build_for_a_checkout()
    {
        using var dir = new TempDir();
        using var ops = RigOps.For(EstateLayout.Isolated(dir.Path) with { Checkout = dir.Path });

        Assert.NotNull(ops);
    }

    [Fact]
    public void File_traces_summarise_traces_filter_and_order_them()
    {
        using var dir = new TempDir();
        var now = TimeProvider.System.GetUtcNow();
        var start = (now.ToUnixTimeMilliseconds() * 1_000_000) - 1_000_000;
        var day = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var path = Directory.CreateDirectory(Path.Join(dir.Path, "rig.proc")).FullName;
        File.WriteAllText(Path.Join(path, day + ".ndjson"), Envelope(start) + "\n");

        var traces = Telemetry.Files([dir.Path], new TraceQuery(1, 10, "rig.proc"));
        var trace = Assert.Single(traces);
        Assert.Equal("trace-1", trace.TraceId);
        Assert.Equal("rig.proc", trace.Service);
        Assert.Equal("root", trace.Root);
        Assert.Equal(2, trace.Spans);
        Assert.Equal(1, trace.Errors);
        Assert.True(trace.DurationMs >= 30);

        Assert.Empty(Telemetry.Files([dir.Path], new TraceQuery(1, 10, "other")));
        Assert.Single(Telemetry.Files([dir.Path], new TraceQuery(1, 1, "rig.proc")));
    }

    [Fact]
    public async Task Clickhouse_rows_become_trace_views()
    {
        using var handler = new JsonHandler(
            "{\"trace_id\":\"t1\",\"service\":\"rig.proc\",\"root\":\"run\",\"start_ns\":1000000,\"end_ns\":3500000,\"spans\":3,\"errors\":1}\n");
        using var http = new HttpClient(handler);
        var clickhouse = new ClickHouseClient(http);

        var traces = await Telemetry.ClickHouse(clickhouse, new TraceQuery(2, 5, "rig.proc"));

        var trace = Assert.Single(traces);
        Assert.Equal("t1", trace.TraceId);
        Assert.Equal("rig.proc", trace.Service);
        Assert.Equal("run", trace.Root);
        Assert.Equal(2.5, trace.DurationMs);
        Assert.Equal(3, trace.Spans);
        Assert.Equal(1, trace.Errors);
    }

    [Fact]
    public void Cadence_text_says_idle_and_flag_and_run_output_refuses_what_it_cannot_name()
    {
        var job = new Job("align", "align", ["tool"], new HashSet<JobTrigger> { JobTrigger.Tick }, JobCadence.Daily, 5)
        {
            IdleMinutes = 3,
            Feature = "claude-session-restore",
        };
        Assert.Equal("daily, idle 3 min, flag claude-session-restore", JobViews.CadenceText(job));

        using var dir = new TempDir();
        var jobs = new[] { job };
        Assert.Throws<KeyNotFoundException>(() => JobViews.Output(jobs, dir.Path, "nope", "tick-20261010-090000"));
        Assert.Throws<ArgumentException>(() => JobViews.Output(jobs, dir.Path, "align", "../../etc"));
        Assert.Throws<ArgumentException>(() => JobViews.Output(jobs, dir.Path, "align", "tick-20261010-090000"));

        var outcome = new Outcome("2026-10-10T09:00:00+00:00", 2.0, 0, "tick");
        Assert.Equal("tick-20261010-090000", JobViews.OutcomeRunId(outcome));
        Assert.Null(JobViews.LoggedOutput(dir.Path, "align", outcome));
    }

    [Fact]
    public void Decision_feed_and_run_history_ignore_shapes_they_cannot_read()
    {
        using var dir = new TempDir();
        var path = Path.Join(dir.Path, "decisions.json");
        File.WriteAllText(path, "{}");
        Assert.Contains("expected shape", DecisionFeed.Read(path).Note, StringComparison.Ordinal);
        File.WriteAllText(path, """[{"kind":"x","repo":"r","path":"p","reason":"why","size_bytes":"ten","suggested_action":"a","command":"c"}]""");
        Assert.Contains("expected shape", DecisionFeed.Read(path).Note, StringComparison.Ordinal);
        File.WriteAllText(path, """["not an object"]""");
        Assert.Contains("expected shape", DecisionFeed.Read(path).Note, StringComparison.Ordinal);

        var history = Path.Join(dir.Path, RunHistory.HistoryName);
        File.WriteAllText(history, """{"job":"a","run_id":"tick-20261010-090000","started":"2026-10-10T09:00:00+00:00","seconds":1,"trigger":"tick"}""" + "\n");
        File.WriteAllText(history, File.ReadAllText(history) + """{"job":1}""" + "\n");
        File.WriteAllText(history, File.ReadAllText(history) + """{"job":"b","run_id":"tick-20261010-090001","started":"not a time","seconds":1,"trigger":"tick","returncode":3.0,"error":1,"output":2,"trace_id":3,"span_id":4}""" + "\n");
        var problems = new List<string>();
        var runs = RunHistory.ReadRuns(dir.Path, problem: problems.Add);
        var run = Assert.Single(runs);
        Assert.Equal("a", run.Job);
        Assert.Null(run.ReturnCode);
        Assert.Equal(string.Empty, run.Output);
        Assert.Equal(string.Empty, run.TraceId);
        Assert.Equal(2, problems.Count);
        Assert.Contains(":2: $.job: expected a string, found the number 1", problems[1], StringComparison.Ordinal);
        Assert.Contains(":3: $.returncode: expected a whole number, found the number 3.0", problems[0], StringComparison.Ordinal);
        Assert.Empty(RunHistory.ReadRuns(Path.Join(dir.Path, "missing")));
        Assert.Null(RunHistory.ReadOutput(dir.Path, run));
        Assert.Single(JobViews.Runs(dir.Path));
    }

    private static string Envelope(long startNs)
    {
        static JsonObject Span(string name, string spanId, string parent, long start, long end, int? status) => new()
        {
            ["traceId"] = "trace-1",
            ["spanId"] = spanId,
            ["parentSpanId"] = parent,
            ["name"] = name,
            ["startTimeUnixNano"] = start.ToString(CultureInfo.InvariantCulture),
            ["endTimeUnixNano"] = end.ToString(CultureInfo.InvariantCulture),
            ["status"] = status is { } code ? new JsonObject { ["code"] = code } : null,
        };
        return new JsonObject
        {
            ["resourceSpans"] = new JsonArray(
                new JsonObject
                {
                    ["resource"] = new JsonObject
                    {
                        ["attributes"] = new JsonArray(
                            new JsonObject { ["key"] = "service.name", ["value"] = new JsonObject { ["stringValue"] = "rig.proc" } }),
                    },
                    ["scopeSpans"] = new JsonArray(
                        new JsonObject
                        {
                            ["scope"] = new JsonObject { ["name"] = "rig.proc" },
                            ["spans"] = new JsonArray(
                                Span("root", "s1", "", startNs, startNs + 20_000_000, 1),
                                Span("child", "s2", "s1", startNs + 5_000_000, startNs + 30_000_000, 2)),
                        }),
                }),
        }.ToJsonString();
    }

    [Fact]
    public async Task The_real_telemetry_source_probes_clickhouse_and_the_explorers()
    {
        using var dir = new TempDir();
        using var telemetry = Telemetry.ForCheckout(dir.Path);

        var status = await telemetry.StatusAsync(TestContext.Current.CancellationToken);

        Assert.True(status.Store.Source is "clickhouse" or "ndjson");
        Assert.False(string.IsNullOrEmpty(status.Store.Detail));
    }

    private static IReadOnlyList<string> Required(Tool tool) =>
        tool.InputSchema.ValueKind == System.Text.Json.JsonValueKind.Object
        && tool.InputSchema.TryGetProperty("required", out var required)
            ? [.. required.EnumerateArray().Select(item => item.GetString()!)]
            : [];

    private static async Task<string> CallAsync(McpClient client, string name, params (string Name, object? Value)[] arguments)
    {
        var result = await client.CallToolAsync(
            name,
            arguments.ToDictionary(item => item.Name, item => item.Value),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotEqual(true, result.IsError);
        return Text(result);
    }

    private static string Text(CallToolResult result) =>
        Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;

    private sealed class Harness : IAsyncDisposable
    {
        private readonly TempDir _dir = new();
        private RigWebHost _host = null!;
        private HttpClientTransport? _transport;
        private readonly List<string[]> _started = [];

        private Harness()
        {
        }

        private async Task BuildAsync(IReadOnlyList<Trimmable> trims)
        {
            Root = _dir.Path;
            var jobs = new Job[]
            {
                new("align", "align things", ["tool", "align"], new HashSet<JobTrigger> { JobTrigger.Tick }, JobCadence.EveryRun, 5),
                new("sync", "sync things", ["tool", "sync"], new HashSet<JobTrigger> { JobTrigger.Tick }, JobCadence.EveryRun, 5),
            };
            JobState.WriteState(
                Path.Join(Root, JobState.StateName),
                new Dictionary<string, Outcome>
                {
                    ["align"] = new("2026-10-10T09:00:00+00:00", 2.0, 3, "tick") { RunId = "tick-20261010-090000" },
                    ["sync"] = new("2026-10-10T09:00:00+00:00", 1.0, 0, "tick") { RunId = "tick-20261010-090000" },
                });
            RunHistory.Append(Root, new JobRun("align", "tick-20261010-090000", "2026-10-10T09:00:00+00:00", 2.0, 3, "tick", null, string.Empty));

            Trims = new TrimCache(
                _ => Task.FromResult(trims),
                (_, _) => Task.FromResult(string.Empty),
                (_, _) => Task.FromResult("removed"),
                () => "then");
            Ops = new RigOps(
                () => jobs,
                Root,
                argv =>
                {
                    _started.Add([.. argv]);
                    return Task.FromResult(4242);
                },
                job =>
                {
                    List<string> argv = ["rig", "jobs", "run", "--trigger", "tick"];
                    if (job is not null)
                    {
                        argv.Add("--only");
                        argv.Add(job);
                    }

                    return argv;
                },
                Path.Join(Root, "decisions.json"),
                Root,
                Trims,
                () => new Telemetry(
                    () => Task.FromResult(true),
                    query => Task.FromResult<IReadOnlyList<TraceView>>([new TraceView("trace-1", query.Service ?? "", "root", 1, 12.5, 2, 0)]),
                    _ => [new TraceView("file-1", "rig", "root", 1, 1, 1, 0)],
                    _ => Task.FromResult(new ExplorerStatusView("http://hyperdx.test", true, "http://hyperdx.test/{trace_id}", "", false, "")),
                    () => 0));
            _host = await RigWebHost.CreateAsync(
                new WebHostSettings(
                    "127.0.0.1",
                    0,
                    false,
                    _dir.Folder("templates"),
                    _dir.Folder("assets"),
                    Path.Join(Root, "rig.db"),
                    Path.Join(Root, "managed-projects.json"),
                    Root,
                    Root,
                    Path.Join(Root, "decisions.json"),
                    Path.Join(Root, "temp", "triage", "queue.md"),
                    Path.Join(Root, "temp", "traces", "sdk")),
                Ops);
        }

        public RigOps Ops { get; private set; } = null!;

        public TrimCache Trims { get; private set; } = null!;

        public string Root { get; private set; } = null!;

        public List<string[]> Started => _started;

        public HttpClient Http { get; } = new();

        public static async Task<Harness> StartAsync(IReadOnlyList<Trimmable>? trims = null)
        {
            var harness = new Harness();
            await harness.BuildAsync(trims ?? []);
            await harness._host.StartAsync(TestContext.Current.CancellationToken);
            harness.Http.BaseAddress = harness._host.Address;
            return harness;
        }

        public async Task<McpClient> ConnectAsync()
        {
            _transport = new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri(_host.Address, "/api/mcp"),
                TransportMode = HttpTransportMode.StreamableHttp,
            });
            return await McpClient.CreateAsync(_transport, cancellationToken: TestContext.Current.CancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            Http.Dispose();
            if (_transport is not null)
            {
                await _transport.DisposeAsync();
            }

            await _host.DisposeAsync();
            Ops.Dispose();
            _dir.Dispose();
        }
    }

    private sealed class JsonHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(Response());

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken) => Response();

        private HttpResponseMessage Response() => new(System.Net.HttpStatusCode.OK) { Content = new StringContent(body) };
    }
}
