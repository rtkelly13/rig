using System.Text.Json.Nodes;
using CsCheck;
using Rig.Core.Tests.Fakes;
using Rig.Core.Workspace;

namespace Rig.Core.Tests.Workspace;

/// <summary><see cref="CiTelemetry"/>: runner inference, billable weighting and the gh reads. Ported from <c>tests/estate/test_ci_telemetry.py</c>.</summary>
public sealed class CiTelemetryTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static JsonObject Json(string text) => (JsonObject)JsonNode.Parse(text)!;

    [Theory]
    [InlineData("lint", RunnerOs.Ubuntu)]
    [InlineData("test", RunnerOs.Ubuntu)]
    [InlineData("test-windows", RunnerOs.Windows)]
    [InlineData("Build (WIN32)", RunnerOs.Windows)]
    [InlineData("build-macos", RunnerOs.MacOs)]
    [InlineData("darwin-arm64", RunnerOs.MacOs)]
    public void The_runner_is_read_from_the_job_name(string name, RunnerOs expected) =>
        Assert.Equal(expected, CiTelemetry.InferRunner(name));

    [Theory]
    [InlineData("test", 1.0, 120.0)]
    [InlineData("test-windows", 2.0, 240.0)]
    [InlineData("test-macos", 10.0, 1200.0)]
    public void A_job_s_minutes_are_weighted_by_its_runner(string name, double multiplier, double billable)
    {
        var job = CiTelemetry.Job(Json($$"""{"id": 101, "name": "{{name}}", "started_at": "2026-10-10T12:00:00Z", "completed_at": "2026-10-10T12:02:00Z"}"""));

        Assert.Equal(101, job.JobId);
        Assert.Equal(120.0, job.DurationSeconds);
        Assert.Equal(multiplier, job.Multiplier);
        Assert.Equal(billable, job.BillableSeconds);
    }

    [Theory]
    [InlineData(null, "2026-10-10T12:02:00Z")]
    [InlineData("2026-10-10T12:00:00Z", null)]
    [InlineData("not a time", "2026-10-10T12:02:00Z")]
    [InlineData("2026-10-10T12:02:00Z", "2026-10-10T12:00:00Z")]
    public void A_missing_unreadable_or_backwards_span_is_zero(string? start, string? end) =>
        Assert.Equal(0.0, CiTelemetry.Seconds(start, end));

    [Fact]
    public void A_bare_job_record_takes_the_defaults()
    {
        var job = CiTelemetry.Job(new JsonObject());

        Assert.Equal(new JobTelemetry(0, "unknown", RunnerOs.Ubuntu, 0.0), job);
    }

    [Fact]
    public void A_run_sums_its_jobs_and_times_from_its_start_to_its_last_update()
    {
        var run = CiTelemetry.Run(
            "rig",
            Json("""
                {"id": 9, "name": "CI", "event": "pull_request", "head_branch": "85-s19d", "conclusion": "success",
                 "created_at": "2026-10-10T11:59:00Z", "run_started_at": "2026-10-10T12:00:00Z", "updated_at": "2026-10-10T12:05:30Z"}
                """),
            [
                Json("""{"id": 1, "name": "lint", "started_at": "2026-10-10T12:00:00Z", "completed_at": "2026-10-10T12:01:00Z"}"""),
                Json("""{"id": 2, "name": "test-windows", "started_at": "2026-10-10T12:00:00Z", "completed_at": "2026-10-10T12:02:00Z"}"""),
            ]);

        Assert.Equal(("rig", 9L, "CI", "pull_request", "85-s19d", "success"), (run.Repo, run.RunId, run.WorkflowName, run.Event, run.HeadBranch, run.Conclusion));
        Assert.Equal(330.0, run.DurationSeconds);
        Assert.Equal(60.0 + 240.0, run.BillableSeconds);
        Assert.Equal([1L, 2L], run.Jobs.Select(job => job.JobId));
    }

    [Fact]
    public void A_run_not_yet_started_times_from_its_creation_and_has_no_conclusion()
    {
        var run = CiTelemetry.Run(
            "rig",
            Json("""{"id": 9, "created_at": "2026-10-10T12:00:00Z", "run_started_at": "", "updated_at": "2026-10-10T12:01:00Z", "head_branch": null, "conclusion": null}"""),
            []);

        Assert.Equal(("Unknown", "unknown", string.Empty, "unknown"), (run.WorkflowName, run.Event, run.HeadBranch, run.Conclusion));
        Assert.Equal(60.0, run.DurationSeconds);
        Assert.Equal(0.0, run.BillableSeconds);
    }

    [Fact]
    public async Task Recent_runs_are_read_with_their_jobs()
    {
        var github = new FakeGitHub()
            .On("GET", "repos/o/app/actions/runs?per_page=2", """{"workflow_runs": [{"id": 7, "name": "CI"}, {"id": 8, "name": "Docs"}]}""")
            .On("GET", "repos/o/app/actions/runs/7/jobs", """{"jobs": [{"id": 70, "name": "lint", "started_at": "2026-10-10T12:00:00Z", "completed_at": "2026-10-10T12:00:30Z"}]}""");

        var runs = await new CiTelemetry(github).RecentAsync("app", "o/app", 2, Token);

        Assert.Equal(["CI", "Docs"], runs.Select(run => run.WorkflowName));
        Assert.Equal(30.0, runs[0].BillableSeconds);
        Assert.Empty(runs[1].Jobs); // its jobs call 404s
        Assert.Equal(3, github.Calls.Count);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"workflow_runs": "nope"}""")]
    [InlineData("[]")]
    public async Task An_unreadable_answer_is_no_runs(string answer)
    {
        var github = new FakeGitHub().On("GET", "repos/o/app/actions/runs?per_page=10", answer);

        Assert.Empty(await new CiTelemetry(github).RecentAsync("app", "o/app", 10, Token));
    }

    [Fact]
    public async Task A_failed_gh_call_is_no_runs() =>
        Assert.Empty(await new CiTelemetry(new FakeGitHub().Fail("GET", "repos/o/app/actions/runs?per_page=10", 403)).RecentAsync("app", "o/app", 10, Token));

    [Fact]
    public void Billable_seconds_are_never_less_than_wall_seconds() =>
        Gen.Select(Gen.String[Gen.Char.AlphaNumeric, 0, 20], Gen.Int[0, 100_000]).Sample((name, seconds) =>
        {
            var job = new JobTelemetry(1, name, CiTelemetry.InferRunner(name), seconds);
            return job.BillableSeconds >= job.DurationSeconds && job.BillableSeconds == seconds * CiTelemetry.Multiplier(job.Runner);
        });
}
