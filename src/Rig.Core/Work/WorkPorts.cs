namespace Rig.Core.WorkItems;

/// <summary>The git facts the work list needs, behind one seam so the collector can be tested without a checkout.</summary>
public interface IWorkGitFacts
{
    /// <summary>Every clone under <paramref name="root"/> with its checkouts and branch refs.</summary>
    /// <param name="root">The code root.</param>
    /// <param name="refresh">Rescan the clone cache.</param>
    /// <param name="cancellationToken">Stops the reads.</param>
    Task<IReadOnlyList<RepoFacts>> ReadEstateAsync(string root, bool refresh, CancellationToken cancellationToken = default);

    /// <summary><paramref name="repos"/> with each checkout's dirty state filled in.</summary>
    /// <param name="repos">The repos from <see cref="ReadEstateAsync"/>.</param>
    /// <param name="cancellationToken">Stops the reads.</param>
    Task<IReadOnlyList<RepoFacts>> ProbeCheckoutsAsync(IReadOnlyList<RepoFacts> repos, CancellationToken cancellationToken = default);

    /// <summary><c>owner/name</c> for each clone, empty when its origin is not a GitHub remote.</summary>
    /// <param name="repos">The clones to read.</param>
    /// <param name="cancellationToken">Stops the reads.</param>
    Task<IReadOnlyDictionary<string, string>> ReadSlugsAsync(IReadOnlyList<string> repos, CancellationToken cancellationToken = default);
}

/// <summary>The newest PR per branch, behind one seam so the collector never touches the network.</summary>
public interface IWorkPrSource
{
    /// <summary>Looks up the newest PR for every requested branch; the cache first unless <paramref name="refresh"/>.</summary>
    /// <param name="requests">One request per repo.</param>
    /// <param name="refresh">Ignore the PR cache.</param>
    /// <param name="now">The moment cache age is measured from.</param>
    /// <param name="cancellationToken">Stops the calls.</param>
    Task<PrResults> FetchAsync(IReadOnlyList<PrRequest> requests, bool refresh, DateTimeOffset now, CancellationToken cancellationToken = default);
}

/// <summary>Claude Code sessions for the work list, behind one seam.</summary>
public interface IWorkSessionSource
{
    /// <summary>The sessions touched at or after <paramref name="since"/>, classified but not attributed.</summary>
    /// <param name="since">The oldest transcript to read.</param>
    /// <param name="cancellationToken">Stops the reads.</param>
    Task<SessionScan> ReadAsync(DateTimeOffset since, CancellationToken cancellationToken = default);
}

/// <summary>Whether a session's directory exists, and which repo it belongs to when it is outside the estate.</summary>
public interface IWorkFolderProbe
{
    /// <summary>Whether <paramref name="path"/> is a directory.</summary>
    /// <param name="path">A session's working directory.</param>
    bool Exists(string path);

    /// <summary>The attribution for a directory outside every known checkout, or null when it is not in a git repo.</summary>
    /// <param name="path">A session's working directory.</param>
    Attribution? Resolve(string path);
}

/// <summary>The sessions a scan found, and how many transcripts it read.</summary>
/// <param name="Sessions">The classified sessions.</param>
/// <param name="TranscriptsRead">How many transcript files were read.</param>
public sealed record SessionScan(IReadOnlyList<SessionFacts> Sessions, int TranscriptsRead);

/// <summary>A directory probe over the real filesystem, with the session-store resolver supplied by the caller.</summary>
/// <param name="resolveUnknown">Reads the repo and branch a directory outside the estate belongs to.</param>
public sealed class FileSystemFolderProbe(Func<string, Attribution?> resolveUnknown) : IWorkFolderProbe
{
    /// <inheritdoc />
    public bool Exists(string path) => Directory.Exists(path);

    /// <inheritdoc />
    public Attribution? Resolve(string path) => resolveUnknown(path);
}
