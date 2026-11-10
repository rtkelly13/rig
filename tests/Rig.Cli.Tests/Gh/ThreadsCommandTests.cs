using System.Text.Json;
using System.Text.Json.Nodes;
using Rig.Cli.Forwarding;
using Rig.Cli.Gh.Threads;
using Rig.Cli.Tests.Fakes;
using Rig.Core.GitHub;
using Rig.Core.Tests.Fakes;

namespace Rig.Cli.Tests.Gh;

/// <summary>
/// <c>rig gh threads</c> (TEMPORARY): list, reply to and resolve PR review threads through a fake GitHub. Ported case
/// for case from the Python CLI's tests/test_gh_threads.py, through the whole command line.
/// </summary>
public sealed class ThreadsCommandTests
{
    private const string Repo = "octo/repo";
    private const string Fix = "3f1c2a9b7d4e5f60718293a4b5c6d7e8f9012345";
    private const string Other = "aa11bb22cc33dd44ee55ff6600778899aabbccdd";
    private const string Refused = "Resource not accessible by integration";

    private const string CodexBody =
        "**<sub><sub>![P1 Badge](https://img.shields.io/badge/P1-orange?style=flat)</sub></sub>  "
        + "Use the portable helper for the dangling file symlink**\n\nOn Windows without Developer Mode...\n\n"
        + "Useful? React with 👍 / 👎.\n\n<!-- codex-code-review-finding:v1 -->";

    private sealed record Result(int Code, string Out, string Err)
    {
        public string Output => Out + Err;
    }

    private static FakeGraphQL GitHub(int pageSize = 100) => new(
        new Dictionary<string, FakePullRequest>
        {
            [$"{Repo}#7"] = new(
                "feat/x",
                [Other, Fix],
                [
                    new FakeThread("PRRT_open", "src/a.py", 12, CodexBody),
                    new FakeThread("PRRT_old", "src/b.py", 40, "Rename `snake_case_name`") { Outdated = true },
                    new FakeThread("PRRT_done", "src/c.py", 3, "fine") { Resolved = true },
                ]),
        },
        pageSize);

    private static FakePullRequest Pr(FakeGraphQL github) => github.PullRequests[$"{Repo}#7"];

    private static async Task<Result> Run(FakeGraphQL github, RepoSlug? origin, params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var app = new RigApp(new PythonRig(new FakeProc(), "/rig"), github, _ => Task.FromResult(origin), output, error);
        var code = await app.RunAsync(["gh", "threads", .. args], TestContext.Current.CancellationToken);
        return new Result(code, output.ToString(), error.ToString());
    }

    private static Task<Result> Run(FakeGraphQL github, params string[] args) => Run(github, null, args);

    private static Task<Result> Resolve(FakeGraphQL github, params string[] args) =>
        Run(github, ["resolve", "7", "PRRT_open", "--repo", Repo, .. args]);

    // -- list -------------------------------------------------------------------------

    [Fact]
    public async Task List_shows_unresolved_threads_with_location_flag_author_and_clean_summary()
    {
        var result = await Run(GitHub(), "list", "7", "--repo", Repo);

        Assert.Equal(1, result.Code);
        var lines = result.Out.Split('\n').Select(line => line.TrimEnd('\r')).ToList();
        Assert.Equal("octo/repo#7: 2 unresolved review thread(s)", lines[0]);
        Assert.Contains("PRRT_open  src/a.py:12  @chatgpt-codex-connector", lines);
        Assert.Contains("    [P1] Use the portable helper for the dangling file symlink", lines);
        Assert.Contains("PRRT_old  src/b.py:40 (outdated)  @chatgpt-codex-connector", lines);
        Assert.DoesNotContain("PRRT_done", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_reads_every_page()
    {
        var github = GitHub(pageSize: 1);

        var result = await Run(github, "list", "7", "--repo", Repo, "--json");

        var ids = JsonNode.Parse(result.Out)!.AsArray().Select(thread => thread!["id"]!.GetValue<string>());
        Assert.Equal(["PRRT_open", "PRRT_old"], ids);
        Assert.Equal(["ReviewThreads", "ReviewThreads", "ReviewThreads"], github.Calls);
    }

    [Fact]
    public async Task List_json_and_a_clean_pr_exits_zero()
    {
        var github = GitHub();
        foreach (var thread in Pr(github).Threads)
        {
            thread.Resolved = true;
        }

        var asJson = await Run(github, "list", "7", "--repo", Repo, "--json");
        var text = await Run(github, "list", "7", "--repo", Repo);

        Assert.Equal(0, asJson.Code);
        Assert.Empty(JsonNode.Parse(asJson.Out)!.AsArray());
        Assert.Equal((0, "octo/repo#7: no unresolved review threads"), (text.Code, text.Output.Trim()));
    }

    [Fact]
    public async Task List_json_prints_every_field_in_snake_case()
    {
        var result = await Run(GitHub(), "list", "7", "--repo", Repo, "--json");

        var first = JsonNode.Parse(result.Out)!.AsArray()[0]!.AsObject();
        Assert.Equal(["id", "path", "line", "outdated", "resolved", "author", "summary", "url", "body"], first.Select(pair => pair.Key));
        Assert.Equal(CodexBody, first["body"]!.GetValue<string>());
        var outdated = JsonNode.Parse(result.Out)!.AsArray()[1]!;
        Assert.Equal(40, outdated["line"]!.GetValue<int>());
    }

    [Fact]
    public async Task List_defaults_to_the_origin_repo_and_errors_without_one()
    {
        var found = await Run(GitHub(), new RepoSlug("octo", "repo"), "list", "7");
        var missing = await Run(GitHub(), "list", "7");

        Assert.Equal(1, found.Code);
        Assert.Equal(2, missing.Code);
        Assert.Contains("pass --repo owner/name", missing.Err, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("8", Repo, "octo/repo#8: no such pull request")]
    [InlineData("7", "not-a-slug", "'not-a-slug' is not one")]
    public async Task List_errors_exit_two(string pr, string repo, string message)
    {
        var result = await Run(GitHub(), "list", pr, "--repo", repo);

        Assert.Equal(2, result.Code);
        Assert.Contains(message, result.Err, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_graphql_error_exits_two_with_githubs_message()
    {
        var github = GitHub();
        github.Failures["ReviewThreads"] = Refused;

        var result = await Run(github, "list", "7", "--repo", Repo);

        Assert.Equal(2, result.Code);
        Assert.Contains(Refused, result.Err, StringComparison.Ordinal);
    }

    // -- resolve ----------------------------------------------------------------------

    [Fact]
    public async Task Resolve_replies_with_the_fix_sha_and_note_then_resolves()
    {
        var github = GitHub();

        var result = await Resolve(github, "--sha", Fix[..9], "--note", "Used file_symlink_or_skip.");

        Assert.Equal(0, result.Code);
        Assert.Equal(
            [
                new FakeWrite("ReplyToThread", "PRRT_open", $"Fixed in {Fix}.\n\nUsed file_symlink_or_skip."),
                new FakeWrite("ResolveThread", "PRRT_open"),
            ],
            github.Writes);
        Assert.Contains("PRRT_open (src/a.py:12): replied https://", result.Out, StringComparison.Ordinal);
        Assert.Contains($"resolved (fix {Fix[..12]})", result.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resolve_finds_the_fix_on_a_later_page_of_commits()
    {
        var github = GitHub(pageSize: 1);

        Assert.Equal(0, (await Resolve(github, "--sha", Fix)).Code);
        Assert.True(Pr(github).Threads[0].Resolved);
    }

    [Theory]
    [InlineData("0123456789abcdef0123456789abcdef01234567", "is not a commit on the PR's head branch feat/x")]
    [InlineData("3f1c2a", "need at least 7 hex characters")]
    [InlineData("not-a-sha", "is not a commit SHA")]
    public async Task Resolve_refuses_a_fix_that_is_not_on_the_pr(string sha, string reason)
    {
        var github = GitHub();

        var result = await Resolve(github, "--sha", sha);

        Assert.Equal(1, result.Code);
        Assert.Contains(reason, result.Err, StringComparison.Ordinal);
        Assert.Empty(github.Writes);
        Assert.False(Pr(github).Threads[0].Resolved);
    }

    [Fact]
    public async Task Resolve_refuses_an_ambiguous_prefix()
    {
        var github = GitHub();
        Pr(github).Commits.Add(Fix[..8] + new string('0', 32));

        var result = await Resolve(github, "--sha", Fix[..8]);

        Assert.Equal(1, result.Code);
        Assert.Contains("ambiguous", result.Err, StringComparison.Ordinal);
        Assert.Empty(github.Writes);
    }

    [Fact]
    public async Task Resolve_refuses_a_thread_from_another_pr()
    {
        var github = GitHub();

        var result = await Run(github, "resolve", "7", "PRRT_elsewhere", "--sha", Fix, "--repo", Repo);

        Assert.Equal(1, result.Code);
        Assert.Contains("PRRT_elsewhere is not a review thread on this pull request", result.Err, StringComparison.Ordinal);
        Assert.Empty(github.Writes);
    }

    [Fact]
    public async Task Dry_run_writes_nothing()
    {
        var github = GitHub();

        var result = await Resolve(github, "--sha", Fix, "--dry-run", "--json");

        Assert.Equal(0, result.Code);
        var expected = new JsonObject
        {
            ["thread_id"] = "PRRT_open",
            ["sha"] = Fix,
            ["reply_url"] = null,
            ["replied"] = false,
            ["resolved"] = false,
            ["dry_run"] = true,
        };
        Assert.True(JsonNode.DeepEquals(expected, JsonNode.Parse(result.Out)), result.Out);
        Assert.Empty(github.Writes);
    }

    [Fact]
    public async Task Dry_run_text_says_what_it_would_do()
    {
        var result = await Resolve(GitHub(), "--sha", Fix, "--dry-run");

        Assert.Equal(
            $"Dry run, PRRT_open (src/a.py:12): would reply 'Fixed in {Fix}.' then resolve.",
            result.Out.Trim());
    }

    [Fact]
    public async Task A_second_run_changes_nothing()
    {
        var github = GitHub();
        await Resolve(github, "--sha", Fix);

        var again = await Resolve(github, "--sha", Fix);

        Assert.Equal(0, again.Code);
        Assert.Contains("nothing to do", again.Out, StringComparison.Ordinal);
        Assert.Equal(2, github.Writes.Count);
    }

    [Fact]
    public async Task A_failed_resolve_is_retried_without_repeating_the_reply()
    {
        var github = GitHub();
        github.Failures["ResolveThread"] = Refused;

        var first = await Resolve(github, "--sha", Fix);
        github.Failures.Remove("ResolveThread");
        var second = await Resolve(github, "--sha", Fix);

        Assert.Equal(2, first.Code);
        Assert.Contains("Replied (https://", first.Err, StringComparison.Ordinal);
        Assert.Contains(Refused, first.Err, StringComparison.Ordinal);
        Assert.Equal(0, second.Code);
        Assert.Contains("reply already posted; resolved", second.Out, StringComparison.Ordinal);
        Assert.Equal(["ReplyToThread", "ResolveThread"], github.Writes.Select(write => write.Operation));
    }

    [Fact]
    public async Task A_failed_resolve_with_json_prints_the_partial_outcome()
    {
        var github = GitHub();
        github.Failures["ResolveThread"] = Refused;

        var result = await Resolve(github, "--sha", Fix, "--json");

        Assert.Equal(2, result.Code);
        var partial = JsonNode.Parse(result.Out)!;
        Assert.True(partial["replied"]!.GetValue<bool>());
        Assert.False(partial["resolved"]!.GetValue<bool>());
        Assert.StartsWith("https://", partial["reply_url"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failed_reply_reports_githubs_message_and_resolves_nothing()
    {
        var github = GitHub();
        github.Failures["ReplyToThread"] = Refused;

        var result = await Resolve(github, "--sha", Fix);

        Assert.Equal(2, result.Code);
        Assert.DoesNotContain("Replied (", result.Err, StringComparison.Ordinal);
        Assert.Contains(Refused, result.Err, StringComparison.Ordinal);
        Assert.Equal(["ReviewThreads", "PullRequestCommits", "ReplyToThread"], github.Calls);
    }

    [Fact]
    public async Task A_retry_with_a_changed_note_posts_the_new_note()
    {
        var github = GitHub();
        github.Failures["ResolveThread"] = Refused;
        await Resolve(github, "--sha", Fix, "--note", "First try.");
        github.Failures.Remove("ResolveThread");

        var changed = await Resolve(github, "--sha", Fix, "--note", "Also covered the outdated path.");

        Assert.Equal(0, changed.Code);
        Assert.Equal(
            [$"Fixed in {Fix}.\n\nFirst try.", $"Fixed in {Fix}.\n\nAlso covered the outdated path."],
            github.Writes.Where(write => write.Operation == "ReplyToThread").Select(write => write.Body));
    }

    [Fact]
    public async Task A_retry_without_a_note_keeps_the_earlier_noted_reply()
    {
        var github = GitHub();
        github.Failures["ResolveThread"] = Refused;
        await Resolve(github, "--sha", Fix, "--note", "First try.");
        github.Failures.Remove("ResolveThread");

        var plain = await Resolve(github, "--sha", Fix);

        Assert.Equal(0, plain.Code);
        Assert.Equal(["ReplyToThread", "ResolveThread"], github.Writes.Select(write => write.Operation));
    }

    [Fact]
    public async Task A_resolved_thread_with_a_new_reply_says_it_was_already_resolved()
    {
        var github = GitHub();
        Pr(github).Threads[0].Resolved = true;

        var result = await Resolve(github, "--sha", Fix);

        Assert.Equal(0, result.Code);
        Assert.Contains("was already resolved", result.Out, StringComparison.Ordinal);
        Assert.Equal(["ReplyToThread"], github.Writes.Select(write => write.Operation));
    }

    [Fact]
    public async Task Resolve_errors_exit_two()
    {
        var github = GitHub();
        github.Failures["PullRequestCommits"] = Refused;

        var badRepo = await Run(GitHub(), "resolve", "7", "PRRT_open", "--sha", Fix, "--repo", "nope");
        var failed = await Resolve(github, "--sha", Fix);

        Assert.Equal((2, 2), (badRepo.Code, failed.Code));
        Assert.Contains("'nope' is not one", badRepo.Err, StringComparison.Ordinal);
        Assert.Contains(Refused, failed.Err, StringComparison.Ordinal);
        Assert.Empty(github.Writes);
    }

    [Fact]
    public async Task A_dry_run_on_a_replied_and_resolved_thread_skips_both()
    {
        var github = GitHub();
        await Resolve(github, "--sha", Fix);

        var result = await Resolve(github, "--sha", Fix, "--dry-run");

        Assert.Contains("would skip the reply (already posted) then skip resolving (already resolved).", result.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resolve_without_sha_is_a_usage_error()
    {
        var result = await Run(GitHub(), "resolve", "7", "PRRT_open", "--repo", Repo);

        Assert.Equal(2, result.Code);
        Assert.Contains("--sha", result.Err, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_note_travels_as_a_variable_not_in_the_query()
    {
        var github = GitHub();
        const string note = "Quotes \" and $vars and } braces";

        await Resolve(github, "--sha", Fix, "--note", note);

        var reply = github.Bodies.Select(body => JsonNode.Parse(body)!).Single(body => body["variables"]!["body"] is not null);
        Assert.DoesNotContain("braces", reply["query"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.EndsWith(note, reply["variables"]!["body"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    // -- summaries ----------------------------------------------------------------------

    public static TheoryData<string, string> Summaries => new()
    {
        { CodexBody, "[P1] Use the portable helper for the dangling file symlink" },
        {
            "_⚠️ Potential issue_ | _🟠 Major_\n\n**Guard against a missing cursor.**\n\nDetails...\n\n"
            + "<details>\n<summary>🤖 Prompt for AI Agents</summary>\n\nfix it\n</details>",
            "⚠️ Potential issue | 🟠 Major — Guard against a missing cursor."
        },
        {
            "Keep `snake_case_name` and __init__ intact? No: `x_y` stays.",
            "Keep `snake_case_name` and init intact? No: `x_y` stays."
        },
        { "<!-- only a marker -->", string.Empty },
        { string.Concat(Enumerable.Repeat("word ", 40)), string.Concat(Enumerable.Repeat("word ", 24)).Trim() + "…" },
    };

    [Theory]
    [MemberData(nameof(Summaries))]
    public void Summaries_strip_badges_html_and_emphasis(string body, string summary) =>
        Assert.Equal(summary, ThreadModel.Summarise(body));

    [Fact]
    public void A_summary_cut_counts_an_emoji_as_one_character()
    {
        var summary = ThreadModel.Summarise(string.Concat(Enumerable.Repeat("👍", 130)));

        Assert.Equal(string.Concat(Enumerable.Repeat("👍", 119)) + "…", summary);
    }
}
