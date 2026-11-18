using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Rig.Core.Text;

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

        var versions = (flat.Field("versions") as JsonArray)?.Select(JsonShape.AsText).OfType<string>().ToList() ?? [];
        if (versions.Count == 0)
        {
            return RegistryInfo.NotPublished;
        }

        var latest = versions.Aggregate((a, b) => PackageVersions.Compare(b, a) > 0 ? b : a);

        // The registration index says whether the latest version is listed or deprecated. An index or page that could
        // not be read leaves both unknown, so the lookup counts as failed rather than as confirmed listed.
        JsonObject? target = null;
        var (indexStatus, index) = await GetAsync($"{NuGetRegistrations}{id}/index.json", cancellationToken).ConfigureAwait(false);
        var failed = Unread(indexStatus, index) == RegistryInfo.LookupFailed;
        foreach (var page in ((index.Field("items") as JsonArray) ?? []).Reverse())
        {
            var leaves = page.Field("items") as JsonArray;
            if (leaves is null && JsonShape.AsText(page.Field("@id")) is { } pageUrl)
            {
                var (pageStatus, pageBody) = await GetAsync(pageUrl, cancellationToken).ConfigureAwait(false);
                failed |= Unread(pageStatus, pageBody) == RegistryInfo.LookupFailed;
                leaves = pageBody.Field("items") as JsonArray;
            }

            target = (leaves ?? []).Select(leaf => leaf.Field("catalogEntry") as JsonObject).OfType<JsonObject>().LastOrDefault(IsLatest);
            if (target is not null)
            {
                failed = false;
                break;
            }
        }

        return new(latest)
        {
            AllVersions = versions,
            Unlisted = target is not null && target.Field("listed")?.GetValueKind() == JsonValueKind.False,
            Deprecated = target.Field("deprecation") is JsonObject,
            Failed = failed,
        };

        bool IsLatest(JsonObject entry) => string.Equals(JsonShape.AsText(entry.Field("version")), latest, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<RegistryInfo> NpmAsync(string name, CancellationToken cancellationToken)
    {
        var (status, packument) = await GetAsync($"{NpmRegistry}{name.Replace("/", "%2f", StringComparison.Ordinal)}", cancellationToken).ConfigureAwait(false);
        if (Unread(status, packument) is { } unread)
        {
            return unread;
        }

        if (packument.Field("versions") is not JsonObject { Count: > 0 } versions)
        {
            return RegistryInfo.NotPublished;
        }

        var all = versions.Select(pair => pair.Key).ToList();
        var latest = JsonShape.AsText(packument.Field("dist-tags").Field("latest")) is { } tagged && all.Contains(tagged)
            ? tagged
            : all.Aggregate((a, b) => PackageVersions.Compare(b, a) > 0 ? b : a);
        var deprecated = JsonShape.AsText(versions[latest].Field("deprecated"));
        return new(latest) { AllVersions = all, Deprecated = !string.IsNullOrEmpty(deprecated) };
    }

}
