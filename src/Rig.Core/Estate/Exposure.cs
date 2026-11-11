using System.Globalization;
using Rig.Core.Processes;

namespace Rig.Core.Estate;

/// <summary>
/// One clone's irreplaceable state: what exists on this machine and nowhere else. Ported from
/// <c>src/rig/estate/risk.py</c>, the only estate module about loss; everything else the estate reports regenerates.
/// </summary>
/// <param name="Repo">The clone.</param>
/// <param name="LocalOnlyCommits">Distinct commits reachable from a local ref and on no remote.</param>
/// <param name="UnpushedBranches">Branches carrying at least one such commit; empty unless asked for.</param>
/// <param name="DirtyFiles">Uncommitted paths, staged or not.</param>
/// <param name="Stashes">Stash entries, one stack shared by every worktree of the clone.</param>
/// <param name="HasRemote">False for a clone with no remote, where every commit is local-only.</param>
/// <param name="Origin">The origin URL, or empty.</param>
public sealed record Exposure(
    string Repo,
    int LocalOnlyCommits = 0,
    IReadOnlyList<string>? UnpushedBranches = null,
    int DirtyFiles = 0,
    int Stashes = 0,
    bool HasRemote = true,
    string Origin = "")
{
    /// <summary>The branches carrying unpushed work, never null.</summary>
    public IReadOnlyList<string> Branches => UnpushedBranches ?? [];

    /// <summary>True when something here would be lost with the disk.</summary>
    public bool AtRisk => LocalOnlyCommits > 0 || DirtyFiles > 0 || Stashes > 0;

    /// <summary>A sort weight, not a unit: commits outrank dirt because there is more of it.</summary>
    public int Score => (LocalOnlyCommits * 10) + (DirtyFiles * 3) + (Stashes * 5);

    /// <summary>
    /// Measures <paramref name="repo"/>. Local-only commits are counted once across all refs
    /// (<c>rev-list --all --not --remotes</c>): summing per-branch counts overcounts, since branches share history.
    /// </summary>
    /// <param name="git">Runs git.</param>
    /// <param name="repo">The clone.</param>
    /// <param name="perBranch">Also name the branches carrying unpushed work: one <c>rev-list</c> per branch.</param>
    public static async Task<Exposure> InspectAsync(Git git, string repo, bool perBranch)
    {
        ArgumentNullException.ThrowIfNull(git);
        var remotes = await git.OutAsync(repo, "remote").ConfigureAwait(false);
        var origin = await git.OutAsync(repo, "remote", "get-url", "origin").ConfigureAwait(false);
        var dirty = (await git.LinesAsync(repo, "status", "--porcelain").ConfigureAwait(false))?.Count ?? 0;
        var stashes = (await git.LinesAsync(repo, "stash", "list").ConfigureAwait(false))?.Count ?? 0;
        return new Exposure(
            repo,
            await CountAsync(git, repo, "rev-list", "--count", "--all", "--not", "--remotes").ConfigureAwait(false),
            perBranch ? await UnpushedBranchesAsync(git, repo).ConfigureAwait(false) : [],
            dirty,
            stashes,
            remotes.Length > 0,
            origin);
    }

    /// <summary>The number <c>git <paramref name="args"/></c> prints, or zero when it prints anything else.</summary>
    public static async Task<int> CountAsync(Git git, string repo, params string[] args)
    {
        ArgumentNullException.ThrowIfNull(git);
        var output = await git.OutAsync(repo, args).ConfigureAwait(false);
        return output.Length > 0 && output.All(char.IsAsciiDigit) && int.TryParse(output, CultureInfo.InvariantCulture, out var count) ? count : 0;
    }

    // Local branches holding a commit that is on no remote: the answer to "what do I push".
    private static async Task<IReadOnlyList<string>> UnpushedBranchesAsync(Git git, string repo)
    {
        var carrying = new List<string>();
        foreach (var name in await git.LinesAsync(repo, "for-each-ref", "--format=%(refname:short)", "refs/heads").ConfigureAwait(false) ?? [])
        {
            if (await CountAsync(git, repo, "rev-list", "--count", name, "--not", "--remotes").ConfigureAwait(false) > 0)
            {
                carrying.Add(name);
            }
        }

        return carrying;
    }
}

/// <summary>Two or more checkouts of one origin, whose local-only commits an estate total would count twice.</summary>
/// <param name="Origin">The origin, as <c>host/owner/name</c>.</param>
/// <param name="Repos">The checkouts.</param>
/// <param name="LocalOnly">Local-only commits per checkout folder name, in checkout order.</param>
public sealed record DuplicateClone(string Origin, IReadOnlyList<string> Repos, IReadOnlyList<KeyValuePair<string, int>> LocalOnly)
{
    /// <summary>The checkouts that share an origin, by origin.</summary>
    public static IReadOnlyList<DuplicateClone> Find(IEnumerable<Exposure> exposures)
    {
        ArgumentNullException.ThrowIfNull(exposures);
        return [.. exposures
            .Where(e => e.Origin.Length > 0)
            .GroupBy(e => Normalise(e.Origin), StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new DuplicateClone(
                group.Key,
                [.. group.Select(e => e.Repo)],
                Distinct(group.Select(e => new KeyValuePair<string, int>(Path.GetFileName(e.Repo), e.LocalOnlyCommits)))))];
    }

    /// <summary>A remote URL reduced to <c>host/owner/name</c>, so the SSH and HTTPS forms of one repo match.</summary>
    public static string Normalise(string remote)
    {
        ArgumentNullException.ThrowIfNull(remote);
        var cleaned = remote.Trim();
        cleaned = cleaned.EndsWith(".git", StringComparison.Ordinal) ? cleaned[..^4] : cleaned;
        foreach (var prefix in new[] { "git@", "ssh://git@", "https://", "http://" })
        {
            cleaned = cleaned.StartsWith(prefix, StringComparison.Ordinal) ? cleaned[prefix.Length..] : cleaned;
        }

        var colon = cleaned.IndexOf(':', StringComparison.Ordinal);
        return colon < 0 ? cleaned : string.Concat(cleaned.AsSpan(0, colon), "/", cleaned.AsSpan(colon + 1));
    }

    // Two checkouts with one folder name keep the later count, as a mapping keyed by name does.
    private static List<KeyValuePair<string, int>> Distinct(IEnumerable<KeyValuePair<string, int>> pairs)
    {
        var kept = new List<KeyValuePair<string, int>>();
        foreach (var pair in pairs)
        {
            var at = kept.FindIndex(p => p.Key == pair.Key);
            if (at < 0)
            {
                kept.Add(pair);
            }
            else
            {
                kept[at] = pair;
            }
        }

        return kept;
    }
}
