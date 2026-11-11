using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Rig.Core.Processes;

namespace Rig.Core.Packages;

/// <summary>What a registry says about one package, for the dashboard.</summary>
/// <param name="LatestVersion">The newest version; null when it is not published or could not be read.</param>
public sealed record RegistryInfo(string? LatestVersion)
{
    /// <summary>A package the registry does not have.</summary>
    public static readonly RegistryInfo NotPublished = new((string?)null);

    /// <summary>A registry that could not be read (network error, 429, 5xx): nothing here is a confirmed state.</summary>
    public static readonly RegistryInfo LookupFailed = new((string?)null) { Failed = true };

    /// <summary>Every published version.</summary>
    public IReadOnlyList<string> AllVersions { get; init; } = [];

    /// <summary>Whether the latest version is unlisted.</summary>
    public bool Unlisted { get; init; }

    /// <summary>Whether the latest version is deprecated.</summary>
    public bool Deprecated { get; init; }

    /// <summary>The registry could not be read.</summary>
    public bool Failed { get; init; }
}

/// <summary>A git tag that names one of a package's versions.</summary>
/// <param name="Tag">The tag.</param>
/// <param name="Version">The version it names.</param>
public sealed record MatchedTag(string Tag, string Version);

/// <summary>A package's changelog.</summary>
/// <param name="Found">Whether there is one.</param>
/// <param name="HasUnreleased">Whether its <c>[Unreleased]</c> section has entries.</param>
public sealed record ChangelogState(bool Found, bool HasUnreleased);

/// <summary>One dashboard row: a package against its registry, tags and changelog.</summary>
public sealed record PackageStatusRow(
    [property: JsonPropertyName("project")] string Project,
    [property: JsonPropertyName("package")] string Package,
    [property: JsonPropertyName("registry")] string Registry,
    [property: JsonPropertyName("manifest")] string Manifest,
    [property: JsonPropertyName("registry_version")] string? RegistryVersion,
    [property: JsonPropertyName("latest_tag")] string? LatestTag,
    [property: JsonPropertyName("tag_version")] string? TagVersion,
    [property: JsonPropertyName("changelog_found")] bool ChangelogFound,
    [property: JsonPropertyName("changelog_unreleased")] bool ChangelogUnreleased,
    [property: JsonPropertyName("unlisted")] bool Unlisted,
    [property: JsonPropertyName("deprecated")] bool Deprecated,
    [property: JsonPropertyName("flags")] IReadOnlyList<string> Flags,
    [property: JsonPropertyName("status")] string Status);

/// <summary>Reads a package's registry state for the dashboard.</summary>
public interface IRegistryInfo
{
    /// <summary>What <paramref name="registry"/> says about <paramref name="name"/>.</summary>
    Task<RegistryInfo> InfoAsync(string registry, string name, CancellationToken cancellationToken = default);
}

/// <summary>
/// The package and version dashboard: every declared package against its registry, its git tags and its changelog.
/// Flags unreleased changes, a tag with no registry version, a registry version with no tag, deprecated or unlisted
/// packages, and version drift across a multi-package project. Ported from <c>rig.workspace.package_dashboard</c>.
/// </summary>
public static partial class PackageDashboard
{
    /// <summary>The flag for an <c>[Unreleased]</c> changelog section with entries.</summary>
    public const string UnreleasedChanges = "unreleased-changes";

    /// <summary>The flag for a registry that could not be read.</summary>
    public const string RegistryUnavailable = "registry-unavailable";

    /// <summary>The flag for a clone whose tags could not be listed.</summary>
    public const string TagsUnavailable = "tags-unavailable";

    /// <summary>The flag for a tag the registry does not have.</summary>
    public const string TagWithoutRegistry = "tag-without-registry";

    /// <summary>The flag for a registry version with no tag.</summary>
    public const string RegistryWithoutTag = "registry-without-tag";

    /// <summary>The flag for an unlisted latest version.</summary>
    public const string UnlistedFlag = "unlisted";

    /// <summary>The flag for a deprecated latest version.</summary>
    public const string DeprecatedFlag = "deprecated";

    /// <summary>The flag for packages of one project at different versions.</summary>
    public const string VersionFamilyDrift = "version-family-drift";

    /// <summary>The status of a package with neither a release nor a tag.</summary>
    public const string Unpublished = "unpublished";

    /// <summary>The status of a package with no flags.</summary>
    public const string Synced = "synced";

    /// <summary>Whether <paramref name="content"/> has entries under an <c>[Unreleased]</c> heading.</summary>
    public static bool ChangelogHasUnreleased(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var inUnreleased = false;
        var entries = new List<string>();
        foreach (var line in CommentPattern().Replace(content, string.Empty).Split('\n'))
        {
            var stripped = line.Trim();
            if (UnreleasedPattern().IsMatch(stripped))
            {
                inUnreleased = true;
                continue;
            }

            if (inUnreleased)
            {
                if (HeadingPattern().IsMatch(stripped))
                {
                    break;
                }

                entries.Add(stripped);
            }
        }

        return inUnreleased && entries.Any(line =>
            line.Length > 0
            && !line.StartsWith("---", StringComparison.Ordinal)
            && !line.StartsWith("***", StringComparison.Ordinal)
            && !line.StartsWith("___", StringComparison.Ordinal)
            && !line.StartsWith("### ", StringComparison.Ordinal));
    }

    /// <summary>The changelog beside the manifest, else at the clone's root.</summary>
    public static ChangelogState ReadChangelog(string clonePath, string manifest)
    {
        if (!Directory.Exists(clonePath))
        {
            return new(false, false);
        }

        foreach (var candidate in new[] { Path.Join(clonePath, Path.GetDirectoryName(manifest), "CHANGELOG.md"), Path.Join(clonePath, "CHANGELOG.md") })
        {
            if (File.Exists(candidate))
            {
                try
                {
                    return new(true, ChangelogHasUnreleased(File.ReadAllText(candidate)));
                }
                catch (IOException)
                {
                    return new(true, false);
                }
            }
        }

        return new(false, false);
    }

    /// <summary>
    /// The tags naming <paramref name="package"/>'s versions: <c>&lt;PackageId&gt;/v&lt;version&gt;</c> when there are
    /// any, else <c>v&lt;version&gt;</c>.
    /// </summary>
    public static IReadOnlyList<MatchedTag> FindMatchingTags(IEnumerable<string> tags, string package)
    {
        ArgumentNullException.ThrowIfNull(tags);
        ArgumentNullException.ThrowIfNull(package);
        var prefix = $"{package}/v";
        var scoped = new List<MatchedTag>();
        var global = new List<MatchedTag>();
        foreach (var tag in tags)
        {
            if (tag.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                Add(scoped, tag, tag[prefix.Length..]);
            }
            else if (tag.StartsWith('v') || tag.StartsWith('V'))
            {
                Add(global, tag, tag[1..]);
            }
        }

        return scoped.Count > 0 ? scoped : global;

        static void Add(List<MatchedTag> into, string tag, string version)
        {
            version = version.Trim();
            if (version.Length > 0 && char.IsDigit(version[0]))
            {
                into.Add(new(tag, version));
            }
        }
    }

    /// <summary>
    /// The local clone of <paramref name="project"/>: under <paramref name="root"/> when given (directly, or under its
    /// Orca group's top folder), else under the code root's group folder, <c>personal/</c>, or the code root itself.
    /// </summary>
    public static string? ResolveClone(string project, string? orcaGroup, string? root, string codeRoot)
    {
        var top = orcaGroup?.Split('/')[0] is { Length: > 0 } group ? group : null;
        var candidates = new List<string?>();
        if (root is not null)
        {
            root = Path.GetFullPath(root);
            candidates.Add(Path.Join(root, project));
            candidates.Add(top is null ? null : Path.Join(root, top, project));
        }

        candidates.Add(top is null ? null : Path.Join(codeRoot, top, project));
        candidates.Add(Path.Join(codeRoot, "personal", project));
        candidates.Add(Path.Join(codeRoot, project));
        return candidates.FirstOrDefault(Directory.Exists) is { } found ? Path.GetFullPath(found) : null;
    }

    /// <summary>A dashboard row for each binding, in order.</summary>
    /// <param name="bindings">The declared packages.</param>
    /// <param name="registry">The registry reader.</param>
    /// <param name="git">Reads each clone's tags.</param>
    /// <param name="root">Where clones are looked for first.</param>
    /// <param name="codeRoot">The code root.</param>
    /// <param name="groups">Each project's Orca group.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    public static async Task<IReadOnlyList<PackageStatusRow>> CollectAsync(
        IEnumerable<PackageBinding> bindings,
        IRegistryInfo registry,
        Git git,
        string? root,
        string codeRoot,
        IReadOnlyDictionary<string, string> groups,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(git);
        ArgumentNullException.ThrowIfNull(groups);
        var rows = new List<PackageStatusRow>();
        foreach (var binding in bindings)
        {
            var spec = binding.Spec;
            var clone = ResolveClone(binding.Project, groups.GetValueOrDefault(binding.Project), root, codeRoot);
            var info = await registry.InfoAsync(spec.Registry, spec.Name, cancellationToken).ConfigureAwait(false);
            IReadOnlyList<string>? listed = clone is null ? [] : await git.LinesAsync(clone, "tag", "-l").ConfigureAwait(false);
            var tags = listed ?? [];
            var matching = FindMatchingTags(tags, spec.Name);
            var latest = matching.Count == 0 ? null : matching.Aggregate((a, b) => PackageVersions.Compare(b.Version, a.Version) > 0 ? b : a);
            var changelog = clone is null ? new ChangelogState(false, false) : ReadChangelog(clone, spec.Manifest);

            var flags = new List<string>();
            if (changelog.HasUnreleased)
            {
                flags.Add(UnreleasedChanges);
            }

            if (listed is null)
            {
                flags.Add(TagsUnavailable);
            }

            if (info.Failed)
            {
                flags.Add(RegistryUnavailable);
            }
            else if (latest is not null && (info.LatestVersion is null || !info.AllVersions.Contains(latest.Version)))
            {
                flags.Add(TagWithoutRegistry);
            }

            if (listed is not null && info.LatestVersion is not null && !matching.Any(m => m.Version == info.LatestVersion))
            {
                flags.Add(RegistryWithoutTag);
            }

            if (info.Unlisted)
            {
                flags.Add(UnlistedFlag);
            }

            if (info.Deprecated)
            {
                flags.Add(DeprecatedFlag);
            }

            rows.Add(new(
                binding.Project, spec.Name, spec.Registry, spec.Manifest, info.LatestVersion, latest?.Tag, latest?.Version,
                changelog.Found, changelog.HasUnreleased, info.Unlisted, info.Deprecated, flags, string.Empty));
        }

        // Packages of one project are released together; a readable registry at two versions is drift.
        return
        [
            .. rows.GroupBy(row => row.Project, StringComparer.Ordinal).SelectMany(project =>
            {
                var drift = project.Count() > 1
                    && project.Where(row => !row.Flags.Contains(RegistryUnavailable)).Select(row => row.RegistryVersion).Distinct().Count() > 1;
                return project.Select(row =>
                {
                    IReadOnlyList<string> flags = drift ? [.. row.Flags, VersionFamilyDrift] : row.Flags;
                    var status = flags.Count > 0
                        ? string.Join(", ", flags)
                        : row.RegistryVersion is null && row.LatestTag is null ? Unpublished : Synced;
                    return row with { Flags = flags, Status = status };
                });
            }),
        ];
    }

    [GeneratedRegex(@"^##\s+\[?unreleased\]?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UnreleasedPattern();

    [GeneratedRegex(@"^#{1,2}\s+", RegexOptions.CultureInvariant)]
    private static partial Regex HeadingPattern();

    [GeneratedRegex("<!--.*?-->", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex CommentPattern();
}
