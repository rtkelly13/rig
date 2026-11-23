using Rig.Core.Processes;
using Rig.Core.Text;

namespace Rig.Core.Estate;

/// <summary>
/// Moves a renamed project's clone with <c>git</c> and the file system. A move loses nothing: uncommitted changes,
/// stashes and local branches travel with the folder. A clone with linked worktrees is left alone, because their
/// folders and Orca's records of them would still name the old path.
/// </summary>
/// <param name="git">Runs git in the clone.</param>
public sealed class GitCloneMover(Git git) : ICloneMover
{
    /// <inheritdoc />
    public async Task<string?> MoveAsync(string source, string destination, string repo, IReadOnlySet<string> accepted, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(accepted);
        var origin = await git.RunAsync(source, ["remote", "get-url", "origin"], cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!origin.Ok)
        {
            return "it has no origin remote git can read";
        }

        var was = origin.Out.Trim();
        if (RepoSlug.FromRemote(was) is not { } slug || !accepted.Contains(slug.Key))
        {
            return $"its origin is {was}, not one of the project's names";
        }

        var worktrees = await git.RunAsync(source, ["worktree", "list", "--porcelain"], cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!worktrees.Ok)
        {
            return "git could not list its worktrees";
        }

        if (worktrees.Out.Split('\n').Count(line => line.StartsWith("worktree ", StringComparison.Ordinal)) > 1)
        {
            return "it has linked worktrees; finish or remove them first";
        }

        var moving = !string.Equals(Path.GetFullPath(source), Path.GetFullPath(destination), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        if (moving && Path.Exists(destination))
        {
            return $"{destination} already exists";
        }

        var set = await git.RunAsync(source, ["remote", "set-url", "origin", repo], cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!set.Ok)
        {
            return $"git could not point origin at {repo}: {set.Err.Trim()}";
        }

        if (!moving)
        {
            return null;
        }

        try
        {
            if (Path.GetDirectoryName(destination) is { Length: > 0 } parent)
            {
                _ = Directory.CreateDirectory(parent);
            }

            Directory.Move(source, destination);
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _ = await git.RunAsync(source, ["remote", "set-url", "origin", was], cancellationToken: CancellationToken.None).ConfigureAwait(false);
            return $"the folder could not be moved: {e.Message}";
        }
    }
}
