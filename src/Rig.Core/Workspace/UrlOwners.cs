using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Rig.Core.GitHub;

namespace Rig.Core.Workspace;

/// <summary>A project's local port range, inclusive at both ends.</summary>
/// <param name="Start">The lowest port.</param>
/// <param name="End">The highest port.</param>
public sealed record PortRange(int Start, int End)
{
    /// <summary>The range holds <paramref name="port"/>.</summary>
    public bool Contains(int port) => Start <= port && port <= End;

    /// <summary>The two ranges share a port.</summary>
    public bool Overlaps(PortRange other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return Start <= other.End && other.Start <= End;
    }

    /// <inheritdoc/>
    public override string ToString() => $"{Start}-{End}";
}

/// <summary>One project's port range, with the GitHub <c>owner/repo</c> its tabs group under.</summary>
/// <param name="Project">The project's name.</param>
/// <param name="Slug">Its GitHub <c>owner/repo</c>.</param>
/// <param name="Ports">The range it claims.</param>
public sealed record PortClaim(string Project, string Slug, PortRange Ports);

/// <summary>One project's Vercel project names, with the GitHub <c>owner/repo</c> they deploy.</summary>
/// <param name="Project">The project's name.</param>
/// <param name="Slug">Its GitHub <c>owner/repo</c>.</param>
/// <param name="Names">The Vercel project names it declares.</param>
public sealed record VercelClaim(string Project, string Slug, IReadOnlyList<string> Names);

/// <summary>
/// Which managed project a URL belongs to, from what the projects declare in <c>config/managed-projects.json</c>: a
/// local app's port inside the project's <c>ports</c> range, and a <c>*.vercel.app</c> host starting with one of its
/// <c>vercel</c> project names. Ported from <c>rig.workspace.url_owners</c>, <c>ports</c> and <c>vercel_names</c>.
/// </summary>
/// <param name="ports">The port claims.</param>
/// <param name="vercel">The Vercel claims.</param>
public sealed partial class UrlOwners(IReadOnlyList<PortClaim> ports, IReadOnlyList<VercelClaim> vercel)
{
    private const int Lowest = 1024;
    private const int Highest = 65535;
    private const string VercelApp = ".vercel.app";

    private static readonly HashSet<string> LocalHosts = new(["localhost", "127.0.0.1", "::1"], StringComparer.Ordinal);

    /// <summary>No declarations: no URL has an owner.</summary>
    public static UrlOwners None { get; } = new([], []);

    /// <summary>The port claims.</summary>
    public IReadOnlyList<PortClaim> Ports { get; } = ports;

    /// <summary>The Vercel claims.</summary>
    public IReadOnlyList<VercelClaim> Vercel { get; } = vercel;

    /// <summary>The GitHub <c>owner/repo</c> of the project a local or Vercel URL belongs to, or null.</summary>
    public string? SlugFor(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        if (!url.IsAbsoluteUri)
        {
            return null;
        }

        var host = url.Host.Trim('[', ']').ToLowerInvariant();
        if (LocalHosts.Contains(host))
        {
            return url.IsDefaultPort ? null : ClaimFor(url.Port)?.Slug;
        }

        return ClaimForHost(host)?.Slug;
    }

    /// <summary>The project whose range holds <paramref name="port"/>, or null when none claims it.</summary>
    public PortClaim? ClaimFor(int port) => Ports.FirstOrDefault(claim => claim.Ports.Contains(port));

    /// <summary>
    /// The project whose declared name is the longest prefix of a <c>*.vercel.app</c> host, or null. Longest wins, so
    /// <c>design-system-storybook-…</c> is never taken by <c>design-system</c>.
    /// </summary>
    public VercelClaim? ClaimForHost(string host)
    {
        ArgumentNullException.ThrowIfNull(host);
        var name = host.ToLowerInvariant();
        if (!name.EndsWith(VercelApp, StringComparison.Ordinal))
        {
            return null;
        }

        var label = name[..^VercelApp.Length];
        VercelClaim? best = null;
        var bestLength = 0;
        foreach (var claim in Vercel)
        {
            foreach (var declared in claim.Names)
            {
                if ((label == declared || label.StartsWith(declared + "-", StringComparison.Ordinal)) && declared.Length > bestLength)
                {
                    (best, bestLength) = (claim, declared.Length);
                }
            }
        }

        return best;
    }

    /// <summary>Both declarations in <paramref name="path"/>; a malformed one throws <see cref="InvalidDataException"/>.</summary>
    public static UrlOwners Load(string path) => new(LoadPortClaims(path), LoadVercelClaims(path));

    /// <summary>
    /// Every enabled project's port range. The ranges are governance: each lies inside 1024-65535 with start &lt;= end,
    /// belongs to a GitHub repo and overlaps no other; anything else throws <see cref="InvalidDataException"/>.
    /// </summary>
    public static IReadOnlyList<PortClaim> LoadPortClaims(string path)
    {
        List<PortClaim> claims = [];
        foreach (var project in Projects(path))
        {
            var name = Text(project, "name");
            if (!Enabled(project, name) || project["ports"] is not { } node)
            {
                continue;
            }

            var ports = node is JsonObject range
                ? new PortRange(Number(range, "start", name), Number(range, "end", name))
                : throw new InvalidDataException($"{name}: ports: expected an object");
            if (!(Lowest <= ports.Start && ports.Start <= ports.End && ports.End <= Highest))
            {
                throw new InvalidDataException($"{name}: ports {ports} must lie within {Lowest}-{Highest}, start <= end");
            }

            var slug = Slug(project, name, "ports");
            if (claims.FirstOrDefault(other => other.Ports.Overlaps(ports)) is { } clash)
            {
                throw new InvalidDataException($"{name}: ports {ports} overlap {clash.Project}'s {clash.Ports}");
            }

            claims.Add(new(name, slug, ports));
        }

        return claims;
    }

    /// <summary>
    /// Every project's declared Vercel names. Names are lowercase Vercel names and none is declared twice; anything else
    /// throws <see cref="InvalidDataException"/>.
    /// </summary>
    public static IReadOnlyList<VercelClaim> LoadVercelClaims(string path)
    {
        Dictionary<string, string> owners = new(StringComparer.Ordinal);
        List<VercelClaim> claims = [];
        foreach (var project in Projects(path))
        {
            var name = Text(project, "name");
            if (project["vercel"] is not { } node)
            {
                continue;
            }

            var names = node is JsonArray list
                ? list.Select(item => item is JsonValue value && value.GetValueKind() == JsonValueKind.String
                    ? value.GetValue<string>()
                    : throw new InvalidDataException($"{name}: vercel: expected a list of strings")).ToList()
                : throw new InvalidDataException($"{name}: vercel: expected a list of strings");
            if (names.Count == 0)
            {
                continue;
            }

            var slug = Slug(project, name, "vercel projects");
            foreach (var declared in names)
            {
                if (!VercelName().IsMatch(declared))
                {
                    throw new InvalidDataException($"{name}: '{declared}' is not a Vercel project name");
                }

                if (!owners.TryAdd(declared, name))
                {
                    throw new InvalidDataException($"{name}: vercel project '{declared}' is already declared by {owners[declared]}");
                }
            }

            claims.Add(new(name, slug, names));
        }

        return claims;
    }

    private static IEnumerable<JsonObject> Projects(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(File.ReadAllText(path));
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"{path}: {e.Message}", e);
        }

        if (root is not JsonObject top)
        {
            throw new InvalidDataException($"{path}: expected a JSON object");
        }

        return top["projects"] switch
        {
            null => [],
            JsonArray list when list.All(item => item is JsonObject) => list.Cast<JsonObject>(),
            _ => throw new InvalidDataException($"{path}: projects: expected a list of objects"),
        };
    }

    private static string Text(JsonObject entry, string key) =>
        entry[key] is JsonValue value && value.GetValueKind() == JsonValueKind.String
            ? value.GetValue<string>()
            : throw new InvalidDataException($"{key}: expected a string");

    private static bool Enabled(JsonObject entry, string name) => entry["enabled"]?.GetValueKind() switch
    {
        null or JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => throw new InvalidDataException($"{name}: enabled: expected a boolean"),
    };

    private static int Number(JsonObject range, string key, string name) =>
        range[key] is JsonValue value && value.GetValueKind() == JsonValueKind.Number && value.TryGetValue<int>(out var number)
            ? number
            : throw new InvalidDataException($"{name}: ports.{key}: expected an integer");

    private static string Slug(JsonObject project, string name, string what)
    {
        var repo = Text(project, "repo");
        return RepoSlug.FromRemote(repo)?.FullName
            ?? throw new InvalidDataException(string.Create(CultureInfo.InvariantCulture, $"{name}: {what} need a GitHub repo to belong to, not '{repo}'"));
    }

    [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]*[a-z0-9])?$", RegexOptions.CultureInvariant)]
    private static partial Regex VercelName();
}
