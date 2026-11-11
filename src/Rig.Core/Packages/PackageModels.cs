using System.Diagnostics.CodeAnalysis;

[assembly: SuppressMessage("Design", "CA1054", Justification = "Repository and registry URLs are compared and printed as written; System.Uri normalizes them.", Scope = "namespaceanddescendants", Target = "~N:Rig.Core.Packages")]
[assembly: SuppressMessage("Design", "CA1055", Justification = "Repository and registry URLs are compared and printed as written; System.Uri normalizes them.", Scope = "namespaceanddescendants", Target = "~N:Rig.Core.Packages")]
[assembly: SuppressMessage("Design", "CA1056", Justification = "Repository and registry URLs are compared and printed as written; System.Uri normalizes them.", Scope = "namespaceanddescendants", Target = "~N:Rig.Core.Packages")]

namespace Rig.Core.Packages;

/// <summary>How a package check came out.</summary>
public enum CheckStatus
{
    /// <summary>Nothing to do.</summary>
    Ok,

    /// <summary>Worth a look.</summary>
    Warn,

    /// <summary>Publishing is broken or will break.</summary>
    Fail,

    /// <summary>Not a check: something only a person can confirm.</summary>
    Manual,

    /// <summary>Could not be checked.</summary>
    Skip,
}

/// <summary>Spellings of <see cref="CheckStatus"/> as the JSON and tables show them.</summary>
public static class CheckStatusText
{
    /// <summary><c>ok</c>, <c>warn</c>, <c>fail</c>, <c>manual</c> or <c>skip</c>.</summary>
    public static string Label(this CheckStatus status) => status switch
    {
        CheckStatus.Ok => "ok",
        CheckStatus.Warn => "warn",
        CheckStatus.Fail => "fail",
        CheckStatus.Manual => "manual",
        _ => "skip",
    };
}

/// <summary>One published package, as declared under a project's <c>packages</c> list in <c>managed-projects.json</c>.</summary>
/// <param name="Registry"><c>npm</c> or <c>nuget</c>.</param>
/// <param name="Name">The package id.</param>
/// <param name="Manifest">The manifest's path in the repo: <c>package.json</c>, a csproj or props file.</param>
/// <param name="Workflow">The publish workflow's file name under <c>.github/workflows/</c>.</param>
/// <param name="Auth"><c>oidc</c> (trusted publishing) or <c>token</c>.</param>
/// <param name="Environment">The GitHub environment the publish job runs in, when the trust config requires one.</param>
public sealed record PackageSpec(string Registry, string Name, string Manifest, string Workflow, string Auth = PackageSpec.Oidc, string? Environment = null)
{
    /// <summary>The npm registry.</summary>
    public const string Npm = "npm";

    /// <summary>The NuGet registry.</summary>
    public const string NuGet = "nuget";

    /// <summary>Trusted publishing.</summary>
    public const string Oidc = "oidc";

    /// <summary>A long-lived token.</summary>
    public const string Token = "token";

    /// <summary>NuGet only: the nuget.org user or organisation that owns the trusted publishing policy.</summary>
    public string? NuGetOwner { get; init; }

    /// <summary>NuGet only: the profile name NuGet/login sends as <c>user:</c>; an organisation's policy is exchanged by a member.</summary>
    public string? NuGetUser { get; init; }

    /// <summary>NuGet only: the package's expected nuget.org owners.</summary>
    public IReadOnlyList<string> Owners { get; init; } = [];

    /// <summary>NuGet only: the policy's scopes, such as <c>push-new</c> and <c>push-version</c>.</summary>
    public IReadOnlyList<string> Scopes { get; init; } = [];

    /// <summary>Whether it is published to npm.</summary>
    public bool IsNpm => Registry == Npm;

    /// <summary>Whether it is published by trusted publishing.</summary>
    public bool IsOidc => Auth == Oidc;
}

/// <summary>A package bound to the managed project that publishes it.</summary>
/// <param name="Project">The project's name.</param>
/// <param name="Repo">The project's declared remote.</param>
/// <param name="Spec">The package.</param>
public sealed record PackageBinding(string Project, string Repo, PackageSpec Spec);

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
