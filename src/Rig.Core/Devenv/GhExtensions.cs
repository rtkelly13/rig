using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Rig.Core.Processes;

namespace Rig.Core.Devenv;

/// <summary>A gh extension every machine needs.</summary>
/// <param name="Repo">The extension's repository, <c>owner/gh-name</c>, as <c>gh extension install</c> takes it.</param>
/// <param name="Rationale">Why it is required.</param>
public sealed record GhExtension(string Repo, string Rationale);

/// <summary>One row of <c>gh extension list</c>.</summary>
/// <param name="Name">The command it adds, such as <c>gh stack</c>.</param>
/// <param name="Repo">The repository it was installed from, <c>owner/gh-name</c>.</param>
/// <param name="Version">The installed release tag, a commit for an extension installed from source, or empty for a local one.</param>
public sealed record InstalledGhExtension(string Name, string Repo, string Version)
{
    /// <summary>Whether <see cref="Version"/> is a release tag, so it can be compared with the latest release.</summary>
    public bool Tagged => GhExtensions.IsTag(Version);
}

/// <summary>An installed extension compared with its repository's latest release.</summary>
/// <param name="Extension">The installed extension.</param>
/// <param name="Latest">The latest release's tag, or null when it could not be read.</param>
/// <param name="Problem">Why it could not be compared, or null.</param>
public sealed record GhExtensionRelease(InstalledGhExtension Extension, string? Latest, string? Problem)
{
    /// <summary>Whether a newer release than the installed one exists.</summary>
    public bool Outdated => Problem is null && Latest is { } latest && !GhExtensions.SameVersion(Extension.Version, latest);
}

/// <summary>
/// The gh extensions rig requires, from the embedded <c>gh-extensions.json</c>, and how <c>gh extension list</c> is read.
/// </summary>
public static partial class GhExtensions
{
    private static readonly Lazy<IReadOnlyList<GhExtension>> Bundled = new(() =>
    {
        using var stream = typeof(GhExtensions).Assembly.GetManifestResourceStream("Rig.Core.Devenv.gh-extensions.json")
            ?? throw new InvalidOperationException("gh-extensions.json is not embedded in Rig.Core.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return Parse(reader.ReadToEnd());
    });

    /// <summary>The required extensions, in the order the bundled file lists them.</summary>
    public static IReadOnlyList<GhExtension> Required => Bundled.Value;

    /// <summary>
    /// Reads a required-extensions document: <c>{"required": [{"repo": "owner/gh-name", "rationale": "..."}]}</c>.
    /// </summary>
    /// <exception cref="InvalidDataException">The document is not that shape.</exception>
    public static IReadOnlyList<GhExtension> Parse(string text)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(text);
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"gh-extensions.json is not JSON: {e.Message}", e);
        }

        if (root is not JsonObject document || document["required"] is not JsonArray entries)
        {
            throw new InvalidDataException("gh-extensions.json: $.required must be an array.");
        }

        return [.. entries.Select((entry, index) => new GhExtension(Text(entry, index, "repo"), Text(entry, index, "rationale")))];
    }

    /// <summary>
    /// The rows of <c>gh extension list</c>: tab-separated name, repository and version. A row without a repository is
    /// skipped.
    /// </summary>
    public static IReadOnlyList<InstalledGhExtension> ParseList(string listed)
    {
        ArgumentNullException.ThrowIfNull(listed);
        return [.. listed
            .Split('\n')
            .Select(line => line.TrimEnd('\r').Split('\t'))
            .Where(fields => fields.Length >= 2 && fields[1].Trim().Length > 0)
            .Select(fields => new InstalledGhExtension(fields[0].Trim(), fields[1].Trim(), fields.Length > 2 ? fields[2].Trim() : string.Empty))];
    }

    /// <summary>The required extensions <paramref name="installed"/> does not hold; repositories match case-insensitively.</summary>
    public static IReadOnlyList<GhExtension> Missing(IEnumerable<InstalledGhExtension> installed)
    {
        var present = installed.Select(extension => extension.Repo).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return [.. Required.Where(extension => !present.Contains(extension.Repo))];
    }

    /// <summary>Whether <paramref name="version"/> looks like a release tag (<c>v1.2.3</c> or <c>1.2.3</c>) rather than a commit.</summary>
    public static bool IsTag(string version) => TagPattern().IsMatch(version);

    /// <summary>Whether two tags name the same version, ignoring a leading <c>v</c>.</summary>
    public static bool SameVersion(string installed, string latest)
    {
        ArgumentNullException.ThrowIfNull(installed);
        ArgumentNullException.ThrowIfNull(latest);
        return string.Equals(installed.TrimStart('v', 'V'), latest.TrimStart('v', 'V'), StringComparison.OrdinalIgnoreCase);
    }

    private static string Text(JsonNode? entry, int index, string field) =>
        entry is JsonObject fields && fields[field] is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 0
            ? text
            : throw new InvalidDataException($"gh-extensions.json: $.required[{index}].{field} must be a non-empty string.");

    [GeneratedRegex(@"^[vV]?\d+(\.\d+)*([-+.][0-9A-Za-z.-]+)?$")]
    private static partial Regex TagPattern();
}

/// <summary>
/// gh's extension commands, through processes, and the latest-release lookup, through the REST seam. A failure is a
/// null or <see langword="false"/>, never an exception.
/// </summary>
/// <param name="proc">Runs gh.</param>
/// <param name="api">Reads the latest release.</param>
public sealed class GhExtensionManager(IProc proc, IGitHubApi api)
{
    private static readonly TimeSpan Limit = TimeSpan.FromMinutes(5);

    /// <summary>The installed extensions, or null with gh's error when <c>gh extension list</c> fails.</summary>
    public async Task<(IReadOnlyList<InstalledGhExtension>? Installed, string Error)> ListAsync(CancellationToken cancellationToken)
    {
        var listed = await proc.RunAsync(new ProcRequest(["gh", "extension", "list"]) { Timeout = TimeSpan.FromSeconds(60) }, cancellationToken).ConfigureAwait(false);
        return listed.Ok ? (GhExtensions.ParseList(listed.Out), string.Empty) : (null, Reason(listed));
    }

    /// <summary>Installs <paramref name="repo"/>; null when it installed, otherwise gh's error.</summary>
    public async Task<string?> InstallAsync(string repo, CancellationToken cancellationToken)
    {
        var ran = await proc.RunAsync(new ProcRequest(["gh", "extension", "install", repo]) { Timeout = Limit }, cancellationToken).ConfigureAwait(false);
        return ran.Ok ? null : Reason(ran);
    }

    /// <summary>Runs <c>gh extension upgrade --all</c> attached to the console; whether it exited 0.</summary>
    public async Task<bool> UpgradeAllAsync(CancellationToken cancellationToken) =>
        (await proc.RunAsync(new ProcRequest(["gh", "extension", "upgrade", "--all"]) { Timeout = Limit, Attached = true }, cancellationToken).ConfigureAwait(false)).Ok;

    /// <summary>
    /// Each of <paramref name="installed"/> compared with its repository's latest release. An extension without a release
    /// tag, or whose lookup fails (no releases answers 404), has a <see cref="GhExtensionRelease.Problem"/>.
    /// </summary>
    public async Task<IReadOnlyList<GhExtensionRelease>> ReleasesAsync(IReadOnlyList<InstalledGhExtension> installed, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(installed);
        var releases = await Task.WhenAll(installed.Select(extension => ReleaseAsync(extension, cancellationToken))).ConfigureAwait(false);
        return releases;
    }

    private async Task<GhExtensionRelease> ReleaseAsync(InstalledGhExtension extension, CancellationToken cancellationToken)
    {
        if (!extension.Tagged)
        {
            return new(extension, null, extension.Version.Length == 0 ? "installed locally, not from a release" : $"installed at {extension.Version}, not a release");
        }

        var ran = await api.CallAsync(ApiRequest.Get($"repos/{extension.Repo}/releases/latest"), cancellationToken).ConfigureAwait(false);
        if (!ran.Ok)
        {
            return new(extension, null, $"could not read the latest release: {Reason(ran)}");
        }

        try
        {
            return JsonNode.Parse(ran.Out) is JsonObject release && release["tag_name"] is JsonValue value && value.TryGetValue<string>(out var tag) && tag.Length > 0
                ? new(extension, tag, null)
                : new(extension, null, "the latest release has no tag_name");
        }
        catch (JsonException)
        {
            return new(extension, null, "the latest release is not JSON");
        }
    }

    private static string Reason(Ran ran) =>
        ran.Err.Trim() is { Length: > 0 } err ? err.Split('\n')[^1].Trim() : $"gh exited {ran.Code}";
}
