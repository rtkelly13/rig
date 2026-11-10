using Rig.Cli.Gh.Threads;
using Rig.Core.GitHub;
using Rig.Core.Processes;

namespace Rig.Cli.Tests.Gh;

/// <summary>How the GraphQL client reads what GitHub (through <c>gh api graphql</c>) answers, failures first.</summary>
public sealed class ThreadsGitHubTests
{
    private static readonly RepoSlug Repo = new("octo", "repo");

    private sealed class Scripted(Func<ApiRequest, Ran> answer) : IGitHubApi
    {
        public int Calls { get; private set; }

        public Task<Ran> CallAsync(ApiRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(answer(request));
        }
    }

    private static ThreadsGitHub Client(Func<ApiRequest, Ran> answer) => new(new Scripted(answer));

    private static Ran Ok(string json) => new(0, json, string.Empty);

    private static async Task<string> Failure(Func<Task> call) => (await Assert.ThrowsAsync<GitHubException>(call)).Message;

    private static string ThreadsPage(string nodes, bool more, string? cursor) =>
        """{"data":{"repository":{"pullRequest":{"reviewThreads":{"pageInfo":{"hasNextPage":"""
        + (more ? "true" : "false")
        + ""","endCursor":"""
        + (cursor is null ? "null" : $"\"{cursor}\"")
        + """},"nodes":["""
        + nodes
        + "]}}}}}";

    [Theory]
    [InlineData(1, "", "gh: HTTP 502", "review threads of octo/repo#7: gh: HTTP 502")]
    [InlineData(4, "", "", "review threads of octo/repo#7: gh exited 4")]
    [InlineData(0, "not json", "", "review threads of octo/repo#7: unreadable response from GitHub")]
    [InlineData(0, """{"data":null}""", "", "review threads of octo/repo#7: unreadable response from GitHub")]
    [InlineData(0, """{"data":{"repository":{"pullRequest":{}}}}""", "", "review threads of octo/repo#7: response has no reviewThreads")]
    [InlineData(1, """{"errors":[{"message":"one"},{"message":""},{"message":"two"}]}""", "gh: one", "review threads of octo/repo#7: one; two")]
    public async Task A_failed_or_unreadable_call_says_what_went_wrong(int code, string stdout, string stderr, string message)
    {
        var github = Client(_ => new Ran(code, stdout, stderr));

        Assert.Equal(message, await Failure(() => github.FetchThreadsAsync(Repo, 7, TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task A_thread_missing_a_required_field_is_unreadable()
    {
        var github = Client(_ => Ok(ThreadsPage("""{"id":"PRRT_x","isResolved":false,"isOutdated":false}""", more: false, cursor: null)));

        Assert.EndsWith("unreadable response from GitHub", await Failure(() => github.FetchThreadsAsync(Repo, 7, TestContext.Current.CancellationToken)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_thread_with_no_comments_reads_as_a_ghost_with_an_empty_summary()
    {
        var github = Client(_ => Ok(ThreadsPage("""{"id":"PRRT_x","isResolved":false,"isOutdated":true,"path":"a.py","line":null,"originalLine":null}""", more: false, cursor: null)));

        var observed = Assert.Single(await github.FetchThreadsAsync(Repo, 7, TestContext.Current.CancellationToken));

        Assert.Equal(("ghost", string.Empty, string.Empty, null, "a.py"), (observed.Thread.Author, observed.Thread.Summary, observed.LatestBody, observed.Thread.Line, observed.Thread.Location));
    }

    [Fact]
    public async Task A_deleted_author_reads_as_ghost()
    {
        var github = Client(_ => Ok(ThreadsPage("""{"id":"PRRT_x","isResolved":false,"isOutdated":false,"path":"a.py","line":3,"first":{"nodes":[{"author":null,"body":"hi","url":"u"}]},"latest":{"nodes":[]}}""", more: false, cursor: null)));

        var observed = Assert.Single(await github.FetchThreadsAsync(Repo, 7, TestContext.Current.CancellationToken));

        Assert.Equal(("ghost", "hi"), (observed.Thread.Author, observed.LatestBody));
    }

    [Theory]
    [InlineData("c1")]
    [InlineData(null)]
    public async Task A_repeated_or_missing_cursor_stops_the_paging(string? cursor)
    {
        var github = Client(_ => Ok(ThreadsPage(string.Empty, more: true, cursor)));

        var message = await Failure(() => github.FetchThreadsAsync(Repo, 7, TestContext.Current.CancellationToken));

        Assert.Contains("repeated or dropped a page cursor", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Paging_stops_after_the_page_limit()
    {
        var page = 0;
        var api = new Scripted(_ => Ok(ThreadsPage(string.Empty, more: true, $"c{page++}")));

        var message = await Failure(() => new ThreadsGitHub(api).FetchThreadsAsync(Repo, 7, TestContext.Current.CancellationToken));

        Assert.Contains($"more than {ThreadsGitHub.MaxPages} pages", message, StringComparison.Ordinal);
        Assert.Equal(ThreadsGitHub.MaxPages, api.Calls);
    }

    [Fact]
    public async Task A_pull_request_without_commits_is_unreadable()
    {
        var github = Client(_ => Ok("""{"data":{"repository":{"pullRequest":{"headRefName":"x"}}}}"""));

        Assert.Equal(
            "commits of octo/repo#7: response has no commits",
            await Failure(() => github.FetchPullRequestAsync(Repo, 7, TestContext.Current.CancellationToken)));
    }

    [Theory]
    [InlineData("""{"data":{}}""")]
    [InlineData("""{"data":{"addPullRequestReviewThreadReply":{}}}""")]
    [InlineData("""{"data":{"addPullRequestReviewThreadReply":{"comment":null}}}""")]
    public async Task A_reply_without_the_new_comment_is_an_error(string json)
    {
        var github = Client(_ => Ok(json));

        Assert.Equal(
            "reply on PRRT_x: GitHub did not return the new comment",
            await Failure(() => github.ReplyAsync("PRRT_x", "Fixed.", TestContext.Current.CancellationToken)));
    }

    [Theory]
    [InlineData("""{"data":{}}""")]
    [InlineData("""{"data":{"resolveReviewThread":{}}}""")]
    [InlineData("""{"data":{"resolveReviewThread":{"thread":{"isResolved":false}}}}""")]
    public async Task A_resolve_github_does_not_confirm_is_an_error(string json)
    {
        var github = Client(_ => Ok(json));

        Assert.Equal(
            "resolve PRRT_x: GitHub did not report the thread resolved",
            await Failure(() => github.ResolveAsync("PRRT_x", TestContext.Current.CancellationToken)));
    }
}
