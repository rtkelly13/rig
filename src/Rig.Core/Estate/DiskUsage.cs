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
        : File.Exists(path) ? HardLinks.Alone(path, Rounded(new FileInfo(path).Length))
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
        var groups = new Dictionary<ShareGroup, Group>();
        long plain = 0;
        foreach (var file in TreeMeasure.Files(path))
        {
            var allocated = Rounded(file.Length);
            if (identify(file.FullName)?.Share(allocated) is not { } share)
            {
                plain += allocated;
                continue;
            }

            var met = new Group(share.Needed, share.Shared, share.Private, 1);
            groups[share.Group] = groups.TryGetValue(share.Group, out var seen) ? seen.With(met) : met;
        }

        var whole = groups.Values.Where(group => group.Seen >= group.Needed).ToList();
        var partial = groups.Values.Where(group => group.Seen < group.Needed).ToList();
        return new TreeSize(
            plain + whole.Sum(group => group.Shared + group.Private) + partial.Sum(group => group.Private),
            partial.Sum(group => group.Shared));
    }

    private static long Rounded(long length) => (length + Cluster - 1) / Cluster * Cluster;

    // A share group as met so far: the blocks its members share, counted once, and their private blocks.
    private readonly record struct Group(ulong Needed, long Shared, long Private, ulong Seen)
    {
        public Group With(Group other) => this with { Shared = Math.Max(Shared, other.Shared), Private = Private + other.Private, Seen = Seen + other.Seen };
    }
}
