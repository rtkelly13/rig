using System.Text.Json.Serialization;
using Rig.Core.GitHub;
using Rig.Core.Processes;

namespace Rig.Cli.Gh.Threads;

/// <summary>A GraphQL call failed or answered with something this module cannot read.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1032", Justification = "Always carries the message the command prints.")]
internal sealed class GitHubException : GitHubApiException
{
    public GitHubException(string message)
        : base(message)
    {
    }

    public GitHubException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The GraphQL calls behind <c>rig gh threads</c>, through the <see cref="IGitHubApi"/> port. Variables travel in the
/// JSON body (<see cref="ApiRequest.GraphQL"/>), never spliced into the query text, so a reply note can hold any characters.
/// </summary>
internal sealed class ThreadsGitHub(IGitHubApi api)
{
    /// <summary>Stop paginating after this many pages, so a cursor GitHub keeps repeating cannot loop forever.</summary>
    public const int MaxPages = 50;

    public const string ThreadsQuery = """
        query ReviewThreads($owner: String!, $name: String!, $number: Int!, $cursor: String) {
          repository(owner: $owner, name: $name) {
            pullRequest(number: $number) {
              reviewThreads(first: 100, after: $cursor) {
                pageInfo { hasNextPage endCursor }
                nodes {
                  id
                  isResolved
                  isOutdated
                  path
                  line
                  originalLine
                  first: comments(first: 1) { nodes { author { login } body url } }
                  latest: comments(last: 1) { nodes { body } }
                }
              }
            }
          }
        }
        """;

    public const string CommitsQuery = """
        query PullRequestCommits($owner: String!, $name: String!, $number: Int!, $cursor: String) {
          repository(owner: $owner, name: $name) {
            pullRequest(number: $number) {
              headRefName
              headRefOid
              commits(first: 100, after: $cursor) {
                pageInfo { hasNextPage endCursor }
                nodes { commit { oid } }
              }
            }
          }
        }
        """;

    public const string ReplyMutation = """
        mutation ReplyToThread($thread: ID!, $body: String!) {
          addPullRequestReviewThreadReply(input: {pullRequestReviewThreadId: $thread, body: $body}) {
            comment { url }
          }
        }
        """;

    public const string ResolveMutation = """
        mutation ResolveThread($thread: ID!) {
          resolveReviewThread(input: {threadId: $thread}) {
            thread { id isResolved }
          }
        }
        """;

    /// <summary>Every review thread on the PR, resolved or not, across all pages.</summary>
    public async Task<IReadOnlyList<ObservedThread>> FetchThreadsAsync(RepoSlug repo, int number, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(repo);
        var what = $"review threads of {repo}#{number}";
        var nodes = await PagesAsync(
            async cursor =>
            {
                var data = await CallAsync(what, ThreadsQuery, Variables(repo, number, cursor), cancellationToken).ConfigureAwait(false);
                var threads = PullRequestOf(data, repo, number).ReviewThreads
                    ?? throw new GitHubException($"{what}: response has no reviewThreads");
                return (threads.Nodes, threads.PageInfo);
            },
            what).ConfigureAwait(false);
        return [.. nodes.Select(Observed)];
    }

    /// <summary>The PR's head branch and all of its commits.</summary>
    public async Task<PullRequestCommits> FetchPullRequestAsync(RepoSlug repo, int number, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(repo);
        var what = $"commits of {repo}#{number}";
        WirePullRequest? head = null;
        var nodes = await PagesAsync(
            async cursor =>
            {
                var data = await CallAsync(what, CommitsQuery, Variables(repo, number, cursor), cancellationToken).ConfigureAwait(false);
                var pr = PullRequestOf(data, repo, number);
                var commits = pr.Commits ?? throw new GitHubException($"{what}: response has no commits");
                head = pr;
                return (commits.Nodes, commits.PageInfo);
            },
            what).ConfigureAwait(false);
        return new PullRequestCommits(head!.HeadRefName, head.HeadRefOid, [.. nodes.Select(node => node.Commit.Oid)]);
    }

    /// <summary>Posts <paramref name="body"/> as a reply on the thread; returns the new comment's URL.</summary>
    public async Task<string> ReplyAsync(string threadId, string body, CancellationToken cancellationToken)
    {
        var what = $"reply on {threadId}";
        var data = await CallAsync(what, ReplyMutation, new Dictionary<string, object?> { ["thread"] = threadId, ["body"] = body }, cancellationToken)
            .ConfigureAwait(false);
        return data.AddReply?.Comment?.Url ?? throw new GitHubException($"{what}: GitHub did not return the new comment");
    }

    /// <summary>Marks the thread resolved.</summary>
    public async Task ResolveAsync(string threadId, CancellationToken cancellationToken)
    {
        var what = $"resolve {threadId}";
        var data = await CallAsync(what, ResolveMutation, new Dictionary<string, object?> { ["thread"] = threadId }, cancellationToken)
            .ConfigureAwait(false);
        if (data.Resolve?.Thread?.IsResolved != true)
        {
            throw new GitHubException($"{what}: GitHub did not report the thread resolved");
        }
    }

    private Task<WireData> CallAsync(
        string what, string query, IReadOnlyDictionary<string, object?> variables, CancellationToken cancellationToken) =>
        GraphQLCall.DataAsync<WireData>(api, what, query, variables, cancellationToken);

    private static WirePullRequest PullRequestOf(WireData data, RepoSlug repo, int number) =>
        data.Repository?.PullRequest ?? throw new GitHubException($"{repo}#{number}: no such pull request (or no access to it)");

    private static async Task<List<T>> PagesAsync<T>(Func<string?, Task<(List<T> Items, WirePageInfo Info)>> fetchPage, string what)
    {
        string? cursor = null;
        HashSet<string> seen = new(StringComparer.Ordinal);
        IReadOnlyList<T> items = [];
        for (var page = 0; page < MaxPages; page++)
        {
            var (pageItems, info) = await fetchPage(cursor).ConfigureAwait(false);
            items = [.. items, .. pageItems];
            if (!info.HasNextPage)
            {
                return [.. items];
            }

            cursor = info.EndCursor;
            if (string.IsNullOrEmpty(cursor) || !seen.Add(cursor))
            {
                throw new GitHubException($"{what}: GitHub repeated or dropped a page cursor; the list is incomplete");
            }
        }

        throw new GitHubException($"{what}: more than {MaxPages} pages; the list is incomplete");
    }

    private static Dictionary<string, object?> Variables(RepoSlug repo, int number, string? cursor) =>
        new() { ["owner"] = repo.Owner, ["name"] = repo.Name, ["number"] = number, ["cursor"] = cursor };

    private static ObservedThread Observed(WireThread node)
    {
        var first = node.First.Nodes.FirstOrDefault() ?? new WireComment();
        var latest = node.Latest.Nodes.FirstOrDefault() ?? first;
        var login = first.Author?.Login;
        var thread = new ReviewThread(
            node.Id,
            node.Path,
            node.Line ?? node.OriginalLine,
            node.IsOutdated,
            node.IsResolved,
            string.IsNullOrEmpty(login) ? "ghost" : login,
            ThreadModel.Summarise(first.Body),
            first.Url,
            first.Body);
        return new ObservedThread(thread, latest.Body);
    }

    // -- wire shapes: required members fail the decode, as pydantic did, and the call reports an unreadable response.

    private sealed record WirePageInfo(bool HasNextPage, string? EndCursor = null);

    private sealed record WireAuthor(string Login = "");

    private sealed record WireComment(WireAuthor? Author = null, string Body = "", string Url = "");

    private sealed record WireComments(List<WireComment> Nodes)
    {
        public WireComments()
            : this([])
        {
        }
    }

    private sealed record WireThread
    {
        public required string Id { get; init; }

        public required bool IsResolved { get; init; }

        public required bool IsOutdated { get; init; }

        public required string Path { get; init; }

        public int? Line { get; init; }

        public int? OriginalLine { get; init; }

        public WireComments First { get; init; } = new();

        public WireComments Latest { get; init; } = new();
    }

    private sealed record WireThreadPage(List<WireThread> Nodes, WirePageInfo PageInfo);

    private sealed record WireCommitRef(string Oid);

    private sealed record WireCommitNode(WireCommitRef Commit);

    private sealed record WireCommitPage(List<WireCommitNode> Nodes, WirePageInfo PageInfo);

    private sealed record WirePullRequest(
        WireThreadPage? ReviewThreads = null, string HeadRefName = "", string HeadRefOid = "", WireCommitPage? Commits = null);

    private sealed record WireRepository(WirePullRequest? PullRequest = null);

    private sealed record WireReplyComment(string Url = "");

    private sealed record WireReplyPayload(WireReplyComment? Comment = null);

    private sealed record WireResolvedThread(bool IsResolved);

    private sealed record WireResolvePayload(WireResolvedThread? Thread = null);

    private sealed record WireData(
        WireRepository? Repository = null,
        [property: JsonPropertyName("addPullRequestReviewThreadReply")] WireReplyPayload? AddReply = null,
        [property: JsonPropertyName("resolveReviewThread")] WireResolvePayload? Resolve = null);
}
