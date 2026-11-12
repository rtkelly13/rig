using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Rig.Core.Chrome;

/// <summary>One configured domain and the site group it joins.</summary>
/// <param name="Domain">A lowercase domain, such as <c>npmjs.com</c>.</param>
/// <param name="Site">The group's title.</param>
public sealed record SiteDomain(string Domain, string Site);

/// <summary>
/// Site groups by host, from <c>config/chrome-site-groups.json</c>. A tab whose title names no repo or site still has
/// a host: its URL's where the driver reports one (macOS), else the one history ties its title to (Windows). A host
/// matches a domain it equals or ends with (<c>www.npmjs.com</c> matches <c>npmjs.com</c>), and the longest matching
/// domain wins, so <c>gemini.google.com</c> is AI while <c>docs.google.com</c> stays Google. Call hosts are not here:
/// they never group, and that is a safety rule kept in code. Ported from <c>desktop_manager.chrome.site_hosts</c>.
/// </summary>
/// <param name="domains">The configured domains.</param>
public sealed partial class SiteHosts(IReadOnlyList<SiteDomain> domains)
{
    /// <summary>The config's file name under a rig checkout's <c>config/</c>.</summary>
    public const string ConfigName = "chrome-site-groups.json";

    private const string PackagedResource = "Rig.Core.Chrome.chrome-site-groups.json";

    /// <summary>No groups.</summary>
    public static SiteHosts None { get; } = new([]);

    /// <summary>The configured domains, in config order.</summary>
    public IReadOnlyList<SiteDomain> Domains { get; } = domains;

    /// <summary>The group of the longest configured domain <paramref name="host"/> equals or ends with, if any.</summary>
    public string? SiteFor(string host)
    {
        ArgumentNullException.ThrowIfNull(host);
        var name = host.ToLowerInvariant();
        string? best = null;
        var bestLength = 0;
        foreach (var entry in Domains)
        {
            var domain = entry.Domain;
            if ((name == domain || name.EndsWith("." + domain, StringComparison.Ordinal)) && domain.Length > bestLength)
            {
                (best, bestLength) = (entry.Site, domain.Length);
            }
        }

        return best;
    }

    /// <summary>
    /// The groups in <paramref name="json"/>; a malformed config, an untitled group, or a domain that is not lowercase
    /// or listed twice throws <see cref="InvalidDataException"/>.
    /// </summary>
    public static SiteHosts Parse(string json)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException error)
        {
            throw new InvalidDataException($"{ConfigName} is not JSON: {error.Message}", error);
        }

        if (root is not JsonObject { } config || config["groups"] is not JsonObject groups)
        {
            throw new InvalidDataException($"{ConfigName} needs a \"groups\" object");
        }

        Dictionary<string, string> owners = [];
        List<SiteDomain> domains = [];
        foreach (var (site, listed) in groups)
        {
            if (string.IsNullOrWhiteSpace(site))
            {
                throw new InvalidDataException("a site group needs a title");
            }

            if (listed is not JsonArray list)
            {
                throw new InvalidDataException($"{site}: the domains must be a list");
            }

            foreach (var item in list)
            {
                if (item is not JsonValue value || !value.TryGetValue(out string? domain) || !DomainPattern().IsMatch(domain))
                {
                    throw new InvalidDataException($"{site}: {item?.ToJsonString() ?? "null"} is not a lowercase domain");
                }

                if (!owners.TryAdd(domain, site))
                {
                    throw new InvalidDataException($"{site}: {domain} is already in {owners[domain]}");
                }

                domains.Add(new SiteDomain(domain, site));
            }
        }

        return new SiteHosts(domains);
    }

    /// <summary>The groups in the file at <paramref name="path"/>; see <see cref="Parse"/>.</summary>
    public static SiteHosts Load(string path) => Parse(File.ReadAllText(path));

    /// <summary>The copy built into rig, for when there is no checkout.</summary>
    public static SiteHosts Packaged()
    {
        using var stream = typeof(SiteHosts).Assembly.GetManifestResourceStream(PackagedResource)
            ?? throw new InvalidOperationException($"{PackagedResource} is not embedded");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }

    /// <summary>The config in <paramref name="checkout"/>'s <c>config/</c> when there is one, else <see cref="Packaged"/>.</summary>
    public static SiteHosts Default(string checkout)
    {
        var path = Path.Join(checkout, "config", ConfigName);
        return File.Exists(path) ? Load(path) : Packaged();
    }

    [GeneratedRegex("^(?:[a-z0-9](?:[a-z0-9-]*[a-z0-9])?\\.)+[a-z]{2,}$", RegexOptions.CultureInvariant)]
    private static partial Regex DomainPattern();
}
