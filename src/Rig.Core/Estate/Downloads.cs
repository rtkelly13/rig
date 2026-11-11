using System.Security.Cryptography;

namespace Rig.Core.Estate;

/// <summary>One large item in Downloads.</summary>
/// <param name="Name">Its name.</param>
/// <param name="Path">Where it is.</param>
/// <param name="Bytes">What it occupies.</param>
/// <param name="Category">What kind of thing it is: one of <see cref="Downloads.Categories"/>.</param>
/// <param name="IsDir">Whether it is a folder.</param>
/// <param name="CloudDuplicate">The byte-identical copy in Google Drive, when there is one.</param>
public sealed record DownloadItem(string Name, string Path, long Bytes, string Category, bool IsDir, string? CloudDuplicate);

/// <summary>
/// The large items in Downloads, what kind of thing each is, and whether Google Drive already holds an identical copy.
/// Ported from <c>src/rig/estate/downloads.py</c>. Read-only: nothing is moved or deleted.
/// </summary>
/// <param name="disk">Sizes a folder.</param>
public sealed class Downloads(IDiskUsage disk)
{
    /// <summary>The categories, in the order an extension is checked against them; <c>other</c> when none matches.</summary>
    public static readonly IReadOnlyList<string> Categories = ["installer", "archive", "dataset", "media", "document", "other"];

    private static readonly HashSet<string> Installer = [".dmg", ".pkg", ".iso", ".app"];
    private static readonly HashSet<string> Archive = [".zip", ".tar", ".gz", ".tgz", ".bz2", ".7z", ".rar"];
    private static readonly HashSet<string> Dataset = [".csv", ".parquet", ".json", ".sql", ".sqlite", ".db", ".tsv"];
    private static readonly HashSet<string> Media = [".jpg", ".jpeg", ".png", ".gif", ".mp4", ".mov", ".mkv", ".mp3", ".wav"];
    private static readonly HashSet<string> Document = [".pdf", ".docx", ".xlsx", ".pptx", ".txt", ".md"];

    // How many of a folder's entries are sampled to guess what it holds.
    private const int Sample = 10;

    /// <summary>The Downloads folder under <paramref name="home"/>.</summary>
    public static string DefaultFolder(string home) => System.IO.Path.Join(home, "Downloads");

    /// <summary>The Google Drive folders a download may already be backed up to, where they exist: Photos first.</summary>
    public static IReadOnlyList<string> DefaultCloudRoots(string home)
    {
        var drive = System.IO.Path.Join(home, "Google Drive", "My Drive");
        return [.. new[] { System.IO.Path.Join(drive, "Photos"), drive }.Where(Directory.Exists)];
    }

    /// <summary>
    /// What kind of thing <paramref name="path"/> is, by its extension; a folder with none is media or a dataset when
    /// one of its first few files is.
    /// </summary>
    public static string Classify(string path)
    {
        var ext = InstalledApps.Suffix(path).ToLowerInvariant();
        foreach (var (category, extensions) in new[] { ("installer", Installer), ("archive", Archive), ("dataset", Dataset), ("media", Media), ("document", Document) })
        {
            if (extensions.Contains(ext))
            {
                return category;
            }
        }

        if (Directory.Exists(path))
        {
            try
            {
                var exts = Directory.EnumerateFileSystemEntries(path).Take(Sample).Where(File.Exists)
                    .Select(child => InstalledApps.Suffix(child).ToLowerInvariant()).ToHashSet();
                if (exts.Overlaps(Media))
                {
                    return "media";
                }

                if (exts.Overlaps(Dataset))
                {
                    return "dataset";
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return "other";
            }
        }

        return "other";
    }

    /// <summary>
    /// Whether <paramref name="local"/> and <paramref name="remote"/> hold the same bytes: one file each, or folders with the
    /// same files (<c>.DS_Store</c> aside) at the same relative paths. A name alone proves nothing; anything unreadable is false.
    /// </summary>
    public static bool ContentsIdentical(string local, string remote)
    {
        try
        {
            var isDir = Directory.Exists(local);
            if (isDir != Directory.Exists(remote))
            {
                return false;
            }

            if (!isDir)
            {
                return SameFile(local, remote);
            }

            var left = TreeFiles(local);
            var right = TreeFiles(remote);
            return left.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(right.Keys) && left.All(pair => SameFile(pair.Value, right[pair.Key]));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Every entry of <paramref name="folder"/> (dot-names aside) at or above <paramref name="minSize"/>, largest first, each
    /// with the first of <paramref name="cloudRoots"/> holding an identical copy of the same name. An entry that cannot be
    /// read, such as a broken link, is skipped.
    /// </summary>
    public async Task<IReadOnlyList<DownloadItem>> ScanAsync(string folder, IReadOnlyList<string> cloudRoots, long minSize, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cloudRoots);
        List<string> children;
        try
        {
            children = [.. Directory.EnumerateFileSystemEntries(folder).Where(child => !System.IO.Path.GetFileName(child).StartsWith('.'))];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        var items = new List<DownloadItem>();
        foreach (var child in children)
        {
            // As Python's is_dir and is_file, a link is followed: a broken one is neither, and is skipped.
            var target = Followed(child);
            var isDir = Directory.Exists(target);
            long size;
            try
            {
                size = isDir ? await disk.SizeAsync(child, cancellationToken).ConfigureAwait(false)
                    : File.Exists(target) ? new FileInfo(target).Length
                    : -1;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            if (size < 0 || size < minSize)
            {
                continue;
            }

            var name = System.IO.Path.GetFileName(child);
            var duplicate = cloudRoots.Select(root => System.IO.Path.Join(root, name)).FirstOrDefault(candidate => Path.Exists(candidate) && ContentsIdentical(child, candidate));
            items.Add(new DownloadItem(name, child, size, Classify(child), isDir, duplicate));
        }

        return [.. items.OrderByDescending(item => item.Bytes)];
    }

    private static string Followed(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.LinkTarget is null ? path : info.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? path;
        }
        catch (IOException)
        {
            return path;
        }
    }

    private static bool SameFile(string left, string right) =>
        new FileInfo(left).Length == new FileInfo(right).Length && Digest(left).AsSpan().SequenceEqual(Digest(right));

    private static byte[] Digest(string path)
    {
        using var stream = File.OpenRead(path);
        return SHA256.HashData(stream);
    }

    // Every file under the folder by its relative path, links to folders not entered, as Python's rglob walks it.
    private static Dictionary<string, string> TreeFiles(string root)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        var pending = new Stack<string>([root]);
        var options = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false };
        while (pending.TryPop(out var current))
        {
            foreach (var entry in new DirectoryInfo(current).EnumerateFileSystemInfos("*", options))
            {
                if (entry is DirectoryInfo)
                {
                    if (!entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        pending.Push(entry.FullName);
                    }
                }
                else if (entry.Name != ".DS_Store" && File.Exists(entry.FullName))
                {
                    files[System.IO.Path.GetRelativePath(root, entry.FullName)] = entry.FullName;
                }
            }
        }

        return files;
    }
}
