using System.Text.Json.Nodes;
using Rig.Core.GitHub;
using Rig.Core.Processes;

namespace Rig.Core.Estate;

/// <summary>The newest PR that described a head ref.</summary>
/// <param name="Number">The PR number.</param>
/// <param name="State">GitHub's state: <c>OPEN</c>, <c>CLOSED</c> or <c>MERGED</c>.</param>
/// <param name="HeadRef">The head branch on the remote.</param>
/// <param name="HeadOid">The PR's last head commit; for a merged PR, what was merged.</param>
public sealed record PullRequest(int Number, string State, string HeadRef, string HeadOid = "")
{
    /// <summary>The PR merged.</summary>
    public bool Merged => State == "MERGED";
}

/// <summary>
/// What GitHub knows about a clone's branches, in as few calls as possible. Ported from <c>src/rig/estate/prs.py</c>.
/// </summary>
/// <remarks>
/// One GraphQL page answers number, state and head together, so the newest PR per head wins with its state attached. A
/// reused branch can carry an old merged PR and a new open one; keeping only a merged map would authorise deleting live
/// work. Two passes, because "most recently updated" and "mine" are different windows: on a repo with other
/// contributors the first can push out the user's older PRs, which are exactly the branches still in their clone.
/// </remarks>
/// <param name="git">Reads the clone's origin and refs.</param>
/// <param name="github">Runs the GraphQL queries.</param>
public sealed class PullRequests(Git git, IGitHubApi github)
{
    /// <summary>One GraphQL page; GitHub caps <c>first</c> at 100.</summary>
    public const int Page = 100;

    /// <summary>How many PRs each pass reads at most.</summary>
    public const int DefaultLimit = 200;

    private const string Recent = """
        query($owner: String!, $name: String!, $page: Int!, $after: String) {
          repository(owner: $owner, name: $name) {
            pullRequests(first: $page, after: $after, orderBy: {field: UPDATED_AT, direction: DESC}) {
              pageInfo { hasNextPage endCursor }
              nodes { number state headRefName headRefOid }
            }
          }
        }
        """;

    private const string Mine = """
        query($q: String!, $page: Int!, $after: String) {
          search(query: $q, type: ISSUE, first: $page, after: $after) {
            pageInfo { hasNextPage endCursor }
            nodes { ... on PullRequest { number state headRefName headRefOid } }
          }
        }
        """;

    /// <summary>The clone's origin as owner and name, or null when origin is not a GitHub remote.</summary>
    public async Task<RepoSlug?> OriginAsync(string repo, CancellationToken cancellationToken = default)
    {
        var ran = await git.RunAsync(repo, ["remote", "get-url", "origin"], TimeSpan.FromSeconds(10), cancellationToken: cancellationToken).ConfigureAwait(false);
        return ran.Ok ? RepoSlug.FromRemote(ran.Out.Trim()) : null;
    }

    /// <summary>The newest PR per head ref, both passes merged so the higher number always wins.</summary>
    /// <param name="repo">The clone.</param>
    /// <param name="limit">How many PRs each pass reads at most.</param>
    /// <param name="includeMine">Run the <c>author:@me</c> pass too.</param>
    /// <param name="cancellationToken">Cancels the calls.</param>
    public async Task<Dictionary<string, PullRequest>> HeadsAsync(string repo, int limit = DefaultLimit, bool includeMine = true, CancellationToken cancellationToken = default)
    {
        if (await OriginAsync(repo, cancellationToken).ConfigureAwait(false) is not { } slug)
        {
            return [];
        }

        var heads = await PagedAsync(
            (page, after) => ApiRequest.GraphQL(Recent, new Dictionary<string, object?> { ["owner"] = slug.Owner, ["name"] = slug.Name, ["page"] = page, ["after"] = after }),
            data => data["repository"]?["pullRequests"],
            limit,
            cancellationToken).ConfigureAwait(false);
        if (includeMine)
        {
            var query = $"repo:{slug.FullName} is:pr author:@me";
            var mine = await PagedAsync(
                (page, after) => ApiRequest.GraphQL(Mine, new Dictionary<string, object?> { ["q"] = query, ["page"] = page, ["after"] = after }),
                data => data["search"],
                limit,
                cancellationToken).ConfigureAwait(false);
            foreach (var pull in mine.Values)
            {
                Keep(heads, pull);
            }
        }

        return heads;
    }

    /// <summary>
    /// The head refs whose newest PR merged. A head whose newest PR is open is absent: the branch was reused, and the open
    /// PR is the live fact.
    /// </summary>
    public async Task<Dictionary<string, PullRequest>> MergedHeadsAsync(string repo, int limit = DefaultLimit, CancellationToken cancellationToken = default) =>
        (await HeadsAsync(repo, limit, cancellationToken: cancellationToken).ConfigureAwait(false))
            .Where(pair => pair.Value.Merged)
            .ToDictionary(StringComparer.Ordinal);

    /// <summary>
    /// The merged PR holding <paramref name="branch"/>'s tip, by the branch's own name, else by the origin branch it pushes
    /// to; null when none. The tip must be the PR's head or behind it: a commit made after the last push was never in the
    /// PR, and a squash leaves no other trace of it.
    /// </summary>
    /// <param name="git">Reads the branch's upstream and ancestry.</param>
    /// <param name="repo">The clone.</param>
    /// <param name="branch">The local branch.</param>
    /// <param name="mergedHeads">From <see cref="MergedHeadsAsync"/>; null when GitHub was not asked.</param>
    public static async Task<int?> MergedPrAsync(Git git, string repo, string branch, IReadOnlyDictionary<string, PullRequest>? mergedHeads)
    {
        ArgumentNullException.ThrowIfNull(git);
        if (mergedHeads is null)
        {
            return null;
        }

        if (!mergedHeads.TryGetValue(branch, out var pull))
        {
            var upstream = await git.OutAsync(repo, "for-each-ref", "--format=%(upstream:remotename)\t%(upstream:remoteref)", $"refs/heads/{branch}").ConfigureAwait(false);
            var tab = upstream.IndexOf('\t', StringComparison.Ordinal);
            var (remote, reference) = tab < 0 ? (upstream, string.Empty) : (upstream[..tab], upstream[(tab + 1)..]);
            if (remote != "origin" || !reference.StartsWith("refs/heads/", StringComparison.Ordinal)
                || !mergedHeads.TryGetValue(reference["refs/heads/".Length..], out pull))
            {
                return null;
            }
        }

        if (pull.HeadOid.Length == 0)
        {
            return null;
        }

        var contained = await git.RunAsync(repo, ["merge-base", "--is-ancestor", $"refs/heads/{branch}", pull.HeadOid]).ConfigureAwait(false);
        return contained.Ok ? pull.Number : null;
    }

    // Reads pages until the connection runs out or `limit` nodes are seen. A failed or unreadable page ends the pass.
    private async Task<Dictionary<string, PullRequest>> PagedAsync(
        Func<int, string?, ApiRequest> request,
        Func<JsonNode, JsonNode?> connectionOf,
        int limit,
        CancellationToken cancellationToken)
    {
        var found = new Dictionary<string, PullRequest>(StringComparer.Ordinal);
        string? cursor = null;
        for (var seen = 0; seen < limit; seen += Page)
        {
            var ran = await github.CallAsync(request(Math.Min(Page, limit - seen), cursor), cancellationToken).ConfigureAwait(false);
            if (!ran.Ok || Connection(ran.Out, connectionOf) is not { } connection)
            {
                break;
            }

            foreach (var node in connection["nodes"] as JsonArray ?? [])
            {
                if (Pull(node) is { } pull)
                {
                    Keep(found, pull);
                }
            }

            var info = connection["pageInfo"];
            cursor = Text(info?["endCursor"]);
            if (!(info?["hasNextPage"] is JsonValue more && more.TryGetValue<bool>(out var next) && next) || string.IsNullOrEmpty(cursor))
            {
                break;
            }
        }

        return found;
    }

    private static JsonNode? Connection(string output, Func<JsonNode, JsonNode?> connectionOf)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(output)?["data"] is { } data ? connectionOf(data) as JsonObject : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            // A node of the wrong kind (an array where an object belongs) reads as no page.
            return null;
        }
    }

    // A search hit that is an issue, not a PR, arrives as {} and is skipped.
    private static PullRequest? Pull(JsonNode? node) =>
        node is JsonObject pull && Text(pull["headRefName"]) is { Length: > 0 } head
            && pull["number"] is JsonValue number && number.TryGetValue<int>(out var value)
            ? new PullRequest(value, Text(pull["state"]) ?? string.Empty, head, Text(pull["headRefOid"]) ?? string.Empty)
            : null;

    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    // The newest PR number wins per head, bringing its own state.
    private static void Keep(Dictionary<string, PullRequest> into, PullRequest pull)
    {
        if (!into.TryGetValue(pull.HeadRef, out var existing) || pull.Number > existing.Number)
        {
            into[pull.HeadRef] = pull;
        }
    }
}
