using System.Diagnostics.CodeAnalysis;
using Rig.Core.Config;

[assembly: SuppressMessage("Design", "CA1054", Justification = "Repository and registry URLs are compared and printed as written; System.Uri normalizes them.", Scope = "namespaceanddescendants", Target = "~N:Rig.Core.Packages")]
[assembly: SuppressMessage("Design", "CA1055", Justification = "Repository and registry URLs are compared and printed as written; System.Uri normalizes them.", Scope = "namespaceanddescendants", Target = "~N:Rig.Core.Packages")]
[assembly: SuppressMessage("Design", "CA1056", Justification = "Repository and registry URLs are compared and printed as written; System.Uri normalizes them.", Scope = "namespaceanddescendants", Target = "~N:Rig.Core.Packages")]

namespace Rig.Core.Packages;

/// <summary>A repository as GitHub has it now.</summary>
/// <param name="FullName">Its canonical <c>owner/name</c>, after any redirect.</param>
/// <param name="Id">Its id, kept across a transfer.</param>
/// <param name="OwnerId">Its owner's id, changed by a transfer.</param>
public sealed record RepoIdentity(string FullName, long Id, long OwnerId);

/// <summary>Where npm's provenance says a version was built.</summary>
/// <param name="RepositoryUrl">The building repository.</param>
/// <param name="WorkflowPath">The workflow that built it.</param>
/// <param name="RepositoryId">The building repository's id.</param>
/// <param name="OwnerId">The building repository's owner's id.</param>
public sealed record Provenance(string RepositoryUrl, string WorkflowPath, long? RepositoryId, long? OwnerId);

/// <summary>A registry's version information for a package.</summary>
/// <param name="Version">The latest version (NuGet: the newest listed one, or the newest unlisted when none is listed).</param>
/// <param name="Published">When it was published.</param>
public sealed record Release(string Version, DateTimeOffset? Published)
{
    private readonly string? _stableVersion;
    private readonly DateTimeOffset? _stablePublished;

    /// <summary>npm: whether <c>_npmUser.trustedPublisher</c> is set (observed, not a documented contract).</summary>
    public bool? TrustedPublisher { get; init; }

    /// <summary>npm: the latest version's provenance.</summary>
    public Provenance? Provenance { get; init; }

    /// <summary>NuGet: the catalog entry's <c>repository.url</c>, copied from the nuspec.</summary>
    public string? SourceRepoUrl { get; init; }

    /// <summary>The latest stable version; <see cref="Version"/> itself when that is stable and none was given.</summary>
    public string? StableVersion
    {
        get => _stableVersion is null && PackageVersions.IsStable(Version) ? Version : _stableVersion;
        init => _stableVersion = value;
    }

    /// <summary>When <see cref="StableVersion"/> was published.</summary>
    public DateTimeOffset? StablePublished
    {
        get => _stableVersion is null && PackageVersions.IsStable(Version) ? Published : _stablePublished;
        init => _stablePublished = value;
    }

    /// <summary>NuGet: false when every version is unlisted.</summary>
    public bool Listed { get; init; } = true;

    /// <summary>NuGet: search's owners; null when search could not be read.</summary>
    public IReadOnlyList<string>? Owners { get; init; }

    /// <summary>NuGet: the latest listed version's deprecation, summarised; null when it is not deprecated.</summary>
    public string? Deprecation { get; init; }

    /// <summary>NuGet: the latest listed version's advisories, as <c>severity advisoryUrl</c>.</summary>
    public IReadOnlyList<string> Vulnerabilities { get; init; } = [];
}

/// <summary>One npm trusted publisher entry.</summary>
/// <param name="Repository">The trusted <c>owner/name</c>.</param>
/// <param name="File">The trusted workflow file.</param>
/// <param name="Environment">The required environment, if any.</param>
public sealed record TrustConfig(string Repository, string File, string? Environment = null);

/// <summary>What <c>npm trust list</c> returned: its entries, or why it could not be read.</summary>
/// <param name="Configs">The entries; empty on failure.</param>
/// <param name="Error">Why the listing failed; null when it worked.</param>
public sealed record NpmTrustListing(IReadOnlyList<TrustConfig> Configs, string? Error = null);

/// <summary>One check's outcome for one package.</summary>
/// <param name="Package">The package as <c>registry:name</c>.</param>
/// <param name="Check">What was checked.</param>
/// <param name="Status">How it came out.</param>
/// <param name="Detail">What was found.</param>
/// <param name="Fix">What to do about it, when anything.</param>
public sealed record Finding(string Package, string Check, CheckStatus Status, string Detail, string Fix = "");

/// <summary>Where the audit reads GitHub and the registries from.</summary>
public interface IPackageFetcher
{
    /// <summary>The repository <paramref name="slug"/> resolves to, following redirects; null when it does not resolve.</summary>
    Task<RepoIdentity?> GitHubRepoAsync(string slug, CancellationToken cancellationToken = default);

    /// <summary>A file on <paramref name="slug"/>'s default branch; null when it is not there.</summary>
    Task<string?> GitHubFileAsync(string slug, string path, CancellationToken cancellationToken = default);

    /// <summary>The conclusions of <paramref name="workflow"/>'s latest completed runs, newest first; null when unreadable.</summary>
    Task<IReadOnlyList<string>?> WorkflowConclusionsAsync(string slug, string workflow, int limit, CancellationToken cancellationToken = default);

    /// <summary>The registry's release information for <paramref name="spec"/>; null when it is not published.</summary>
    Task<Release?> RegistryReleaseAsync(PackageSpec spec, CancellationToken cancellationToken = default);

    /// <summary>npm's trusted publishers for <paramref name="name"/>.</summary>
    Task<NpmTrustListing> NpmTrustAsync(string name, CancellationToken cancellationToken = default);
}
