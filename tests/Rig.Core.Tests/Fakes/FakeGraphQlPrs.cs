using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Rig.Core.Processes;

namespace Rig.Core.Tests.Fakes;

/// <summary>One repository as GitHub would answer for it.</summary>
/// <param name="States">PR number to <c>OPEN</c>, <c>MERGED</c> or <c>CLOSED</c>; any other number is not found.</param>
/// <param name="OpenNodes">The open-PR listing's nodes, as JSON.</param>
/// <param name="PageSize">Nodes per page of the listing.</param>
/// <param name="Fail">Fails the whole repository with this message.</param>
public sealed record FakePrRepo(
    IReadOnlyDictionary<int, string>? States = null,
    IReadOnlyList<JsonNode?>? OpenNodes = null,
    int PageSize = 100,
    string Fail = "");

/// <summary>
/// GitHub's GraphQL API as <see cref="Rig.Core.GitHub.PrResolver"/> queries it: the paged <c>open:</c> listing and
/// <c>prN: pullRequest(number:)</c> aliases, failing per alias the way <c>gh</c> does. Ported from
/// <c>tests/fakes/gh_runner.py</c>.
/// </summary>
public sealed partial class FakeGraphQlPrs(IReadOnlyDictionary<string, FakePrRepo> repos) : IGitHubApi
{
    /// <summary>Answers as if <c>gh</c> were not installed.</summary>
    public bool Missing { get; init; }

    /// <summary>Every query document sent, in order.</summary>
    public List<string> Queries { get; } = [];

    /// <summary>Every request's variables, in order.</summary>
    public List<JsonObject> Variables { get; } = [];

    /// <inheritdoc/>
    public Task<Ran> CallAsync(ApiRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Missing)
        {
            return Task.FromResult(new Ran(127, string.Empty, "gh is not on PATH"));
        }

        var body = JsonNode.Parse(request.Body!)!.AsObject();
        var query = body["query"]!.GetValue<string>();
        var variables = body["variables"]!.AsObject();
        Queries.Add(query);
        Variables.Add(variables);
        return Task.FromResult(Answer(query, variables));
    }

    private static Ran RepoError(string message)
    {
        var error = new JsonObject { ["type"] = "NOT_FOUND", ["path"] = new JsonArray("repository"), ["message"] = message };
        var body = new JsonObject { ["data"] = new JsonObject { ["repository"] = null }, ["errors"] = new JsonArray(error) };
        return new Ran(1, body.ToJsonString(), $"gh: {message}");
    }

    private Ran Answer(string query, JsonObject variables)
    {
        var slug = $"{variables["owner"]!.GetValue<string>()}/{variables["name"]!.GetValue<string>()}";
        if (!repos.TryGetValue(slug, out var repo))
        {
            return RepoError($"Could not resolve to a Repository with the name '{slug}'.");
        }

        if (repo.Fail.Length > 0)
        {
            return RepoError(repo.Fail);
        }

        var data = new JsonObject();
        var errors = new JsonArray();
        if (query.Contains("open: pullRequests", StringComparison.Ordinal))
        {
            var nodes = repo.OpenNodes ?? [];
            var start = variables["after"] is { } after ? int.Parse(after.GetValue<string>(), CultureInfo.InvariantCulture) : 0;
            var end = start + repo.PageSize;
            var info = new JsonObject { ["hasNextPage"] = end < nodes.Count, ["endCursor"] = end.ToString(CultureInfo.InvariantCulture) };
            data["open"] = new JsonObject
            {
                ["pageInfo"] = info,
                ["nodes"] = new JsonArray([.. nodes.Skip(start).Take(repo.PageSize).Select(node => node?.DeepClone())]),
            };
        }

        foreach (Match match in Lookup().Matches(query))
        {
            var number = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            var alias = $"pr{number}";
            if (repo.States?.GetValueOrDefault(number) is { } state)
            {
                data[alias] = new JsonObject { ["number"] = number, ["state"] = state, ["merged"] = state == "MERGED" };
            }
            else
            {
                data[alias] = null;
                errors.Add(new JsonObject
                {
                    ["type"] = "NOT_FOUND",
                    ["path"] = new JsonArray("repository", alias),
                    ["message"] = $"Could not resolve to a PullRequest with the number of {number}.",
                });
            }
        }

        var reply = new JsonObject { ["data"] = new JsonObject { ["repository"] = data } };
        if (errors.Count > 0)
        {
            reply["errors"] = errors;
        }

        // Like gh: exit 1 when any alias failed, with the data still on stdout.
        var stderr = errors.Count > 0 ? $"gh: {errors[0]!["message"]}\n" : string.Empty;
        return new Ran(errors.Count > 0 ? 1 : 0, reply.ToJsonString(), stderr);
    }

    [GeneratedRegex(@"pr(\d+): pullRequest")]
    private static partial Regex Lookup();
}
