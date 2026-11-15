using System.Globalization;
using Rig.Core.Estate;

namespace Rig.Web.Ops;

/// <summary>One folder a trim scan offers to delete, as an ops answer shows it.</summary>
/// <param name="Worktree">The worktree holding it.</param>
/// <param name="Path">The folder.</param>
/// <param name="Category">What kind of regenerable output it is.</param>
/// <param name="Freeable">What deleting it frees.</param>
/// <param name="Refused">Why it may not be removed; empty when it may.</param>
public sealed record TrimCandidate(string Worktree, string Path, string Category, long Freeable, string Refused);

/// <summary>The last finished trim scan, and whether another is running.</summary>
/// <param name="ScannedAt">When the shown list was taken, ISO 8601 UTC; empty before the first scan finishes.</param>
/// <param name="Candidates">The regenerable folders found, most freeable first.</param>
/// <param name="Scanning">Whether a scan is running now, so the list may be replaced.</param>
/// <param name="Error">Why the last scan failed; empty when it did not.</param>
public sealed record TrimScan(string ScannedAt, IReadOnlyList<TrimCandidate> Candidates, bool Scanning, string Error);

/// <summary>What applying a trim did to one path.</summary>
/// <param name="Path">The path asked for.</param>
/// <param name="Status"><c>removed</c>, <c>not-in-scan</c>, <c>refused: &lt;reason&gt;</c> or <c>failed: &lt;reason&gt;</c>.</param>
/// <param name="Freeable">What the scan measured it as holding.</param>
public sealed record TrimResult(string Path, string Status, long Freeable);

/// <summary>
/// The last finished trim scan, refreshed on a background task; safe to share between requests. Ported from
/// <c>rig.estate.web.TrimCache</c>: a GET never waits for a scan, and an apply removes only paths the last finished
/// scan offered and still allows.
/// </summary>
public sealed class TrimCache : IDisposable
{
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _applyGate = new(1, 1);
    private Task? _running;
    private List<Trimmable> _items = [];
    private string _scannedAt = string.Empty;
    private string _error = string.Empty;
    private bool _stale = true;

    /// <summary>Builds a cache over the seams.</summary>
    /// <param name="scanAll">Every candidate across every clone, in one pass.</param>
    /// <param name="recheck">Why a candidate found by an earlier scan may no longer be removed; empty when it still may.</param>
    /// <param name="remove">Deletes one candidate: <c>removed</c>, <c>refused</c> or <c>failed: reason</c>.</param>
    /// <param name="clock">Stamps a finished scan, ISO 8601.</param>
    public TrimCache(
        Func<CancellationToken, Task<IReadOnlyList<Trimmable>>> scanAll,
        Func<Trimmable, CancellationToken, Task<string>> recheck,
        Func<Trimmable, CancellationToken, Task<string>> remove,
        Func<string> clock)
    {
        ArgumentNullException.ThrowIfNull(scanAll);
        ArgumentNullException.ThrowIfNull(recheck);
        ArgumentNullException.ThrowIfNull(remove);
        ArgumentNullException.ThrowIfNull(clock);
        _scanAll = scanAll;
        _recheck = recheck;
        _remove = remove;
        _clock = clock;
    }

    /// <summary>A result path the last scan did not offer.</summary>
    public const string NotInScan = "not-in-scan";

    /// <summary>A candidate was removed.</summary>
    public const string Removed = "removed";

    private readonly Func<CancellationToken, Task<IReadOnlyList<Trimmable>>> _scanAll;
    private readonly Func<Trimmable, CancellationToken, Task<string>> _recheck;
    private readonly Func<Trimmable, CancellationToken, Task<string>> _remove;
    private readonly Func<string> _clock;

    /// <inheritdoc/>
    public void Dispose() => _applyGate.Dispose();

    /// <summary>The last finished scan, starting a new one when none has run, the last is stale, or <paramref name="refresh"/> asks.</summary>
    public TrimScan Current(bool refresh = false)
    {
        lock (_gate)
        {
            var running = Running();
            if (!running && (refresh || _stale))
            {
                _stale = false;
                _running = Task.Run(() => RunScanAsync(CancellationToken.None), CancellationToken.None);
                running = true;
            }

            return new TrimScan(
                _scannedAt,
                [.. _items.Select(item => new TrimCandidate(item.Worktree, item.Path, item.Category, item.Size.Freeable, item.Refused))],
                running,
                _error);
        }
    }

    /// <summary>Waits for the running scan, if any, up to <paramref name="timeout"/>.</summary>
    public async Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        Task? running;
        lock (_gate)
        {
            running = _running;
        }

        if (running is not null)
        {
            _ = await Task.WhenAny(running, Task.Delay(timeout, cancellationToken)).ConfigureAwait(false);
        }
    }

    /// <summary>Removes each requested path the last finished scan offered and the disk still allows.</summary>
    public async Task<IReadOnlyList<TrimResult>> ApplyAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        await _applyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Dictionary<string, Trimmable> offered;
            lock (_gate)
            {
                offered = _items.ToDictionary(item => Key(item.Path), StringComparer.Ordinal);
            }

            var results = new List<TrimResult>();
            var removed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var path in paths.Distinct(StringComparer.Ordinal))
            {
                var result = await ApplyOneAsync(path, offered.GetValueOrDefault(Key(path)), cancellationToken).ConfigureAwait(false);
                results.Add(result);
                if (result.Status == Removed)
                {
                    _ = removed.Add(Key(result.Path));
                }
            }

            lock (_gate)
            {
                _items = [.. _items.Where(item => !removed.Contains(Key(item.Path)))];
                _stale = true;
            }

            return results;
        }
        finally
        {
            _ = _applyGate.Release();
        }
    }

    /// <summary>A cache over <paramref name="root"/>'s clones, scanning with <paramref name="trim"/> and deleting with <paramref name="removal"/>.</summary>
    public static TrimCache ForEstate(string root, Trim trim, Func<IReadOnlyList<string>> clones, TreeRemoval removal) =>
        new(
            async cancellationToken =>
            {
                var found = new List<Trimmable>();
                foreach (var clone in clones())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    found.AddRange(await trim.ScanAsync(clone).ConfigureAwait(false));
                }

                return [.. found.OrderByDescending(item => item.Size.Freeable)];
            },
            (item, _) => trim.RecheckAsync(item),
            (item, cancellationToken) => Trim.RemoveAsync(item, removal, cancellationToken),
            () => DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture));

    private bool Running()
    {
        if (_running is null)
        {
            return false;
        }

        if (_running.IsCompleted)
        {
            _running = null;
            return false;
        }

        return true;
    }

    private async Task RunScanAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<Trimmable> items;
        try
        {
            items = await _scanAll(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            lock (_gate)
            {
                _error = e.Message;
            }

            return;
        }

        var scannedAt = _clock();
        lock (_gate)
        {
            _items = [.. items];
            _scannedAt = scannedAt;
            _error = string.Empty;
        }
    }

    private async Task<TrimResult> ApplyOneAsync(string path, Trimmable? item, CancellationToken cancellationToken)
    {
        if (item is null)
        {
            return new TrimResult(path, NotInScan, 0);
        }

        var freeable = item.Size.Freeable;
        var reason = item.Refused.Length > 0 ? item.Refused : await _recheck(item, cancellationToken).ConfigureAwait(false);
        return reason.Length > 0
            ? new TrimResult(path, $"refused: {reason}", freeable)
            : new TrimResult(path, await _remove(item, cancellationToken).ConfigureAwait(false), freeable);
    }

    // Python's Path(path): the same path written two ways is one key.
    private static string Key(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }
}
