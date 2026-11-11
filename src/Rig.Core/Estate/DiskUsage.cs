using System.Globalization;
using Rig.Core.Processes;

namespace Rig.Core.Estate;

/// <summary>How many bytes a folder occupies on disk. A seam, so tests size folders without spawning <c>du</c>.</summary>
public interface IDiskUsage
{
    /// <summary>The bytes <paramref name="path"/> occupies; zero when it cannot be measured.</summary>
    Task<long> SizeAsync(string path, CancellationToken cancellationToken = default);
}

/// <summary>
/// The bytes deleting a tree would free. Ported from <c>src/rig/estate/disksize.py</c> <c>du</c>: on POSIX,
/// <c>du -sk</c>; on Windows, where <c>du</c> is not on the PATH a scheduled task gets, a managed walk.
/// </summary>
/// <param name="proc">Runs <c>du</c>.</param>
public sealed class DiskUsage(IProc proc) : IDiskUsage
{
    /// <summary>How long one <c>du</c> may run.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(120);

    /// <summary>NTFS's default cluster: allocation is a whole number of these.</summary>
    public const long Cluster = 4096;

    /// <inheritdoc />
    public async Task<long> SizeAsync(string path, CancellationToken cancellationToken = default)
    {
        if (OperatingSystem.IsWindows())
        {
            return Walk(path);
        }

        var ran = await proc.RunAsync(new ProcRequest(["du", "-sk", path]) { Timeout = Timeout }, cancellationToken).ConfigureAwait(false);
        var first = ran.Ok ? ran.Out.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() : null;
        return long.TryParse(first, NumberStyles.None, CultureInfo.InvariantCulture, out var kib) ? kib * 1024 : 0;
    }

    /// <summary>What deleting <paramref name="path"/> frees, by <see cref="WalkTree"/>; a lone file is its own rounded size.</summary>
    public static long Walk(string path) =>
        Directory.Exists(path) ? WalkTree(path).Freeable : File.Exists(path) ? Rounded(new FileInfo(path).Length) : 0;

    /// <summary>
    /// Every file under <paramref name="path"/>, cluster-rounded, with each hardlinked file counted once and as freeable
    /// only when every one of its names was met inside the tree. Links and junctions are not followed: their target is
    /// not this tree's.
    /// </summary>
    public static TreeSize WalkTree(string path)
    {
        var linked = new Dictionary<(ulong Device, ulong Inode), (ulong Links, long Allocated, ulong Seen)>();
        long plain = 0;
        var stack = new Stack<string>([path]);
        while (stack.TryPop(out var current))
        {
            foreach (var entry in TreeMeasure.Entries(current))
            {
                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    continue;
                }

                if (entry is DirectoryInfo)
                {
                    stack.Push(entry.FullName);
                    continue;
                }

                var allocated = Rounded(((FileInfo)entry).Length);
                if (HardLinks.Identify(entry.FullName) is not { Links: > 1 } identity)
                {
                    plain += allocated;
                    continue;
                }

                var key = (identity.Device, identity.Inode);
                linked[key] = linked.TryGetValue(key, out var seen) ? seen with { Seen = seen.Seen + 1 } : (identity.Links, allocated, 1);
            }
        }

        var shared = linked.Values.Where(file => file.Seen < file.Links).Sum(file => file.Allocated);
        return new TreeSize(plain + linked.Values.Where(file => file.Seen >= file.Links).Sum(file => file.Allocated), shared);
    }

    private static long Rounded(long length) => (length + Cluster - 1) / Cluster * Cluster;
}
