using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Rig.Core.Processes;

namespace Rig.Core.GitHub;

/// <summary>A repository as GitHub names it now.</summary>
/// <param name="Owner">The owner, as queried.</param>
/// <param name="CanonicalName">The repository's current name.</param>
/// <param name="NameWithOwner">GitHub's <c>owner/name</c>.</param>
/// <param name="QueriedName">The name it was asked for by, which GitHub redirects when the repository was renamed.</param>
/// <param name="IsArchived">Whether it is archived.</param>
/// <param name="Url">Its web address, when GitHub gave one.</param>
public sealed record CanonicalRepo(string Owner, string CanonicalName, string NameWithOwner, string QueriedName, bool IsArchived = false, Uri? Url = null)
{
    /// <summary>Whether it was asked for by a name other than its current one.</summary>
    public bool IsRenamed => !QueriedName.Equals(CanonicalName, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Resolves repositories to their current GitHub names in one GraphQL call, through a disk cache. GraphQL has no
/// <c>previousNames</c>, so a rename shows as <c>repository(owner, name)</c> answering with a different name.
/// The cache keeps the format the Python resolver wrote, so caches already on disk stay valid:
/// <c>{"repos": {"owner/name": {owner, canonical_name, …, fetched_at}}}</c> with an ISO timestamp.
/// </summary>
/// <param name="github">The GitHub API.</param>
/// <param name="cacheFile">Where resolved names are cached.</param>
/// <param name="time">The clock the cache's age is measured by.</param>
public sealed class RepoResolver(IGitHubApi github, string cacheFile, TimeProvider time)
{
    /// <summary>How long a cached name is trusted.</summary>
    public static readonly TimeSpan Ttl = TimeSpan.FromHours(24);

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>The cache under <paramref name="home"/>.</summary>
    public static string DefaultCacheFile(string home) => Path.Join(home, ".estate-repo-names.json");

    /// <summary>
    /// Each slug's current repository, or null when GitHub does not know it. Fresh cache entries answer without a
    /// call; the rest go in one batched query, and what it resolves is cached.
    /// </summary>
    public async Task<IReadOnlyDictionary<RepoSlug, CanonicalRepo?>> ResolveAsync(IReadOnlyList<RepoSlug> slugs, bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(slugs);
        var now = time.GetUtcNow();
        var cache = forceRefresh ? [] : await ReadCacheAsync(cacheFile, cancellationToken).ConfigureAwait(false);
        var resolved = new Dictionary<RepoSlug, CanonicalRepo?>();
        var missing = new List<RepoSlug>();
        foreach (var slug in slugs.Distinct())
        {
            if (cache.TryGetValue(slug.Key, out var hit) && now - hit.FetchedAt < Ttl)
            {
                resolved[slug] = hit.Repo;
            }
            else
            {
                missing.Add(slug);
            }
        }

        if (missing.Count == 0)
        {
            return resolved;
        }

        // A repository GitHub cannot find makes gh exit non-zero with the rest of the answer still on stdout.
        var ran = await github.CallAsync(BatchQuery(missing), cancellationToken).ConfigureAwait(false);
        var fetched = ParseBatch(ran.Out, missing);
        foreach (var slug in missing)
        {
            var repo = fetched.GetValueOrDefault(slug);
            resolved[slug] = repo;
            if (repo is not null)
            {
                cache[slug.Key] = new CacheEntry(repo, now);
            }
        }

        if (fetched.Count > 0)
        {
            await WriteCacheAsync(cacheFile, cache, cancellationToken).ConfigureAwait(false);
        }

        return resolved;
    }

    /// <summary>One GraphQL query asking for every slug, aliased <c>r0</c>, <c>r1</c>, … with the names as variables.</summary>
    public static ApiRequest BatchQuery(IReadOnlyList<RepoSlug> slugs)
    {
        ArgumentNullException.ThrowIfNull(slugs);
        var variables = new Dictionary<string, object?>(StringComparer.Ordinal);
        var declarations = new StringBuilder();
        var lookups = new StringBuilder();
        for (var i = 0; i < slugs.Count; i++)
        {
            variables[$"o{i}"] = slugs[i].Owner;
            variables[$"n{i}"] = slugs[i].Name;
            _ = declarations.Append(CultureInfo.InvariantCulture, $"{(i == 0 ? string.Empty : ", ")}$o{i}: String!, $n{i}: String!");
            _ = lookups.Append(CultureInfo.InvariantCulture, $"  r{i}: repository(owner: $o{i}, name: $n{i}) {{ name nameWithOwner isArchived url }}\n");
        }

        return ApiRequest.GraphQL($"query({declarations}) {{\n{lookups}}}", variables);
    }

    /// <summary>The repositories a <see cref="BatchQuery"/> answer names; a slug GitHub did not find is left out.</summary>
    public static IReadOnlyDictionary<RepoSlug, CanonicalRepo> ParseBatch(string json, IReadOnlyList<RepoSlug> slugs)
    {
        ArgumentNullException.ThrowIfNull(slugs);
        var found = new Dictionary<RepoSlug, CanonicalRepo>();
        if (Parse(json)?["data"] is not JsonObject data)
        {
            return found;
        }

        for (var i = 0; i < slugs.Count; i++)
        {
            if (data[$"r{i}"] is JsonObject node && Text(node, "name") is { Length: > 0 } name)
            {
                var slug = slugs[i];
                found[slug] = new CanonicalRepo(
                    slug.Owner,
                    name,
                    Text(node, "nameWithOwner") is { Length: > 0 } full ? full : $"{slug.Owner}/{name}",
                    slug.Name,
                    Flag(node, "isArchived"),
                    Address(Text(node, "url")));
            }
        }

        return found;
    }

    private static async Task<Dictionary<string, CacheEntry>> ReadCacheAsync(string path, CancellationToken cancellationToken)
    {
        var entries = new Dictionary<string, CacheEntry>(StringComparer.Ordinal);
        string text;
        try
        {
            text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return entries;
        }

        if (Parse(text)?["repos"] is not JsonObject repos)
        {
            return entries;
        }

        foreach (var (key, value) in repos)
        {
            if (value is JsonObject item
                && Text(item, "canonical_name") is { Length: > 0 } name
                && DateTimeOffset.TryParse(Text(item, "fetched_at"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var fetchedAt))
            {
                var repo = new CanonicalRepo(
                    Text(item, "owner") ?? string.Empty,
                    name,
                    Text(item, "name_with_owner") ?? string.Empty,
                    Text(item, "queried_name") ?? string.Empty,
                    Flag(item, "is_archived"),
                    Address(Text(item, "url")));
                entries[key] = new CacheEntry(repo, fetchedAt);
            }
        }

        return entries;
    }

    // Python's datetime.isoformat() spelling, which its fromisoformat reads back.
    private static async Task WriteCacheAsync(string path, Dictionary<string, CacheEntry> entries, CancellationToken cancellationToken)
    {
        var repos = new JsonObject();
        foreach (var (key, entry) in entries)
        {
            repos[key] = new JsonObject
            {
                ["owner"] = entry.Repo.Owner,
                ["canonical_name"] = entry.Repo.CanonicalName,
                ["name_with_owner"] = entry.Repo.NameWithOwner,
                ["queried_name"] = entry.Repo.QueriedName,
                ["fetched_at"] = entry.FetchedAt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'+00:00'", CultureInfo.InvariantCulture),
                ["is_archived"] = entry.Repo.IsArchived,
                ["url"] = entry.Repo.Url?.ToString() ?? string.Empty,
            };
        }

        try
        {
            _ = Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            await File.WriteAllTextAsync(path, new JsonObject { ["repos"] = repos }.ToJsonString(Indented) + "\n", cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A cache that cannot be written only costs the next run a call.
        }
    }

    private static JsonNode? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonObject node, string name) =>
        node[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static Uri? Address(string? text) => Uri.TryCreate(text, UriKind.Absolute, out var address) ? address : null;

    private static bool Flag(JsonObject node, string name) =>
        node[name] is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;

    private sealed record CacheEntry(CanonicalRepo Repo, DateTimeOffset FetchedAt);
}
