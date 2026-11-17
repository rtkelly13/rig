using System.Text.Json.Nodes;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Rig.Web.BrowserTests;

/// <summary>
/// The <c>/ops</c> pages of the SPA, driven against canned API answers. Playwright supplies every ops route's answer, so
/// the pages are tested without live state on either side.
/// </summary>
public sealed class OpsBrowserTests(BrowserFixture fixture) : BrowserTest(fixture)
{
    private const string Trace = "0af7651916cd43dd8448eb211c80319c";

    private const string Explorers = """
        {"hyperdx_url": "http://localhost:8080", "hyperdx_reachable": true, "hyperdx_trace_url": "http://localhost:8080/trace/{trace_id}",
         "aspire_url": "", "aspire_reachable": false, "aspire_trace_url": ""}
        """;

    private const string Jobs = """
        [
          {"name": "sync", "description": "Fetch every managed repo", "cadence": "hourly", "triggers": ["tick"], "timeout_minutes": 10,
           "last": {"started": "2026-11-05T09:00:00+00:00", "seconds": 4.2, "returncode": 1, "trigger": "tick", "error": null, "ok": false}},
          {"name": "prune", "description": "Prune merged branches", "cadence": "daily", "triggers": ["tick", "manual"], "timeout_minutes": 5, "last": null}
        ]
        """;

    private const string Runs = $$"""
        [{"job": "sync", "run_id": "r2", "started": "2026-11-05T09:00:00+00:00", "seconds": 4.2, "returncode": 1, "trigger": "tick",
          "error": null, "ok": false, "has_output": true, "trace_id": "{{Trace}}"}]
        """;

    private const string Decisions = """
        {"decisions": [{"kind": "unpushed", "repo": "rig", "path": "C:/code/personal/rig/.worktrees/old", "reason": "idle with unpushed commits",
                        "size_bytes": 2048, "suggested_action": "push or delete", "command": "git -C C:/code/personal/rig/.worktrees/old push"}],
         "written": "2026-11-05T08:00:00+00:00", "note": ""}
        """;

    private const string Trim = """
        {"scanned_at": "2026-11-05T09:30:00+00:00",
         "candidates": [
           {"worktree": "C:/code/personal/blog/.worktrees/a", "path": "C:/code/personal/blog/.worktrees/a/node_modules", "category": "node_modules", "freeable": 300000000, "refused": ""},
           {"worktree": "C:/code/personal/blog/.worktrees/b", "path": "C:/code/personal/blog/.worktrees/b/node_modules", "category": "node_modules", "freeable": 100000000, "refused": "dirty worktree"}],
         "scanning": false, "error": ""}
        """;

    private readonly List<string[]> _applied = [];
    private readonly List<string?> _started = [];

    /// <inheritdoc />
    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        var traces = $$"""
            {"source": "clickhouse", "note": "",
             "traces": [{"trace_id": "{{Trace}}", "service": "rig", "root": "job.sync", "start_ns": 1793000000000000000, "duration_ms": 4200, "spans": 7, "errors": 1}]}
            """;
        var answers = new (string Pattern, string Json)[]
        {
            ("**/api/ops/telemetry/status", $$"""{"store": {"source": "clickhouse", "clickhouse_reachable": true, "detail": "ClickHouse answered."}, "explorers": {{Explorers}} }"""),
            ("**/api/ops/telemetry/traces*", traces),
            ("**/api/ops/decisions", Decisions),
            ("**/api/ops/estate/disk", """{"path": "C:\\", "total": 1000, "used": 950, "free": 50}"""),
            ("**/api/ops/estate/trim*", Trim),
            ("**/api/ops/jobs", Jobs),
            ("**/api/ops/runs/*/*/output", """{"job": "sync", "run_id": "r2", "output": "fatal: remote hung up"}"""),
            ("**/api/ops/runs?*", Runs),
            ("**/api/ops/runs", Runs),
        };
        foreach (var (pattern, json) in answers)
        {
            await Page.RouteAsync(pattern, route => route.FulfillAsync(new() { Body = json, ContentType = "application/json" }));
        }

        await Page.RouteAsync("**/api/ops/estate/trim/apply", async route =>
        {
            var paths = JsonNode.Parse(route.Request.PostData ?? "{}")!["paths"]!.AsArray().Select(path => (string)path!).ToArray();
            _applied.Add(paths);
            var removed = new JsonArray([.. paths.Select(path => (JsonNode)new JsonObject { ["path"] = path, ["status"] = "removed", ["freeable"] = 300_000_000 })]);
            await route.FulfillAsync(new() { Body = removed.ToJsonString(), ContentType = "application/json" });
        });
        await Page.RouteAsync("**/api/ops/jobs/run", async route =>
        {
            var job = (string?)JsonNode.Parse(route.Request.PostData ?? "{}")!["job"];
            _started.Add(job);
            await route.FulfillAsync(new() { Body = new JsonObject { ["job"] = job, ["pid"] = 4242 }.ToJsonString(), ContentType = "application/json" });
        });
    }

    [Fact]
    public async Task Attention_lists_failed_jobs_and_decisions_and_opens_the_run()
    {
        await Page.GotoAsync($"{Origin}/ops");
        await Expect(Page.GetByRole(AriaRole.Heading, new() { Name = "Attention", Level = 1 })).ToBeVisibleAsync();
        await Expect(Page).ToHaveTitleAsync("Rig / Operations / Attention");
        await Expect(Page.Text("sync failed")).ToBeVisibleAsync();
        await Expect(Page.Text("unpushed: rig")).ToBeVisibleAsync();
        await Page.GetByRole(AriaRole.Button, new() { Name = "Open the run's output" }).ClickAsync();
        await Expect(Page).ToHaveURLAsync($"{Origin}/ops/runs?job=sync&run=r2");
        await Expect(Page.Text("fatal: remote hung up")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task Jobs_link_to_their_runs_and_runs_link_to_traces()
    {
        await Page.GotoAsync($"{Origin}/ops/jobs");
        await Expect(Page.GetByRole(AriaRole.Table)).ToContainTextAsync("Fetch every managed repo");
        await Expect(Page.GetByRole(AriaRole.Table)).ToContainTextAsync("never");
        await Page.Button("sync").ClickAsync();
        await Expect(Page).ToHaveURLAsync($"{Origin}/ops/runs?job=sync");
        await Expect(Page.Link("HyperDX", exact: false)).ToHaveAttributeAsync("href", $"http://localhost:8080/trace/{Trace}");
        await Page.Button("View", exact: false).ClickAsync();
        await Expect(Page.Text("fatal: remote hung up")).ToBeVisibleAsync();
        await Page.Button("Close", exact: false).ClickAsync();
        await Expect(Page).ToHaveURLAsync($"{Origin}/ops/runs?job=sync");
    }

    [Fact]
    public async Task Telemetry_lists_traces_with_explorer_links()
    {
        await Page.GotoAsync($"{Origin}/ops/telemetry");
        await Expect(Page.Text("Source: ClickHouse")).ToBeVisibleAsync();
        await Expect(Page.GetByRole(AriaRole.Table)).ToContainTextAsync("job.sync");
        await Expect(Page.Link("Open HyperDX", exact: false)).ToHaveAttributeAsync("href", "http://localhost:8080");
    }

    [Fact]
    public async Task Decisions_show_the_suggested_command()
    {
        await Page.GotoAsync($"{Origin}/ops/decisions");
        await Expect(Page.GetByRole(AriaRole.Table)).ToContainTextAsync("idle with unpushed commits");
        await Expect(Page.Text("git -C C:/code/personal/rig/.worktrees/old push")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task Trim_applies_only_the_selected_safe_paths()
    {
        await Page.GotoAsync($"{Origin}/ops/trim");
        await Expect(Page.GetByRole(AriaRole.Table)).ToContainTextAsync("dirty worktree");
        var refused = Page.GetByRole(AriaRole.Checkbox, new() { Name = "Trim C:/code/personal/blog/.worktrees/b/node_modules" });
        await Expect(refused).ToBeDisabledAsync();
        await Page.GetByRole(AriaRole.Checkbox, new() { Name = "Trim C:/code/personal/blog/.worktrees/a/node_modules" }).CheckAsync();
        await Page.Button("Trim 1 selected", exact: false).ClickAsync();
        await Page.GetByRole(AriaRole.Alertdialog).Button("Trim", exact: false).ClickAsync();
        await Expect(Page.Text("Removed 1 of 1")).ToBeVisibleAsync();
        Assert.Equal(["C:/code/personal/blog/.worktrees/a/node_modules"], Assert.Single(_applied));
    }

    [Fact]
    public async Task An_unknown_ops_page_is_not_found()
    {
        await Page.GotoAsync($"{Origin}/ops/nope");
        await Expect(Page.GetByRole(AriaRole.Heading, new() { Name = "Page not found" })).ToBeVisibleAsync();
    }

    [Fact]
    public async Task Jobs_start_one_job_or_every_due_job_in_the_background()
    {
        await Page.GotoAsync($"{Origin}/ops/jobs");
        await Page.Button("Run due jobs", exact: false).ClickAsync();
        await Expect(Page.Text("Started a run of the due jobs")).ToBeVisibleAsync();
        await Page.Button("Run sync now", exact: false).ClickAsync();
        await Expect(Page.Text("Started sync")).ToBeVisibleAsync();
        Assert.Equal([null, "sync"], _started);
    }
}
