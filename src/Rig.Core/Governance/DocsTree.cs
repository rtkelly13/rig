using Rig.Core.Roots;

namespace Rig.Core.Governance;

/// <summary>
/// The file-system questions the AGENTS.md audit asks, answered as Python's <c>pathlib</c> answers them, so the C#
/// audit finds what the Python one did: links are followed (a broken one does not exist), a recursive walk does not
/// descend into a linked folder, and paths sort and compare segment by segment, ignoring case only on Windows.
/// </summary>
internal static class DocsTree
{
    private static readonly char[] Separators = OperatingSystem.IsWindows() ? ['\\', '/'] : ['/'];

    /// <summary>How resolved paths compare for equality, as <c>PurePath</c> equality does on this platform.</summary>
    public static StringComparison Comparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>The same comparison, for a set of resolved paths.</summary>
    public static StringComparer Comparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>Paths in <c>sorted()</c> order: segment by segment, lower-cased first on Windows.</summary>
    public static IComparer<string> Order { get; } = Comparer<string>.Create(CompareSegments);

    /// <summary>A file's text as <c>read_text(encoding="utf-8")</c> gives it: a byte-order mark kept, line endings made <c>\n</c>.</summary>
    public static string ReadText(string path) =>
        new System.Text.UTF8Encoding(false).GetString(File.ReadAllBytes(path)).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    /// <summary>Whether <paramref name="path"/> is itself a symbolic link, whatever it points at.</summary>
    public static bool IsLink(string path)
    {
        try
        {
            return new FileInfo(path).LinkTarget is not null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Whether something exists at <paramref name="path"/>, following links: a broken link does not.</summary>
    public static bool Exists(string path) => Final(path) is { } final && (File.Exists(final) || Directory.Exists(final));

    /// <summary>Whether <paramref name="path"/> is a file, following links.</summary>
    public static bool IsFile(string path) => Final(path) is { } final && File.Exists(final);

    /// <summary>Whether <paramref name="path"/> is a folder, following links.</summary>
    public static bool IsDir(string path) => Final(path) is { } final && Directory.Exists(final);

    /// <summary>
    /// <paramref name="target"/> joined to <paramref name="folder"/> and resolved, or null when it cannot name a file
    /// (a decoded <c>%00</c>). An absolute target replaces the folder, as <c>Path</c>'s <c>/</c> does.
    /// </summary>
    public static string? Resolve(string folder, string target)
    {
        if (target.Contains('\0', StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            return PathResolution.Resolve(Path.Combine(folder, target));
        }
        catch (Exception e) when (e is IOException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Whether two paths are equal, as <c>PurePath</c> equality decides.</summary>
    public static bool Same(string path, string other) =>
        string.Equals(Trim(path), Trim(other), Comparison);

    /// <summary>Whether <paramref name="path"/> is <paramref name="folder"/> or lies under it, by name alone.</summary>
    public static bool IsRelativeTo(string path, string folder)
    {
        var trimmed = Trim(folder);
        return Same(path, trimmed) || path.StartsWith(trimmed.EndsWith(Path.DirectorySeparatorChar) ? trimmed : trimmed + Path.DirectorySeparatorChar, Comparison);
    }

    /// <summary><paramref name="path"/> relative to <paramref name="root"/>, with forward slashes. Both share a prefix.</summary>
    public static string Relative(string root, string path) =>
        path[(Trim(root).Length + 1)..].Replace('\\', '/');

    /// <summary><paramref name="path"/> and each folder above it, up to the root.</summary>
    public static IEnumerable<string> SelfAndParents(string path)
    {
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            yield return current;
        }
    }

    /// <summary>Whether a name matches <c>*.md</c>, as <c>glob</c> does on this platform.</summary>
    public static bool IsMarkdownName(string name) => name.EndsWith(".md", Comparison);

    /// <summary>The entries directly in <paramref name="folder"/>; empty when it cannot be read.</summary>
    public static IEnumerable<string> List(string folder) => Entries(folder).Select(entry => entry.Path);

    /// <summary>Every entry beneath <paramref name="folder"/>, as <c>rglob("*")</c> walks it: linked folders are listed, not entered.</summary>
    public static IEnumerable<string> Walk(string folder)
    {
        foreach (var (path, enter) in Entries(folder))
        {
            yield return path;
            if (enter)
            {
                foreach (var nested in Walk(path))
                {
                    yield return nested;
                }
            }
        }
    }

    private static List<(string Path, bool Enter)> Entries(string folder)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            AttributesToSkip = 0,
            MatchType = MatchType.Simple,
            IgnoreInaccessible = true,
        };
        try
        {
            return [.. new DirectoryInfo(folder).EnumerateFileSystemInfos("*", options)
                .Select(info => (Path.Join(folder, info.Name), info is DirectoryInfo && info.LinkTarget is null))];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    // The path a chain of links ends at, or null when a link in it is broken or loops.
    private static string? Final(string path)
    {
        if (path.Contains('\0', StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            var info = new FileInfo(path);
            if (info.LinkTarget is null)
            {
                return path;
            }

            var final = info.ResolveLinkTarget(returnFinalTarget: true);
            return final is null || IsLink(final.FullName) ? null : final.FullName;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static string Trim(string path)
    {
        var root = Path.GetPathRoot(path) ?? string.Empty;
        return path.Length > root.Length ? path.TrimEnd(Separators) : path;
    }

    private static int CompareSegments(string? left, string? right)
    {
        var a = Segments(left ?? string.Empty);
        var b = Segments(right ?? string.Empty);
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            var compared = string.CompareOrdinal(a[i], b[i]);
            if (compared != 0)
            {
                return compared;
            }
        }

        return a.Length.CompareTo(b.Length);
    }

    private static string[] Segments(string path) =>
        (OperatingSystem.IsWindows() ? path.ToLowerInvariant() : path).Split(Separators);
}
