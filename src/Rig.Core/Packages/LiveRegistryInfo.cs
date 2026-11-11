using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Rig.Core.Packages;

/// <summary>
/// The dashboard's registry reads: NuGet's flat container and registration index, npm's packument. A 404 is "not
/// published"; any other failure is a lookup that failed, so nothing is claimed about the release. Read once per
/// package.
/// </summary>
/// <param name="http">The registry client.</param>
public sealed class LiveRegistryInfo(HttpClient http) : IRegistryInfo
{
    /// <summary>NuGet's flat container.</summary>
    public const string NuGetFlat = "https://api.nuget.org/v3-flatcontainer/";

    /// <summary>NuGet's SemVer 2 registration index.</summary>
    public const string NuGetRegistrations = "https://api.nuget.org/v3/registration5-gz-semver2/";

    /// <summary>The npm registry.</summary>
    public const string NpmRegistry = "https://registry.npmjs.org/";

    private readonly Dictionary<string, RegistryInfo> _cache = new(StringComparer.Ordinal);

    /// <inheritdoc/>
    public async Task<RegistryInfo> InfoAsync(string registry, string name, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        var key = $"{registry}:{name.ToUpperInvariant()}";
        if (!_cache.TryGetValue(key, out var info))
        {
            info = registry switch
            {
                PackageSpec.NuGet => await NuGetAsync(name, cancellationToken).ConfigureAwait(false),
                PackageSpec.Npm => await NpmAsync(name, cancellationToken).ConfigureAwait(false),
                _ => RegistryInfo.NotPublished,
            };
            _cache[key] = info;
        }

        return info;
    }

    private async Task<(HttpStatusCode? Status, JsonNode? Body)> GetAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await http.GetAsync(new Uri(url), cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return (response.StatusCode, null);
            }

            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return (response.StatusCode, JsonNode.Parse(text));
            }
            catch (JsonException)
            {
                return (response.StatusCode, null);
            }
        }
        catch (HttpRequestException)
        {
            return (null, null);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // HttpClient's own timeout.
            return (null, null);
        }
    }

    private static RegistryInfo? Unread(HttpStatusCode? status, JsonNode? body) => body is not null
        ? null
        : status switch
        {
            HttpStatusCode.NotFound => RegistryInfo.NotPublished,
            HttpStatusCode code when (int)code is >= 200 and < 300 => RegistryInfo.NotPublished,
            _ => RegistryInfo.LookupFailed,
        };

    private async Task<RegistryInfo> NuGetAsync(string name, CancellationToken cancellationToken)
    {
        var id = name.ToLowerInvariant();
        var (status, flat) = await GetAsync($"{NuGetFlat}{id}/index.json", cancellationToken).ConfigureAwait(false);
        if (Unread(status, flat) is { } unread)
        {
            return unread;
        }

        var versions = (flat!["versions"] as JsonArray)?.Select(Str).OfType<string>().ToList() ?? [];
        if (versions.Count == 0)
        {
            return RegistryInfo.NotPublished;
        }

        var latest = versions.Aggregate((a, b) => PackageVersions.Compare(b, a) > 0 ? b : a);

        // The registration index says whether the latest version is listed or deprecated; failing that, it is neither.
        JsonObject? target = null;
        var (_, index) = await GetAsync($"{NuGetRegistrations}{id}/index.json", cancellationToken).ConfigureAwait(false);
        foreach (var page in ((index?["items"] as JsonArray) ?? []).Reverse())
        {
            var leaves = page?["items"] as JsonArray;
            if (leaves is null && Str(page?["@id"]) is { } pageUrl)
            {
                leaves = (await GetAsync(pageUrl, cancellationToken).ConfigureAwait(false)).Body?["items"] as JsonArray;
            }

            foreach (var entry in (leaves ?? []).Reverse().Select(leaf => leaf?["catalogEntry"] as JsonObject).OfType<JsonObject>())
            {
                target ??= entry;
                if (IsLatest(entry))
                {
                    target = entry;
                    break;
                }
            }

            if (target is not null && IsLatest(target))
            {
                break;
            }
        }

        return new(latest)
        {
            AllVersions = versions,
            Unlisted = target is not null && target["listed"]?.GetValueKind() == JsonValueKind.False,
            Deprecated = target?["deprecation"] is JsonObject,
        };

        bool IsLatest(JsonObject entry) => string.Equals(Str(entry["version"]), latest, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<RegistryInfo> NpmAsync(string name, CancellationToken cancellationToken)
    {
        var (status, packument) = await GetAsync($"{NpmRegistry}{name.Replace("/", "%2f", StringComparison.Ordinal)}", cancellationToken).ConfigureAwait(false);
        if (Unread(status, packument) is { } unread)
        {
            return unread;
        }

        if (packument!["versions"] is not JsonObject { Count: > 0 } versions)
        {
            return RegistryInfo.NotPublished;
        }

        var all = versions.Select(pair => pair.Key).ToList();
        var latest = Str(packument["dist-tags"]?["latest"]) is { } tagged && all.Contains(tagged)
            ? tagged
            : all.Aggregate((a, b) => PackageVersions.Compare(b, a) > 0 ? b : a);
        var deprecated = Str(versions[latest]?["deprecated"]);
        return new(latest) { AllVersions = all, Deprecated = !string.IsNullOrEmpty(deprecated) };
    }

    private static string? Str(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
