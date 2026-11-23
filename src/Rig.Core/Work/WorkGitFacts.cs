using Rig.Core.Processes;
using Rig.Core.Text;
using Rig.Core.Worktrees;

namespace Rig.Core.WorkItems;

/// <summary>The local branches and default branch one <c>git for-each-ref</c> answered with.</summary>
/// <param name="Branches">The local branches, in ref order.</param>
/// <param name="DefaultBranch">The branch <c>origin/HEAD</c> names; empty when the clone has no such ref.</param>
public sealed record RefScan(IReadOnlyList<BranchRef> Branches, string DefaultBranch);

/// <summary>
/// The pure parsers for the three git reads the work list makes,
/// </summary>
public static class WorkGitParsers
{
    /// <summary>A clone with no remote would otherwise list its whole history; the unpushed walk stops here.</summary>
    public const int UnpushedLimit = 5000;

    /// <summary>How many clones or checkouts are read at once.</summary>
    public const int DefaultWorkers = 16;

    private const char Field = '\t';
    private const int RefFields = 6;

    /// <summary>The <c>for-each-ref</c> format: refname, sha, committer date, upstream, track state and symref.</summary>
    public const string RefFormat =
        "%(refname)\t%(objectname)\t%(committerdate:iso-strict)\t%(upstream:short)\t%(upstream:track)\t%(symref)";

    /// <summary>The tip sha of every complete line of <see cref="RefFormat"/> output.</summary>
    /// <param name="output">Raw <c>git for-each-ref</c> output.</param>
    public static IReadOnlyList<string> RefTips(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var tips = new List<string>();
        foreach (var line in Lines(output))
        {
            var parts = line.Split(Field);
            if (parts.Length == RefFields)
            {
                tips.Add(parts[1]);
            }
        }

        return tips;
    }

    /// <summary>
    /// <c>{tip sha: commits on no remote reachable from it}</c>. A tip outside the set is fully pushed. The walk stays
    /// inside the set: a parent outside it is on a remote, and so is its history.
    /// </summary>
    /// <param name="revListParents">Raw <c>git rev-list --parents</c> output over the commits on no remote.</param>
    /// <param name="tips">The branch tips to count for.</param>
    public static IReadOnlyDictionary<string, int> UnpushedCounts(string revListParents, IEnumerable<string> tips)
    {
        ArgumentNullException.ThrowIfNull(revListParents);
        ArgumentNullException.ThrowIfNull(tips);
        var parents = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var line in Lines(revListParents))
        {
            var shas = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (shas.Length > 0)
            {
                parents[shas[0]] = [.. shas.Skip(1)];
            }
        }

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var tip in tips)
        {
            if (!parents.ContainsKey(tip))
            {
                counts[tip] = 0;
                continue;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal) { tip };
            var stack = new Stack<string>();
            stack.Push(tip);
            while (stack.Count > 0)
            {
                foreach (var parent in parents[stack.Pop()])
                {
                    if (parents.ContainsKey(parent) && seen.Add(parent))
                    {
                        stack.Push(parent);
                    }
                }
            }

            counts[tip] = seen.Count;
        }

        return counts;
    }

    /// <summary>Parses <see cref="RefFormat"/> output over <c>refs/heads</c> and <c>refs/remotes/origin/HEAD</c>.</summary>
    /// <param name="output">Raw <c>git for-each-ref</c> output.</param>
    /// <param name="unpushed">A tip sha's unpushed count; a missing entry means 0.</param>
    public static RefScan ParseRefs(string output, IReadOnlyDictionary<string, int>? unpushed = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        var branches = new List<BranchRef>();
        var defaultBranch = string.Empty;
        foreach (var line in Lines(output))
        {
            var parts = line.Split(Field);
            if (parts.Length != RefFields)
            {
                continue;
            }

            var (refname, sha, date, upstream, track, symref) = (parts[0], parts[1], parts[2], parts[3], parts[4], parts[5]);
            if (refname == "refs/remotes/origin/HEAD")
            {
                defaultBranch = symref.StartsWith("refs/remotes/origin/", StringComparison.Ordinal)
                    ? symref["refs/remotes/origin/".Length..]
                    : symref;
                continue;
            }

            if (!refname.StartsWith("refs/heads/", StringComparison.Ordinal))
            {
                continue;
            }

            var tracked = Track.Parse(upstream, track);
            branches.Add(new BranchRef(
                refname["refs/heads/".Length..],
                IsoTime.Parse(date),
                upstream,
                tracked.Gone,
                tracked.Ahead,
                tracked.Behind,
                unpushed is not null && unpushed.TryGetValue(sha, out var count) ? count : 0));
        }

        return new RefScan(branches, defaultBranch);
    }

    /// <summary>The clone's display name: its path relative to the root, with forward slashes.</summary>
    /// <param name="repo">The clone's folder.</param>
    /// <param name="root">The code root.</param>
    public static string RepoDisplayName(string repo, string root)
    {
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(root);
        var trimmed = repo.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var relative = Path.GetRelativePath(root, trimmed);
        var outside = Path.IsPathRooted(relative)
            || relative == ".."
            || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
        if (outside)
        {
            return Path.GetFileName(trimmed);
        }

        return relative.Replace(Path.DirectorySeparatorChar, '/');
    }

    private static string[] Lines(string output) =>
        output.ReplaceLineEndings("\n").Split('\n');
}

/// <summary>
/// Reads the work list's git facts through <see cref="Git"/> and <see cref="CloneDiscovery"/>,
/// </summary>
/// <param name="git">Runs git.</param>
/// <param name="discovery">Finds the clones under the code root.</param>
/// <param name="workers">How many clones or checkouts are read at once.</param>
public sealed class WorkGitFacts(Git git, CloneDiscovery discovery, int workers = WorkGitParsers.DefaultWorkers) : IWorkGitFacts
{
    private static readonly string[] FallbackDefaultBranches = ["main", "master"];
    /// <inheritdoc />
    public async Task<IReadOnlyList<RepoFacts>> ReadEstateAsync(string root, bool refresh, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(root);
        var paths = discovery.Repos(root, refresh).Paths;
        var results = new RepoFacts[paths.Count];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, paths.Count),
            new ParallelOptions { MaxDegreeOfParallelism = workers, CancellationToken = cancellationToken },
            async (index, token) => results[index] = await ReadRepoAsync(paths[index], root, token).ConfigureAwait(false)).ConfigureAwait(false);
        return results;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RepoFacts>> ProbeCheckoutsAsync(IReadOnlyList<RepoFacts> repos, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repos);
        var results = new IReadOnlyList<Checkout>[repos.Count];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, repos.Count),
            new ParallelOptions { MaxDegreeOfParallelism = workers, CancellationToken = cancellationToken },
            async (index, token) =>
            {
                var probed = new List<Checkout>(repos[index].Checkouts.Count);
                foreach (var checkout in repos[index].Checkouts)
                {
                    probed.Add(await ProbeAsync(checkout, token).ConfigureAwait(false));
                }

                results[index] = probed;
            }).ConfigureAwait(false);
        return [.. repos.Select((repo, index) => repo with { Checkouts = results[index] })];
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, string>> ReadSlugsAsync(IReadOnlyList<string> repos, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repos);
        var slugs = new string[repos.Count];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, repos.Count),
            new ParallelOptions { MaxDegreeOfParallelism = workers, CancellationToken = cancellationToken },
            async (index, token) => slugs[index] = await ReadSlugAsync(repos[index], token).ConfigureAwait(false)).ConfigureAwait(false);
        var found = new Dictionary<string, string>(WorkPaths.Comparer);
        for (var index = 0; index < repos.Count; index++)
        {
            found[repos[index]] = slugs[index];
        }

        return found;
    }

    /// <summary>The cheap facts for one clone: its checkouts and branch refs. No status, no network.</summary>
    /// <param name="repo">The clone.</param>
    /// <param name="root">The code root, for the display name.</param>
    /// <param name="cancellationToken">Stops the reads.</param>
    public async Task<RepoFacts> ReadRepoAsync(string repo, string root, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(root);
        var refs = await git.RunAsync(repo, ["for-each-ref", $"--format={WorkGitParsers.RefFormat}", "refs/heads", "refs/remotes/origin/HEAD"], cancellationToken: cancellationToken).ConfigureAwait(false);
        var refText = refs.Ok ? refs.Out : string.Empty;
        var orphans = await git.RunAsync(
            repo,
            ["rev-list", "--parents", $"--max-count={WorkGitParsers.UnpushedLimit}", "--branches", "--not", "--remotes"],
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var scan = WorkGitParsers.ParseRefs(
            refText,
            WorkGitParsers.UnpushedCounts(orphans.Ok ? orphans.Out : string.Empty, WorkGitParsers.RefTips(refText)));
        var names = scan.Branches.Select(branch => branch.Name).ToHashSet(StringComparer.Ordinal);
        var defaultBranch = scan.DefaultBranch.Length > 0
            ? scan.DefaultBranch
            : FallbackDefaultBranches.FirstOrDefault(names.Contains) ?? string.Empty;
        return new RepoFacts(repo, WorkGitParsers.RepoDisplayName(repo, root), defaultBranch, await CheckoutsAsync(repo, cancellationToken).ConfigureAwait(false))
        {
            Branches = scan.Branches,
        };
    }

    /// <summary>The clone's <c>origin</c> as <c>owner/name</c>, empty when it is not a GitHub remote.</summary>
    /// <param name="repo">The clone.</param>
    /// <param name="cancellationToken">Stops the read.</param>
    public async Task<string> ReadSlugAsync(string repo, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repo);
        var ran = await git.RunAsync(repo, ["remote", "get-url", "origin"], TimeSpan.FromSeconds(10), cancellationToken: cancellationToken).ConfigureAwait(false);
        return ran.Ok ? RepoSlug.FromRemote(ran.Out.Trim())?.FullName ?? string.Empty : string.Empty;
    }

    internal async Task<IReadOnlyList<Checkout>> CheckoutsAsync(string repo, CancellationToken cancellationToken)
    {
        var trees = await Worktree.ListAsync(git, repo, cancellationToken).ConfigureAwait(false);
        var checkouts = new List<Checkout>();
        foreach (var tree in trees)
        {
            if (tree.Bare || tree.Prunable)
            {
                continue;
            }

            checkouts.Add(new Checkout(tree.Path, tree.Branch, tree.IsMain));
        }

        // git failed; still list the clone itself.
        return checkouts.Count > 0 ? checkouts : [new Checkout(repo, string.Empty, IsMainCheckout: true)];
    }

    private async Task<Checkout> ProbeAsync(Checkout checkout, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(checkout.Path))
        {
            return checkout;
        }

        var ran = await git.RunAsync(checkout.Path, ["status", "--porcelain"], cancellationToken: cancellationToken).ConfigureAwait(false);
        return checkout with { Dirty = ran.Ok ? ran.Out.Trim().Length > 0 : null };
    }
}
