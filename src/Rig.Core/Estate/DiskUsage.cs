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
/// The machine's own measure. Ported from <c>src/rig/estate/disksize.py</c>: on POSIX, <c>du -sk</c>, which already
/// counts a hardlinked file once; on Windows, a managed walk of cluster-rounded sizes that never follows a junction.
/// </summary>
/// <remarks>
/// The Windows walk does not yet dedupe hardlinks (pnpm's store links one file into every <c>node_modules</c>), so it can
/// overstate them. File identity lands with the reclaim and trim commands (s20b), which act on these numbers; the
/// read-only reports ported here only rank by them.
/// </remarks>
/// <param name="proc">Runs <c>du</c>.</param>
public sealed class DiskUsage(IProc proc) : IDiskUsage
{
    /// <summary>How long one <c>du</c> may run.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(120);

    private const long Cluster = 4096;

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

    /// <summary>The cluster-rounded size of every file under <paramref name="path"/>, junctions and links not followed.</summary>
    public static long Walk(string path)
    {
        var info = new DirectoryInfo(path);
        if (!info.Exists)
        {
            return File.Exists(path) ? Rounded(new FileInfo(path).Length) : 0;
        }

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };
        try
        {
            return info.EnumerateFiles("*", options).Sum(file => Rounded(file.Length));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static long Rounded(long length) => (length + Cluster - 1) / Cluster * Cluster;
}
