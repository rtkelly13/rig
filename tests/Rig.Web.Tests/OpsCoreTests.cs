using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Rig.Core.Estate;
using Rig.Core.Otel;
using Rig.Core.ReviewQueue;
using Rig.Core.Roots;
using Rig.Core.Tests;
using Rig.Web.Ops;

namespace Rig.Web.Tests;

/// <summary>The view builders behind the ops routes: feeds, trim scans, telemetry and agent verdicts.</summary>
public sealed class OpsCoreTests
{
    [Fact]
    public void Decision_feed_reads_a_missing_empty_valid_or_unreadable_file()
    {
        using var dir = new TempDir();
        var path = Path.Join(dir.Path, "decisions.json");

        Assert.Contains("No decisions feed yet", DecisionFeed.Read(path).Note, StringComparison.Ordinal);

        Decisions.WriteFeed(path, []);
        var empty = DecisionFeed.Read(path);
        Assert.Empty(empty.Decisions);
        Assert.Equal("Nothing needs deciding.", empty.Note);
        Assert.NotEmpty(empty.Written);

        Decisions.WriteFeed(path, [new Decision("locked-path", "repo", "/repo/temp/a", "left behind", 12, "delete", "rm -rf")]);
        var found = Assert.Single(DecisionFeed.Read(path).Decisions);
        Assert.Equal("locked-path", found.Kind);
        Assert.Equal(12, found.SizeBytes);
        Assert.Equal("rm -rf", found.Command);

        File.WriteAllText(path, "{}", Encoding.UTF8);
        Assert.Contains("expected shape", DecisionFeed.Read(path).Note, StringComparison.Ordinal);
        File.WriteAllText(path, """[{"kind":"x","repo":"r","path":"p","reason":"why","size_bytes":"ten","suggested_action":"a","command":"c"}]""", Encoding.UTF8);
        Assert.Contains("expected shape", DecisionFeed.Read(path).Note, StringComparison.Ordinal);
        File.WriteAllText(path, """["not an object"]""", Encoding.UTF8);
        Assert.Contains("expected shape", DecisionFeed.Read(path).Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Trim_cache_reports_a_scan_failure_and_refuses_a_changed_candidate()
    {
        var item = new Trimmable("/wt", "/wt/node_modules", "node_modules", new TreeMeasure(10, 10));
        using var failing = new TrimCache(
            _ => throw new IOException("disk gone"),
            (_, _) => Task.FromResult(string.Empty),
            (_, _) => Task.FromResult("removed"),
            () => "now");
        _ = failing.Current();
        await failing.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal("disk gone", failing.Current().Error);

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
    public async Task Trim_cache_removes_a_clean_candidate_and_refuses_one_the_recheck_rejects()
    {
        var item = new Trimmable("/wt", "/wt/node_modules", "node_modules", new TreeMeasure(10, 10));
        var removed = 0;
        using var trims = new TrimCache(
            _ => Task.FromResult<IReadOnlyList<Trimmable>>([item]),
            (_, _) => Task.FromResult(string.Empty),
            (_, _) =>
            {
                removed++;
                return Task.FromResult("removed");
            },
            () => "then");
        _ = trims.Current();
        await trims.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.False(trims.Current().Scanning);
        var result = Assert.Single(await trims.ApplyAsync([item.Path], TestContext.Current.CancellationToken));
        Assert.Equal("removed", result.Status);
        Assert.Equal(1, removed);

        using var rechecked = new TrimCache(
            _ => Task.FromResult<IReadOnlyList<Trimmable>>([item]),
            (_, _) => Task.FromResult("used since the scan"),
            (_, _) => Task.FromResult("removed"),
            () => "then");
        _ = rechecked.Current();
        await rechecked.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var refused = Assert.Single(await rechecked.ApplyAsync([item.Path], TestContext.Current.CancellationToken));
        Assert.Equal("refused: used since the scan", refused.Status);
    }

    [Fact]
    public async Task Telemetry_prefers_clickhouse_then_falls_back_and_remembers_a_failure()
    {
        var clock = 0.0;
        var probes = 0;
        var files = 0;
        using var source = new Telemetry(
            () =>
            {
                probes++;
                return Task.FromResult(true);
            },
            query => Task.FromResult<IReadOnlyList<TraceView>>([new TraceView("trace-1", query.Service ?? "", "root", 10, 2, 1, 0)]),
            _ =>
            {
                files++;
                return [new TraceView("file-1", "rig", "root", 5, 1, 1, 0)];
            },
            () => new ExplorerStatusView("http://hyperdx.test", true, "http://hyperdx.test/{trace_id}", "", false, ""),
            () => clock);

        var status = await source.StatusAsync();
        Assert.Equal("clickhouse", status.Store.Source);
        Assert.True(status.Store.ClickhouseReachable);
        Assert.Equal(1, probes);
        _ = await source.StatusAsync();
        Assert.Equal(1, probes);
        clock = 31;
        _ = await source.StatusAsync();
        Assert.Equal(2, probes);

        var recent = await source.RecentAsync(new TraceQuery(2, 5, "rig.proc"));
        Assert.Equal("clickhouse", recent.Source);
        Assert.Empty(recent.Note);
        Assert.Equal("trace-1", Assert.Single(recent.Traces).TraceId);

        using var failing = new Telemetry(
            () => Task.FromResult(true),
            _ => throw new ClickHouseException(new string('x', 400)),
            _ =>
            {
                files++;
                return [new TraceView("file-1", "rig", "root", 5, 1, 1, 0)];
            },
            () => new ExplorerStatusView("", false, "", "", false, ""),
            () => 100);
        var fellBack = await failing.RecentAsync(new TraceQuery());
        Assert.Equal("ndjson", fellBack.Source);
        Assert.Contains("ClickHouse query failed", fellBack.Note, StringComparison.Ordinal);
        Assert.True(fellBack.Note.Length < 400);
        Assert.Equal("file-1", Assert.Single(fellBack.Traces).TraceId);
        _ = await failing.StatusAsync();
        Assert.Equal(1, files);
    }

    [Fact]
    public void File_traces_summarise_filter_and_order_them()
    {
        using var dir = new TempDir();
        var now = DateTimeOffset.UtcNow;
        var start = (now.ToUnixTimeMilliseconds() * 1_000_000) - 1_000_000;
        var day = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var path = Directory.CreateDirectory(Path.Join(dir.Path, "rig.proc")).FullName;
        File.WriteAllText(Path.Join(path, day + ".ndjson"), Envelope(start) + "\n", Encoding.UTF8);

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
    public void Explorer_probes_count_any_answer_and_cache_for_the_recheck_window()
    {
        using var handler = new JsonHandler("ok");
        using var probe = new HttpClient(handler);
        var hyperdx = Environment.GetEnvironmentVariable("RIG_HYPERDX_URL");
        var aspire = Environment.GetEnvironmentVariable("RIG_ASPIRE_URL");
        Environment.SetEnvironmentVariable("RIG_HYPERDX_URL", "http://hyperdx.test/");
        Environment.SetEnvironmentVariable("RIG_ASPIRE_URL", "http://aspire.test/");
        try
        {
            var first = Telemetry.ExplorerStatus(probe, 0);
            Assert.True(first.HyperdxReachable);
            Assert.Equal("http://hyperdx.test/search?where=TraceId%3D%27{trace_id}%27&whereLanguage=sql", first.HyperdxTraceUrl);
            Assert.True(first.AspireReachable);
            Assert.Equal("http://aspire.test/traces/detail/{trace_id}", first.AspireTraceUrl);

            var cached = Telemetry.ExplorerStatus(probe, 10);
            Assert.True(cached.HyperdxReachable);
            var after = Telemetry.ExplorerStatus(probe, 100);
            Assert.True(after.HyperdxReachable);

            Environment.SetEnvironmentVariable("RIG_HYPERDX_URL", "");
            Environment.SetEnvironmentVariable("RIG_ASPIRE_URL", "");
            var none = Telemetry.ExplorerStatus(probe, 1000);
            Assert.False(none.HyperdxReachable);
            Assert.False(none.AspireReachable);
            Assert.Empty(none.HyperdxTraceUrl);
            Assert.Empty(none.AspireTraceUrl);
        }
        finally
        {
            Environment.SetEnvironmentVariable("RIG_HYPERDX_URL", hyperdx);
            Environment.SetEnvironmentVariable("RIG_ASPIRE_URL", aspire);
        }
    }

    [Fact]
    public async Task The_real_telemetry_source_builds_for_a_checkout()
    {
        using var dir = new TempDir();
        using var telemetry = Telemetry.ForCheckout(Path.Join(dir.Path, "temp", "traces", "sdk"));

        var status = await telemetry.StatusAsync();

        Assert.True(status.Store.Source is "clickhouse" or "ndjson");
        Assert.False(string.IsNullOrEmpty(status.Store.Detail));
    }

    [Fact]
    public async Task Agent_signals_take_the_newest_answer_then_ask_and_match_a_branch_case_insensitively()
    {
        var ask = new AskRecord("2026-01-01T00:00:00Z", "pane", "session", "head-1", "facts line", "abc123");
        var answer = new AnswerRecord("2026-01-02T00:00:00Z", "pane", "done", "finished", "head-2");
        var state = new WrapupState(WrapupState.CurrentSchema, "", "feature", [ask], [answer]);

        var done = AgentSignals.Latest(state, "worktree");
        Assert.NotNull(done);
        Assert.Equal("done", done.Verdict);
        Assert.Equal("finished", done.Detail);
        Assert.Equal("head-2", done.Head);
        Assert.Equal("worktree", done.Worktree);

        var pending = AgentSignals.Latest(state with { Answers = [answer with { Verdict = "pending", Detail = "left" }] }, "worktree");
        Assert.Equal("pending", pending!.Verdict);
        Assert.Equal("left", pending.Detail);

        var unreadable = AgentSignals.Latest(state with { Answers = [answer with { At = "not a time" }] }, "worktree");
        Assert.Equal("asked", unreadable!.Verdict);
        Assert.Equal("facts line", unreadable.Detail);

        var asked = AgentSignals.Latest(state with { Answers = [] }, "worktree");
        Assert.Equal("asked", asked!.Verdict);
        Assert.Equal("facts line", asked.Detail);

        Assert.Null(AgentSignals.Latest(WrapupState.Empty(), "worktree"));

        var found = new Dictionary<AgentKey, AgentSignal> { [new AgentKey("owner/repo", "feature")] = done };
        Assert.Same(done, AgentSignals.ForPr(found, "Owner/Repo", "feature"));
        Assert.Null(AgentSignals.ForPr(found, "Owner/Repo", ""));
        Assert.Null(AgentSignals.ForPr(found, "Owner/Repo", "other"));

        using var signals = new AgentSignals(() => [], _ => Task.FromResult<IReadOnlyList<Worktree>>([]), _ => null, () => 0);
        Assert.Empty(await signals.AllAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Agent_signals_scan_each_worktree_and_reuse_the_answer_for_the_ttl()
    {
        using var dir = new TempDir();
        var root = dir.Folder("repo");
        var missing = Path.Join(dir.Path, "missing");
        var clock = 0.0;
        var listings = 0;
        var state = new WrapupState(
            WrapupState.CurrentSchema,
            "",
            "feature",
            [],
            [new AnswerRecord("2026-01-02T00:00:00Z", "pane", "done", "", "head")]);
        using var signals = new AgentSignals(
            () => [new AgentRoot("owner/repo", root), new AgentRoot("owner/other", missing)],
            _ =>
            {
                listings++;
                return Task.FromResult<IReadOnlyList<Worktree>>(
                [
                    new Worktree(Path.Join(root, "feature"), Branch: "feature"),
                    new Worktree(root, Bare: true),
                    new Worktree(root),
                    new Worktree(Path.Join(root, "unrecorded"), Branch: "unrecorded"),
                ]);
            },
            worktree => worktree == Path.Join(root, "feature") ? state : null,
            () => clock);

        var first = await signals.AllAsync(TestContext.Current.CancellationToken);
        Assert.Equal("done", first[new AgentKey("owner/repo", "feature")].Verdict);
        Assert.False(first.ContainsKey(new AgentKey("owner/repo", "unrecorded")));
        Assert.Equal(1, listings);

        clock = 10;
        _ = await signals.AllAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, listings);

        clock = 31;
        _ = await signals.AllAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, listings);
    }

    [Fact]
    public void Estate_roots_name_enabled_repositories_in_their_scope_folders()
    {
        using var dir = new TempDir().AsCodeRoot();
        var registry = Path.Join(dir.Path, "config", "managed-projects.json");
        Directory.CreateDirectory(Path.GetDirectoryName(registry)!);
        File.WriteAllText(
            registry,
            """
            {"projects": [
              {"name": "rig", "repo": "git@github.com:owner/rig.git"},
              {"name": "lake", "repo": "git@github.com:labs/lake.git"},
              {"name": "local", "repo": "local/thing"},
              {"name": "off", "repo": "git@github.com:owner/off.git", "enabled": false}
            ]}
            """,
            Encoding.UTF8);
        var personal = Path.Join(dir.Path, "personal");
        var scopes = new EstateScopes(["owner"], ["labs"]);

        var roots = AgentSignals.EstateRoots(registry, personal, scopes);

        Assert.Equal(3, roots.Count);
        Assert.Contains(roots, root => root.Repo == "owner/rig" && root.Root == Path.Join(personal, "rig"));
        Assert.Contains(roots, root => root.Repo == "labs/lake" && root.Root == Path.Join(dir.Path, "labs", "lake"));
        Assert.Contains(roots, root => root.Repo == "local/thing" && root.Root == Path.Join(personal, "local"));
    }

    [Fact]
    public void Ops_request_failures_carry_their_status_and_default_to_a_bad_request()
    {
        Assert.Equal(400, new OpsRequestException().Status);
        Assert.Equal(400, new OpsRequestException("bad").Status);
        Assert.Equal(400, new OpsRequestException("bad", new InvalidOperationException("cause")).Status);
        var refused = new OpsRequestException(503, "down");
        Assert.Equal(503, refused.Status);
        Assert.Equal("down", refused.Message);
    }

    [Fact]
    public void The_real_estate_and_ops_sources_build_for_a_settings_tree()
    {
        using var dir = new TempDir().AsCodeRoot();
        var settings = new WebHostSettings(
            "127.0.0.1",
            0,
            false,
            Path.Join(dir.Path, "templates"),
            Path.Join(dir.Path, "assets"),
            Path.Join(dir.Path, "rig.db"),
            Path.Join(dir.Path, "config", "managed-projects.json"),
            dir.Path,
            Path.Join(dir.Path, "personal"),
            Path.Join(dir.Path, "decisions.json"),
            Path.Join(dir.Path, "temp", "triage", "queue.md"),
            Path.Join(dir.Path, "temp", "traces", "sdk"));
        using var estate = EstateOps.ForSettings(settings);
        using var ops = OpsRoutes.ForSettings(settings);

        Assert.Contains("No decisions feed yet", estate.Decisions().Note, StringComparison.Ordinal);
        Assert.NotNull(ops.Triage);
        using var telemetry = Telemetry.ForCheckout(dir.Path);
        Assert.NotNull(telemetry);
    }

    [Fact]
    public void Triage_watch_needs_a_repository_and_a_positive_number()
    {
        var watch = TriageInbox.Watch("""{"repo":"owner/repo","number":7}""", OpsJson.Boundary);
        Assert.Equal(new TriageWatch("owner/repo", 7), watch);

        Assert.Throws<ArgumentException>(() => TriageInbox.Watch("""{"repo":"owner/repo","number":0}""", OpsJson.Boundary));
        Assert.Throws<JsonException>(() => TriageInbox.Watch("""{"number":1}""", OpsJson.Boundary));
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

    private sealed class JsonHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(Answer());

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken) => Answer();

        private HttpResponseMessage Answer() =>
            new(System.Net.HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
