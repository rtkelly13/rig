using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Rig.Core.Processes;

namespace Rig.Cli.Tests.Fakes;

/// <summary>A review thread the fake holds. Replies are appended as the reply mutation runs.</summary>
public sealed class FakeThread(string id, string path, int? line, string body)
{
    public string Id { get; } = id;

    public string Path { get; } = path;

    public int? Line { get; } = line;

    public string Body { get; } = body;

    public string Author { get; init; } = FakeGraphQL.Codex;

    public bool Outdated { get; init; }

    public bool Resolved { get; set; }

    public List<string> Replies { get; } = [];
}

/// <summary>A pull request the fake holds: its head branch, its commits (head last) and its threads.</summary>
public sealed class FakePullRequest(string headRef, List<string> commits, List<FakeThread> threads)
{
    public string HeadRef { get; } = headRef;

    public List<string> Commits { get; } = commits;

    public List<FakeThread> Threads { get; } = threads;
}

/// <summary>One mutation the fake applied.</summary>
public sealed record FakeWrite(string Operation, string Thread, string Body = "");

/// <summary>
/// A stateful fake of GitHub's GraphQL API behind <see cref="IGitHubApi"/>. It answers the four operations
/// <c>rig gh threads</c> sends (by operation name), pages its connections, and applies the two mutations to its own
/// state, so a second run sees what the first one did. TEMPORARY with <c>rig gh threads</c>.
/// </summary>
public sealed partial class FakeGraphQL(Dictionary<string, FakePullRequest> pullRequests, int pageSize = 100) : IGitHubApi
{
    public const string Codex = "chatgpt-codex-connector";

    public Dictionary<string, FakePullRequest> PullRequests { get; } = pullRequests;

    /// <summary>Every operation name, in the order it was called.</summary>
    public List<string> Calls { get; } = [];

    /// <summary>Every mutation applied.</summary>
    public List<FakeWrite> Writes { get; } = [];

    /// <summary>Operation name to the GraphQL error to answer it with, as GitHub does for a refused call.</summary>
    public Dictionary<string, string> Failures { get; } = [];

    /// <summary>Every request body as sent, for checks on how variables travel.</summary>
    public List<string> Bodies { get; } = [];

    public Task<Ran> CallAsync(ApiRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        Assert.Equal(("POST", "graphql"), (request.Method, request.Route));
        Assert.NotNull(request.Body);
        Bodies.Add(request.Body);
        var body = JsonNode.Parse(request.Body)!.AsObject();
        var query = body["query"]!.GetValue<string>();
        var variables = body["variables"]!.AsObject();
        var operation = Operation().Match(query).Groups[1].Value;
        Assert.NotEmpty(operation);
        Calls.Add(operation);
        return Task.FromResult(Answer(operation, variables));
    }

    private Ran Answer(string operation, JsonObject variables)
    {
        if (Failures.TryGetValue(operation, out var message))
        {
            var errors = new JsonObject { ["errors"] = new JsonArray(new JsonObject { ["message"] = message }) };
            return new Ran(1, errors.ToJsonString(), $"gh: {message}\n");
        }

        switch (operation)
        {
            case "ReviewThreads":
                return Threads(variables);
            case "PullRequestCommits":
                return Commits(variables);
            case "ReplyToThread":
                {
                    var thread = Thread(variables);
                    var text = variables["body"]!.GetValue<string>();
                    thread.Replies.Add(text);
                    Writes.Add(new FakeWrite(operation, thread.Id, text));
                    var url = $"https://github.com/fake/pull/1#discussion_r{Writes.Count}";
                    return Ok(new JsonObject
                    {
                        ["addPullRequestReviewThreadReply"] = new JsonObject { ["comment"] = new JsonObject { ["url"] = url } },
                    });
                }

            case "ResolveThread":
                {
                    var thread = Thread(variables);
                    thread.Resolved = true;
                    Writes.Add(new FakeWrite(operation, thread.Id));
                    return Ok(new JsonObject
                    {
                        ["resolveReviewThread"] = new JsonObject
                        {
                            ["thread"] = new JsonObject { ["id"] = thread.Id, ["isResolved"] = true },
                        },
                    });
                }

            default:
                throw new InvalidOperationException($"unexpected operation {operation}");
        }
    }

    private FakePullRequest? PullRequest(JsonObject variables) =>
        PullRequests.GetValueOrDefault(
            $"{variables["owner"]!.GetValue<string>()}/{variables["name"]!.GetValue<string>()}#{variables["number"]!.GetValue<int>()}");

    private (List<T> Items, JsonObject Info) Page<T>(List<T> items, JsonObject variables)
    {
        var start = variables["cursor"] is { } cursor ? int.Parse(cursor.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture) : 0;
        var end = start + pageSize;
        var more = end < items.Count;
        var info = new JsonObject
        {
            ["hasNextPage"] = more,
            ["endCursor"] = more ? end.ToString(System.Globalization.CultureInfo.InvariantCulture) : null,
        };
        return ([.. items.Skip(start).Take(pageSize)], info);
    }

    private Ran Threads(JsonObject variables)
    {
        if (PullRequest(variables) is not { } pr)
        {
            return Ok(new JsonObject { ["repository"] = new JsonObject { ["pullRequest"] = null } });
        }

        var (nodes, info) = Page(pr.Threads, variables);
        var page = new JsonObject { ["pageInfo"] = info, ["nodes"] = new JsonArray([.. nodes.Select(ThreadNode)]) };
        return Ok(new JsonObject
        {
            ["repository"] = new JsonObject { ["pullRequest"] = new JsonObject { ["reviewThreads"] = page } },
        });
    }

    private Ran Commits(JsonObject variables)
    {
        if (PullRequest(variables) is not { } pr)
        {
            return Ok(new JsonObject { ["repository"] = new JsonObject { ["pullRequest"] = null } });
        }

        var (nodes, info) = Page(pr.Commits, variables);
        var commits = new JsonArray([.. nodes.Select(oid => (JsonNode)new JsonObject { ["commit"] = new JsonObject { ["oid"] = oid } })]);
        return Ok(new JsonObject
        {
            ["repository"] = new JsonObject
            {
                ["pullRequest"] = new JsonObject
                {
                    ["headRefName"] = pr.HeadRef,
                    ["headRefOid"] = pr.Commits[^1],
                    ["commits"] = new JsonObject { ["pageInfo"] = info, ["nodes"] = commits },
                },
            },
        });
    }

    private FakeThread Thread(JsonObject variables)
    {
        var id = variables["thread"]!.GetValue<string>();
        return PullRequests.Values.SelectMany(pr => pr.Threads).FirstOrDefault(thread => thread.Id == id)
            ?? throw new InvalidOperationException($"mutation on unknown thread {id}");
    }

    private static JsonNode ThreadNode(FakeThread thread)
    {
        var first = new JsonObject
        {
            ["author"] = new JsonObject { ["login"] = thread.Author },
            ["body"] = thread.Body,
            ["url"] = $"https://github.com/c/{thread.Id}",
        };
        return new JsonObject
        {
            ["id"] = thread.Id,
            ["isResolved"] = thread.Resolved,
            ["isOutdated"] = thread.Outdated,
            ["path"] = thread.Path,
            ["line"] = thread.Outdated ? null : thread.Line,
            ["originalLine"] = thread.Line,
            ["first"] = new JsonObject { ["nodes"] = new JsonArray(first) },
            ["latest"] = new JsonObject
            {
                ["nodes"] = new JsonArray(new JsonObject { ["body"] = thread.Replies.Count > 0 ? thread.Replies[^1] : thread.Body }),
            },
        };
    }

    private static Ran Ok(JsonObject data) => new(0, new JsonObject { ["data"] = data }.ToJsonString(), string.Empty);

    [GeneratedRegex(@"\b(?:query|mutation)\s+(\w+)")]
    private static partial Regex Operation();
}
