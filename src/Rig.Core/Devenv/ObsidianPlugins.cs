using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Nodes;
using Rig.Core.Policy;

namespace Rig.Core.Devenv;

/// <summary>A community plugin, as the registry lists it.</summary>
/// <param name="Id">The id Obsidian knows it by.</param>
/// <param name="Repo">Its GitHub repo, <c>owner/name</c>.</param>
/// <param name="Name">What the user sees.</param>
public sealed record PluginSpec(string Id, string Repo, string Name);

/// <summary>What installing one plugin came to: the release tag, or why it was skipped.</summary>
/// <param name="Tag">The latest release's tag; null when it failed.</param>
/// <param name="Problem">Why it failed; null when it did not.</param>
public sealed record PluginInstall(string? Tag, string? Problem);

/// <summary>
/// Installs and enables Obsidian community plugins from their GitHub releases. A plugin is only three files in
/// <c>&lt;vault&gt;/.obsidian/plugins/&lt;id&gt;/</c> plus an entry in <c>community-plugins.json</c>, and releases publish
/// exactly those files. Plugin ids resolve to repos through the official registry. Ported from devenv's
/// <c>obsidian_plugins</c>.
/// </summary>
/// <param name="http">Follows redirects (release assets redirect to a CDN); every URL it is given passes <see cref="CheckUrl"/>.</param>
public sealed class ObsidianPlugins(HttpClient http)
{
    /// <summary>The official community plugin registry.</summary>
    public const string RegistryUrl = "https://raw.githubusercontent.com/obsidianmd/obsidian-releases/master/community-plugins.json";

    /// <summary>
    /// Plugins the estate wants in an agent-facing vault. Anything that rewrites files on save is left out: the vault
    /// has several writers fencing generated content between markers, so a formatter or schema enforcer would fight the
    /// generator. That rules out Linter, Metadata Menu and obsidian-git's auto-commit; Dataview is out too, since the
    /// <c>bases</c> core plugin already queries frontmatter.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultPlugins =
    [
        "obsidian-local-rest-api", // serves the vault over MCP
        "templater-obsidian", // stamps provenance at note creation (ADR 0007)
        "table-editor-obsidian", // Advanced Tables: ADRs and hubs are table-dense
        "obsidian-tasks-plugin", // queries plain `- [ ]` markdown, invents no format
    ];

    /// <summary>Core plugins that ship with Obsidian but are off by default.</summary>
    public static readonly IReadOnlyList<string> DefaultCorePlugins =
    [
        "properties", // the frontmatter editor: the whole OKF layer is frontmatter
    ];

    private const string UserAgent = "devenv-obsidian-plugins";
    private static readonly string[] Assets = ["manifest.json", "main.js", "styles.css"]; // styles.css is optional
    private static readonly string[] Required = ["manifest.json", "main.js"];

    // Asset URLs are remote data from the registry and the releases API: only HTTPS, and only the hosts that serve
    // plugin assets.
    private static readonly FrozenSet<string> AllowedHosts =
        new[] { "raw.githubusercontent.com", "api.github.com", "github.com", "objects.githubusercontent.com" }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary><c>~/code/personal/obsidian-vault</c> under <paramref name="home"/>.</summary>
    public static string DefaultVault(string home) => Path.Join(EstateRules.Root(home), "obsidian-vault");

    /// <summary><paramref name="address"/> as a URI when it is an HTTPS fetch from a known host; throws <see cref="InvalidDataException"/> otherwise.</summary>
    public static Uri CheckUrl(string address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidDataException($"refusing non-HTTPS plugin URL: '{address}'");
        }

        return AllowedHosts.Contains(uri.Host)
            ? uri
            : throw new InvalidDataException($"refusing plugin URL from unexpected host '{uri.Host}': '{address}'");
    }

    /// <summary>The registry's well-formed entries: one bad entry must not hide every other plugin. Throws <see cref="InvalidDataException"/> when it is not a list.</summary>
    public static IReadOnlyList<PluginSpec> ParseRegistry(JsonNode? data) =>
        data is JsonArray entries
            ? [.. entries.OfType<JsonObject>().Select(Spec).OfType<PluginSpec>()]
            : throw new InvalidDataException("the plugin registry is not a list");

    /// <summary>The installed version of <paramref name="id"/>, or null when it is not installed or has no readable version.</summary>
    public static string? InstalledVersion(string vault, string id)
    {
        try
        {
            return JsonNode.Parse(File.ReadAllBytes(Path.Join(vault, ".obsidian", "plugins", id, "manifest.json"))) is JsonObject manifest
                && manifest["version"] is JsonValue version
                && version.TryGetValue(out string? text) ? text : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Adds <paramref name="id"/> to <c>community-plugins.json</c>, keeping what is already enabled; false when it already
    /// was. A malformed file is replaced rather than failing the setup.
    /// </summary>
    public static bool Enable(string vault, string id, bool dryRun)
    {
        var path = Path.Join(vault, ".obsidian", "community-plugins.json");
        var enabled = Read(path) is JsonArray listed ? listed.Select(Text).ToList() : [];
        if (enabled.Contains(id, StringComparer.Ordinal))
        {
            return false;
        }

        enabled.Add(id);
        if (!dryRun)
        {
            _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            Write(path, new JsonArray([.. enabled.Select(e => (JsonNode)JsonValue.Create(e))]));
        }

        return true;
    }

    /// <summary>
    /// Turns on built-in plugins that ship disabled and returns those it turned on. <c>core-plugins.json</c> maps id to
    /// bool; older vaults list the enabled ids. The shape found is kept, so Obsidian does not rewrite it. Nothing
    /// happens without the file.
    /// </summary>
    public static IReadOnlyList<string> EnableCore(string vault, IReadOnlyList<string> ids, bool dryRun)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var path = Path.Join(vault, ".obsidian", "core-plugins.json");
        var turnedOn = new List<string>();
        JsonNode payload;
        switch (Read(path))
        {
            case JsonObject mapping:
                foreach (var id in ids.Where(id => mapping[id] is not JsonValue v || v.GetValueKind() != JsonValueKind.True))
                {
                    mapping[id] = true;
                    turnedOn.Add(id);
                }

                payload = mapping;
                break;
            case JsonArray listed:
                var enabled = listed.Select(Text).ToList();
                turnedOn.AddRange(ids.Where(id => !enabled.Contains(id, StringComparer.Ordinal)));
                payload = new JsonArray([.. enabled.Concat(turnedOn).Select(e => (JsonNode)JsonValue.Create(e))]);
                break;
            default:
                return [];
        }

        if (turnedOn.Count > 0 && !dryRun)
        {
            Write(path, payload);
        }

        return turnedOn;
    }

    /// <summary>The community plugin registry. Throws <see cref="HttpRequestException"/>, <see cref="InvalidDataException"/> or <see cref="JsonException"/>.</summary>
    public async Task<IReadOnlyList<PluginSpec>> RegistryAsync(CancellationToken cancellationToken) =>
        ParseRegistry(JsonNode.Parse(await GetAsync(RegistryUrl, cancellationToken).ConfigureAwait(false)));

    /// <summary>Downloads <paramref name="spec"/>'s latest release into the vault; a dry run only reads the release.</summary>
    public async Task<PluginInstall> InstallAsync(string vault, PluginSpec spec, bool dryRun, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(spec);
        (string Tag, Dictionary<string, string> Assets) release;
        try
        {
            release = Release(JsonNode.Parse(await GetAsync($"https://api.github.com/repos/{spec.Repo}/releases/latest", cancellationToken).ConfigureAwait(false)));
        }
        catch (Exception e) when (Fetching(e))
        {
            return new PluginInstall(null, $"{spec.Id}: could not read latest release ({e.Message})");
        }

        var missing = Required.Where(asset => !release.Assets.ContainsKey(asset)).ToList();
        if (missing.Count > 0)
        {
            return new PluginInstall(null, $"{spec.Id}: release {release.Tag} is missing {string.Join(", ", missing)}");
        }

        if (dryRun)
        {
            return new PluginInstall(release.Tag, null);
        }

        var target = Directory.CreateDirectory(Path.Join(vault, ".obsidian", "plugins", spec.Id)).FullName;
        foreach (var asset in Assets.Where(release.Assets.ContainsKey))
        {
            try
            {
                await File.WriteAllBytesAsync(Path.Join(target, asset), await GetAsync(release.Assets[asset], cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (Fetching(e) || e is IOException or UnauthorizedAccessException)
            {
                return new PluginInstall(null, $"{spec.Id}: failed downloading {asset} ({e.Message})");
            }
        }

        return new PluginInstall(release.Tag, null);
    }

    /// <summary>What fetching and reading a remote file can throw: network, a timeout, a refused URL and bad JSON.</summary>
    public static bool Fetching(Exception e) => e is HttpRequestException or TaskCanceledException or InvalidDataException or JsonException;

    private async Task<byte[]> GetAsync(string address, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, CheckUrl(address));
        request.Headers.UserAgent.ParseAdd(UserAgent);
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        _ = response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
    }

    // The release's tag and asset urls by name; InvalidDataException when it is not that shape.
    private static (string Tag, Dictionary<string, string> Assets) Release(JsonNode? release)
    {
        if (release is not JsonObject found || Str(found["tag_name"]) is not { } tag || found["assets"] is not JsonArray assets)
        {
            throw new InvalidDataException("the release has no tag or assets");
        }

        var byName = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var asset in assets)
        {
            if (asset is not JsonObject entry || Str(entry["name"]) is not { } name || Str(entry["browser_download_url"]) is not { } url)
            {
                throw new InvalidDataException("a release asset has no name or url");
            }

            byName[name] = url;
        }

        return (tag, byName);
    }

    private static PluginSpec? Spec(JsonObject entry) =>
        Str(entry["id"]) is { } id && Str(entry["repo"]) is { } repo && Str(entry["name"]) is { } name ? new PluginSpec(id, repo, name) : null;

    private static string? Str(JsonNode? node) => node is JsonValue value && value.TryGetValue(out string? text) ? text : null;

    // Python's str() of a list entry: strings as they are, anything else as JSON.
    private static string Text(JsonNode? node) => Str(node) ?? node?.ToJsonString() ?? "None";

    private static JsonNode? Read(string path)
    {
        try
        {
            return File.Exists(path) ? JsonNode.Parse(File.ReadAllBytes(path)) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static void Write(string path, JsonNode payload) => File.WriteAllText(path, PyJson.Dumps(payload) + "\n");
}
