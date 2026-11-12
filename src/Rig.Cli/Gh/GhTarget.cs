using Rig.Cli.Gh.Threads;
using Rig.Core.GitHub;

namespace Rig.Cli.Gh;

/// <summary>Reads the GitHub repo the working directory's <c>origin</c> points at, or null when there is none.</summary>
internal delegate Task<RepoSlug?> OriginReader(CancellationToken cancellationToken);

/// <summary>Which repository a <c>rig gh</c> command acts on: <c>--repo owner/name</c>, or this clone's origin.</summary>
internal static class GhTarget
{
    /// <summary>The repo <paramref name="repo"/> names, or the origin's when it is null. Null after the reason is written to <paramref name="error"/>.</summary>
    public static async Task<RepoSlug?> ResolveAsync(string? repo, OriginReader origin, TextWriter error, CancellationToken cancellationToken)
    {
        var slug = repo is not null ? RepoSlug.FromPath(repo) : await origin(cancellationToken).ConfigureAwait(false);
        if (slug is null)
        {
            await error.WriteLineAsync(
                repo is not null
                    ? $"Cannot tell which repo: pass --repo owner/name ({PyRepr.Of(repo)} is not one)"
                    : "Cannot tell which repo: no GitHub origin here; pass --repo owner/name").ConfigureAwait(false);
        }

        return slug;
    }
}
