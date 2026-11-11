using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Rig.Core.Processes;

namespace Rig.Core.Packages;

/// <summary>
/// Public registry endpoints over HTTP; GitHub through <c>gh</c>, so its auth applies. npm's and NuGet's payloads are
/// formats rig does not own, so they are read leniently: an odd field costs that field, not the package's audit.
/// Two fields are observed rather than documented, and are read as hints: npm's
/// <c>versions[v]._npmUser.trustedPublisher</c>, present on versions published through OIDC, and the NuGet catalog
/// leaf's <c>repository</c>, copied from the nuspec. NuGet resources come from the V3 service index, as the protocol
/// asks. Search's <c>owners</c> is reindexed on a publish, not on an owner change, so it can lag the Manage page.
/// </summary>
/// <param name="http">The registry client.</param>
/// <param name="github">The GitHub REST seam.</param>
/// <param name="proc">Runs <c>npm trust list</c>.</param>
public sealed class LivePackageFetcher(HttpClient http, IGitHubApi github, IProc proc) : IPackageFetcher
{
    /// <summary>NuGet's V3 service index.</summary>
    public const string NuGetServiceIndex = "https://api.nuget.org/v3/index.json";

    private static readonly string[] Severities = ["low", "moderate", "high", "critical"];

    private readonly Dictionary<PackageSpec, Release?> _releases = [];
    private NuGetEndpoints? _endpoints;

    /// <inheritdoc/>
    public async Task<RepoIdentity?> GitHubRepoAsync(string slug, CancellationToken cancellationToken = default)
    {
        var ran = await github.CallAsync(ApiRequest.Get($"repos/{slug}"), cancellationToken).ConfigureAwait(false);
        var repo = ran.Ok ? Parse(ran.Out) : null;
        return Str(repo?["full_name"]) is { } name && Long(repo?["id"]) is { } id && Long(repo?["owner"]?["id"]) is { } owner
            ? new RepoIdentity(name, id, owner)
            : null;
    }

    /// <inheritdoc/>
    public async Task<string?> GitHubFileAsync(string slug, string path, CancellationToken cancellationToken = default)
    {
        var request = ApiRequest.Get($"repos/{slug}/contents/{path}") with { Headers = ["Accept: application/vnd.github.raw"] };
        var ran = await github.CallAsync(request, cancellationToken).ConfigureAwait(false);
        return ran.Ok ? ran.Out : null;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<string>?> WorkflowConclusionsAsync(string slug, string workflow, int limit, CancellationToken cancellationToken = default)
    {
        var route = string.Create(CultureInfo.InvariantCulture, $"repos/{slug}/actions/workflows/{workflow}/runs?status=completed&per_page={limit}");
        var ran = await github.CallAsync(ApiRequest.Get(route), cancellationToken).ConfigureAwait(false);
        if (!ran.Ok || Parse(ran.Out)?["workflow_runs"] is not JsonArray runs)
        {
            return null;
        }

        return [.. runs.Select(run => Str(run?["conclusion"])).OfType<string>().Where(c => c.Length > 0)];
    }

    /// <inheritdoc/>
    public async Task<Release?> RegistryReleaseAsync(PackageSpec spec, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (!_releases.TryGetValue(spec, out var release))
        {
            release = spec.IsNpm
                ? await NpmAsync(spec.Name, cancellationToken).ConfigureAwait(false)
                : await NuGetAsync(spec.Name, cancellationToken).ConfigureAwait(false);
            _releases[spec] = release;
        }

        return release;
    }

    /// <inheritdoc/>
    public async Task<NpmTrustListing> NpmTrustAsync(string name, CancellationToken cancellationToken = default)
    {
        // npm prompts for a one-time password, so the console's input and errors stay attached.
        var npm = ProgramPath.Find("npm") ?? "npm";
        var ran = await proc.RunAsync(new ProcRequest([npm, "trust", "list", name, "--json"]) { ConsoleInput = true }, cancellationToken)
            .ConfigureAwait(false);
        return ran.Ok
            ? new(ParseNpmTrust(ran.Out))
            : new([], string.Create(CultureInfo.InvariantCulture, $"exit {ran.Code} (needs npm >= 11.15 and `npm login`)"));
    }

    /// <summary>The SLSA statement in <c>/-/npm/v1/attestations/&lt;pkg&gt;@&lt;ver&gt;</c>; null when there is none.</summary>
    public static Provenance? ParseNpmProvenance(JsonNode? document)
    {
        if (document?["attestations"] is not JsonArray attestations)
        {
            return null;
        }

        foreach (var attestation in attestations)
        {
            var payload = Str(attestation?["bundle"]?["dsseEnvelope"]?["payload"]);
            if (payload is null || !(Str(attestation?["predicateType"]) ?? string.Empty).Contains("slsa.dev/provenance", StringComparison.Ordinal))
            {
                continue;
            }

            var build = Decode(payload)?["predicate"]?["buildDefinition"];
            if (build?["externalParameters"]?["workflow"] is not JsonObject workflow)
            {
                return null;
            }

            var gh = build["internalParameters"]?["github"];
            return new(
                Str(workflow["repository"]) ?? string.Empty,
                Str(workflow["path"]) ?? string.Empty,
                Long(gh?["repository_id"]),
                Long(gh?["repository_owner_id"]));
        }

        return null;
    }

    /// <summary><c>npm trust list --json</c>: one entry or a list of them, each carrying the claims it was created with.</summary>
    public static IReadOnlyList<TrustConfig> ParseNpmTrust(string text)
    {
        var entries = Parse(text) switch
        {
            JsonArray list => list.ToList(),
            JsonObject single => [single],
            _ => [],
        };
        return
        [
            .. entries
                .Select(entry => entry?["claims"])
                .OfType<JsonObject>()
                .Select(claims => new TrustConfig(Str(claims["repository"]) ?? string.Empty, Str(claims["workflow_ref"]?["file"]) ?? string.Empty, Str(claims["environment"]))),
        ];
    }

    private async Task<JsonNode?> GetAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(new Uri(url), cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        return Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
    }

    private async Task<Release?> NpmAsync(string name, CancellationToken cancellationToken)
    {
        var packument = await GetAsync($"https://registry.npmjs.org/{name.Replace("/", "%2f", StringComparison.Ordinal)}", cancellationToken).ConfigureAwait(false);
        if (packument is not JsonObject)
        {
            return null;
        }

        var versions = packument["versions"] as JsonObject;
        var times = packument["time"] as JsonObject;
        var all = versions?.Select(pair => pair.Key).ToList() ?? [];
        var distLatest = Str(packument["dist-tags"]?["latest"]);
        if (distLatest is not null && !all.Contains(distLatest))
        {
            all.Add(distLatest);
        }

        if (all.Count == 0)
        {
            return null;
        }

        var latest = all.Aggregate(Newer);
        var stables = all.Where(PackageVersions.IsStable).ToList();
        var stable = stables.Count == 0 ? null : stables.Aggregate(Newer);
        var meta = versions?[latest] as JsonObject ?? (distLatest is null ? null : versions?[distLatest] as JsonObject);
        var user = meta?["_npmUser"] as JsonObject;
        var attestations = Str(meta?["dist"]?["attestations"]?["url"]);
        return new(latest, Time(times?[latest]))
        {
            TrustedPublisher = user is null ? null : user["trustedPublisher"] is JsonObject,
            Provenance = attestations is null ? null : ParseNpmProvenance(await GetAsync(attestations, cancellationToken).ConfigureAwait(false)),
            StableVersion = stable,
            StablePublished = stable is null ? null : Time(times?[stable]),
        };
    }

    private async Task<NuGetEndpoints?> EndpointsAsync(CancellationToken cancellationToken)
    {
        if (_endpoints is null && await GetAsync(NuGetServiceIndex, cancellationToken).ConfigureAwait(false) is { } index)
        {
            // The first resource of exactly each type: nuget.org lists each search host twice.
            string? Find(string type) => (index["resources"] as JsonArray)?
                .FirstOrDefault(resource => Str(resource?["@type"]) == type && Str(resource?["@id"]) is not null) is { } found
                ? Str(found["@id"])
                : null;
            if (Find("RegistrationsBaseUrl/3.6.0") is { } registrations && Find("PackageBaseAddress/3.0.0") is { } flat
                && Find("SearchQueryService/3.5.0") is { } search)
            {
                _endpoints = new(registrations, flat, search);
            }
        }

        return _endpoints;
    }

    private async Task<IReadOnlyList<string>?> NuGetOwnersAsync(string search, string name, CancellationToken cancellationToken)
    {
        var found = await GetAsync($"{search}?q=packageid:{name}&prerelease=true&semVerLevel=2.0.0", cancellationToken).ConfigureAwait(false);
        var hit = (found?["data"] as JsonArray)?.FirstOrDefault(h => string.Equals(Str(h?["id"]), name, StringComparison.OrdinalIgnoreCase));
        return hit?["owners"] switch
        {
            JsonArray owners => [.. owners.Select(Str).OfType<string>()],
            JsonValue one when Str(one) is { } owner => [owner],
            _ => null,
        };
    }

    private async Task<Release?> NuGetAsync(string name, CancellationToken cancellationToken)
    {
        if (await EndpointsAsync(cancellationToken).ConfigureAwait(false) is not { } endpoints)
        {
            return null;
        }

        var id = name.ToLowerInvariant();
        var flat = await GetAsync($"{endpoints.Flat}{id}/index.json", cancellationToken).ConfigureAwait(false);
        var versions = (flat?["versions"] as JsonArray)?.Select(Str).OfType<string>().ToList() ?? [];
        if (versions.Count == 0)
        {
            return null;
        }

        var owners = await NuGetOwnersAsync(endpoints.Search, name, cancellationToken).ConfigureAwait(false);
        if (await GetAsync($"{endpoints.Registrations}{id}/index.json", cancellationToken).ConfigureAwait(false) is not JsonObject index)
        {
            return null;
        }

        JsonObject? latest = null, stable = null;
        foreach (var page in ((index["items"] as JsonArray) ?? []).Reverse())
        {
            var leaves = page?["items"] as JsonArray;
            if (leaves is null && Str(page?["@id"]) is { } pageUrl)
            {
                leaves = (await GetAsync(pageUrl, cancellationToken).ConfigureAwait(false))?["items"] as JsonArray;
            }

            var listed = (leaves ?? []).Select(leaf => leaf?["catalogEntry"] as JsonObject)
                .OfType<JsonObject>()
                .Where(entry => Str(entry["version"]) is not null && Str(entry["@id"]) is not null && entry["listed"]?.GetValueKind() != JsonValueKind.False)
                .Reverse();
            foreach (var entry in listed)
            {
                latest ??= entry;
                if (stable is null && PackageVersions.IsStable(Str(entry["version"])!))
                {
                    stable = entry;
                }

                if (stable is not null)
                {
                    break;
                }
            }

            if (latest is not null && stable is not null)
            {
                break;
            }
        }

        if (latest is null)
        {
            // Every version is unlisted: the package exists, so a policy still has to cover it.
            return new(versions.Aggregate(Newer), null) { Listed = false, Owners = owners };
        }

        var leafDoc = await GetAsync(Str(latest["@id"])!, cancellationToken).ConfigureAwait(false);
        return new(Str(latest["version"])!, Time(latest["published"]))
        {
            SourceRepoUrl = Str(leafDoc?["repository"]?["url"]),
            StableVersion = stable is null ? null : Str(stable["version"]),
            StablePublished = stable is null ? null : Time(stable["published"]),
            Owners = owners,
            Deprecation = latest["deprecation"] is JsonObject deprecation ? DeprecationSummary(deprecation) : null,
            Vulnerabilities = latest["vulnerabilities"] is JsonArray vulnerabilities
                ? [.. vulnerabilities.OfType<JsonObject>().Select(VulnerabilitySummary)]
                : [],
        };
    }

    private static string DeprecationSummary(JsonObject deprecation)
    {
        var reasons = (deprecation["reasons"] as JsonArray)?.Select(Str).OfType<string>().ToList() ?? [];
        var message = Str(deprecation["message"]) is { Length: > 0 } text ? $" ({text})" : string.Empty;
        var alternate = Str(deprecation["alternatePackage"]?["id"]) is { Length: > 0 } other ? $"; use {other}" : string.Empty;
        return $"{(reasons.Count == 0 ? "no reason given" : string.Join(", ", reasons))}{message}{alternate}";
    }

    private static string VulnerabilitySummary(JsonObject vulnerability)
    {
        var level = int.TryParse(Str(vulnerability["severity"]), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : -1;
        var name = level >= 0 && level < Severities.Length ? Severities[level] : "unknown";
        return $"{name} {Str(vulnerability["advisoryUrl"])}".Trim();
    }

    private static string Newer(string left, string right) => PackageVersions.Compare(right, left) > 0 ? right : left;

    private static JsonNode? Parse(string text)
    {
        try
        {
            return JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonNode? Decode(string base64)
    {
        try
        {
            return Parse(Encoding.UTF8.GetString(Convert.FromBase64String(base64)));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static string? Str(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    // GitHub sends ids as numbers; npm's provenance carries them as strings.
    private static long? Long(JsonNode? node) => node switch
    {
        JsonValue value when value.TryGetValue<long>(out var number) => number,
        JsonValue value when value.TryGetValue<string>(out var text)
            && long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => null,
    };

    private static DateTimeOffset? Time(JsonNode? node) =>
        Str(node) is { } text && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var instant)
            ? instant
            : null;

    private sealed record NuGetEndpoints(string Registrations, string Flat, string Search);
}
