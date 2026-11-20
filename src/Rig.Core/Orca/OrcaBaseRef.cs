using System.Collections.Concurrent;
using System.Globalization;
using Rig.Core.Processes;
using Rig.Core.WorkItems;

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
    /// The <c>for-each-ref</c> fields read for every local branch: its short name, upstream and ahead/behind track.
    /// One call answers every branch, so a doctor run makes one round trip per repo rather than three per ref.
    /// </summary>
    public const string BranchFormat = "%(refname:short)%09%(upstream:short)%09%(upstream:track)";

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
    /// The upstream and track text <c>for-each-ref</c> reported for <paramref name="branch"/>, or null when the
    /// branch is not in the output.
    /// </summary>
    /// <param name="output">Raw <c>for-each-ref</c> output in <see cref="BranchFormat"/>.</param>
    /// <param name="branch">The local branch to find.</param>
    public static (string Upstream, string Track)? FindBranch(string output, string branch)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(branch);
        foreach (var line in output.ReplaceLineEndings("\n").Split('\n'))
        {
            var parts = line.Split('\t');
            if (parts.Length == 3 && parts[0] == branch)
            {
                return (parts[1], parts[2]);
            }
        }

        return null;
    }

    /// <summary>
    /// The state <c>for-each-ref</c> reported for <paramref name="branch"/> when its configured upstream is
    /// <c>&lt;remote&gt;/&lt;branch&gt;</c>; null when the upstream differs or was never set, and the caller must
    /// compare the two refs itself.
    /// </summary>
    /// <param name="output">Raw <c>for-each-ref</c> output in <see cref="BranchFormat"/>.</param>
    /// <param name="branch">The local branch.</param>
    /// <param name="remote">The remote.</param>
    public static LocalBaseState? FromTrack(string output, string branch, string remote)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(branch);
        ArgumentNullException.ThrowIfNull(remote);
        if (FindBranch(output, branch) is not { } row || row.Upstream != $"{remote}/{branch}")
        {
            return null;
        }

        var track = WorkGitParsers.ParseTrack(row.Upstream, row.Track);
        return track is { Gone: false, Ahead: int ahead, Behind: int behind }
            ? new LocalBaseState(branch, remote, ahead, behind)
            : null;
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
    /// Reads each repo's local branch against <c>&lt;remote&gt;/&lt;branch&gt;</c>. One <c>for-each-ref</c> answers
    /// the branch and its upstream track; a <c>rev-list</c> fallback runs only when the configured upstream is not
    /// the policy ref. A repo whose branch or remote-tracking ref is missing, or whose git read fails, is left out.
    /// </summary>
    /// <param name="git">Runs git.</param>
    /// <param name="repos">Each repo's display name, checkout path and configured <c>base_ref</c>.</param>
    /// <param name="cancellationToken">Stops the reads.</param>
    /// <param name="maxConcurrency">
    /// How many repos to read at once. The default, one, keeps a caller's process fake deterministic; the doctor
    /// passes a bounded fan-out so a repo's spawn does not wait behind the previous repo's.
    /// </param>
    public static async Task<IReadOnlyDictionary<string, LocalBaseState>> InspectAsync(
        Git git,
        IReadOnlyList<(string Name, string Path, string BaseRef)> repos,
        CancellationToken cancellationToken,
        int maxConcurrency = 1)
    {
        ArgumentNullException.ThrowIfNull(git);
        ArgumentNullException.ThrowIfNull(repos);
        var states = new ConcurrentDictionary<string, LocalBaseState>(StringComparer.Ordinal);
        if (maxConcurrency <= 1)
        {
            foreach (var repo in repos)
            {
                if (await InspectOneAsync(git, repo, cancellationToken).ConfigureAwait(false) is { } state)
                {
                    states[repo.Name] = state;
                }
            }

            return states;
        }

        await Parallel.ForEachAsync(
            repos,
            new ParallelOptions { MaxDegreeOfParallelism = maxConcurrency, CancellationToken = cancellationToken },
            async (repo, token) =>
            {
                if (await InspectOneAsync(git, repo, token).ConfigureAwait(false) is { } state)
                {
                    states[repo.Name] = state;
                }
            }).ConfigureAwait(false);
        return states;
    }

    private static async Task<LocalBaseState?> InspectOneAsync(
        Git git, (string Name, string Path, string BaseRef) repo, CancellationToken cancellationToken)
    {
        if (Local(repo.BaseRef) is not { } local)
        {
            return null;
        }

        var (branch, remote) = local;
        var refs = await git.RunAsync(
            repo.Path,
            ["for-each-ref", $"--format={BranchFormat}", "refs/heads"],
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var output = refs.Ok ? refs.Out : string.Empty;
        if (FindBranch(output, branch) is null)
        {
            return null;
        }

        if (FromTrack(output, branch, remote) is { } state)
        {
            return state;
        }

        var ran = await git.RunAsync(
            repo.Path,
            ["rev-list", "--count", "--left-right", $"{branch}...{remote}/{branch}"],
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return Parse(branch, remote, ran.OutOrEmpty);
    }
}
