using System.Globalization;

namespace Rig.Core.Packages;

/// <summary>How a package's local manifest version stands against its registry.</summary>
public enum PackageSyncStatus
{
    /// <summary>The registry has the manifest's version.</summary>
    Synced,

    /// <summary>The manifest is ahead: a release is due.</summary>
    Unreleased,

    /// <summary>The registry is ahead of the manifest.</summary>
    Behind,

    /// <summary>The registry does not have the package.</summary>
    Unpublished,

    /// <summary>The manifest or its version could not be read.</summary>
    Unknown,
}

/// <summary>Spellings of <see cref="PackageSyncStatus"/> as the JSON and tables show them.</summary>
public static class PackageSyncStatusText
{
    /// <summary><c>synced</c>, <c>unreleased</c>, <c>behind</c>, <c>unpublished</c> or <c>unknown</c>.</summary>
    public static string Label(this PackageSyncStatus status) => status switch
    {
        PackageSyncStatus.Synced => "synced",
        PackageSyncStatus.Unreleased => "unreleased",
        PackageSyncStatus.Behind => "behind",
        PackageSyncStatus.Unpublished => "unpublished",
        _ => "unknown",
    };
}

/// <summary>One package's local and registry versions.</summary>
/// <param name="Project">The managed project.</param>
/// <param name="Package">The package id.</param>
/// <param name="Registry"><c>npm</c> or <c>nuget</c>.</param>
/// <param name="ManifestPath">The manifest's path in the repo.</param>
/// <param name="LocalVersion">The manifest's version.</param>
/// <param name="Status">How the two stand.</param>
/// <param name="RegistryUrl">The package's page on the registry.</param>
/// <param name="Detail">Why, in words.</param>
public sealed record PackageTrackingRecord(
    string Project, string Package, string Registry, string ManifestPath, string? LocalVersion, PackageSyncStatus Status, string RegistryUrl, string Detail)
{
    /// <summary>The registry's latest version.</summary>
    public string? LatestVersion { get; init; }

    /// <summary>When <see cref="LatestVersion"/> was published.</summary>
    public DateTimeOffset? LatestPublished { get; init; }

    /// <summary>The registry's latest stable version.</summary>
    public string? StableVersion { get; init; }

    /// <summary>When <see cref="StableVersion"/> was published.</summary>
    public DateTimeOffset? StablePublished { get; init; }
}

/// <summary>Compares each package's manifest version with its registry's latest.</summary>
public static class PackageTracking
{
    /// <summary>The package's page on its registry.</summary>
    public static string RegistryWebUrl(PackageSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        return spec.IsNpm ? $"https://www.npmjs.com/package/{spec.Name}" : $"https://www.nuget.org/packages/{spec.Name}";
    }

    /// <summary><c>3h ago</c>, <c>2d ago</c>, <c>1w ago</c>, <c>4mo ago</c> or <c>1y ago</c>; <c>--</c> for nothing.</summary>
    public static string RelativeAge(DateTimeOffset? instant, DateTimeOffset now)
    {
        if (instant is not { } then)
        {
            return "--";
        }

        var diff = now - then;
        if (diff < TimeSpan.Zero)
        {
            return "in future";
        }

        var days = diff.Days;
        return days switch
        {
            < 1 => diff.Hours > 0 ? string.Create(CultureInfo.InvariantCulture, $"{diff.Hours}h ago") : "just now",
            < 7 => string.Create(CultureInfo.InvariantCulture, $"{days}d ago"),
            < 30 => string.Create(CultureInfo.InvariantCulture, $"{days / 7}w ago"),
            < 365 => string.Create(CultureInfo.InvariantCulture, $"{days / 30}mo ago"),
            _ => string.Create(CultureInfo.InvariantCulture, $"{days / 365}y ago"),
        };
    }

    /// <summary>
    /// One package's record: its manifest read from <paramref name="projectDir"/> when that checkout has it, else from
    /// GitHub. A NuGet manifest without a version falls back to the checkout's <c>Directory.Build.props</c>, then to
    /// MSBuild's default <c>1.0.0</c>.
    /// </summary>
    public static async Task<PackageTrackingRecord> TrackAsync(
        PackageBinding binding, IPackageFetcher fetcher, string? projectDir, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(fetcher);
        var spec = binding.Spec;
        var text = projectDir is null ? null : ReadLocal(Path.Combine(projectDir, spec.Manifest));
        if (text is null && PackageAudit.Slug(binding.Repo) is { } slug)
        {
            text = await fetcher.GitHubFileAsync(slug, spec.Manifest, cancellationToken).ConfigureAwait(false);
        }

        string? local = null;
        if (text is not null)
        {
            local = ManifestFields.Read(spec.Registry, text).Version;
            if (local is null && !spec.IsNpm)
            {
                var props = projectDir is null ? null : ReadLocal(Path.Combine(projectDir, "Directory.Build.props"));
                local = (props is null ? null : ManifestFields.Read(spec.Registry, props).Version) ?? "1.0.0";
            }
        }

        var release = await fetcher.RegistryReleaseAsync(spec, cancellationToken).ConfigureAwait(false);
        var (status, detail) = (release, text, local) switch
        {
            (null, _, _) => (PackageSyncStatus.Unpublished, "not found on registry"),
            (_, null, _) => (PackageSyncStatus.Unknown, "local manifest not found"),
            (_, _, null) => (PackageSyncStatus.Unknown, "version not specified in manifest"),
            _ when PackageVersions.IsAhead(local, release.Version) => (PackageSyncStatus.Unreleased, $"local ahead ({local} > {release.Version})"),
            _ when PackageVersions.IsAhead(release.Version, local) =>
                (PackageSyncStatus.Behind, $"local behind ({local} < {release.Version})"),
            _ => (PackageSyncStatus.Synced, "up to date"),
        };
        return new(binding.Project, spec.Name, spec.Registry, spec.Manifest, local, status, RegistryWebUrl(spec), detail)
        {
            LatestVersion = release?.Version,
            LatestPublished = release?.Published,
            StableVersion = release?.StableVersion,
            StablePublished = release?.StablePublished,
        };
    }

    private static string? ReadLocal(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
