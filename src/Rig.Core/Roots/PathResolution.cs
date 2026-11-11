namespace Rig.Core.Roots;

/// <summary>Absolute paths with every symbolic link along them followed, as Python's <c>Path.resolve()</c> gives.</summary>
public static class PathResolution
{
    // The POSIX symlink-hop limit (ELOOP): a cycle stops here instead of recursing forever.
    private const int MaxLinkHops = 40;

    /// <summary>
    /// <paramref name="path"/> made absolute, with each existing component that is a link replaced by its fully
    /// resolved target. Components that do not exist are kept as written, and a link cycle is left unresolved.
    /// On Unix a <c>..</c> steps out of the folder reached so far, after the links before it are followed, as the
    /// kernel walks it: <c>personal/link/..</c> is the link target's parent, not <c>personal</c>. Windows collapses
    /// <c>..</c> by name before it opens anything, so there it is collapsed first.
    /// </summary>
    public static string Resolve(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var absolute = OperatingSystem.IsWindows() ? Path.GetFullPath(path) : Path.Combine(Environment.CurrentDirectory, path);
        return Resolve(absolute, MaxLinkHops);
    }

    /// <summary>
    /// How two resolved paths compare: exactly on Linux, ignoring case on Windows and macOS, whose default file systems
    /// do. git can name one folder in a different case (a lower-case drive letter), so equal paths must compare this way.
    /// </summary>
    public static StringComparison Comparison => OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    /// <summary>Whether two resolved paths name the same place, trailing separators aside.</summary>
    public static bool Same(string path, string other)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(other);
        return path.TrimEnd(Path.DirectorySeparatorChar).Equals(other.TrimEnd(Path.DirectorySeparatorChar), Comparison);
    }

    /// <summary>Whether <paramref name="path"/> is <paramref name="folder"/> or lies inside it. Both must be resolved.</summary>
    public static bool IsUnder(string path, string folder)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(folder);
        var comparison = Comparison;
        var trimmed = folder.TrimEnd(Path.DirectorySeparatorChar);
        return path.Equals(trimmed, comparison)
            || path.StartsWith(trimmed + Path.DirectorySeparatorChar, comparison);
    }

    private static string Resolve(string full, int hops)
    {
        var root = Path.GetPathRoot(full) ?? string.Empty;
        var resolved = root;
        foreach (var part in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".")
            {
                continue;
            }

            if (part == "..")
            {
                // Everything so far is link-free, so its parent by name is its real parent.
                resolved = Path.GetDirectoryName(resolved) ?? resolved;
                continue;
            }

            resolved = Path.Combine(resolved, part);
            var link = new FileInfo(resolved);
            if (link.LinkTarget is { } target && hops > 0)
            {
                // A relative target is relative to the folder holding the link, and its own ".." walk the same way.
                resolved = Resolve(Path.Combine(Path.GetDirectoryName(resolved)!, target), hops - 1);
            }
        }

        return resolved;
    }
}
