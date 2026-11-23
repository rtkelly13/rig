namespace Rig.Core.Config;

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
