namespace Rig.Core.Vault;

/// <summary>A file or folder in the vault whose name ends in <c>.md</c>.</summary>
/// <param name="FullPath">The vault's path as given, joined with the entry's; never resolved, so the lint's substring filters see what Python saw.</param>
/// <param name="Relative">The entry's path relative to the vault.</param>
public sealed record VaultEntry(string FullPath, NotePath Relative)
{
    /// <summary>What follows the stem: <c>.md</c>, or nothing for a file named <c>.md</c>.</summary>
    public string Suffix => Relative.Name[Relative.Stem.Length..];
}

/// <summary>
/// The vault's markdown entries as pathlib's <c>rglob("*.md")</c> finds them on Python 3.13: dot-folders included, a
/// folder named <c>*.md</c> yielded alongside files, symlinked folders listed but never entered, unreadable folders
/// passed over. The suffix match is case-sensitive on every platform, and entries come in code-point order.
/// </summary>
public static class VaultWalk
{
    private static readonly EnumerationOptions Listing = new()
    {
        AttributesToSkip = 0,
        IgnoreInaccessible = true,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
    };

    /// <summary>Every entry under <paramref name="root"/> whose name ends in <c>.md</c>; the root itself is entered even when it is a symlink.</summary>
    public static IReadOnlyList<VaultEntry> Markdown(string root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var found = new List<VaultEntry>();
        Walk(root, [], found);
        return found;
    }

    private static void Walk(string directory, List<string> parts, List<VaultEntry> found)
    {
        FileSystemInfo[] entries;
        try
        {
            entries = new DirectoryInfo(directory).GetFileSystemInfos("*", Listing);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return;
        }

        Array.Sort(entries, (a, b) => PyText.CodePointOrder.Compare(a.Name, b.Name));
        foreach (var entry in entries)
        {
            var path = Path.Join(directory, entry.Name);
            parts.Add(entry.Name);
            if (entry.Name.EndsWith(".md", StringComparison.Ordinal))
            {
                found.Add(new VaultEntry(path, NotePath.Parse(string.Join('/', parts))));
            }

            if (entry is DirectoryInfo && entry.LinkTarget is null)
            {
                Walk(path, parts, found);
            }

            parts.RemoveAt(parts.Count - 1);
        }
    }
}
