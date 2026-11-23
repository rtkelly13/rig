using Rig.Core.Estate;
using Rig.Core.Processes;
using Rig.Core.Worktrees;

namespace Rig.Web.Ops;

/// <summary>Free, used and total bytes of the drive holding a folder.</summary>
/// <param name="Path">The folder measured.</param>
/// <param name="Total">Its drive's capacity.</param>
/// <param name="Used">Bytes in use.</param>
/// <param name="Free">Bytes available.</param>
public sealed record DiskUsageView(string Path, long Total, long Used, long Free);

/// <summary>
/// The estate's ops answers: the decisions feed, the disk's headroom, and trimming idle worktrees. One <see cref="TrimCache"/> lives as long as the host.
/// </summary>
public sealed class EstateOps : IDisposable
{
    private readonly string _root;
    private readonly string _feedPath;

    /// <summary>Builds the answers over a code root, a feed and a trim cache.</summary>
    /// <param name="root">The personal estate folder trimming and disk usage measure.</param>
    /// <param name="feedPath">The decisions feed the <c>estate-decisions</c> job writes.</param>
    /// <param name="trims">The last finished trim scan.</param>
    public EstateOps(string root, string feedPath, TrimCache trims)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);
        ArgumentException.ThrowIfNullOrEmpty(feedPath);
        ArgumentNullException.ThrowIfNull(trims);
        _root = root;
        _feedPath = feedPath;
        Trims = trims;
    }

    /// <summary>The trim scan this host answers from.</summary>
    public TrimCache Trims { get; }

    /// <summary>The decisions feed, with a note saying why it is empty when it is.</summary>
    public DecisionFeedView Decisions() => DecisionFeed.Read(_feedPath);

    /// <summary>Total, used and free bytes on the drive holding the code root.</summary>
    public DiskUsageView Disk()
    {
        var root = Path.GetPathRoot(Path.GetFullPath(_root)) ?? Path.GetFullPath(_root);
        var drive = new DriveInfo(root);
        var free = drive.AvailableFreeSpace;
        return new DiskUsageView(_root, drive.TotalSize, drive.TotalSize - free, free);
    }

    /// <summary>The last finished trim scan, starting a new one when <paramref name="refresh"/> asks.</summary>
    public TrimScan Trim(bool refresh) => Trims.Current(refresh);

    /// <summary>Deletes caches the last finished scan offered; each is rechecked first and refused if it changed.</summary>
    public async Task<IReadOnlyList<TrimResult>> ApplyAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return await Trims.ApplyAsync(paths, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The real sources for a host serving <paramref name="settings"/>' estate.</summary>
    public static EstateOps ForSettings(WebHostSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var proc = new SystemProc();
        var git = new Git(proc, settings.EstateRoot);
        var trim = new Trim(git, TimeProvider.System);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var discovery = new CloneDiscovery(CloneDiscovery.CacheFileUnder(home), TimeProvider.System);
        var trims = TrimCache.ForEstate(
            settings.EstateRoot,
            trim,
            () => discovery.Repos(settings.EstateRoot).Paths,
            new TreeRemoval(proc));
        return new EstateOps(settings.EstateRoot, settings.DecisionsFeed, trims);
    }

    /// <inheritdoc/>
    public void Dispose() => Trims.Dispose();
}
