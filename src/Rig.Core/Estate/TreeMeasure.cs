namespace Rig.Core.Estate;

/// <summary>What deleting a tree would free, and whether it may be deleted at all.</summary>
/// <param name="Apparent">Every file's length.</param>
/// <param name="Freeable">The lengths of files with no other name: deleting a hardlinked copy frees nothing.</param>
/// <param name="HoldsCheckout">A <c>.git</c> file or folder exists below the root; the walk stopped there.</param>
/// <param name="Estimated">Freeable was extrapolated from a sample of the small files.</param>
public sealed record TreeMeasure(long Apparent, long Freeable, bool HoldsCheckout = false, bool Estimated = false)
{
    /// <summary>A tree whose walk stopped at a <c>.git</c>; its sizes are unknown, not zero.</summary>
    public static readonly TreeMeasure Unmeasured = new(0, 0, HoldsCheckout: true);

    /// <summary>Every file of at least this many bytes has its link count read exactly on Windows.</summary>
    public const long LargeFile = 1024 * 1024;

    /// <summary>On Windows, one in this many small files has its link count read; the rest share its ratio.</summary>
    public const int SampleStride = 32;

    /// <summary>
    /// Walks <paramref name="path"/> once, returning apparent and freeable bytes. Ported from
    /// <c>src/rig/estate/disksize.py</c> <c>measure</c>.
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
        var stack = new Stack<string>([path]);
        while (stack.TryPop(out var current))
        {
            foreach (var entry in Entries(current))
            {
                if (entry.Name == ".git")
                {
                    return Unmeasured;
                }

                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    continue;
                }

                if (entry is DirectoryInfo)
                {
                    stack.Push(entry.FullName);
                    continue;
                }

                var size = ((FileInfo)entry).Length;
                apparent += size;
                if (!sampled || size >= LargeFile)
                {
                    known += HardLinks.Count(entry.FullName) <= 1 ? size : 0;
                    continue;
                }

                smallTotal += size;
                smallSeen++;
                if (smallSeen % SampleStride == 1)
                {
                    sampledBytes += size;
                    sampledFreeable += HardLinks.Count(entry.FullName) <= 1 ? size : 0;
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
    /// a worktree of the clone being cleaned. Names only, links not followed, so it costs a listing per folder.
    /// </summary>
    public static bool HasCheckoutBelow(string path)
    {
        var stack = new Stack<string>([path]);
        while (stack.TryPop(out var current))
        {
            foreach (var entry in Entries(current))
            {
                if (entry.Name == ".git")
                {
                    return true;
                }

                if (entry is DirectoryInfo && !entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    stack.Push(entry.FullName);
                }
            }
        }

        return false;
    }

    /// <summary>One folder's entries, hidden ones included; none when it cannot be listed.</summary>
    internal static List<FileSystemInfo> Entries(string folder)
    {
        try
        {
            return [.. new DirectoryInfo(folder).EnumerateFileSystemInfos("*", ListOne)];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    // The defaults skip Hidden and System, and on Unix a dot-name is Hidden: `.git` and `.venv` would vanish.
    private static readonly EnumerationOptions ListOne = new() { AttributesToSkip = 0, IgnoreInaccessible = true };
}

/// <summary>A tree's allocation, split by whether deleting the tree frees it.</summary>
/// <param name="Freeable">Bytes that go with the tree: every name of the file is inside it.</param>
/// <param name="Shared">Bytes also named from outside the tree: deleting it frees none of them.</param>
public sealed record TreeSize(long Freeable, long Shared);
