using System.CommandLine;
using Rig.Cli.Governance;
using Rig.Core.Governance;
using Rig.Core.Processes;
using Rig.Core.Tests;
using Rig.Core.Tests.Fakes;

namespace Rig.Cli.Tests.Governance;

/// <summary>
/// <c>rig repo-governance issue-triage</c>, <c>pr-triage</c> and <c>verify-pr-checks</c> end to end, with gh and the
/// GitHub API faked: nothing is read from or merged on GitHub.
/// </summary>
public sealed class GovernanceTriageCommandTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 6, 30, 12, 0, 0, TimeSpan.Zero);

    private const string Issues = """
        [{"number": 1, "title": "Fresh", "url": "https://x/1", "author": {"login": "amy"}, "createdAt": "2026-06-29T00:00:00Z", "updatedAt": "2026-06-29T00:00:00Z", "comments": [], "labels": []},
         {"number": 2, "title": "Crash", "url": "https://x/2", "author": null, "createdAt": "2026-06-01T00:00:00Z", "updatedAt": "2026-06-29T00:00:00Z", "comments": [{}], "labels": [{"name": "bug"}]},
         {"number": 3, "title": "Help", "url": "https://x/3", "author": null, "createdAt": "2026-06-01T00:00:00Z", "updatedAt": "2026-06-29T00:00:00Z", "comments": [{}], "labels": [{"name": "Help Wanted"}, {"name": "docs"}]},
         {"number": 4, "title": "Repro", "url": "https://x/4", "author": null, "createdAt": "2026-05-01T00:00:00Z", "updatedAt": "2026-05-01T00:00:00Z", "comments": [{}], "labels": [{"name": "needs-repro"}]},
         {"number": 5, "title": "Done", "url": "https://x/5", "author": null, "createdAt": "2026-05-01T00:00:00Z", "updatedAt": "2026-06-29T00:00:00Z", "comments": [{}], "labels": [{"name": "type: feature"}]}]
        """;

    private const string Prs = """
        [{"number": 10, "title": "Ready", "url": "https://x/10", "headRefName": "10-ready", "baseRefName": "main", "author": null, "isDraft": false, "mergeable": "MERGEABLE", "mergeStateStatus": "CLEAN", "updatedAt": "2026-06-29T00:00:00Z",
          "statusCheckRollup": [{"name": "ci", "conclusion": "SUCCESS"}]},
         {"number": 11, "title": "Behind", "url": "https://x/11", "headRefName": "11-behind", "baseRefName": "main", "author": null, "isDraft": false, "mergeable": "MERGEABLE", "mergeStateStatus": "BEHIND", "updatedAt": "2026-06-29T00:00:00Z",
          "statusCheckRollup": [{"name": "ci", "conclusion": "SUCCESS"}]},
         {"number": 12, "title": "Broken", "url": "https://x/12", "headRefName": "12-broken", "baseRefName": "main", "author": null, "isDraft": true, "mergeable": "CONFLICTING", "mergeStateStatus": "DIRTY", "updatedAt": "2026-06-29T00:00:00Z",
          "statusCheckRollup": [{"name": "lint", "conclusion": "FAILURE"}]},
         {"number": 13, "title": "Running", "url": "https://x/13", "headRefName": "13-run", "baseRefName": "main", "author": null, "isDraft": false, "mergeable": "MERGEABLE", "mergeStateStatus": "UNKNOWN", "updatedAt": "2026-06-29T00:00:00Z",
          "statusCheckRollup": [{"name": "ci", "status": "IN_PROGRESS"}]},
         {"number": 14, "title": "Idle", "url": "https://x/14", "headRefName": "14-idle", "baseRefName": "main", "author": null, "isDraft": false, "mergeable": "MERGEABLE", "mergeStateStatus": "UNKNOWN", "updatedAt": "2026-05-01T00:00:00Z",
          "statusCheckRollup": [{"name": "ci", "conclusion": "SUCCESS"}]}]
        """;

    private const string NoThreads = """{"data":{"repository":{"pullRequest":{"reviewThreads":{"pageInfo":{"hasNextPage":false,"endCursor":null},"nodes":[]}}}}}""";

    private const string OpenThread = """
        {"data":{"repository":{"pullRequest":{"reviewThreads":{"pageInfo":{"hasNextPage":false,"endCursor":null},"nodes":[
          {"id":"PRRT_a","isResolved":false,"isOutdated":false,"path":"a.py","line":3,"first":{"nodes":[{"author":{"login":"rabbit"},"body":"fix","url":"u"}]},"latest":{"nodes":[]}},
          {"id":"PRRT_b","isResolved":true,"isOutdated":false,"path":"b.py","line":1,"first":{"nodes":[{"author":null,"body":"ok","url":"u"}]},"latest":{"nodes":[]}}]}}}}}
        """;

    private readonly TempDir _root = new();
    private readonly FakeProc _proc = new();
    private readonly FakeGitHub _gitHub = new();

    public GovernanceTriageCommandTests() =>
        File.WriteAllText(_root.File($"checkout/config/{RepoPolicies.FileName}"), """{"repo_overrides": {"r": {"required_checks": ["ci"]}}}""");

    public void Dispose() => _root.Dispose();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Issue_triage_prints_each_bucket_with_its_issues()
    {
        _ = _proc.Answer(new Ran(0, Issues, string.Empty), "gh", "issue", "list");

        var (code, output) = await RunAsync("issue-triage", "--repo", "o/r");

        Assert.Equal(0, code);
        Assert.Contains("│ Open Source Issue Triage: 5 Open Issues Across 1 Repos │", output, StringComparison.Ordinal);
        Assert.Contains("\n🚨 NEEDS TRIAGE (No type label and 0 responses) (1)\n  • o/r#1: Fresh by @amy\n    https://x/1\n", output, StringComparison.Ordinal);
        Assert.Contains("\n🐛 ACTIVE BUGS (1)\n  • o/r#2: Crash [type: bug]\n", output, StringComparison.Ordinal);
        Assert.Contains("\n🙋 COMMUNITY / GOOD FIRST ISSUE (1)\n  • o/r#3: Help [Help Wanted]\n", output, StringComparison.Ordinal);
        Assert.Contains("\n💤 STALE WAITING ON REPRO (> 14 days) (1)\n  • o/r#4: Repro [status: needs-repro]\n", output, StringComparison.Ordinal);
        Assert.EndsWith("\nOther open issues (already triaged): 1\n", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_empty_bucket_says_none_and_no_other_line_is_printed()
    {
        _ = _proc.Answer(new Ran(0, "[]", string.Empty), "gh", "issue", "list");

        var (_, output) = await RunAsync("issue-triage", "--repo", "o/r", "--label", "bug");

        Assert.Contains("(0)\n  None\n", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Other open issues", output, StringComparison.Ordinal);
        Assert.Equal(["--label", "bug"], _proc.Requests.Single().Argv.TakeLast(2));
    }

    [Fact]
    public async Task Issue_triage_json_is_the_dashboard()
    {
        _ = _proc.Answer(new Ran(0, Issues, string.Empty), "gh", "issue", "list");

        var (code, output) = await RunAsync("issue-triage", "--repo", "o/r", "--json");

        Assert.Equal(0, code);
        Assert.StartsWith("{\n  \"total_issues\": 5,\n  \"needs_triage\": [\n", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_org_alone_triages_the_repos_gh_lists_for_it()
    {
        _ = _proc.Answer(new Ran(0, """[{"nameWithOwner": "acme/a"}, {"nameWithOwner": "acme/b"}]""", string.Empty), "gh", "repo", "list")
            .Answer(new Ran(0, "[]", string.Empty), "gh", "issue", "list");

        var (_, output) = await RunAsync("issue-triage", "--org", "acme");

        Assert.Contains("Across 2 Repos", output, StringComparison.Ordinal);
        Assert.Equal(["acme/a", "acme/b"], _proc.Requests.Where(request => request.Argv[1] == "issue").Select(request => request.Argv[4]));
    }

    [Fact]
    public async Task Without_a_repo_the_current_checkout_is_triaged_by_its_github_name()
    {
        _ = _proc.Answer(new Ran(0, "o/r\n", string.Empty), "gh", "repo", "view")
            .Answer(new Ran(0, "[]", string.Empty), "gh", "issue", "list");

        var (_, output) = await RunAsync("issue-triage");

        Assert.Contains("Across 1 Repos", output, StringComparison.Ordinal);
        Assert.Contains(_proc.Requests, request => request.Argv is ["gh", "issue", "list", "--repo", "o/r", ..]);
    }

    [Fact]
    public async Task An_org_whose_targets_have_no_github_name_falls_back_to_listing_the_org()
    {
        _ = _proc.Answer(new Ran(0, """[{"nameWithOwner": "acme/a"}]""", string.Empty), "gh", "repo", "list")
            .Answer(new Ran(0, "[]", string.Empty), "gh", "issue", "list");

        var (_, output) = await RunAsync("issue-triage", "--org", "acme", "--all");

        Assert.Contains("Across 1 Repos", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pr_triage_prints_each_bucket_with_why_a_pr_is_held()
    {
        _ = _proc.Answer(new Ran(0, Prs, string.Empty), "gh", "pr", "list");

        var (code, output) = await RunAsync("pr-triage", "--repo", "o/r");

        Assert.Equal(0, code);
        Assert.Contains("│ Open Source Pull Request Triage: 5 Open PRs Across 1 Repos │", output, StringComparison.Ordinal);
        Assert.Contains("\n🟢 READY TO MERGE (1)\n  • o/r#10: Ready (branch: 10-ready)\n    https://x/10\n", output, StringComparison.Ordinal);
        Assert.Contains("  • o/r#12: Broken [draft, conflict, failing: lint, missing: ci]\n", output, StringComparison.Ordinal);
        Assert.Contains("\n🔴 BLOCKED / FAILING (1)\n", output, StringComparison.Ordinal);
        Assert.Contains("  • o/r#13: Running [pending: ci]\n", output, StringComparison.Ordinal);
        Assert.Contains("\n💤 STALE / IDLE (> 14 days) (1)\n  • o/r#14: Idle\n", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_repo_the_policy_requires_nothing_of_falls_back_to_the_legacy_checks()
    {
        _ = _proc.Answer(new Ran(0, Prs, string.Empty), "gh", "pr", "list");

        var (_, output) = await RunAsync("pr-triage", "--repo", "x/y", "--json");

        Assert.Contains("\"Lint & Typecheck\"", output, StringComparison.Ordinal);
        Assert.StartsWith("{\n  \"total_prs\": 5,", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Batch_actions_update_behind_branches_and_merge_ready_prs_once_threads_are_resolved()
    {
        _ = _proc.Answer(new Ran(0, Prs, string.Empty), "gh", "pr", "list")
            .Answer(new Ran(1, string.Empty, "conflict\n"), "gh", "pr", "update-branch")
            .Answer(new Ran(0, string.Empty, string.Empty), "gh", "pr", "merge");
        _ = _gitHub.On("POST", "graphql", NoThreads);

        var (code, output) = await RunAsync("pr-triage", "--repo", "o/r", "--update-behind", "--merge-ready");

        Assert.Equal(0, code);
        Assert.Contains("\n── Updating behind branches ──\n  ✗ Failed to update branch for o/r#11: conflict\n", output, StringComparison.Ordinal);
        Assert.Contains("\n── Merging ready PRs ──\n  ✓ Successfully merged o/r#10 and deleted branch\n", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Merge_ready_holds_a_pr_with_an_open_thread()
    {
        _ = _proc.Answer(new Ran(0, Prs, string.Empty), "gh", "pr", "list")
            .Answer(new Ran(0, string.Empty, string.Empty), "gh", "pr", "merge");
        _ = _gitHub.On("POST", "graphql", OpenThread);

        var (_, output) = await RunAsync("pr-triage", "--repo", "o/r", "--merge-ready");

        Assert.Contains("  ✗ Blocked o/r#10: 1 unresolved conversation thread(s)!\nThread PRRT_a by @rabbit is unresolved\nAll conversation threads", output, StringComparison.Ordinal);
        Assert.DoesNotContain(_proc.Requests, request => request.Argv is [_, _, "merge", ..]);
    }

    [Fact]
    public async Task Verify_passes_when_checks_merge_state_and_threads_all_allow_it()
    {
        _ = _proc.Answer(new Ran(0, "ci pass", string.Empty), "gh", "pr", "checks")
            .Answer(new Ran(0, """{"number": 7, "mergeStateStatus": "UNSTABLE"}""", string.Empty), "gh", "pr", "view");
        _ = _gitHub.On("POST", "graphql", NoThreads);

        Assert.Equal(
            (0, "✓ Required checks pass, GitHub can merge it, and every conversation thread is resolved for PR 7!\n"),
            await RunAsync("verify-pr-checks", "7", "--repo", "o/r"));
    }

    [Fact]
    public async Task A_repo_that_requires_no_checks_is_gated_on_its_merge_state_alone()
    {
        _ = _proc.Answer(new Ran(1, string.Empty, "no required checks reported on the 'x' branch"), "gh", "pr", "checks")
            .Answer(new Ran(0, """{"number": 7, "mergeStateStatus": "BEHIND"}""", string.Empty), "gh", "pr", "view");

        Assert.Equal((1, "BLOCKED: PR 7 in o/r cannot merge: mergeStateStatus is BEHIND\n"), await RunAsync("verify-pr-checks", "7", "--repo", "o/r"));
    }

    [Theory]
    [InlineData(8, "ci pending", "", "BLOCKED: required checks for PR 7 in o/r are still pending\nci pending\n")]
    [InlineData(1, "", " ci failed \n", "BLOCKED: required checks for PR 7 in o/r are not passing\nci failed\n")]
    [InlineData(1, "", "", "BLOCKED: required checks for PR 7 in o/r are not passing\n")]
    public async Task Failing_or_pending_required_checks_block(int code, string stdout, string stderr, string expected)
    {
        _ = _proc.Answer(new Ran(code, stdout, stderr), "gh", "pr", "checks");

        Assert.Equal((1, expected), await RunAsync("verify-pr-checks", "7", "--repo", "o/r"));
    }

    [Theory]
    [InlineData(1, "")]
    [InlineData(0, "not json")]
    [InlineData(0, """{"number": 7}""")]
    [InlineData(0, """{"number": "x", "mergeStateStatus": "CLEAN"}""")]
    public async Task An_unreadable_pr_blocks(int code, string stdout)
    {
        _ = _proc.Answer(new Ran(0, string.Empty, string.Empty), "gh", "pr", "checks").Answer(new Ran(code, stdout, string.Empty), "gh", "pr", "view");

        Assert.Equal((1, "BLOCKED: could not read PR '7' in o/r\n"), await RunAsync("verify-pr-checks", "7", "--repo", "o/r"));
    }

    [Fact]
    public async Task Unresolved_threads_block_and_name_each_thread()
    {
        _ = _proc.Answer(new Ran(0, string.Empty, string.Empty), "gh", "pr", "checks")
            .Answer(new Ran(0, """{"number": 7, "mergeStateStatus": "CLEAN"}""", string.Empty), "gh", "pr", "view");
        _ = _gitHub.On("POST", "graphql", OpenThread);

        var (code, output) = await RunAsync("verify-pr-checks", "7", "--repo", "o/r");

        Assert.Equal(1, code);
        Assert.Equal(
            "BLOCKED: PR 7 in o/r: 1 unresolved conversation thread(s)!\nThread PRRT_a by @rabbit is unresolved\nAll conversation threads must be resolved before merging.\n",
            output);
    }

    [Fact]
    public async Task A_failed_thread_query_blocks()
    {
        _ = _proc.Answer(new Ran(0, string.Empty, string.Empty), "gh", "pr", "checks")
            .Answer(new Ran(0, """{"number": 7, "mergeStateStatus": "CLEAN"}""", string.Empty), "gh", "pr", "view");
        _ = _gitHub.Fail("POST", "graphql", 502);

        var (_, output) = await RunAsync("verify-pr-checks", "7", "--repo", "o/r");

        Assert.Equal("BLOCKED: PR 7 in o/r: Failed to query review threads: review threads of o/r#7: gh: HTTP 502\n", output);
    }

    [Fact]
    public async Task Without_a_repo_the_current_checkout_names_it()
    {
        _ = _proc.Answer(new Ran(0, "o/r\n", string.Empty), "gh", "repo", "view").Answer(new Ran(8, "wait", string.Empty), "gh", "pr", "checks");

        var (_, output) = await RunAsync("verify-pr-checks", "7");

        Assert.StartsWith("BLOCKED: required checks for PR 7 in o/r are still pending", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_a_repo_or_a_github_checkout_verify_says_to_pass_one()
    {
        Assert.Equal((1, "BLOCKED: no GitHub repository found for the current directory; pass --repo\n"), await RunAsync("verify-pr-checks", "7"));
    }

    [Theory]
    [InlineData("o", 7)]
    [InlineData("o/", 7)]
    [InlineData("o/r/x", 7)]
    [InlineData("o/r", 0)]
    public async Task An_invalid_pr_cannot_have_its_threads_verified(string repo, int number)
    {
        var result = await GovernanceCommand.ReviewThreadsAsync(_gitHub, repo, number, Token);

        Assert.Equal(new ReviewThreadsResult(false, $"Cannot verify review threads: invalid PR {repo}#{number}"), result);
        Assert.Empty(_gitHub.Calls);
    }

    private async Task<(int Code, string Out)> RunAsync(params string[] args)
    {
        using var output = new StringWriter();
        var command = new GovernanceCommand(
            new GovernanceHost(_root.Path, _root.Path, Path.Join(_root.Path, "checkout")), _proc, new FixedTime(Now), output, _gitHub);
        var code = await new RootCommand { command.Build() }.Parse(["repo-governance", .. args])
            .InvokeAsync(new InvocationConfiguration { Output = output, Error = TextWriter.Null }, Token);
        return (code, output.ToString().ReplaceLineEndings("\n"));
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
