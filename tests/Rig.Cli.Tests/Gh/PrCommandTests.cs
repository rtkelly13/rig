using System.Text.Json.Nodes;
using Rig.Cli.Forwarding;
using Rig.Cli.Gh.Pr;
using Rig.Cli.Gh.Threads;
using Rig.Cli.Tests.Fakes;
using Rig.Core.GitHub;
using Rig.Core.Processes;
using Rig.Core.Tests.Fakes;

namespace Rig.Cli.Tests.Gh;

/// <summary><c>rig gh pr wait|land</c> through the whole command line, against a fake GitHub and a clock that never sleeps.</summary>
public sealed class PrCommandTests
{
    private const string Repo = "octo/repo";

    /// <summary>The estate's ruleset: every thread resolved, two required checks, a merge queue.</summary>
    private const string Ruleset = """
        [
          {"type": "deletion"},
          {"type": "pull_request", "parameters": {"required_review_thread_resolution": true, "required_approving_review_count": 0}},
          {"type": "required_status_checks", "parameters": {"required_status_checks": [{"context": "build"}, {"context": "pr-title"}]}},
          {"type": "merge_queue", "parameters": {"merge_method": "SQUASH"}}
        ]
        """;

    private static readonly FakeCheck[] Green = [new("build"), new("pr-title")];

    private static readonly FakeLandingState Merged = new() { State = "MERGED", MergeState = "UNKNOWN", Checks = Green };

    private readonly FakeTime _time = new();
    private readonly List<int> _merges = [];
    private Ran _mergeAnswer = new(0, string.Empty, string.Empty);

    private sealed record Result(int Code, string Out, string Err)
    {
        public JsonObject Json => JsonNode.Parse(Out)!.AsObject();
    }

    private static FakeLanding GitHub(int threads, params FakeLandingState[] script)
    {
        List<FakeThread> all = [.. Enumerable.Range(0, threads).Select(n => new FakeThread($"PRRT_{n}", "src/a.py", n + 1, $"Fix thing {n}"))];
        all.Add(new FakeThread("PRRT_done", "src/c.py", 3, "fine") { Resolved = true });
        var graphql = new FakeGraphQL(new Dictionary<string, FakePullRequest> { [$"{Repo}#7"] = new("feat/x", ["aa11"], all) });
        return new FakeLanding(graphql, script) { RulesJson = Ruleset };
    }

    private async Task<Result> Run(FakeLanding github, RepoSlug? origin, params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var command = new PrCommand(
            new LandingGitHub(github, new ThreadsGitHub(github)),
            _ => Task.FromResult(origin),
            (_, number, _) =>
            {
                _merges.Add(number);
                return Task.FromResult(_mergeAnswer);
            },
            new LandingWait(_time, _time.SleepAsync),
            output,
            error);
        var app = new RigApp(new PythonRig(new FakeProc(), "/rig"), github, _ => Task.FromResult(origin), output, error, gh: [command.Build]);
        var code = await app.RunAsync(["gh", "pr", .. args], TestContext.Current.CancellationToken);
        return new Result(code, output.ToString().ReplaceLineEndings("\n"), error.ToString().ReplaceLineEndings("\n"));
    }

    private Task<Result> Wait(FakeLanding github, params string[] args) => Run(github, null, ["wait", "7", "--repo", Repo, .. args]);

    private Task<Result> Land(FakeLanding github, params string[] args) => Run(github, null, ["land", "7", "--repo", Repo, .. args]);

    // -- the incident -----------------------------------------------------------------

    [Fact]
    public async Task Green_checks_and_queued_auto_merge_blocked_by_unresolved_threads_stops_on_the_first_poll()
    {
        var github = GitHub(14, new FakeLandingState { MergeState = "BLOCKED", Checks = Green });

        var result = await Wait(github);

        Assert.Equal(1, result.Code);
        var lines = result.Out.Split('\n');
        Assert.Equal(
            "octo/repo#7: BLOCKED (unresolved_threads) after 0:00: 14 unresolved review thread(s), and the ruleset requires every one resolved",
            lines[0]);
        Assert.Contains("  PRRT_0  src/a.py:1  @chatgpt-codex-connector", lines);
        Assert.Contains("      Fix thing 13", lines);
        Assert.DoesNotContain("PRRT_done", result.Out, StringComparison.Ordinal);
        Assert.Contains("Resolve each once its fix is pushed: rig gh threads resolve 7 <thread> --sha <fix> --repo octo/repo", lines);
        Assert.Equal((1, 0), (github.Polls, _time.Sleeps.Count));
    }

    [Fact]
    public async Task Json_report_names_the_reason_and_lists_the_threads()
    {
        var github = GitHub(2, new FakeLandingState { MergeState = "BLOCKED", Checks = Green });

        var result = await Wait(github, "--json");

        Assert.Equal(1, result.Code);
        var report = result.Json;
        Assert.Equal(
            ("octo/repo", 7, "blocked", "unresolved_threads", "BLOCKED", true, 1),
            (report["repo"]!.GetValue<string>(), report["pr"]!.GetValue<int>(), report["outcome"]!.GetValue<string>(),
                report["reason"]!.GetValue<string>(), report["merge_state"]!.GetValue<string>(), report["auto_merge"]!.GetValue<bool>(),
                report["polls"]!.GetValue<int>()));
        Assert.Null(report["queue_position"]);
        Assert.Empty(report["pending_checks"]!.AsArray());
        Assert.Equal(["PRRT_0", "PRRT_1"], report["unresolved_threads"]!.AsArray().Select(thread => thread!["id"]!.GetValue<string>()));
        Assert.Equal("src/a.py:1", report["unresolved_threads"]![0]!["location"]!.GetValue<string>());
        Assert.Equal(string.Empty, result.Err);
    }

    // -- which rules make threads block -----------------------------------------------

    [Fact]
    public async Task Threads_do_not_block_and_are_not_read_when_no_rule_requires_them_resolved()
    {
        var github = GitHub(3, new FakeLandingState { Checks = Green }, Merged);
        github.RulesJson = "[]";

        var result = await Wait(github);

        Assert.Equal((0, "octo/repo#7: merged after 0:05\n"), (result.Code, result.Out));
        Assert.Empty(github.Threads.Calls);
        Assert.Equal(1, github.RulesReads);
    }

    [Fact]
    public async Task Classic_branch_protection_requiring_resolved_conversations_blocks_too()
    {
        var github = GitHub(1, new FakeLandingState { MergeState = "BLOCKED" });
        github.RulesJson = "[]";
        github.ClassicThreadResolution = true;

        var result = await Wait(github, "--json");

        Assert.Equal((1, "unresolved_threads"), (result.Code, result.Json["reason"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Unreadable_rules_still_name_the_threads_once_the_merge_is_blocked_with_nothing_pending()
    {
        var github = GitHub(2, new FakeLandingState { MergeState = "BLOCKED" });
        github.RulesJson = null;

        var result = await Wait(github, "--json");

        Assert.Equal((1, "unresolved_threads"), (result.Code, result.Json["reason"]!.GetValue<string>()));
        Assert.Contains("the base branch's rules could not be read", result.Json["message"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unreadable_rules_are_read_again_each_poll_and_open_threads_alone_do_not_stop_a_moving_pr()
    {
        var github = GitHub(2, new FakeLandingState(), new FakeLandingState(), Merged);
        github.RulesJson = null;

        var result = await Wait(github);

        Assert.Equal(0, result.Code);
        Assert.Equal(3, github.RulesReads);
    }

    // -- landing ------------------------------------------------------------------------

    [Fact]
    public async Task Reports_pending_checks_then_the_merge_queue_then_merges()
    {
        var github = GitHub(
            0,
            new FakeLandingState { MergeState = "BLOCKED", Checks = [new("build", "PENDING"), new("pr-title")] },
            new FakeLandingState { MergeState = "BLOCKED", Checks = [new("build", "PENDING"), new("pr-title")] },
            new FakeLandingState { Checks = Green },
            new FakeLandingState { Checks = Green, QueuePosition = 2 },
            new FakeLandingState { Checks = Green, QueuePosition = 1 },
            Merged);

        var result = await Wait(github);

        Assert.Equal((0, "octo/repo#7: merged after 0:27\n"), (result.Code, result.Out));
        Assert.Equal(
            [
                "octo/repo#7 [0:00] 1 check(s) pending: build (next look in 5s)",
                "octo/repo#7 [0:12] every requirement is met; waiting for GitHub to merge or enqueue it (next look in 5s)",
                "octo/repo#7 [0:17] in the merge queue at position 2 (QUEUED) (next look in 5s)",
                "octo/repo#7 [0:22] in the merge queue at position 1 (QUEUED) (next look in 5s)",
            ],
            result.Err.TrimEnd('\n').Split('\n'));
        Assert.Equal([5, 7.5, 5, 5, 5], _time.Sleeps);
        Assert.Equal(6, github.Polls);
    }

    [Fact]
    public async Task The_gap_between_polls_grows_by_half_up_to_a_minute_while_nothing_changes()
    {
        var pending = new FakeLandingState { MergeState = "BLOCKED", Checks = [new("build", "PENDING")] };
        var github = GitHub(0, [.. Enumerable.Repeat(pending, 9), Merged]);

        var result = await Wait(github);

        Assert.Equal(0, result.Code);
        Assert.Equal([5, 7.5, 11.25, 16.875, 25.3125, 37.96875, 56.953125, 60, 60], _time.Sleeps);
        Assert.Single(result.Err.TrimEnd('\n').Split('\n'));
    }

    [Fact]
    public async Task A_required_check_that_has_not_reported_yet_counts_as_pending()
    {
        var github = GitHub(0, new FakeLandingState { MergeState = "BLOCKED", Checks = [new("build")] }, Merged);

        var result = await Wait(github);

        Assert.Equal(0, result.Code);
        Assert.Contains("1 check(s) pending: pr-title", result.Err, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Every_page_of_checks_is_read()
    {
        var github = GitHub(0, new FakeLandingState { Checks = [new("build"), new("pr-title"), new("slow", "PENDING", Required: false)] }, Merged);
        github.PageSize = 1;

        var result = await Wait(github);

        Assert.Contains("1 check(s) pending: slow", result.Err, StringComparison.Ordinal);
        Assert.Equal((2, 5), (github.Polls, github.LandingCalls)); // three pages while open, two once merged
    }

    [Fact]
    public async Task A_commit_status_is_read_like_a_check_run()
    {
        var github = GitHub(0, new FakeLandingState { Checks = [.. Green, new("ci/legacy", "PENDING", CommitStatus: true)] }, Merged);

        var result = await Wait(github);

        Assert.Contains("1 check(s) pending: ci/legacy", result.Err, StringComparison.Ordinal);
    }

    // -- causes GitHub names: stop at once ----------------------------------------------

    public static TheoryData<string, FakeLandingState> NamedBlockers => new()
    {
        { "draft", new FakeLandingState { Draft = true, Checks = Green } },
        { "check_failed", new FakeLandingState { Checks = [new("build", "FAILURE"), new("pr-title", "PENDING")] } },
        { "check_failed", new FakeLandingState { Checks = [new("build", "CANCELLED"), new("pr-title")] } },
        { "check_failed", new FakeLandingState { Checks = [new("build", "ERROR", CommitStatus: true), new("pr-title")] } },
        { "changes_requested", new FakeLandingState { ReviewDecision = "CHANGES_REQUESTED", Checks = Green } },
        { "review_required", new FakeLandingState { ReviewDecision = "REVIEW_REQUIRED", Checks = Green } },
        { "conflicts", new FakeLandingState { Mergeable = "CONFLICTING", MergeState = "DIRTY", Checks = Green } },
        { "closed", new FakeLandingState { State = "CLOSED", Checks = Green } },
    };

    [Theory]
    [MemberData(nameof(NamedBlockers))]
    public async Task A_named_cause_stops_the_wait_on_the_poll_that_sees_it(string reason, FakeLandingState state)
    {
        var github = GitHub(0, state);

        var result = await Wait(github, "--json");

        Assert.Equal((1, "blocked", reason), (result.Code, result.Json["outcome"]!.GetValue<string>(), result.Json["reason"]!.GetValue<string>()));
        Assert.Equal((1, 0), (github.Polls, _time.Sleeps.Count));
    }

    [Fact]
    public async Task A_failed_check_that_is_not_required_is_ignored()
    {
        var github = GitHub(0, new FakeLandingState { Checks = [.. Green, new("lint", "FAILURE", Required: false)] }, Merged);

        var result = await Wait(github, "--json");

        Assert.Equal(0, result.Code);
        Assert.Empty(result.Json["failed_checks"]!.AsArray());
    }

    // -- states with no named cause: stop once they last --------------------------------

    [Theory]
    [InlineData("BLOCKED", "blocked")]
    [InlineData("BEHIND", "behind")]
    public async Task A_merge_state_with_nothing_pending_and_no_named_cause_blocks_once_it_has_lasted_the_settle_time(string mergeState, string reason)
    {
        var github = GitHub(0, new FakeLandingState { MergeState = mergeState, Checks = Green });

        var result = await Wait(github, "--json");

        Assert.Equal((1, reason, 30), (result.Code, result.Json["reason"]!.GetValue<string>(), result.Json["waited_seconds"]!.GetValue<int>()));
        Assert.Equal([5, 5, 5, 5, 5, 5], _time.Sleeps);
        Assert.Contains("(confirming for 30s before giving up)", result.Err, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_blocked_state_that_clears_inside_the_settle_time_is_not_reported()
    {
        var lagging = new FakeLandingState { MergeState = "BLOCKED", Checks = Green };
        var github = GitHub(0, lagging, lagging, Merged);

        var result = await Wait(github);

        Assert.Equal((0, "octo/repo#7: merged after 0:10\n"), (result.Code, result.Out));
    }

    [Fact]
    public async Task Settle_can_be_shortened()
    {
        var github = GitHub(0, new FakeLandingState { MergeState = "BLOCKED", Checks = Green });

        var result = await Wait(github, "--settle", "10s", "--json");

        Assert.Equal((1, 10), (result.Code, result.Json["waited_seconds"]!.GetValue<int>()));
    }

    [Fact]
    public async Task Auto_merge_off_blocks_because_nothing_will_merge_the_pr()
    {
        var github = GitHub(0, new FakeLandingState { AutoMerge = false, Checks = Green });

        var result = await Wait(github);

        Assert.Equal(1, result.Code);
        Assert.Equal(
            "octo/repo#7: BLOCKED (auto_merge_off) after 0:30: auto-merge is off although it is ready to merge, so nothing will merge it\n",
            result.Out);
    }

    [Fact]
    public async Task Auto_merge_off_blocks_even_while_checks_still_run()
    {
        var github = GitHub(0, new FakeLandingState { AutoMerge = false, MergeState = "BEHIND", Checks = [new("build", "PENDING"), new("pr-title")] });

        var result = await Wait(github, "--json");

        Assert.Equal(
            (1, "auto_merge_off", "auto-merge is off, so nothing will merge it", 30),
            (result.Code, result.Json["reason"]!.GetValue<string>(), result.Json["message"]!.GetValue<string>(), result.Json["waited_seconds"]!.GetValue<int>()));
        Assert.Equal(["build"], result.Json["pending_checks"]!.AsArray().Select(check => check!.GetValue<string>()));
    }

    [Fact]
    public async Task Leaving_the_merge_queue_without_merging_blocks()
    {
        var github = GitHub(
            0,
            new FakeLandingState { Checks = Green, QueuePosition = 1 },
            new FakeLandingState { Checks = Green, AutoMerge = false });

        var result = await Wait(github, "--json");

        Assert.Equal((1, "removed_from_merge_queue"), (result.Code, result.Json["reason"]!.GetValue<string>()));
    }

    // -- time and errors ------------------------------------------------------------------

    [Fact]
    public async Task Timing_out_while_checks_still_run_exits_three_and_says_what_was_last_seen()
    {
        var github = GitHub(0, new FakeLandingState { MergeState = "BLOCKED", Checks = [new("build", "PENDING"), new("pr-title")] });

        var result = await Wait(github, "--timeout", "2", "--json");

        Assert.Equal(3, result.Code);
        Assert.Equal(
            ("timeout", "checks_pending", "timed out; last seen: 1 check(s) pending: build", 120),
            (result.Json["outcome"]!.GetValue<string>(), result.Json["reason"]!.GetValue<string>(), result.Json["message"]!.GetValue<string>(),
                result.Json["waited_seconds"]!.GetValue<int>()));
        Assert.Equal(120, _time.Sleeps.Sum());
    }

    [Fact]
    public async Task Timeout_zero_looks_once()
    {
        var moving = await Wait(GitHub(0, new FakeLandingState { Checks = [new("build", "PENDING")] }), "--timeout", "0");
        var unsettled = await Wait(GitHub(0, new FakeLandingState { AutoMerge = false, Checks = Green }), "--timeout", "0");

        Assert.Equal((3, 1), (moving.Code, unsettled.Code));
        Assert.StartsWith("octo/repo#7: TIMEOUT (checks_pending) after 0:00", moving.Out, StringComparison.Ordinal);
        Assert.StartsWith("octo/repo#7: BLOCKED (auto_merge_off) after 0:00", unsettled.Out, StringComparison.Ordinal);
        Assert.Empty(_time.Sleeps);
    }

    [Fact]
    public async Task A_first_read_that_fails_exits_two_without_retrying()
    {
        var github = GitHub(0, new FakeLandingState { Error = "Bad credentials" });

        var text = await Wait(github);
        var json = await Wait(github, "--json");

        Assert.Equal((2, string.Empty), (text.Code, text.Out));
        Assert.Equal("octo/repo#7: landing state of octo/repo#7: Bad credentials\n", text.Err);
        Assert.Equal((2, "error", "github_error"), (json.Code, json.Json["outcome"]!.GetValue<string>(), json.Json["reason"]!.GetValue<string>()));
        Assert.Empty(_time.Sleeps);
    }

    [Fact]
    public async Task A_pull_request_that_does_not_exist_exits_two()
    {
        var result = await Run(GitHub(0, new FakeLandingState()), null, "wait", "8", "--repo", Repo);

        Assert.Equal((2, "octo/repo#8: octo/repo#8: no such pull request (or no access to it)\n"), (result.Code, result.Err));
    }

    [Fact]
    public async Task Failed_reads_mid_wait_are_retried_and_the_wait_carries_on()
    {
        var pending = new FakeLandingState { MergeState = "BLOCKED", Checks = [new("build", "PENDING")] };
        var down = new FakeLandingState { Error = "502 Bad Gateway" };
        var github = GitHub(0, pending, down, down, Merged);

        var result = await Wait(github);

        Assert.Equal(0, result.Code);
        Assert.Contains("GitHub read failed (1 in a row), retrying: landing state of octo/repo#7: 502 Bad Gateway", result.Err, StringComparison.Ordinal);
        Assert.Contains("GitHub read failed (2 in a row)", result.Err, StringComparison.Ordinal);
    }

    [Fact]
    public async Task More_than_five_failed_reads_in_a_row_exit_two()
    {
        var github = GitHub(0, new FakeLandingState { Checks = [new("build", "PENDING")] }, new FakeLandingState { Error = "502 Bad Gateway" });

        var result = await Wait(github, "--json");

        Assert.Equal((2, "github_error", 1), (result.Code, result.Json["reason"]!.GetValue<string>(), result.Json["polls"]!.GetValue<int>()));
        Assert.Equal(6, _time.Sleeps.Count);
    }

    // -- arguments ------------------------------------------------------------------------

    [Theory]
    [InlineData("45m", 2700)]
    [InlineData("2h", 7200)]
    [InlineData("90s", 90)]
    [InlineData("15", 900)]
    [InlineData(" 0 ", 0)]
    public void Durations_take_a_unit_and_a_bare_number_is_minutes(string text, int seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), PrCommand.ParseDuration(text));

    [Theory]
    [InlineData("soon")]
    [InlineData("1d")]
    [InlineData("-5")]
    [InlineData("")]
    public async Task A_duration_that_does_not_parse_exits_two_before_reading_github(string text)
    {
        var github = GitHub(0, new FakeLandingState());

        var result = await Wait(github, "--timeout", text);

        Assert.Equal((2, 0), (result.Code, github.Polls));
        Assert.Contains("Expected a duration such as 45m, 2h or 90s", result.Err, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_repo_defaults_to_the_origin_and_its_absence_exits_two()
    {
        var github = GitHub(0, Merged);

        var here = await Run(github, new RepoSlug("octo", "repo"), "wait", "7");
        var nowhere = await Run(github, null, "wait", "7");
        var wrong = await Run(github, null, "wait", "7", "--repo", "nope");

        Assert.Equal((0, 2, 2), (here.Code, nowhere.Code, wrong.Code));
        Assert.Contains("no GitHub origin here", nowhere.Err, StringComparison.Ordinal);
        Assert.Contains("'nope' is not one", wrong.Err, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Wait_help_documents_the_exit_codes_and_the_settle_time()
    {
        var result = await Run(GitHub(0, Merged), null, "wait", "--help");

        Assert.Equal(0, result.Code);
        Assert.Contains("--settle", result.Out, StringComparison.Ordinal);
        Assert.Contains("Exits 0 merged, 1 blocked", result.Out.ReplaceLineEndings(" "), StringComparison.Ordinal);
    }

    // -- land -----------------------------------------------------------------------------

    [Fact]
    public async Task Land_queues_auto_merge_then_waits()
    {
        var github = GitHub(0, new FakeLandingState { AutoMerge = false, Checks = Green }, new FakeLandingState { Checks = Green }, Merged);

        var result = await Land(github);

        Assert.Equal((0, "octo/repo#7: merged after 0:05\n"), (result.Code, result.Out));
        Assert.Equal([7], _merges);
        Assert.StartsWith("octo/repo#7: queued squash auto-merge\n", result.Err, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, null)]
    [InlineData(false, 1)]
    public async Task Land_does_not_queue_again_when_auto_merge_is_on_or_the_pr_is_in_the_queue(bool autoMerge, int? position)
    {
        var github = GitHub(0, new FakeLandingState { AutoMerge = autoMerge, QueuePosition = position, Checks = Green }, Merged);

        var result = await Land(github);

        Assert.Equal(0, result.Code);
        Assert.Empty(_merges);
    }

    [Fact]
    public async Task Land_on_a_merged_pr_reports_merged_without_queueing()
    {
        var result = await Land(GitHub(0, Merged));

        Assert.Equal(0, result.Code);
        Assert.Empty(_merges);
    }

    [Fact]
    public async Task Land_exits_two_when_the_merge_cannot_be_queued_or_github_cannot_be_read()
    {
        _mergeAnswer = new Ran(1, string.Empty, "GraphQL: Pull request is in clean status\n");
        var refused = await Land(GitHub(0, new FakeLandingState { AutoMerge = false, Checks = Green }));
        var unread = await Land(GitHub(0, new FakeLandingState { Error = "Bad credentials" }));

        Assert.Equal((2, "Could not queue auto-merge: GraphQL: Pull request is in clean status\n"), (refused.Code, refused.Err));
        Assert.Equal((2, "landing state of octo/repo#7: Bad credentials\n"), (unread.Code, unread.Err));
        Assert.Equal(string.Empty, refused.Out + unread.Out);
    }

    [Fact]
    public async Task The_real_merger_runs_gh_pr_merge_auto_squash()
    {
        var proc = new FakeProc().Answer(new Ran(0, string.Empty, string.Empty), "gh");

        await PrCommand.GhMerge(proc)(new RepoSlug("octo", "repo"), 7, TestContext.Current.CancellationToken);

        Assert.Equal(["gh", "pr", "merge", "7", "--auto", "--squash", "--repo", "octo/repo"], proc.Requests.Single().Argv);
    }
}
