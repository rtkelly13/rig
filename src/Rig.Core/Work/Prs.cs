using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Rig.Core.GitHub;
using Rig.Core.Processes;
using Rig.Core.Text;

namespace Rig.Core.WorkItems;

/// <summary>One repo's PR lookup: its slug and the branches to ask about.</summary>
/// <param name="RepoPath">The clone's folder.</param>
/// <param name="Slug"><c>owner/name</c>; empty when the clone has no GitHub origin.</param>
/// <param name="Branches">The branches to look up.</param>
public sealed record PrRequest(string RepoPath, string Slug, IReadOnlyList<string> Branches);

/// <summary>What the PR lookups found, and how far each repo's answer can be trusted.</summary>
/// <param name="Prs">The newest PR per (repo, branch); a branch with none is absent.</param>
/// <param name="Lookups">Per repo path: ok, skipped or unavailable.</param>
/// <param name="Warnings">Why an answer is unavailable, for stderr.</param>
public sealed record PrResults(
    IReadOnlyDictionary<BranchKey, PrInfo> Prs,
    IReadOnlyDictionary<string, PrLookup> Lookups,
    IReadOnlyList<string> Warnings)
{
    /// <summary>How many repos answered from the cache.</summary>
    public int Cached { get; init; }

    /// <summary>How many repos answered from the network.</summary>
    public int Fetched { get; init; }

    /// <summary>An answer that asked nothing, as <c>--offline</c> produces.</summary>
    public static PrResults None() => new(
        new Dictionary<BranchKey, PrInfo>(BranchKeyComparer.Instance),
        new Dictionary<string, PrLookup>(WorkPaths.Comparer),
        []);
}

/// <summary>
/// The newest PR for each work-item branch: one aliased GraphQL query per repo, cached briefly. Ported from
/// <c>desktop_manager/work/prs.py</c>. A failed call is never cached and never stops the command.
/// </summary>
/// <param name="github">Runs the GraphQL queries.</param>
/// <param name="cacheFile">The PR cache, or null to keep no cache.</param>
/// <param name="ttl">How long a cached answer stands.</param>
/// <param name="workers">How many repos are queried at once.</param>
public sealed class WorkPrSource(
    IGitHubApi github,
    string? cacheFile = null,
    TimeSpan? ttl = null,
    int workers = WorkPrSource.DefaultWorkers) : IWorkPrSource
{
    /// <summary>The default time a cached answer stands.</summary>
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(10);

    /// <summary>How many repos are queried at once.</summary>
    public const int DefaultWorkers = 8;

    /// <summary>The cache file's name in the home folder.</summary>
    public const string CacheFileName = ".work-items-prs.json";

    private readonly TimeSpan _ttl = ttl ?? DefaultTtl;

    /// <summary>The cache file under <paramref name="home"/>.</summary>
    /// <param name="home">The home folder.</param>
    public static string CacheFileUnder(string home) => Path.Join(home, CacheFileName);

    /// <summary>One query, aliased <c>b0..bN</c> in the order of <paramref name="branches"/>.</summary>
    /// <param name="slug"><c>owner/name</c>.</param>
    /// <param name="branches">The branches to look up.</param>
    public static string BuildQuery(string slug, IReadOnlyList<string> branches)
    {
        ArgumentNullException.ThrowIfNull(slug);
        ArgumentNullException.ThrowIfNull(branches);
        var slash = slug.IndexOf('/', StringComparison.Ordinal);
        var owner = slash < 0 ? slug : slug[..slash];
        var name = slash < 0 ? string.Empty : slug[(slash + 1)..];
        var lookups = string.Join(
            "\n",
            branches.Select((branch, index) =>
                $"    b{index}: pullRequests(headRefName: {JsonQuote(branch)}, first: 1, "
                + "orderBy: {field: CREATED_AT, direction: DESC}) { nodes { number state isDraft url updatedAt } }"));
        return $"query {{\n  repository(owner: {JsonQuote(owner)}, name: {JsonQuote(name)}) {{\n{lookups}\n  }}\n}}";
    }

    /// <summary><c>{branch: newest PR or null}</c>, or null when the response is unusable.</summary>
    /// <param name="stdout">The GraphQL reply.</param>
    /// <param name="branches">The branches asked about, in alias order.</param>
    public static IReadOnlyDictionary<string, PrInfo?>? ParseResponse(string stdout, IReadOnlyList<string> branches)
    {
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(branches);
        try
        {
            if (JsonNode.Parse(stdout) is not JsonObject document
                || document["data"] is not JsonObject data
                || data["repository"] is not JsonObject repository)
            {
                return null;
            }

            var answers = new Dictionary<string, PrInfo?>(StringComparer.Ordinal);
            for (var index = 0; index < branches.Count; index++)
            {
                var branch = branches[index];
                if (!repository.TryGetPropertyValue($"b{index}", out var connection) || connection is null)
                {
                    answers[branch] = null;
                    continue;
                }

                if (connection is not JsonObject connectionObject)
                {
                    return null;
                }

                if (!connectionObject.TryGetPropertyValue("nodes", out var nodes))
                {
                    answers[branch] = null;
                    continue;
                }

                if (nodes is not JsonArray array)
                {
                    return null;
                }

                if (array.Count == 0 || array[0] is null)
                {
                    answers[branch] = null;
                    continue;
                }

                if (array[0] is not JsonObject node)
                {
                    return null;
                }

                answers[branch] = NodeInfo(node);
            }

            return answers;
        }
        catch (Exception error) when (error is FormatException or JsonException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<PrResults> FetchAsync(
        IReadOnlyList<PrRequest> requests,
        bool refresh,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requests);
        var cache = ReadCache();
        var answered = new Dictionary<string, CachedRepo>(StringComparer.Ordinal);
        var toFetch = new List<PrRequest>();
        var warnings = new List<string>();
        var lookups = new Dictionary<string, PrLookup>(WorkPaths.Comparer);
        foreach (var request in requests)
        {
            if (request.Slug.Length == 0)
            {
                lookups[request.RepoPath] = PrLookup.Unavailable;
                warnings.Add($"{Path.GetFileName(request.RepoPath.TrimEnd(Path.DirectorySeparatorChar))}: origin is not a GitHub remote");
                continue;
            }

            if (!refresh && cache.TryGetValue(request.Slug, out var hit) && hit.Answers(request.Branches, now, _ttl))
            {
                answered[request.Slug] = hit;
            }
            else
            {
                toFetch.Add(request);
            }
        }

        var fetched = new IReadOnlyDictionary<string, PrInfo?>?[toFetch.Count];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, toFetch.Count),
            new ParallelOptions { MaxDegreeOfParallelism = workers, CancellationToken = cancellationToken },
            async (index, token) => fetched[index] = await FetchOneAsync(toFetch[index], token).ConfigureAwait(false)).ConfigureAwait(false);

        var prs = new Dictionary<BranchKey, PrInfo>(BranchKeyComparer.Instance);
        for (var index = 0; index < toFetch.Count; index++)
        {
            var request = toFetch[index];
            if (fetched[index] is not { } result)
            {
                lookups[request.RepoPath] = PrLookup.Unavailable;
                warnings.Add($"{request.Slug}: gh lookup failed (offline, unauthenticated or no access)");
                continue;
            }

            var entry = new CachedRepo(now, result);
            answered[request.Slug] = entry;
            cache[request.Slug] = entry;
        }

        foreach (var request in requests)
        {
            if (request.Slug.Length == 0 || !answered.TryGetValue(request.Slug, out var entry))
            {
                continue;
            }

            lookups[request.RepoPath] = PrLookup.Ok;
            foreach (var branch in request.Branches)
            {
                if (entry.Branches.TryGetValue(branch, out var pr) && pr is not null)
                {
                    prs[new BranchKey(request.RepoPath, branch)] = pr;
                }
            }
        }

        if (cacheFile is not null && toFetch.Count > 0)
        {
            WriteCache(cache);
        }

        return new PrResults(prs, lookups, warnings)
        {
            Cached = requests.Count - toFetch.Count - requests.Count(request => request.Slug.Length == 0),
            Fetched = fetched.Count(result => result is not null),
        };
    }

    /// <summary>Escapes a string as Python's <c>json.dumps</c> does by default: non-ASCII becomes <c>\uXXXX</c>.</summary>
    /// <param name="value">The text to quote.</param>
    internal static string JsonQuote(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var builder = new StringBuilder("\"");
        foreach (var character in value)
        {
            switch (character)
            {
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\b':
                    builder.Append("\\b");
                    break;
                case '\f':
                    builder.Append("\\f");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                default:
                    if (character < ' ' || character > '~')
                    {
                        builder.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(character);
                    }

                    break;
            }
        }

        return builder.Append('"').ToString();
    }

    private async Task<IReadOnlyDictionary<string, PrInfo?>?> FetchOneAsync(PrRequest request, CancellationToken cancellationToken)
    {
        var ran = await github.CallAsync(
            ApiRequest.GraphQL(BuildQuery(request.Slug, request.Branches), new Dictionary<string, object?>()),
            cancellationToken).ConfigureAwait(false);
        return ran.Ok ? ParseResponse(ran.Out, request.Branches) : null;
    }

    private static PrInfo? NodeInfo(JsonObject node)
    {
        var number = JsonShape.Int(node, "number");
        var state = JsonShape.String(node, "state");
        var draft = JsonShape.Bool(node, "isDraft");
        var url = JsonShape.String(node, "url");
        var updated = IsoTime.Parse(JsonShape.OptionalString(node, "updatedAt"));
        return state switch
        {
            "OPEN" => new PrInfo(number, draft ? PrStatus.Draft : PrStatus.Open, url, updated),
            "MERGED" => new PrInfo(number, PrStatus.Merged, url, updated),
            "CLOSED" => new PrInfo(number, PrStatus.Closed, url, updated),
            _ => null,
        };
    }

    private Dictionary<string, CachedRepo> ReadCache()
    {
        var entries = new Dictionary<string, CachedRepo>(StringComparer.Ordinal);
        if (cacheFile is null)
        {
            return entries;
        }

        string text;
        try
        {
            text = File.ReadAllText(cacheFile);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return entries;
        }

        try
        {
            if (JsonNode.Parse(text) is not JsonObject document)
            {
                return entries;
            }

            foreach (var (slug, value) in document)
            {
                if (value is not JsonObject entry
                    || IsoTime.Parse(JsonShape.OptionalString(entry, "fetched_at")) is not { } fetched)
                {
                    return new Dictionary<string, CachedRepo>(StringComparer.Ordinal);
                }

                var branches = new Dictionary<string, PrInfo?>(StringComparer.Ordinal);
                if (entry.TryGetPropertyValue("branches", out var branchesNode))
                {
                    if (branchesNode is not JsonObject branchesObject)
                    {
                        return new Dictionary<string, CachedRepo>(StringComparer.Ordinal);
                    }

                    foreach (var (branch, prNode) in branchesObject)
                    {
                        if (prNode is null)
                        {
                            branches[branch] = null;
                            continue;
                        }

                        if (prNode is not JsonObject pr)
                        {
                            return new Dictionary<string, CachedRepo>(StringComparer.Ordinal);
                        }

                        var status = JsonShape.String(pr, "status") switch
                        {
                            "open" => PrStatus.Open,
                            "draft" => PrStatus.Draft,
                            "merged" => PrStatus.Merged,
                            "closed" => PrStatus.Closed,
                            _ => throw new FormatException("expected a PR status"),
                        };
                        branches[branch] = new PrInfo(
                            JsonShape.Int(pr, "number"),
                            status,
                            JsonShape.String(pr, "url"),
                            IsoTime.Parse(JsonShape.OptionalString(pr, "updated_at")));
                    }
                }

                entries[slug] = new CachedRepo(fetched, branches);
            }

            return entries;
        }
        catch (Exception error) when (error is FormatException or JsonException)
        {
            return new Dictionary<string, CachedRepo>(StringComparer.Ordinal);
        }
    }

    private void WriteCache(IReadOnlyDictionary<string, CachedRepo> entries)
    {
        var payload = new JsonObject();
        foreach (var (slug, entry) in entries)
        {
            var branches = new JsonObject();
            foreach (var (branch, pr) in entry.Branches)
            {
                branches[branch] = pr is null
                    ? null
                    : new JsonObject
                    {
                        ["number"] = pr.Number,
                        ["status"] = pr.Status.Wire(),
                        ["url"] = pr.Url,
                        ["updated_at"] = IsoTime.Format(pr.UpdatedAt),
                    };
            }

            payload[slug] = new JsonObject
            {
                ["fetched_at"] = IsoTime.Format(entry.FetchedAt),
                ["branches"] = branches,
            };
        }

        try
        {
            _ = Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(cacheFile!))!);
            File.WriteAllText(cacheFile!, JsonOutput.Format(payload) + "\n");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // An unwritable cache only costs the next run a lookup.
        }
    }

    private sealed record CachedRepo(DateTimeOffset FetchedAt, IReadOnlyDictionary<string, PrInfo?> Branches)
    {
        public bool Answers(IReadOnlyList<string> wanted, DateTimeOffset now, TimeSpan ttl) =>
            now - FetchedAt < ttl && wanted.All(Branches.ContainsKey);
    }
}
