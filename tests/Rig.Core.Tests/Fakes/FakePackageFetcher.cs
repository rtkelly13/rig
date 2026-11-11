using Rig.Core.Packages;

namespace Rig.Core.Tests.Fakes;

/// <summary>An <see cref="IPackageFetcher"/> that answers from its fields, whatever repository is asked about.</summary>
public sealed class FakePackageFetcher : IPackageFetcher
{
    /// <summary>What every repository resolves to; null for none.</summary>
    public RepoIdentity? Repo { get; set; }

    /// <summary>Files by path, the same on every repository.</summary>
    public Dictionary<string, string> Files { get; init; } = new(StringComparer.Ordinal);

    /// <summary>The registry's release for every package.</summary>
    public Release? Release { get; set; }

    /// <summary>The publish workflow's conclusions, newest first; null when unreadable.</summary>
    public IReadOnlyList<string>? Runs { get; set; } = ["success"];

    /// <summary>What <c>npm trust list</c> says.</summary>
    public NpmTrustListing Trust { get; set; } = new([]);

    /// <summary>How many times <see cref="NpmTrustAsync"/> ran.</summary>
    public int TrustReads { get; private set; }

    /// <inheritdoc/>
    public Task<RepoIdentity?> GitHubRepoAsync(string slug, CancellationToken cancellationToken = default) => Task.FromResult(Repo);

    /// <inheritdoc/>
    public Task<string?> GitHubFileAsync(string slug, string path, CancellationToken cancellationToken = default) =>
        Task.FromResult(Files.GetValueOrDefault(path));

    /// <inheritdoc/>
    public Task<IReadOnlyList<string>?> WorkflowConclusionsAsync(string slug, string workflow, int limit, CancellationToken cancellationToken = default) =>
        Task.FromResult(Runs);

    /// <inheritdoc/>
    public Task<Release?> RegistryReleaseAsync(PackageSpec spec, CancellationToken cancellationToken = default) => Task.FromResult(Release);

    /// <inheritdoc/>
    public Task<NpmTrustListing> NpmTrustAsync(string name, CancellationToken cancellationToken = default)
    {
        TrustReads++;
        return Task.FromResult(Trust);
    }
}
