using System.Globalization;
using Rig.Core.Processes;

namespace Rig.Core.Orca;

/// <summary>How a repo's local branch stands against the remote-tracking ref Orca's new worktrees start from.</summary>
/// <param name="Branch">The local branch, such as <c>main</c>.</param>
/// <param name="Remote">The remote, such as <c>origin</c>.</param>
/// <param name="Ahead">Commits the local branch has that the remote does not.</param>
/// <param name="Behind">Commits the remote has that the local branch does not.</param>
public sealed record LocalBaseState(string Branch, string Remote, int Ahead, int Behind)
{
    /// <summary>The remote-tracking ref, such as <c>origin/main</c>.</summary>
    public string Tracking => $"{Remote}/{Branch}";
}

/// <summary>
/// Reads how each repo's local branch stands against the remote-tracking ref its <c>base_ref</c> names, so
/// <c>workspace orca doctor</c> can warn when a checkout is behind the ref new worktrees start from.
/// </summary>
public static class OrcaBaseRef
{
    /// <summary>
    /// The local branch and remote a configured base ref resolves to, or null when it names a tag or another ref
    /// rather than a branch. <c>origin/main</c> is <c>main</c> on <c>origin</c>; a bare <c>main</c> is <c>main</c>
    /// on <c>origin</c>, Orca's default remote.
    /// </summary>
    /// <param name="baseRef">The policy's <c>base_ref</c>.</param>
    public static (string Branch, string Remote)? Local(string baseRef)
    {
        ArgumentNullException.ThrowIfNull(baseRef);
        var reference = baseRef.Trim();
        if (reference.Length == 0)
        {
            return null;
        }

        if (reference.StartsWith("refs/heads/", StringComparison.Ordinal))
        {
            var branch = reference["refs/heads/".Length..];
            return branch.Length > 0 ? (branch, "origin") : null;
        }

        if (reference.StartsWith("refs/remotes/", StringComparison.Ordinal))
        {
            reference = reference["refs/remotes/".Length..];
        }
        else if (reference.StartsWith("refs/", StringComparison.Ordinal))
        {
            return null;
        }

        var slash = reference.IndexOf('/', StringComparison.Ordinal);
        if (slash < 0)
        {
            return (reference, "origin");
        }

        var remote = reference[..slash];
        var local = reference[(slash + 1)..];
        return remote.Length > 0 && local.Length > 0 ? (local, remote) : null;
    }

    /// <summary>
    /// The state <c>git rev-list --count --left-right &lt;branch&gt;...&lt;remote&gt;/&lt;branch&gt;</c> reports:
    /// <c>ahead&#9;behind</c>.
    /// </summary>
    /// <param name="branch">The local branch.</param>
    /// <param name="remote">The remote.</param>
    /// <param name="output">The command's output.</param>
    public static LocalBaseState? Parse(string branch, string remote, string output)
    {
        ArgumentNullException.ThrowIfNull(branch);
        ArgumentNullException.ThrowIfNull(remote);
        ArgumentNullException.ThrowIfNull(output);
        var parts = output.Trim().Split('\t');
        return parts.Length == 2
            && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ahead)
            && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var behind)
            ? new LocalBaseState(branch, remote, ahead, behind)
            : null;
    }

    /// <summary>
    /// Reads each repo's local branch against <c>&lt;remote&gt;/&lt;branch&gt;</c>. A repo whose branch or
    /// remote-tracking ref is missing, or whose git read fails, is left out: there is nothing to compare.
    /// </summary>
    /// <param name="git">Runs git.</param>
    /// <param name="repos">Each repo's display name, checkout path and configured <c>base_ref</c>.</param>
    /// <param name="cancellationToken">Stops the reads.</param>
    public static async Task<IReadOnlyDictionary<string, LocalBaseState>> InspectAsync(
        Git git,
        IReadOnlyList<(string Name, string Path, string BaseRef)> repos,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(git);
        ArgumentNullException.ThrowIfNull(repos);
        var states = new Dictionary<string, LocalBaseState>(StringComparer.Ordinal);
        foreach (var (name, path, baseRef) in repos)
        {
            if (Local(baseRef) is not { } local)
            {
                continue;
            }

            var (branch, remote) = local;
            if (!await ExistsAsync(git, path, $"refs/heads/{branch}", cancellationToken).ConfigureAwait(false)
                || !await ExistsAsync(git, path, $"refs/remotes/{remote}/{branch}", cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            var ran = await git.RunAsync(
                path,
                ["rev-list", "--count", "--left-right", $"{branch}...{remote}/{branch}"],
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (Parse(branch, remote, ran.OutOrEmpty) is { } state)
            {
                states[name] = state;
            }
        }

        return states;
    }

    private static async Task<bool> ExistsAsync(Git git, string path, string reference, CancellationToken cancellationToken)
    {
        var ran = await git.RunAsync(path, ["rev-parse", "--verify", "--quiet", reference], cancellationToken: cancellationToken).ConfigureAwait(false);
        return ran.OutOrEmpty.Length > 0;
    }
}
