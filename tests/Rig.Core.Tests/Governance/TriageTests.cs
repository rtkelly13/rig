using Rig.Core.Governance;
using Rig.Core.Policy;
using Rig.Core.Processes;
using Rig.Core.Tests.Fakes;

namespace Rig.Core.Tests.Governance;

/// <summary>The triage buckets, the gh reads behind them and the batch actions, with gh faked.</summary>
public sealed class TriageTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 30, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeProc _proc = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static IssueItem Issue(int comments = 0, int ageDays = 1, params string[] labels) =>
        new("o/r", 1, "t", "u", "me", Now.AddDays(-30), Now.AddDays(-ageDays), comments, labels);

    private static PrItem Pr(
        string state = "CLEAN", string mergeable = "MERGEABLE", bool draft = false, int ageDays = 1, string[]? failed = null, string[]? pending = null, string[]? missing = null) =>
        new("o/r", 7, "t", "u", "feat", "main", "me", draft, mergeable, state, Now.AddDays(-ageDays), ["ci"], failed, pending, missing);

    [Theory]
    [InlineData(0, 1, new[] { "question" }, IssueBucket.NeedsTriage)]
    [InlineData(2, 1, new string[0], IssueBucket.Other)]
    [InlineData(0, 1, new[] { "type: feature" }, IssueBucket.Other)]
    [InlineData(0, 1, new[] { "type/docs" }, IssueBucket.Other)]
    [InlineData(3, 1, new[] { "Bug" }, IssueBucket.ActiveBugs)]
    [InlineData(3, 1, new[] { "type: bug" }, IssueBucket.ActiveBugs)]
    [InlineData(3, 1, new[] { "help wanted" }, IssueBucket.Community)]
    [InlineData(3, 14, new[] { "needs-repro", "bug" }, IssueBucket.StaleRepro)]
    [InlineData(3, 13, new[] { "status: needs-repro" }, IssueBucket.Other)]
    public void Issues_fall_in_the_first_bucket_that_claims_them(int comments, int ageDays, string[] labels, IssueBucket bucket) =>
        Assert.Equal(bucket, Triage.Categorize(Issue(comments, ageDays, labels), Now));

    [Fact]
    public void A_needs_repro_issue_never_updated_is_not_stale() =>
        Assert.Equal(IssueBucket.NeedsTriage, Triage.Categorize(Issue(labels: "needs-repro") with { UpdatedAt = null }, Now));

    [Theory]
    [InlineData("CLEAN", "MERGEABLE", false, 1, null, null, null, PrBucket.Ready)]
    [InlineData("BEHIND", "MERGEABLE", false, 1, null, null, null, PrBucket.Behind)]
    [InlineData("CLEAN", "MERGEABLE", true, 1, null, null, null, PrBucket.Blocked)]
    [InlineData("CLEAN", "CONFLICTING", false, 1, null, null, null, PrBucket.Blocked)]
    [InlineData("DIRTY", "MERGEABLE", false, 1, null, null, null, PrBucket.Blocked)]
    [InlineData("BLOCKED", "MERGEABLE", false, 1, null, null, null, PrBucket.Blocked)]
    [InlineData("CLEAN", "MERGEABLE", false, 1, "lint", null, null, PrBucket.Blocked)]
    [InlineData("CLEAN", "MERGEABLE", false, 1, null, null, "tests", PrBucket.Blocked)]
    [InlineData("CLEAN", "MERGEABLE", false, 1, null, "tests", null, PrBucket.Pending)]
    [InlineData("UNKNOWN", "UNKNOWN", false, 20, null, null, null, PrBucket.Stale)]
    [InlineData("UNKNOWN", "UNKNOWN", false, 2, null, null, null, PrBucket.Other)]
    public void Prs_fall_in_the_first_bucket_that_claims_them(
        string state, string mergeable, bool draft, int ageDays, string? failed, string? pending, string? missing, PrBucket bucket) =>
        Assert.Equal(bucket, Triage.Categorize(Pr(state, mergeable, draft, ageDays, Split(failed), Split(pending), Split(missing)), Now));

    [Fact]
    public void The_pr_dashboard_counts_every_pr_but_shows_no_other_bucket()
    {
        var dashboard = Triage.Dashboard([Pr(), Pr("UNKNOWN", "UNKNOWN")], Now);

        Assert.Equal(2, dashboard.Total);
        Assert.Single(dashboard[PrBucket.Ready]);
        Assert.False(dashboard.Buckets.ContainsKey(PrBucket.Other));
        Assert.Equal(["total_prs", "ready", "behind", "blocked", "pending", "stale"], dashboard.ToJson().Select(pair => pair.Key));
    }

    [Fact]
    public void The_issue_dashboard_writes_python_shaped_json()
    {
        var dashboard = Triage.Dashboard([Issue(labels: "bug")], Now);

        var json = PyJson.Dumps(dashboard.ToJson());

        Assert.Contains("\"total_issues\": 1", json, StringComparison.Ordinal);
        Assert.Contains("\"created_at\": \"2026-05-31T12:00:00Z\"", json, StringComparison.Ordinal);
        Assert.Contains("\"labels\": [\n        \"bug\"\n      ]", json, StringComparison.Ordinal);
        Assert.Contains("\"other\": []", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Issues_are_read_from_gh_with_the_label_filter()
    {
        _ = _proc.Answer(
            new Ran(0, """[{"number": 4, "title": "Crash", "url": "https://x/4", "author": {"login": "amy"}, "createdAt": "2026-06-01T10:00:00Z", "updatedAt": "2026-06-02T10:00:00.5Z", "comments": [{}, {}], "labels": [{"name": "bug"}]}]""", string.Empty),
            "gh", "issue", "list");

        var issue = Assert.Single(await new Triage(_proc).IssuesAsync("o/r", "bug", Token));

        Assert.Equal(("o/r#4", "amy", 2, "bug"), (issue.ShortRef, issue.Author, issue.CommentsCount, Assert.Single(issue.LabelNames)));
        Assert.Equal(["--label", "bug"], _proc.Requests[0].Argv.TakeLast(2));
        Assert.Equal("2026-06-02T10:00:00.500000Z", issue.ToJson()["updated_at"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(1, "[]")]
    [InlineData(0, "not json")]
    [InlineData(0, """{"number": 1}""")]
    [InlineData(0, """[{"number": "x"}]""")]
    public async Task An_unreadable_gh_answer_reads_as_nothing(int code, string stdout)
    {
        _ = _proc.Answer(new Ran(code, stdout, string.Empty), "gh");

        Assert.Empty(await new Triage(_proc).IssuesAsync("o/r", cancellationToken: Token));
        Assert.Empty(await new Triage(_proc).PullRequestsAsync("o/r", [], Token));
    }

    [Fact]
    public async Task Pr_checks_sort_into_passed_failed_pending_and_missing()
    {
        _ = _proc.Answer(
            new Ran(
                0,
                """
                [{"number": 9, "title": "Add", "url": "https://x/9", "headRefName": "9-add", "baseRefName": "main", "author": null, "isDraft": false,
                  "mergeable": "MERGEABLE", "mergeStateStatus": "BLOCKED", "updatedAt": null, "statusCheckRollup": [
                    {"name": "lint", "conclusion": "SUCCESS"},
                    {"context": "legacy", "state": "ERROR"},
                    {"name": "tests", "status": "IN_PROGRESS"},
                    {"name": "", "conclusion": "SUCCESS"},
                    {"name": "skipped", "conclusion": "SKIPPED"}]}]
                """,
                string.Empty),
            "gh", "pr", "list");

        var pr = Assert.Single(await new Triage(_proc).PullRequestsAsync("o/r", ["tests", "deploy", "deploy", "build"], Token));

        Assert.Equal(["lint"], pr.Passed);
        Assert.Equal(["legacy"], pr.Failed);
        Assert.Equal(["tests"], pr.Pending);
        Assert.Equal(["deploy", "build"], pr.Missing);
        Assert.Equal(string.Empty, pr.Author);
        Assert.Null(pr.UpdatedAt);
    }

    [Theory]
    [InlineData(0, "", true, "Successfully updated branch for o/r#7")]
    [InlineData(1, " behind\n", false, "Failed to update branch for o/r#7: behind")]
    public async Task Updating_a_behind_branch_reports_what_gh_did(int code, string stderr, bool ok, string message)
    {
        _ = _proc.Answer(new Ran(code, string.Empty, stderr), "gh", "pr", "update-branch");

        Assert.Equal(new PrActionResult(ok, message), await new Triage(_proc).UpdateBehindAsync(Pr(), Token));
        Assert.Equal(["gh", "pr", "update-branch", "7", "--repo", "o/r"], _proc.Requests[0].Argv);
    }

    [Fact]
    public async Task A_ready_pr_with_open_threads_is_not_merged()
    {
        var result = await new Triage(_proc).MergeReadyAsync(Pr(), (_, _, _) => Task.FromResult(new ReviewThreadsResult(false, "1 open")), Token);

        Assert.Equal(new PrActionResult(false, "Blocked o/r#7: 1 open"), result);
        Assert.Empty(_proc.Requests);
    }

    [Theory]
    [InlineData(0, "", true, "Successfully merged o/r#7 and deleted branch")]
    [InlineData(1, "nope\n", false, "Failed to merge o/r#7: nope")]
    public async Task A_ready_pr_with_resolved_threads_is_squash_merged(int code, string stderr, bool ok, string message)
    {
        _ = _proc.Answer(new Ran(code, string.Empty, stderr), "gh", "pr", "merge");

        var result = await new Triage(_proc).MergeReadyAsync(Pr(), (_, _, _) => Task.FromResult(new ReviewThreadsResult(true, "ok")), Token);

        Assert.Equal(new PrActionResult(ok, message), result);
        Assert.Equal(["gh", "pr", "merge", "7", "--repo", "o/r", "--squash", "--delete-branch"], _proc.Requests[0].Argv);
    }

    [Fact]
    public void A_missing_timestamp_writes_null() => Assert.Null((Pr() with { UpdatedAt = null }).ToJson()["updated_at"]);

    private static string[]? Split(string? names) => names?.Split(',');
}
