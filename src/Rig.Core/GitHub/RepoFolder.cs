using Rig.Core.Processes;

namespace Rig.Core.GitHub;

/// <summary>How a clone's folder and remote compare with its repository's current GitHub name.</summary>
public enum RepoFolderStatus
{
    /// <summary>Folder and remote both use the current name.</summary>
    Ok,

    /// <summary>The folder has the name the remote asks for, which GitHub has since renamed.</summary>
    FolderRenamed,

    /// <summary>The folder has some other name.</summary>
    FolderMismatch,

    /// <summary>The folder is right but the remote still uses an old name.</summary>
    RemoteOutdated,

    /// <summary>No origin, not a repository, or a remote that is not GitHub.</summary>
    LocalOnly,

    /// <summary>GitHub could not be asked, or does not know the repository.</summary>
    Unresolved,
}

/// <summary>One clone checked against its repository's current GitHub name.</summary>
/// <param name="RepoPath">The main checkout's folder (a worktree's main checkout, not the worktree).</param>
/// <param name="Remote">The repository the origin remote names, or null when it names none.</param>
/// <param name="Canonical">The repository GitHub answers with, or null.</param>
/// <param name="Status">How they compare.</param>
/// <param name="Detail">One line saying why.</param>
public sealed record RepoFolderCheck(string RepoPath, RepoSlug? Remote, CanonicalRepo? Canonical, RepoFolderStatus Status, string Detail)
{
    /// <summary>The folder's name.</summary>
    public string FolderName => Path.GetFileName(Path.TrimEndingDirectorySeparator(RepoPath));

    /// <summary>Whether the folder has the canonical name; a local-only clone has nothing to match, so it does.</summary>
    public bool IsMatch => Status == RepoFolderStatus.LocalOnly || (Canonical is { } repo && FolderName.Equals(repo.CanonicalName, StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether the remote uses the canonical name; a local-only clone has nothing to match, so it does.</summary>
    public bool RemoteMatchesCanonical =>
        Status == RepoFolderStatus.LocalOnly || (Canonical is { } repo && Remote is { } remote && remote.Name.Equals(repo.CanonicalName, StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether the folder or remote needs attention: anything but <see cref="RepoFolderStatus.Ok"/> or local-only.</summary>
    public bool IsIssue => Status is not (RepoFolderStatus.Ok or RepoFolderStatus.LocalOnly);

    /// <summary>
    /// Compares <paramref name="repoPath"/>'s folder name and <paramref name="remote"/> with <paramref name="canonical"/>,
    /// ignoring case as GitHub does.
    /// </summary>
    public static RepoFolderCheck Classify(string repoPath, RepoSlug remote, CanonicalRepo? canonical)
    {
        ArgumentNullException.ThrowIfNull(remote);
        if (canonical is null)
        {
            return new(repoPath, remote, null, RepoFolderStatus.Unresolved, $"Could not resolve repository '{remote}' on GitHub");
        }

        var check = new RepoFolderCheck(repoPath, remote, canonical, RepoFolderStatus.Ok, string.Empty);
        var name = canonical.CanonicalName;
        var folder = check.FolderName;
        if (!folder.Equals(name, StringComparison.OrdinalIgnoreCase))
        {
            return folder.Equals(canonical.QueriedName, StringComparison.OrdinalIgnoreCase) && canonical.IsRenamed
                ? check with { Status = RepoFolderStatus.FolderRenamed, Detail = $"Folder '{folder}' is named after a previous repository name; renamed upstream to '{name}'" }
                : check with { Status = RepoFolderStatus.FolderMismatch, Detail = $"Folder '{folder}' does not match canonical upstream name '{name}'" };
        }

        return remote.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
            ? check with { Detail = $"Folder and remote match canonical name '{name}'" }
            : check with { Status = RepoFolderStatus.RemoteOutdated, Detail = $"Remote URL points to '{remote.Name}', but canonical upstream name is '{name}'" };
    }

    /// <summary>
    /// Checks the clone holding <paramref name="path"/>. A worktree is checked as its main checkout, found through
    /// <c>git rev-parse --git-common-dir</c>, since that is the folder named after the repository.
    /// </summary>
    public static async Task<RepoFolderCheck> CheckAsync(Git git, RepoResolver resolver, string path, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        var (root, origin) = await IdentifyAsync(git, path).ConfigureAwait(false);
        if (origin.Length == 0)
        {
            return new(root, null, null, RepoFolderStatus.LocalOnly, "No remote origin URL configured or not a git repository");
        }

        if (RepoSlug.FromRemote(origin) is not { } remote)
        {
            return new(root, null, null, RepoFolderStatus.LocalOnly, $"Non-GitHub remote origin ({origin})");
        }

        var resolved = await resolver.ResolveAsync([remote], cancellationToken: cancellationToken).ConfigureAwait(false);
        return Classify(root, remote, resolved.GetValueOrDefault(remote));
    }

    /// <summary>
    /// Checks every clone directly under <paramref name="parents"/> in one GitHub query, ordered by folder name ignoring
    /// case. Folders without a <c>.git</c>, and clones without a GitHub origin, are left out.
    /// </summary>
    public static async Task<IReadOnlyList<RepoFolderCheck>> EstateAsync(
        Git git,
        RepoResolver resolver,
        IEnumerable<string> parents,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(parents);
        var clones = parents
            .Where(Directory.Exists)
            .SelectMany(Directory.EnumerateDirectories)
            .Where(folder => Path.Exists(Path.Join(folder, ".git")))
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase);
        var found = new List<(string Root, RepoSlug Remote)>();
        foreach (var clone in clones)
        {
            var (root, origin) = await IdentifyAsync(git, clone).ConfigureAwait(false);
            if (RepoSlug.FromRemote(origin) is { } remote)
            {
                found.Add((root, remote));
            }
        }

        var resolved = await resolver.ResolveAsync([.. found.Select(f => f.Remote).Distinct()], forceRefresh, cancellationToken).ConfigureAwait(false);
        return [.. found.Select(f => Classify(f.Root, f.Remote, resolved.GetValueOrDefault(f.Remote)))];
    }

    // The main checkout holding path, found through --git-common-dir so a worktree answers for its main checkout, and its
    // origin URL; empty when path is not a repository or has no origin.
    private static async Task<(string Root, string Origin)> IdentifyAsync(Git git, string path)
    {
        ArgumentNullException.ThrowIfNull(git);
        var common = await git.OutAsync(path, "rev-parse", "--git-common-dir").ConfigureAwait(false);
        if (common.Length == 0)
        {
            return (path, string.Empty);
        }

        var root = Path.GetDirectoryName(Path.GetFullPath(Path.Combine(path, common)))!;
        return (root, await git.OutAsync(root, "config", "--get", "remote.origin.url").ConfigureAwait(false));
    }
}
