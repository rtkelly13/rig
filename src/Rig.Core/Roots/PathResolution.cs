namespace Rig.Core.Roots;

/// <summary>Absolute paths with every symbolic link along them followed, as Python's <c>Path.resolve()</c> gives.</summary>
public static class PathResolution
{
    // The POSIX symlink-hop limit (ELOOP): a cycle stops here instead of recursing forever.
    private const int MaxLinkHops = 40;

    /// <summary>
    /// <paramref name="path"/> made absolute, with each existing component that is a link replaced by its fully
    /// resolved target. Components that do not exist are kept as written, and a link cycle is left unresolved.
    /// </summary>
    public static string Resolve(string path) => Resolve(Path.GetFullPath(path), MaxLinkHops);

    /// <summary>Whether <paramref name="path"/> is <paramref name="folder"/> or lies inside it. Both must be resolved.</summary>
    public static bool IsUnder(string path, string folder)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(folder);
        var comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
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
            resolved = Path.Combine(resolved, part);
            var link = new FileInfo(resolved);
            if (link.LinkTarget is { } target && hops > 0)
            {
                // A relative target is relative to the folder holding the link.
                resolved = Resolve(Path.GetFullPath(target, Path.GetDirectoryName(resolved)!), hops - 1);
            }
        }

        return resolved;
    }
}
