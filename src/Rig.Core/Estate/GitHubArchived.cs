using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Rig.Core.Processes;
using Rig.Core.Text;

namespace Rig.Core.Estate;

/// <summary>What GitHub reports about the estate repos it was asked about.</summary>
/// <param name="Archived">The lowercase <c>owner/name</c> of each archived repo, as GitHub now names it.</param>
/// <param name="Renamed">Each asked-for lowercase <c>owner/name</c> that GitHub now serves under another name, mapped to that name.</param>
public sealed record GitHubRepos(IReadOnlySet<string> Archived, IReadOnlyDictionary<string, string> Renamed);

/// <summary>
/// Which estate repos GitHub reports as archived or renamed, in one GraphQL query for all of them.
/// </summary>
/// <remarks>
/// An archived repo is finished: it does not belong in Orca, and a local clone of it is something to clean up.
/// <c>config/archived-projects.json</c> lists retired projects, but GitHub is the authority, so <c>estate orca</c> asks it
/// too. A repo GitHub does not return (deleted, or no access) is simply absent from the answer: deleted is not archived,
/// and only a human should decide about it. A renamed or transferred repo answers under its new <c>nameWithOwner</c>,
/// because GitHub follows the redirect, so the same query shows which names have moved.
/// </remarks>
public static partial class GitHubArchived
{
    /// <summary>One aliased <c>repository</c> lookup per valid <c>owner/name</c>; empty when none is valid.</summary>
    /// <remarks>Only GitHub owner and repo names are sent, so a config value cannot shape the query.</remarks>
    public static string BuildQuery(IEnumerable<string> slugs)
    {
        var parts = new List<string>();
        var index = 0;
        foreach (var slug in Asked(slugs))
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
    public static async Task<IReadOnlySet<string>?> SlugsAsync(IGitHubApi github, IEnumerable<string> slugs, CancellationToken cancellationToken = default) =>
        (await ReposAsync(github, slugs, cancellationToken).ConfigureAwait(false))?.Archived;

    /// <summary>Which of <paramref name="slugs"/> are archived and which have been renamed; null when GitHub could not be asked.</summary>
    public static async Task<GitHubRepos?> ReposAsync(IGitHubApi github, IEnumerable<string> slugs, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(github);
        var asked = Asked(slugs);
        var query = BuildQuery(asked);
        if (query.Length == 0)
        {
            return new(new HashSet<string>(StringComparer.Ordinal), new Dictionary<string, string>(StringComparer.Ordinal));
        }

        var ran = await github.CallAsync(ApiRequest.GraphQL(query, new Dictionary<string, object?>()), cancellationToken).ConfigureAwait(false);
        return ran.Out.Length > 0 ? Parse(ran.Out, asked) : null;
    }

    /// <summary>The archived repos in a GraphQL reply, or null when it has no readable <c>data</c>.</summary>
    public static IReadOnlySet<string>? Parse(string reply) => Parse(reply, [])?.Archived;

    /// <summary>
    /// The archived and renamed repos in a GraphQL reply to <see cref="BuildQuery"/> for <paramref name="slugs"/>, or null
    /// when it has no readable <c>data</c>. Alias <c>rN</c> answers the Nth of <paramref name="slugs"/> in query order.
    /// </summary>
    public static GitHubRepos? Parse(string reply, IEnumerable<string> slugs)
    {
        var asked = Asked(slugs);
        try
        {
            if (JsonNode.Parse(reply) is not JsonObject root || root["data"] is not JsonObject data)
            {
                return null;
            }

            var archived = new HashSet<string>(StringComparer.Ordinal);
            var renamed = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (alias, node) in data)
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

                var current = name?.GetValue<string>() ?? string.Empty;
                if (flag is not null && flag.GetValueKind() == JsonValueKind.True)
                {
                    _ = archived.Add(current.ToLowerInvariant());
                }

                if (alias.StartsWith('r', StringComparison.Ordinal) && int.TryParse(alias.AsSpan(1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var index)
                    && index < asked.Count
                    && !asked[index].Equals(current, StringComparison.OrdinalIgnoreCase))
                {
                    renamed[asked[index].ToLowerInvariant()] = current;
                }
            }

            return new(archived, renamed);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // The slugs in the order BuildQuery numbers its aliases.
    private static List<string> Asked(IEnumerable<string> slugs) => [.. slugs.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    // A field that is present and of the expected kind (a boolean when expecting True): a repository missing either is unreadable.
    private static bool TryField(JsonObject parent, string key, JsonValueKind kind, out JsonNode? value)
    {
        value = JsonShape.Field(parent, key);
        if (!parent.ContainsKey(key))
        {
            return false;
        }

        var actual = value?.GetValueKind();
        return kind == JsonValueKind.True ? actual is JsonValueKind.True or JsonValueKind.False : actual == kind;
    }

    [GeneratedRegex(@"\A[A-Za-z0-9_.-]+\z")]
    private static partial Regex Name();
}
