using System.Globalization;
using Rig.Core.Processes;

namespace Rig.Core.Estate;

/// <summary>
/// Reads <see cref="WorktreeFacts"/> from git. One port for every command that removes worktrees, so the facts and the
/// decision cannot drift apart again.
/// </summary>
/// <remarks>
/// Every read that fails is conservative: a status git cannot give counts as uncommitted, a commit count it cannot give
/// counts as local-only, and a reflog it cannot read refuses rather than reporting zero. The one exception is an absent
/// reflog, which <c>git log -g</c> reports successfully with no output.
/// </remarks>
/// <param name="git">Runs git.</param>
public sealed class GitWorktreePort(Git git) : IGitWorktreePort
{
    private const string ReflogFormat = "--format=%H";

    /// <inheritdoc />
    public async Task<WorktreeFacts> ReadAsync(string clone, Worktree tree, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tree);
        var common = await git.OutAsync(clone, "rev-parse", "--path-format=absolute", "--git-common-dir").ConfigureAwait(false);
        var foreign = common.Length > 0 && RepoChecks.ForeignAdminDir(tree.Path, Path.Join(common, "worktrees")) is not null;
        var status = await git.RunAsync(tree.Path, ["status", "--porcelain"], cancellationToken: cancellationToken).ConfigureAwait(false);
        var ignored = await git.RunAsync(
            tree.Path,
            ["ls-files", "-z", "--others", "--ignored", "--exclude-standard", "--directory"],
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var notes = ignored.Ok
            ? ignored.Out.Split('\0').Where(entry => entry.Length > 0 && !Shelver.IsRegenerable(entry)).ToList()
            : [];
        var unpushed = await UnpushedAsync(tree, cancellationToken).ConfigureAwait(false);
        var (reflogOnly, reflogUnreadable) = await ReflogOnlyAsync(tree, cancellationToken).ConfigureAwait(false);
        return new WorktreeFacts(
            tree.Path,
            tree.Branch,
            tree.Head,
            tree.Locked,
            tree.LockReason,
            tree.Unborn,
            foreign,
            !status.Ok || status.Out.Trim().Length > 0,
            notes.Count > 0,
            InUse: false,
            reflogOnly,
            unpushed)
        {
            ReflogUnreadable = reflogUnreadable,
            IgnoredUnreadable = !ignored.Ok,
            OwnershipUnreadable = common.Length == 0,
            IgnoredNotes = notes,
        };
    }

    // Commits on HEAD that no remote has. An unborn HEAD has none to lose; a count git cannot give is never zero.
    private async Task<int> UnpushedAsync(Worktree tree, CancellationToken cancellationToken)
    {
        var ran = await git.RunAsync(tree.Path, ["rev-list", "--count", "HEAD", "--not", "--remotes"], cancellationToken: cancellationToken).ConfigureAwait(false);
        if (ran.Ok && int.TryParse(ran.Out.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var count))
        {
            return count;
        }

        return tree.Unborn ? 0 : 1;
    }

    // Commits named by this worktree's HEAD reflog or its branch's reflog and by no ref: removing the worktree, or
    // deleting its branch, drops the last name they have. The repository is asked, never the reflog files, so a linked
    // worktree's own log and a shared branch log are both seen.
    private async Task<(int Count, bool Unreadable)> ReflogOnlyAsync(Worktree tree, CancellationToken cancellationToken)
    {
        var refs = await git.RunAsync(tree.Path, ["rev-list", "--all"], cancellationToken: cancellationToken).ConfigureAwait(false);
        var head = await git.RunAsync(tree.Path, ["log", "-g", ReflogFormat, "HEAD"], cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!refs.Ok || !head.Ok)
        {
            return (0, true);
        }

        var names = head.Out.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        IReadOnlyList<string> branch = [];
        if (!tree.Unborn && tree.Branch.Length > 0)
        {
            // A branch whose ref is gone is unborn: its tip is named by HEAD's reflog, so the branch is not queried.
            var ran = await git.RunAsync(tree.Path, ["log", "-g", ReflogFormat, tree.Branch], cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!ran.Ok)
            {
                return (0, true);
            }

            branch = ran.Out.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        }

        var reachable = refs.Out.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(StringComparer.Ordinal);
        var only = names.Concat(branch).Select(sha => sha.Trim()).Where(sha => sha.Length > 0 && !reachable.Contains(sha))
            .Distinct(StringComparer.Ordinal)
            .Count();
        return (only, false);
    }
}
