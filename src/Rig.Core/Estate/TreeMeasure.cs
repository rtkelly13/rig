using Rig.Core.Storage;
namespace Rig.Core.Estate;

/// <summary>What deleting a tree would free, and whether it may be deleted at all.</summary>
/// <param name="Apparent">Every file's length.</param>
/// <param name="Freeable">
/// The bytes each file frees alone: a hardlinked copy frees nothing, an APFS clone only its private blocks. A lower
/// bound: <see cref="DiskUsage.WalkTree(string)"/> also frees the groups met whole inside the tree.
/// </param>
/// <param name="HoldsCheckout">
/// A <c>.git</c> file or folder exists below the root, or a folder below it could not be listed and might hold one; the
/// walk stopped there.
/// </param>
/// <param name="Estimated">Freeable was extrapolated from a sample of the small files.</param>
public sealed record TreeMeasure(long Apparent, long Freeable, bool HoldsCheckout = false, bool Estimated = false)
{
    /// <summary>A tree whose walk stopped at a <c>.git</c> or an unlistable folder; its sizes are unknown, not zero.</summary>
    public static readonly TreeMeasure Unmeasured = new(0, 0, HoldsCheckout: true);

    /// <summary>Every file of at least this many bytes has its link count read exactly on Windows.</summary>
    public const long LargeFile = 1024 * 1024;

    /// <summary>On Windows, one in this many small files has its link count read; the rest share its ratio.</summary>
    public const int SampleStride = 32;

    /// <summary>
    /// Walks <paramref name="path"/> once, returning apparent and freeable bytes.
    /// </summary>
    /// <remarks>
    /// uv hardlinks <c>.venv</c> files from its cache and pnpm hardlinks <c>node_modules</c> from its store, so a
    /// 3.6 GB <c>node_modules</c> can free 89 MB: only files with a link count of one count as freeable. A link
    /// inside the tree to another file inside it reads as shared, which undercounts; that is the safe direction for a
    /// number that justifies deleting things. Reading a link count costs a file open on Windows, so there every large
    /// file is read and one small file in <see cref="SampleStride"/>, and the result is flagged
    /// <see cref="Estimated"/>; on Unix it is one <c>lstat</c> per file and exact. The walk ends at the first
    /// <c>.git</c>: a caller that refuses checkouts needs no size for one, and links are never followed.
    /// </remarks>
    public static TreeMeasure Measure(string path)
    {
        var sampled = OperatingSystem.IsWindows();
        long apparent = 0, known = 0, smallTotal = 0, smallSeen = 0, sampledBytes = 0, sampledFreeable = 0;
        foreach (var (_, entries) in Folders(path))
        {
            // A folder the walk cannot list is one it cannot clear, and the remover would unlock and empty it.
            if (entries is null || entries.Any(entry => entry.Name == ".git"))
            {
                return Unmeasured;
            }

            foreach (var file in entries.OfType<FileInfo>().Where(file => !IsLink(file)))
            {
                var size = file.Length;
                apparent += size;
                if (!sampled || size >= LargeFile)
                {
                    known += HardLinks.Alone(file.FullName, size);
                    continue;
                }

                smallTotal += size;
                smallSeen++;
                if (smallSeen % SampleStride == 1)
                {
                    sampledBytes += size;
                    sampledFreeable += HardLinks.Alone(file.FullName, size);
                }
            }
        }

        if (smallTotal == 0)
        {
            return new TreeMeasure(apparent, known);
        }

        var share = sampledBytes == 0 ? 1.0 : (double)sampledFreeable / sampledBytes;
        return new TreeMeasure(apparent, Math.Min(known + (long)(smallTotal * share), apparent), Estimated: true);
    }

    /// <summary>
    /// Whether a <c>.git</c> file or folder lies anywhere below <paramref name="path"/>: a checkout git does not list as
    /// a worktree of the clone being cleaned. Names only, links not followed, so it costs a listing per folder. A folder
    /// that cannot be listed counts as holding one; a plain file holds none. With <paramref name="ownGit"/>, the root's
    /// own <c>.git</c> is the checkout being judged and is skipped.
    /// </summary>
    public static bool HasCheckoutBelow(string path, bool ownGit = false)
    {
        if (File.Exists(path))
        {
            return false;
        }

        // Never descending into `.git` loses nothing: any `.git` but the root's own has already answered.
        return Folders(path, folder => folder.Name != ".git").Any(listed =>
            listed.Entries is not { } entries || entries.Any(entry => entry.Name == ".git" && !(ownGit && listed.Folder == path)));
    }

    /// <summary>Every file below <paramref name="root"/>, links not followed; a folder that cannot be listed reads as empty.</summary>
    internal static IEnumerable<FileInfo> Files(string root) =>
        Folders(root).SelectMany(listed => listed.Entries ?? []).OfType<FileInfo>().Where(file => !IsLink(file));

    /// <summary>
    /// Each folder from <paramref name="root"/> down with its entries (null when it cannot be listed), entering every
    /// subfolder that is not a link and that <paramref name="descend"/> allows. Lazy: a caller that stops early stops the walk.
    /// </summary>
    internal static IEnumerable<(string Folder, List<FileSystemInfo>? Entries)> Folders(string root, Func<DirectoryInfo, bool>? descend = null)
    {
        var stack = new Stack<string>([root]);
        while (stack.TryPop(out var current))
        {
            var entries = Entries(current);
            yield return (current, entries);
            foreach (var folder in entries?.OfType<DirectoryInfo>().Where(folder => !IsLink(folder) && (descend?.Invoke(folder) ?? true)) ?? [])
            {
                stack.Push(folder.FullName);
            }
        }
    }

    private static bool IsLink(FileSystemInfo entry) => entry.Attributes.HasFlag(FileAttributes.ReparsePoint);

    /// <summary>One folder's entries, hidden ones included; null when it cannot be listed.</summary>
    internal static List<FileSystemInfo>? Entries(string folder)
    {
        return FileGuard.Try<List<FileSystemInfo>?>(() => [.. new DirectoryInfo(folder).EnumerateFileSystemInfos("*", ListOne)], null);
    }

    // The defaults skip Hidden and System, and on Unix a dot-name is Hidden: `.git` and `.venv` would vanish. An
    // unlistable folder must throw, not read as empty.
    private static readonly EnumerationOptions ListOne = new() { AttributesToSkip = 0, IgnoreInaccessible = false };
}

/// <summary>A tree's allocation, split by whether deleting the tree frees it.</summary>
/// <param name="Freeable">Bytes that go with the tree: every name of the file, or every clone of its blocks, is inside it.</param>
/// <param name="Shared">Bytes also named or cloned from outside the tree: deleting it frees none of them.</param>
public sealed record TreeSize(long Freeable, long Shared);
