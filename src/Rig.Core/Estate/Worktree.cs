using Rig.Core.Processes;

namespace Rig.Core.Estate;

/// <summary>One entry of <c>git worktree list --porcelain</c>. Ported from <c>src/rig/estate/checks.py</c>.</summary>
/// <param name="Path">The worktree's folder, as git reports it.</param>
/// <param name="Head">The commit HEAD names.</param>
/// <param name="Branch">The branch checked out, without <c>refs/heads/</c>; empty when detached.</param>
/// <param name="Detached">HEAD is detached.</param>
/// <param name="Bare">The bare repository's own entry.</param>
/// <param name="Locked">Locked against pruning.</param>
/// <param name="LockReason">Why, when git was told.</param>
/// <param name="Prunable">Registered, but git would prune it.</param>
/// <param name="PruneReason">Why, as git puts it.</param>
/// <param name="IsMain">The first entry: the checkout itself.</param>
public sealed record Worktree(
    string Path,
    string Head = "",
    string Branch = "",
    bool Detached = false,
    bool Bare = false,
    bool Locked = false,
    string LockReason = "",
    bool Prunable = false,
    string PruneReason = "",
    bool IsMain = false)
{
    /// <summary>HEAD names a branch whose ref does not exist: porcelain prints the null object id for it.</summary>
    public bool Unborn => Branch.Length > 0 && Head.Length > 0 && Head.All(c => c == '0');

    /// <summary>Parses <c>git worktree list --porcelain</c>; blank lines separate the records.</summary>
    public static IReadOnlyList<Worktree> Parse(string porcelain)
    {
        ArgumentNullException.ThrowIfNull(porcelain);
        var found = new List<Worktree>();
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);

        void Flush()
        {
            if (fields.TryGetValue("worktree", out var path))
            {
                var branch = fields.GetValueOrDefault("branch", string.Empty);
                found.Add(new Worktree(
                    path,
                    fields.GetValueOrDefault("HEAD", string.Empty),
                    branch.StartsWith("refs/heads/", StringComparison.Ordinal) ? branch["refs/heads/".Length..] : branch,
                    fields.ContainsKey("detached"),
                    fields.ContainsKey("bare"),
                    fields.TryGetValue("locked", out var lockReason),
                    lockReason ?? string.Empty,
                    fields.TryGetValue("prunable", out var pruneReason),
                    pruneReason ?? string.Empty,
                    IsMain: found.Count == 0));
            }

            fields.Clear();
        }

        foreach (var line in porcelain.ReplaceLineEndings("\n").Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                Flush();
                continue;
            }

            var space = line.IndexOf(' ', StringComparison.Ordinal);
            fields[space < 0 ? line : line[..space]] = space < 0 ? string.Empty : line[(space + 1)..];
        }

        Flush();
        return found;
    }

    /// <summary>The worktrees registered against <paramref name="repo"/>; none when git cannot say.</summary>
    public static async Task<IReadOnlyList<Worktree>> ListAsync(Git git, string repo)
    {
        ArgumentNullException.ThrowIfNull(git);
        var ran = await git.RunAsync(repo, ["worktree", "list", "--porcelain"]).ConfigureAwait(false);
        return ran.Ok ? Parse(ran.Out) : [];
    }
}
