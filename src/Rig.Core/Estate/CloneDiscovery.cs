using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Rig.Core.Roots;

namespace Rig.Core.Estate;

/// <summary>The clones found under a scan root, and whether they came from the cache.</summary>
/// <param name="Paths">Each clone's folder.</param>
/// <param name="FromCache">True when no scan ran.</param>
public sealed record Discovered(IReadOnlyList<string> Paths, bool FromCache);

/// <summary>
/// Finds the git clones under a code root cheaply, and remembers where they are. Ported from
/// <c>src/rig/estate/discovery.py</c>.
/// </summary>
/// <remarks>
/// <para>
/// The scan is bounded two levels deep and stops descending the moment a clone is found, so a code root holding twenty
/// clones costs twenty-odd stat calls rather than a walk of every <c>node_modules</c> beneath them.
/// </para>
/// <para>
/// A clone needs <c>.git</c> to be a directory. A linked worktree or a submodule carries a <c>.git</c> file, and neither
/// is a clone: <c>Parquet.SourceGenerator-golden</c>, a worktree living as its clone's sibling, was once counted as a
/// clone of its own and its 118 local-only commits added to the estate total twice.
/// </para>
/// <para>
/// The answer is cached in one file under the home folder, keyed by resolved root, for <see cref="DefaultTtl"/>. The
/// file's shape is the Python tool's, which shares it until the estate commands have all moved.
/// </para>
/// </remarks>
/// <param name="cacheFile">The cache file: <see cref="CacheFileUnder"/> in production, a temp path in tests.</param>
/// <param name="time">The clock a cached answer's age is read from.</param>
public sealed class CloneDiscovery(string cacheFile, TimeProvider time)
{
    /// <summary>The cache file's name in the home folder.</summary>
    public const string CacheFileName = ".estate-repos.json";

    /// <summary>How deep a scan looks: a clone at <c>root/foo</c> is depth 1.</summary>
    public const int DefaultMaxDepth = 2;

    /// <summary>How long a cached scan stands.</summary>
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromHours(12);

    // Never descended into: cheap to skip and never a code root. Hidden folders are skipped too.
    private static readonly HashSet<string> Skipped = new(StringComparer.Ordinal)
    {
        ".git", "node_modules", ".venv", "venv", "__pycache__", ".mypy_cache", ".pytest_cache", ".ruff_cache",
        "dist", "build", ".next", "target", "obj", "bin", "Library", ".Trash",
    };

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>The cache file.</summary>
    public string CacheFile => cacheFile;

    /// <summary>The cache file under <paramref name="home"/>.</summary>
    public static string CacheFileUnder(string home) => Path.Join(home, CacheFileName);

    /// <summary>Whether <paramref name="path"/> is a clone in its own right: its <c>.git</c> is a directory.</summary>
    public static bool IsRepo(string path) => Directory.Exists(Path.Join(path, ".git"));

    /// <summary>
    /// The clones under <paramref name="root"/>, at most <paramref name="maxDepth"/> levels down, in path order. A clone
    /// at the root itself ends the scan: there is nothing to look for inside a checkout.
    /// </summary>
    public static IReadOnlyList<string> Scan(string root, int maxDepth = DefaultMaxDepth)
    {
        var resolved = PathResolution.Resolve(root);
        if (!Directory.Exists(resolved))
        {
            return [];
        }

        if (IsRepo(resolved))
        {
            return [resolved];
        }

        var found = new List<string>();
        var frontier = new List<string> { resolved };
        for (var depth = 0; depth < maxDepth && frontier.Count > 0; depth++)
        {
            var next = new List<string>();
            foreach (var child in frontier.SelectMany(Children))
            {
                (IsRepo(child) ? found : next).Add(child);
            }

            frontier = next;
        }

        found.Sort(PathOrder.Instance);
        return found;
    }

    /// <summary>
    /// <paramref name="root"/>'s clones: from the cache while it is fresh, else from a scan that rewrites it. A clone
    /// deleted since the scan is dropped from a cached answer without a rescan, since deletion is far more common than a
    /// new clone appearing.
    /// </summary>
    /// <param name="root">The scan root.</param>
    /// <param name="refresh">Rescan whatever the cache holds.</param>
    /// <param name="ttl">How long a cached scan stands; <see cref="DefaultTtl"/> when null.</param>
    /// <param name="maxDepth">How deep a scan looks.</param>
    public Discovered Repos(string root, bool refresh = false, TimeSpan? ttl = null, int maxDepth = DefaultMaxDepth)
    {
        var key = PathResolution.Resolve(root);
        var cache = ReadCache();
        var now = time.GetUtcNow();
        if (!refresh && cache.TryGetValue(key, out var entry) && now - entry.ScannedAt < (ttl ?? DefaultTtl))
        {
            return new Discovered([.. entry.Repos.Where(Directory.Exists)], FromCache: true);
        }

        var found = Scan(root, maxDepth);
        cache[key] = new CachedScan(key, now, found);
        WriteCache(cache);
        return new Discovered(found, FromCache: false);
    }

    /// <summary>How old <paramref name="root"/>'s cached scan is, or null when it has none.</summary>
    public TimeSpan? CacheAge(string root) =>
        ReadCache().TryGetValue(PathResolution.Resolve(root), out var entry) ? time.GetUtcNow() - entry.ScannedAt : null;

    private static IEnumerable<string> Children(string parent)
    {
        try
        {
            return [.. Directory.EnumerateDirectories(parent)
                .Where(child => Path.GetFileName(child) is var name && !name.StartsWith('.') && !Skipped.Contains(name))];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    // The whole cache. A missing or corrupt file, or a junk entry, reads as absent: a cache is never the point.
    private Dictionary<string, CachedScan> ReadCache()
    {
        var entries = new Dictionary<string, CachedScan>(StringComparer.Ordinal);
        JsonObject? document;
        try
        {
            document = File.Exists(cacheFile) ? JsonNode.Parse(File.ReadAllText(cacheFile)) as JsonObject : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return entries;
        }

        foreach (var (key, value) in document ?? [])
        {
            if (CachedScan.FromJson(value) is { } entry)
            {
                entries[key] = entry;
            }
        }

        return entries;
    }

    private void WriteCache(Dictionary<string, CachedScan> entries)
    {
        var document = new JsonObject();
        foreach (var (key, entry) in entries)
        {
            document[key] = entry.ToJson();
        }

        try
        {
            _ = Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(cacheFile))!);
            File.WriteAllText(cacheFile, document.ToJsonString(Indented) + "\n");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // An unwritable cache only costs the next run a scan.
        }
    }

    private sealed record CachedScan(string Root, DateTimeOffset ScannedAt, IReadOnlyList<string> Repos)
    {
        // Python's datetime.isoformat() for an aware UTC time, which its fromisoformat() reads back.
        public JsonObject ToJson() => new()
        {
            ["root"] = Root,
            ["scanned_at"] = ScannedAt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'+00:00'", CultureInfo.InvariantCulture),
            ["repos"] = new JsonArray([.. Repos.Select(repo => (JsonNode?)repo)]),
        };

        public static CachedScan? FromJson(JsonNode? node)
        {
            if (node is not JsonObject entry
                || entry["scanned_at"] is not JsonValue stamp || !stamp.TryGetValue<string>(out var text)
                || entry["repos"] is not JsonArray repos
                || !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var scanned))
            {
                return null;
            }

            var paths = new List<string>();
            foreach (var repo in repos)
            {
                if (repo is not JsonValue value || !value.TryGetValue<string>(out var path))
                {
                    return null;
                }

                paths.Add(path);
            }

            var root = entry["root"] is JsonValue rootValue && rootValue.TryGetValue<string>(out var named) ? named : string.Empty;
            return new CachedScan(root, scanned, paths);
        }
    }
}
