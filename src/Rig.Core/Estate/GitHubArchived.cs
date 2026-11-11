using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Rig.Core.Processes;

namespace Rig.Core.Estate;

/// <summary>
/// Which estate repos GitHub reports as archived, in one GraphQL query for all of them. Ported from
/// <c>src/rig/estate/github_archived.py</c>.
/// </summary>
/// <remarks>
/// An archived repo is finished: it does not belong in Orca, and a local clone of it is something to clean up.
/// <c>config/archived-projects.json</c> lists retired projects, but GitHub is the authority, so <c>estate orca</c> asks it
/// too. A repo GitHub does not return (deleted, or no access) is simply absent from the answer: deleted is not archived,
/// and only a human should decide about it.
/// </remarks>
public static partial class GitHubArchived
{
    /// <summary>One aliased <c>repository</c> lookup per valid <c>owner/name</c>; empty when none is valid.</summary>
    /// <remarks>Only GitHub owner and repo names are sent, so a config value cannot shape the query.</remarks>
    public static string BuildQuery(IEnumerable<string> slugs)
    {
        var parts = new List<string>();
        var index = 0;
        foreach (var slug in slugs.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var cut = slug.IndexOf('/', StringComparison.Ordinal);
            var owner = cut < 0 ? slug : slug[..cut];
            var name = cut < 0 ? string.Empty : slug[(cut + 1)..];
            if (Name().IsMatch(owner) && Name().IsMatch(name))
            {
                parts.Add($"r{index}: repository(owner: \"{owner}\", name: \"{name}\") {{ nameWithOwner isArchived }}");
            }

            index++;
        }

        return parts.Count > 0 ? "query { " + string.Join(' ', parts) + " }" : string.Empty;
    }

    /// <summary>The lowercase <c>owner/name</c> of each archived repo among <paramref name="slugs"/>; null when GitHub could not be asked.</summary>
    /// <remarks><c>gh api graphql</c> exits non-zero when any repo is missing but still prints the rest, so the exit code is not read.</remarks>
    public static async Task<IReadOnlySet<string>?> SlugsAsync(IGitHubApi github, IEnumerable<string> slugs, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(github);
        var query = BuildQuery(slugs);
        if (query.Length == 0)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        var ran = await github.CallAsync(ApiRequest.GraphQL(query, new Dictionary<string, object?>()), cancellationToken).ConfigureAwait(false);
        return ran.Out.Length > 0 ? Parse(ran.Out) : null;
    }

    /// <summary>The archived repos in a GraphQL reply, or null when it has no readable <c>data</c>.</summary>
    public static IReadOnlySet<string>? Parse(string reply)
    {
        try
        {
            if (JsonNode.Parse(reply) is not JsonObject root || root["data"] is not JsonObject data)
            {
                return null;
            }

            var archived = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (_, node) in data)
            {
                if (node is null)
                {
                    continue;
                }

                if (node is not JsonObject repository
                    || !TryField(repository, "nameWithOwner", JsonValueKind.String, out var name)
                    || !TryField(repository, "isArchived", JsonValueKind.True, out var flag))
                {
                    return null;
                }

                if (flag is not null && flag.GetValueKind() == JsonValueKind.True)
                {
                    _ = archived.Add((name?.GetValue<string>() ?? string.Empty).ToLowerInvariant());
                }
            }

            return archived;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // A field that is absent, or of the expected kind (a boolean when expecting True).
    private static bool TryField(JsonObject parent, string key, JsonValueKind kind, out JsonNode? value)
    {
        value = parent[key];
        if (!parent.ContainsKey(key))
        {
            return true;
        }

        var actual = value?.GetValueKind();
        return kind == JsonValueKind.True ? actual is JsonValueKind.True or JsonValueKind.False : actual == kind;
    }

    [GeneratedRegex(@"\A[A-Za-z0-9_.-]+\z")]
    private static partial Regex Name();
}
