using Rig.Core.GitHub;
using Rig.Core.Processes;
using Rig.Core.Roots;

namespace Rig.Core.Governance;

/// <summary>The key a local checkout's governance policy is looked up by. Ported from <c>rig.repo_governance.policy_key</c>.</summary>
public static class PolicyKeys
{
    /// <summary>
    /// <c>owner/name</c> from the checkout's origin remote, else <c>&lt;org&gt;/&lt;folder&gt;</c> for a checkout in an
    /// org folder, else the folder name. Only a folder that is itself a checkout is asked for a remote, since git would
    /// otherwise answer for an enclosing repo.
    /// </summary>
    public static async Task<string> OfAsync(Git git, EstateScopes scopes, string repoPath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(git);
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(repoPath);
        string? origin = null;
        if (Path.Exists(Path.Join(repoPath, ".git")))
        {
            var ran = await git.RunAsync(repoPath, ["config", "--get", "remote.origin.url"], TimeSpan.FromSeconds(5), cancellationToken: cancellationToken).ConfigureAwait(false);
            origin = ran.OutOrEmpty;
        }

        return From(scopes, repoPath, origin);
    }

    /// <summary>The key for <paramref name="repoPath"/> given its origin remote (null or empty when it has none).</summary>
    public static string From(EstateScopes scopes, string repoPath, string? origin)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(repoPath);
        if (RepoSlug.FromRemote(origin) is { } slug)
        {
            return slug.FullName;
        }

        var trimmed = Path.TrimEndingDirectorySeparator(repoPath);
        var name = Path.GetFileName(trimmed);
        var org = scopes.OrgFolder(Path.GetFileName(Path.GetDirectoryName(trimmed)) ?? string.Empty);
        return org is null ? name : $"{org}/{name}";
    }
}
