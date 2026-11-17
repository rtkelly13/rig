using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Rig.Core.Estate;
using Rig.Core.ReviewQueue;
using Rig.Core.Tests;
using Rig.Web.Ops;
using Rig.Web.Tests.Fakes;

namespace Rig.Web.Tests;

/// <summary>The routes of the ops surface: triage, the estate's ops answers and telemetry.</summary>
public sealed class OpsRoutesTests : IAsyncLifetime
{
    private Harness _harness = null!;
    private HttpClient _client = null!;

    public async ValueTask InitializeAsync()
    {
        _harness = await Harness.CreateAsync();
        await _harness.StartAsync();
        _client = new HttpClient { BaseAddress = _harness.Host.Address };
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _harness.DisposeAsync();
    }

    [Fact]
    public async Task Triage_serves_the_persistent_queue_in_rank_order_with_agent_verdicts()
    {
        _harness.SeedQueue(
            Item(1, "pr", "Second", priority: 2, readiness: 0, head: new string('a', 40), facts: new QueueFacts { Head = "feature" }),
            Item(2, "issue", "First", priority: 1, readiness: 3));
        _harness.Agent = new WrapupState(
            WrapupState.CurrentSchema,
            "",
            "feature",
            [],
            [new AnswerRecord("2026-01-02T00:00:00Z", "pane", "done", "", new string('a', 40))]);

        using var response = await _client.GetAsync(new Uri("/api/triage", UriKind.Relative), TestContext.Current.CancellationToken);

        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(_harness.QueuePath, (string?)body["queue_path"]);
        var entries = body["entries"]!.AsArray();
        Assert.Equal(2, entries.Count);
        Assert.Equal(2, (int?)entries[0]!["item"]!["number"]);
        Assert.Equal(1, (int?)entries[1]!["item"]!["number"]);
        Assert.Equal("done", (string?)entries[1]!["agent"]!["verdict"]);
        Assert.Equal("repo", (string?)entries[1]!["agent"]!["worktree"]);
        Assert.Null(entries[0]!["agent"]);
        Assert.Equal("observed", (string?)entries[0]!["events"]![0]!["action"]);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task Triage_refresh_watch_and_interact_answer_the_queue_again()
    {
        _harness.Source.Items = [Item(1, "pr", "Refresh me", priority: 1, readiness: 0, head: new string('a', 40))];

        using var refresh = await PostAsync("/api/triage/refresh", "{}");
        Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);
        var body = JsonNode.Parse(await refresh.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;
        Assert.Equal("Refresh me", (string?)body["entries"]![0]!["item"]!["title"]);

        using var watch = await PostAsync("/api/triage/watch", """{"repo":"owner/repo","number":1}""");
        Assert.Equal(HttpStatusCode.OK, watch.StatusCode);

        using var interact = await PostAsync(
            "/api/triage/interact",
            $$"""{"repo":"owner/repo","number":1,"action":"note","head":"{{new string('a', 40)}}","note":"hello"}""");
        Assert.Equal(HttpStatusCode.OK, interact.StatusCode);
        var answered = JsonNode.Parse(await interact.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;
        var events = answered["entries"]![0]!["events"]!.AsArray();
        Assert.Equal("note", (string?)events[^1]!["action"]);
        Assert.Equal("hello", (string?)events[^1]!["note"]);

        using var stale = await PostAsync(
            "/api/triage/interact",
            """{"repo":"owner/repo","number":1,"action":"reviewed","head":"stale","note":""}""");
        Assert.Equal(HttpStatusCode.BadRequest, stale.StatusCode);
        Assert.Contains("head changed", (string?)JsonNode.Parse(await stale.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!["error"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Triage_writes_are_accepted_only_as_json_from_this_dashboard()
    {
        using var form = new StringContent("repo=x", Encoding.UTF8, "application/x-www-form-urlencoded");
        using var notJson = await _client.PostAsync(new Uri("/api/triage/refresh", UriKind.Relative), form, TestContext.Current.CancellationToken);
        Assert.True(notJson.StatusCode == HttpStatusCode.BadRequest, await notJson.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("Expected application/json", (string?)JsonNode.Parse(await notJson.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!["error"]);

        using var crossOriginBody = Json("{}");
        using var crossOrigin = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/triage/refresh", UriKind.Relative)) { Content = crossOriginBody };
        crossOrigin.Headers.Add("Origin", "http://example.test");
        using var refused = await _client.SendAsync(crossOrigin, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal("Cross-origin writes are not allowed", (string?)JsonNode.Parse(await refused.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!["error"]);

        using var empty = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/triage/refresh", UriKind.Relative)) { Content = Json(string.Empty) };
        using var size = await _client.SendAsync(empty, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, size.StatusCode);

        using var elsewhereBody = Json("{}");
        using var elsewhere = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/triage/refresh", UriKind.Relative))
        {
            Content = elsewhereBody,
            Headers = { Host = "example.test" },
        };
        using var remote = await _client.SendAsync(elsewhere, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, remote.StatusCode);
        Assert.Equal("Writes are only accepted on a loopback address", (string?)JsonNode.Parse(await remote.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!["error"]);
    }

    [Fact]
    public async Task Triage_survives_a_failed_agent_scan_without_a_verdict()
    {
        _harness.AgentScanFails = true;
        _harness.SeedQueue(Item(1, "pr", "One", priority: 1, readiness: 0, head: new string('a', 40), facts: new QueueFacts { Head = "feature" }));

        using var response = await _client.GetAsync(new Uri("/api/triage", UriKind.Relative), TestContext.Current.CancellationToken);

        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(body["entries"]![0]!["agent"]);
    }

    [Fact]
    public async Task Triage_unknown_operations_are_a_400_envelope()
    {
        using var unknownGet = await _client.GetAsync(new Uri("/api/triage/nope", UriKind.Relative), TestContext.Current.CancellationToken);
        using var unknownBody = Json("{}");
        using var unknownPost = await _client.PostAsync(new Uri("/api/triage", UriKind.Relative), unknownBody, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, unknownGet.StatusCode);
        Assert.Equal("Unknown triage operation", (string?)JsonNode.Parse(await unknownGet.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!["error"]);
        Assert.Equal(HttpStatusCode.BadRequest, unknownPost.StatusCode);
    }

    [Fact]
    public async Task Triage_read_failure_returns_503_json_envelope()
    {
        var db = Path.Join(_harness.Root, "..", "rig.db");
        if (File.Exists(db)) { File.Delete(db); }
        Directory.CreateDirectory(db);

        using var response = await _client.GetAsync(new Uri("/api/triage", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;
        Assert.NotNull(body["error"]);
    }

    [Fact]
    public async Task Estate_decisions_read_the_feed_or_say_why_there_is_nothing_to_show()
    {
        using var missing = await _client.GetAsync(new Uri("/api/ops/decisions", UriKind.Relative), TestContext.Current.CancellationToken);
        var missingBody = JsonNode.Parse(await missing.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;
        Assert.Equal(HttpStatusCode.OK, missing.StatusCode);
        Assert.Empty(missingBody["decisions"]!.AsArray());
        Assert.Contains("No decisions feed yet", (string?)missingBody["note"], StringComparison.Ordinal);

        Decisions.WriteFeed(_harness.Feed, [new Decision("unpushed-worktree", "repo", "/repo/.worktrees/a", "idle", 10, "push", "git push")]);
        using var written = await _client.GetAsync(new Uri("/api/ops/decisions", UriKind.Relative), TestContext.Current.CancellationToken);
        var writtenBody = JsonNode.Parse(await written.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;
        var decision = writtenBody["decisions"]![0]!;
        Assert.Equal("unpushed-worktree", (string?)decision["kind"]);
        Assert.Equal(10, (long?)decision["size_bytes"]);
        Assert.Equal("push", (string?)decision["suggested_action"]);
        Assert.True(string.IsNullOrEmpty((string?)writtenBody["note"]));

        await File.WriteAllTextAsync(_harness.Feed, "{}", Encoding.UTF8, TestContext.Current.CancellationToken);
        using var broken = await _client.GetAsync(new Uri("/api/ops/decisions", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Contains("expected shape", (string?)JsonNode.Parse(await broken.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!["note"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Estate_disk_names_the_code_root_and_its_free_space()
    {
        using var response = await _client.GetAsync(new Uri("/api/ops/estate/disk", UriKind.Relative), TestContext.Current.CancellationToken);

        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(_harness.Root, (string?)body["path"]);
        Assert.True((long?)body["total"] > 0);
        Assert.True((long?)body["free"] >= 0);
        Assert.Equal((long?)body["total"] - (long?)body["free"], (long?)body["used"]);
    }

    [Fact]
    public async Task Estate_trim_lists_the_last_scan_and_applies_only_what_it_offered()
    {
        using var started = await _client.GetAsync(new Uri("/api/ops/estate/trim?refresh=true", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        await _harness.Trims.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        using var listed = await _client.GetAsync(new Uri("/api/ops/estate/trim", UriKind.Relative), TestContext.Current.CancellationToken);
        var body = JsonNode.Parse(await listed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;
        Assert.Equal("2026-01-01T00:00:00+00:00", (string?)body["scanned_at"]);
        Assert.False((bool?)body["scanning"]);
        var candidate = body["candidates"]![0]!;
        Assert.Equal("node_modules", (string?)candidate["category"]);
        Assert.Equal(10, (long?)candidate["freeable"]);
        Assert.True(string.IsNullOrEmpty((string?)candidate["refused"]));

        using var applied = await PostAsync("/api/ops/estate/trim/apply", """{"paths":["/wt/node_modules","/elsewhere"]}""");
        var results = JsonNode.Parse(await applied.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!.AsArray();
        Assert.Equal(2, results.Count);
        Assert.Equal("removed", (string?)results[0]!["status"]);
        Assert.Equal("not-in-scan", (string?)results[1]!["status"]);
    }

    [Fact]
    public async Task Estate_unknown_paths_are_a_404_envelope()
    {
        using var response = await _client.GetAsync(new Uri("/api/ops/estate/nope", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("No GET /api/ops/estate/nope", (string?)JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!["error"]);
    }

    [Fact]
    public async Task Telemetry_reports_which_store_answers_and_which_explorers_are_up()
    {
        using var response = await _client.GetAsync(new Uri("/api/ops/telemetry/status", UriKind.Relative), TestContext.Current.CancellationToken);

        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("clickhouse", (string?)body["store"]!["source"]);
        Assert.True((bool?)body["store"]!["clickhouse_reachable"]);
        Assert.Equal("http://hyperdx.test", (string?)body["explorers"]!["hyperdx_url"]);
        Assert.True((bool?)body["explorers"]!["hyperdx_reachable"]);
    }

    [Fact]
    public async Task Telemetry_traces_pass_days_limit_and_service_and_refuse_a_bad_range()
    {
        _harness.ClickhouseUp = false;
        using var response = await _client.GetAsync(
            new Uri("/api/ops/telemetry/traces?days=2&limit=5&service=rig.proc", UriKind.Relative),
            TestContext.Current.CancellationToken);

        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ndjson", (string?)body["source"]);
        Assert.Contains("ClickHouse unreachable", (string?)body["note"], StringComparison.Ordinal);
        Assert.Equal("trace-1", (string?)body["traces"]![0]!["trace_id"]);
        Assert.Equal(new TraceQuery(2, 5, "rig.proc"), _harness.LastQuery);

        using var outOfRange = await _client.GetAsync(new Uri("/api/ops/telemetry/traces?days=0", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, outOfRange.StatusCode);
        Assert.Equal("`days` must be between 1 and 30", (string?)JsonNode.Parse(await outOfRange.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!["error"]);

        using var notANumber = await _client.GetAsync(new Uri("/api/ops/telemetry/traces?limit=all", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, notANumber.StatusCode);
        Assert.Equal("`limit` must be an integer", (string?)JsonNode.Parse(await notANumber.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!["error"]);
    }

    [Fact]
    public async Task Telemetry_unknown_paths_are_a_404_envelope()
    {
        using var response = await _client.GetAsync(new Uri("/api/ops/telemetry/nope", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("No telemetry endpoint at /api/ops/telemetry/nope", (string?)JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!["error"]);
    }

    private static QueueItem Item(int number, string kind, string title, int priority, int readiness, string head = "", QueueFacts? facts = null) =>
        new("owner/repo", number, kind, title, "OPEN", "2026-01-01T00:00:00Z", "2026-01-02T00:00:00Z", head, priority, readiness, 0, Body: null, Facts: facts);

    private static ByteArrayContent Json(string body)
    {
        var content = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return content;
    }

    private async Task<HttpResponseMessage> PostAsync(string path, string body)
    {
        using var content = Json(body);
        return await _client.PostAsync(new Uri(path, UriKind.Relative), content, TestContext.Current.CancellationToken);
    }

    /// <summary>A host over recorded-less fakes: no git, no gh, no ClickHouse and no real trim scan.</summary>
    private sealed class Harness : IAsyncDisposable
    {
        private readonly TempDir _dir = new();
        private RigWebHost _host = null!;
        private OpsRoutes _ops = null!;

        private Harness()
        {
        }

        public static async Task<Harness> CreateAsync()
        {
            var harness = new Harness();
            await harness.BuildAsync();
            return harness;
        }

        private async Task BuildAsync()
        {
            Directory.CreateDirectory(Root);
            Directory.CreateDirectory(Path.Join(Root, "repo", "temp", ".rig"));
            WriteRegistry();
            Trims = new TrimCache(
                _ => Task.FromResult<IReadOnlyList<Trimmable>>([Candidate]),
                (_, _) => Task.FromResult(string.Empty),
                (_, _) => Task.FromResult("removed"),
                () => "2026-01-01T00:00:00+00:00");
            Telemetry = new Telemetry(
                () => Task.FromResult(ClickhouseUp),
                query =>
                {
                    LastQuery = query;
                    return Task.FromResult<IReadOnlyList<TraceView>>([new TraceView("clickhouse-1", "rig", "run", 10, 1, 1, 0)]);
                },
                query =>
                {
                    LastQuery = query;
                    return [new TraceView("trace-1", "rig", "run", 5, 1, 1, 0)];
                },
                _ => Task.FromResult(new ExplorerStatusView("http://hyperdx.test", true, "http://hyperdx.test/{trace_id}", "", false, "")),
                () => 0);
#pragma warning disable CA2000 // The queue owns and disposes both.
            var signals = new AgentSignals(
                () => [new AgentRoot("owner/repo", Path.Join(Root, "repo"))],
                _ => Task.FromResult<IReadOnlyList<Worktree>>([new Worktree(Path.Join(Root, "repo"), Branch: "feature")]),
                _ => AgentScanFails ? throw new IOException("git gone") : Agent,
                () => 0);
            Triage = new TriageInbox(DbPath, Source, QueuePath, signals);
            _ops = new OpsRoutes(Triage, new EstateOps(Root, Feed, Trims), Telemetry);
            _host = await RigWebHost.CreateAsync(Settings, routes: _ops);
        }

        public RecordingSource Source { get; } = new();

        public TriageInbox Triage { get; private set; } = null!;

        public TrimCache Trims { get; private set; } = null!;

        public Telemetry Telemetry { get; private set; } = null!;

        public TraceQuery? LastQuery { get; private set; }

        public WrapupState? Agent { get; set; }

        public bool ClickhouseUp { get; set; } = true;

        public bool AgentScanFails { get; set; }

        public RigWebHost Host => _host;

        public string Root => Path.Join(_dir.Path, "personal");

        public string Feed => Path.Join(_dir.Path, "decisions.json");

        public string QueuePath => Path.Join(_dir.Path, "temp", "triage", "queue.md");

        private string DbPath => Path.Join(_dir.Path, "rig.db");

        private string Registry => Path.Join(_dir.Path, "config", "managed-projects.json");

        private WebHostSettings Settings =>
            new("127.0.0.1", 0, false, Path.Join(_dir.Path, "templates"), Path.Join(_dir.Path, "assets"), DbPath, Registry, _dir.Path,
                Root, Feed, QueuePath, Path.Join(_dir.Path, "temp", "traces", "sdk"));

        private static Trimmable Candidate => new("/wt", "/wt/node_modules", "node_modules", new TreeMeasure(10, 10));

        public async Task StartAsync() => await _host.StartAsync(TestContext.Current.CancellationToken);

        public void SeedQueue(params QueueItem[] items)
        {
            var store = new QueueStore(DbPath);
            store.Update(writer =>
            {
                writer.Save(items);
                writer.Interact(new QueueInteraction("owner/repo", items[0].Number, "unread", items[0].Head));
            });
        }

        public async ValueTask DisposeAsync()
        {
            await _host.DisposeAsync();
            _ops.Dispose();
            _dir.Dispose();
        }

        private void WriteRegistry()
        {
            Directory.CreateDirectory(Path.Join(_dir.Path, "config"));
            File.WriteAllText(Registry, """{"projects":[{"name":"repo","repo":"https://github.com/owner/repo"}]}""", Encoding.UTF8);
        }
    }

    /// <summary>The GitHub reads a triage refresh and watch make, stated rather than performed.</summary>
    private sealed class RecordingSource : IQueueSource
    {
        public IReadOnlyList<QueueItem> Items { get; set; } = [];

        public IReadOnlyList<string> Repositories(string registry) => ["owner/repo"];

        public Task<IReadOnlyList<QueueItem>> FetchAsync(string repo, string kind, CancellationToken cancellationToken) =>
            Task.FromResult(kind == "pr" ? Items : (IReadOnlyList<QueueItem>)[]);

        public Task<QueueItem> ObserveAsync(QueueItem item, CancellationToken cancellationToken) => Task.FromResult(item);

        public Task<IReadOnlyList<QueueActivity>> ActivityAsync(QueueItem item, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<QueueActivity>>([]);
    }
}
