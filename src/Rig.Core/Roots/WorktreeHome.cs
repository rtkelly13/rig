namespace Rig.Core.Roots;

/// <summary>
/// Where a clone's linked worktrees live: beside it, at <c>&lt;scope&gt;/.worktrees/&lt;repo&gt;/&lt;name&gt;</c>. Outside
/// every repo, build config that walks up the tree (<c>Directory.*.props</c>, <c>.globalconfig</c>, <c>tsconfig</c>)
/// never reaches the parent checkout, while the scope's <c>AGENTS.md</c> still applies. The older in-repo containers
/// (<c>.worktrees/</c>, <c>temp/</c>) are still read so existing worktrees keep working.
/// </summary>
public static class WorktreeHome
{
    /// <summary>The folder name, under the scope folder, that holds every repo's worktrees.</summary>
    public const string Folder = ".worktrees";

    /// <summary>The folder that holds <paramref name="clone"/>'s worktrees.</summary>
    public static string For(string clone)
    {
        ArgumentNullException.ThrowIfNull(clone);
        var full = Path.GetFullPath(clone).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return Path.Join(Path.GetDirectoryName(full) ?? full, Folder, Path.GetFileName(full));
    }
}
