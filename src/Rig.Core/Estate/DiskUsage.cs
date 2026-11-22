namespace Rig.Core.Estate;

/// <summary>How many bytes a folder occupies on disk. A seam, so tests size folders without walking real trees.</summary>
public interface IDiskUsage
{
    /// <summary>The bytes <paramref name="path"/> occupies; zero when it cannot be measured.</summary>
    Task<long> SizeAsync(string path, CancellationToken cancellationToken = default);
}

/// <summary>
/// The bytes deleting a tree would free, on every OS by the same walk. <c>du</c> counts a file hardlinked or cloned from
/// outside the tree as the tree's own; on macOS, where .NET's <c>File.Copy</c> clones, that overstated 114 GB of
/// worktrees that would free 7 GB.
/// </summary>
public sealed class DiskUsage : IDiskUsage
{
    /// <summary>NTFS's default cluster and APFS's block: allocation is a whole number of these.</summary>
    public const long Cluster = 4096;

    /// <inheritdoc />
    public Task<long> SizeAsync(string path, CancellationToken cancellationToken = default) =>
        Task.Run(() => Walk(path), cancellationToken);

    /// <summary>What deleting <paramref name="path"/> frees, by <see cref="WalkTree(string)"/>; a lone file is what deleting it alone frees.</summary>
    public static long Walk(string path) =>
        Directory.Exists(path) ? WalkTree(path).Freeable
        : File.Exists(path) ? Alone(path, Rounded(new FileInfo(path).Length))
        : 0;

    /// <inheritdoc cref="WalkTree(string, Func{string, FileIdentity?})"/>
    public static TreeSize WalkTree(string path) => WalkTree(path, HardLinks.Identify);

    /// <summary>
    /// Every file under <paramref name="path"/>, cluster-rounded. A hardlinked file counts once, and as freeable only
    /// when every one of its names was met inside the tree; a clone group the same way, by its reference count, with
    /// each clone's private blocks freeable regardless. A file both hardlinked and cloned is shared. Links and junctions
    /// are not followed: their target is not this tree's.
    /// </summary>
    /// <param name="path">The tree.</param>
    /// <param name="identify">Reads a file's identity: <see cref="HardLinks.Identify"/>, or a test's own.</param>
    public static TreeSize WalkTree(string path, Func<string, FileIdentity?> identify)
    {
        ArgumentNullException.ThrowIfNull(identify);
        var groups = new Dictionary<(bool Clone, ulong Device, ulong Id), Group>();
        long plain = 0, stuck = 0;
        var stack = new Stack<string>([path]);
        while (stack.TryPop(out var current))
        {
            foreach (var entry in TreeMeasure.Entries(current) ?? [])
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
                var identity = identify(entry.FullName);
                if (identity is not { } known || (known.Links <= 1 && !known.IsCloned))
                {
                    plain += allocated;
                    continue;
                }

                if (known.Links > 1 && known.IsCloned)
                {
                    stuck += allocated;
                    continue;
                }

                var (key, met) = known.Cloned is { References: > 1 } clone
                    ? ((true, known.Device, clone.Id), new Group(clone.References, allocated - Math.Min(clone.Private, allocated), Math.Min(clone.Private, allocated), 1))
                    : ((false, known.Device, known.Inode), new Group(known.Links, allocated, 0, 1));
                groups[key] = groups.TryGetValue(key, out var seen) ? seen.With(met) : met;
            }
        }

        var whole = groups.Values.Where(group => group.Seen >= group.Needed).ToList();
        var partial = groups.Values.Where(group => group.Seen < group.Needed).ToList();
        return new TreeSize(
            plain + whole.Sum(group => group.Shared + group.Private) + partial.Sum(group => group.Private),
            stuck + partial.Sum(group => group.Shared));
    }

    private static long Alone(string path, long allocated) =>
        HardLinks.Identify(path) is { } identity ? identity.Alone(allocated) : allocated;

    private static long Rounded(long length) => (length + Cluster - 1) / Cluster * Cluster;

    // A hardlink or clone group as met so far: the blocks its members share, counted once, and their private blocks.
    private readonly record struct Group(ulong Needed, long Shared, long Private, ulong Seen)
    {
        public Group With(Group other) => this with { Shared = Math.Max(Shared, other.Shared), Private = Private + other.Private, Seen = Seen + other.Seen };
    }
}
