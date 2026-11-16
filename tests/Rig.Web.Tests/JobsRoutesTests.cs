using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Rig.Core.Estate;
using Rig.Core.Jobs;
using Rig.Core.Otel;
using Rig.Core.Processes;
using Rig.Core.Tests;
using Rig.Web.Mcp;
using Rig.Web.Ops;

namespace Rig.Web.Tests;

/// <summary>Tests for the jobs coordinator ops routes: /api/ops/jobs and /api/ops/runs.</summary>
public sealed class JobsRoutesTests : IAsyncLifetime
{
    private static readonly DateTimeOffset StartedAt = new(2026, 10, 10, 9, 0, 0, TimeSpan.Zero);

    private readonly TempDir _dir = new();
    private readonly List<string[]> _started = [];
    private readonly HttpClient _forwarding = new();
    private TrimCache _trims = null!;
    private RigOps _ops = null!;
    private RigWebHost _host = null!;
    private HttpClient _client = null!;

    public async ValueTask InitializeAsync()
    {
        var root = _dir.Path;
        var jobsDir = Path.Join(root, "jobs");
        SeedJobsDirectory(jobsDir);

        var jobs = new List<Job>
        {
            new("align", "align things", ["tool", "align"], new HashSet<JobTrigger> { JobTrigger.Tick, JobTrigger.Logon }, JobCadence.EveryRun, 5) { IdleMinutes = 3 },
            new("sync", "sync things", ["tool", "sync"], new HashSet<JobTrigger> { JobTrigger.Tick }, JobCadence.Daily, 10),
        };

        _trims = new TrimCache(
            _ => Task.FromResult<IReadOnlyList<Trimmable>>([]),
            (_, _) => Task.FromResult(string.Empty),
            (_, _) => Task.FromResult("removed"),
            () => "then");
        _ops = new RigOps(
            () => jobs,
            jobsDir,
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
            Path.Join(root, "decisions.json"),
            root,
            _trims,
            () => new Telemetry(
                () => Task.FromResult(true),
                query => Task.FromResult<IReadOnlyList<TraceView>>([]),
                _ => [],
                () => new ExplorerStatusView("http://hyperdx.test", true, "http://hyperdx.test/{trace_id}", "", false, ""),
                () => 0));

        var settings = new WebHostSettings(
            "127.0.0.1",
            0,
            false,
            _dir.Folder("templates"),
            _dir.Folder("assets"),
            Path.Join(root, "rig.db"),
            Path.Join(root, "managed-projects.json"),
            root,
            root,
            Path.Join(root, "decisions.json"),
            Path.Join(root, "temp", "triage", "queue.md"),
            Path.Join(root, "temp", "traces", "sdk"),
            CodeRoot: root);

        _host = RigWebHost.Create(settings, _forwarding, () => null, _ops);
        await _host.StartAsync(TestContext.Current.CancellationToken);
        _client = new HttpClient { BaseAddress = _host.Address };
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        _forwarding.Dispose();
        await _host.DisposeAsync();
        _ops.Dispose();
        _trims.Dispose();
        _dir.Dispose();
    }

    [Fact]
    public async Task Jobs_lists_every_registered_job_with_its_last_outcome()
    {
        using var response = await _client.GetAsync(new Uri("/api/ops/jobs", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());

        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!.AsArray();
        Assert.Equal(2, json.Count);

        var align = json[0]!;
        Assert.Equal("align", (string?)align["name"]);
        Assert.Equal("align things", (string?)align["description"]);
        Assert.Equal("every run, idle 3 min", (string?)align["cadence"]);
        Assert.Equal(["logon", "tick"], align["triggers"]!.AsArray().Select(t => (string?)t));
        Assert.Equal(5, (int?)align["timeout_minutes"]);
        Assert.Equal(3, (int?)align["last"]!["returncode"]);
        Assert.False((bool?)align["last"]!["ok"]);

        var sync = json[1]!;
        Assert.Equal("sync", (string?)sync["name"]);
        Assert.Equal("daily", (string?)sync["cadence"]);
        Assert.True((bool?)sync["last"]!["ok"]);
    }

    [Fact]
    public async Task Runs_lists_runs_newest_first_with_job_and_limit_filters()
    {
        using var all = await _client.GetAsync(new Uri("/api/ops/runs", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, all.StatusCode);
        var allRuns = JsonNode.Parse(await all.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!.AsArray();
        Assert.Equal(3, allRuns.Count);
        Assert.Equal("tick-20261010-091500", (string?)allRuns[0]!["run_id"]);
        Assert.Equal("tick-20261010-090000", (string?)allRuns[1]!["run_id"]);
        Assert.Equal("logon-20261010-083000", (string?)allRuns[2]!["run_id"]);
        Assert.True((bool?)allRuns[0]!["has_output"]);
        Assert.False((bool?)allRuns[1]!["has_output"]);

        using var filtered = await _client.GetAsync(new Uri("/api/ops/runs?job=align&limit=1", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, filtered.StatusCode);
        var filteredRuns = JsonNode.Parse(await filtered.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!.AsArray();
        Assert.Single(filteredRuns);
        Assert.Equal("tick-20261010-091500", (string?)filteredRuns[0]!["run_id"]);
    }

    [Fact]
    public async Task Run_output_serves_what_the_run_printed()
    {
        using var response = await _client.GetAsync(new Uri("/api/ops/runs/align/tick-20261010-091500/output", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;
        Assert.Equal("align", (string?)json["job"]);
        Assert.Equal("tick-20261010-091500", (string?)json["run_id"]);
        Assert.Equal("align: 0 moved\n", (string?)json["output"]);

        using var preHistory = await _client.GetAsync(new Uri("/api/ops/runs/sync/logon-20261010-083000/output", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, preHistory.StatusCode);
        var preHistoryJson = JsonNode.Parse(await preHistory.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;
        Assert.Equal("synced 12\n", (string?)preHistoryJson["output"]);
    }

    [Theory]
    [InlineData("/api/ops/runs/nope/tick-20261010-091500/output", HttpStatusCode.NotFound)]
    [InlineData("/api/ops/runs/align/tick-20261010-101500/output", HttpStatusCode.NotFound)]
    [InlineData("/api/ops/runs/align", HttpStatusCode.NotFound)]
    [InlineData("/api/ops/runs?limit=0", HttpStatusCode.BadRequest)]
    [InlineData("/api/ops/runs?limit=abc", HttpStatusCode.BadRequest)]
    [InlineData("/api/ops/runs?limit=1001", HttpStatusCode.BadRequest)]
    public async Task Bad_run_requests_return_the_error_envelope(string path, HttpStatusCode expectedStatus)
    {
        using var response = await _client.GetAsync(new Uri(path, UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(expectedStatus, response.StatusCode);

        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;
        Assert.NotNull(json["error"]);
    }

    [Fact]
    public async Task Post_to_jobs_run_starts_a_detached_run_at_once()
    {
        using var content = Json("{\"job\": \"align\"}");
        using var response = await _client.PostAsync(new Uri("/api/ops/jobs/run", UriKind.Relative), content, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;
        Assert.Equal("align", (string?)json["job"]);
        Assert.Equal(4242, (int?)json["pid"]);

        Assert.Single(_started);
        Assert.Equal(["rig", "jobs", "run", "--trigger", "tick", "--only", "align"], _started[0]);
    }

    [Fact]
    public async Task Post_to_jobs_run_with_empty_payload_starts_every_due_job()
    {
        using var content = Json("{}");
        using var response = await _client.PostAsync(new Uri("/api/ops/jobs/run", UriKind.Relative), content, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;
        Assert.Null(json["job"]);
        Assert.Equal(4242, (int?)json["pid"]);

        Assert.Single(_started);
        Assert.Equal(["rig", "jobs", "run", "--trigger", "tick"], _started[0]);
    }

    [Theory]
    [InlineData("{\"job\": \"nope\"}", "application/json", null, HttpStatusCode.NotFound)]
    [InlineData("not json", "application/json", null, HttpStatusCode.BadRequest)]
    [InlineData("{\"job\": \"align\"}", "text/plain", null, HttpStatusCode.BadRequest)]
    public async Task Run_now_refuses_invalid_payloads(string body, string contentType, string? origin, HttpStatusCode expectedStatus)
    {
        using var content = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        if (origin is not null)
        {
            content.Headers.Add("Origin", origin);
        }

        using var response = await _client.PostAsync(new Uri("/api/ops/jobs/run", UriKind.Relative), content, TestContext.Current.CancellationToken);
        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Empty(_started);
    }

    [Fact]
    public async Task Run_now_is_post_only()
    {
        using var response = await _client.GetAsync(new Uri("/api/ops/jobs/run", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_started);
    }

    [Fact]
    public async Task Jobs_and_runs_are_get_only()
    {
        using var emptyJobs = new StringContent(string.Empty);
        using var postJobs = await _client.PostAsync(new Uri("/api/ops/jobs", UriKind.Relative), emptyJobs, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, postJobs.StatusCode);

        using var emptyRuns = new StringContent(string.Empty);
        using var postRuns = await _client.PostAsync(new Uri("/api/ops/runs", UriKind.Relative), emptyRuns, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, postRuns.StatusCode);
    }

    private static ByteArrayContent Json(string body)
    {
        var content = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return content;
    }

    private static void SeedJobsDirectory(string directory)
    {
        Directory.CreateDirectory(directory);
        var stamp15 = StartedAt.AddMinutes(15).ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture);
        var stamp0 = StartedAt.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture);
        var stampSync = StartedAt.AddMinutes(-30).ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture);

        var output = RunHistory.SaveOutput(directory, "align", "tick-20261010-091500", Encoding.UTF8.GetBytes("align: 0 moved\n"));
        RunHistory.Append(directory, new JobRun("align", "tick-20261010-090000", stamp0, 1.0, 0, "tick", null, string.Empty));
        RunHistory.Append(directory, new JobRun("align", "tick-20261010-091500", stamp15, 2.0, 3, "tick", null, output, new string('a', 32)));

        var logsDir = Path.Join(directory, "logs");
        Directory.CreateDirectory(logsDir);
        File.WriteAllText(
            Path.Join(logsDir, "sync.log"),
            "--- 2026-10-10 08:30:00 [logon-20261010-083000] sync\nsynced 12\n",
            new UTF8Encoding(false));

        JobState.WriteState(
            Path.Join(directory, JobState.StateName),
            new Dictionary<string, Outcome>
            {
                ["align"] = new(stamp15, 2.0, 3, "tick") { RunId = "tick-20261010-091500" },
                ["sync"] = new(stampSync, 4.0, 0, "logon") { RunId = "logon-20261010-083000" },
            });
    }
}
