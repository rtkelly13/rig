namespace Rig.Core.WorkItems;

/// <summary>
/// Path comparison and normalisation, as <c>os.path.normcase(os.path.normpath(...))</c> gives it: separators
/// folded to the platform's, <c>.</c> and <c>..</c> collapsed, and case folded on Windows only.
/// </summary>
internal static class WorkPaths
{
    /// <summary>The comparison of two normalised paths.</summary>
    public static StringComparison Comparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>The comparer for normalised paths.</summary>
    public static StringComparer Comparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary><paramref name="path"/> in the form used for prefix comparison.</summary>
    public static string Normalise(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var separator = Path.DirectorySeparatorChar;
        var replaced = separator == '/' ? path : path.Replace('/', separator);
        var rooted = replaced.Length > 0 && replaced[0] == separator;
        var parts = new List<string>();
        foreach (var part in replaced.Split(separator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".")
            {
                continue;
            }

            if (part == "..")
            {
                if (parts.Count > 0 && parts[^1] != "..")
                {
                    parts.RemoveAt(parts.Count - 1);
                }
                else if (!rooted)
                {
                    parts.Add(part);
                }

                continue;
            }

            parts.Add(part);
        }

        var joined = string.Join(separator, parts);
        var normalised = rooted ? separator + joined : joined.Length > 0 ? joined : ".";
        return OperatingSystem.IsWindows() ? normalised.ToLowerInvariant() : normalised;
    }

    /// <summary>Whether <paramref name="target"/> is <paramref name="folder"/> or lies inside it; both normalised.</summary>
    public static bool IsUnder(string target, string folder)
    {
        var trimmed = folder.TrimEnd(Path.DirectorySeparatorChar);
        return target.Equals(trimmed, Comparison)
            || target.StartsWith(trimmed + Path.DirectorySeparatorChar, Comparison);
    }
}
