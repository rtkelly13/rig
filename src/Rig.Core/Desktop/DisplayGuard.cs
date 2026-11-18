using System.Globalization;
using Rig.Core.Jobs;

namespace Rig.Core.Desktop;

/// <summary>How a window is shown.</summary>
public enum ShowState
{
    /// <summary>Neither minimized nor maximized.</summary>
    Normal,

    /// <summary>Minimized.</summary>
    Minimized,

    /// <summary>Maximized.</summary>
    Maximized,
}

/// <summary>A monitor's rectangle in virtual-screen pixels, as Win32 reports it.</summary>
/// <param name="Left">The left edge.</param>
/// <param name="Top">The top edge.</param>
/// <param name="Right">The right edge, one past the last column.</param>
/// <param name="Bottom">The bottom edge, one past the last row.</param>
public readonly record struct MonitorBounds(int Left, int Top, int Right, int Bottom)
{
    /// <summary>The rectangle's width.</summary>
    public int Width => Right - Left;

    /// <summary>The rectangle's height.</summary>
    public int Height => Bottom - Top;
}

/// <summary>
/// One attached monitor as the topology signature sees it. <paramref name="Device"/> is for logs only: a monitor that
/// drops out can return under a different <c>DISPLAYn</c> name, so only its bounds and primary flag decide whether two
/// topologies are the same.
/// </summary>
/// <param name="Device">Its device name, such as <c>DISPLAY1</c>.</param>
/// <param name="Bounds">Its rectangle.</param>
/// <param name="Primary">Whether it is the primary monitor.</param>
public sealed record GuardMonitor(string Device, MonitorBounds Bounds, bool Primary)
{
    /// <summary>Whether both describe the same monitor position; the device name does not count.</summary>
    public bool Equals(GuardMonitor? other) => other is not null && Bounds == other.Bounds && Primary == other.Primary;

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Bounds, Primary);
}

/// <summary>
/// The monitors attached at one moment, in position order. Names in <see cref="Describe"/> only; equality is over the
/// monitors themselves, so it can key the guard's snapshots.
/// </summary>
public sealed class MonitorSignature(IReadOnlyList<GuardMonitor> monitors) : IEquatable<MonitorSignature>
{
    /// <summary>No monitors, as a machine reports before its display stack is up.</summary>
    public static MonitorSignature Empty { get; } = new([]);

    /// <summary>The monitors, sorted left to right then top to bottom.</summary>
    public IReadOnlyList<GuardMonitor> Monitors { get; } = monitors;

    /// <summary>Whether both are the same set of monitors in the same positions.</summary>
    public bool Equals(MonitorSignature? other) => other is not null && Monitors.SequenceEqual(other.Monitors);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is MonitorSignature other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var monitor in Monitors)
        {
            hash.Add(monitor);
        }

        return hash.ToHashCode();
    }

    /// <summary>The signature in one log line, such as <c>2 monitor(s): DISPLAY1*@0,0 1920x1080; ...</c>.</summary>
    public string Describe()
    {
        if (Monitors.Count == 0)
        {
            return "no monitors";
        }

        var parts = Monitors.Select(monitor =>
            $"{monitor.Device}{(monitor.Primary ? "*" : string.Empty)}@{monitor.Bounds.Left},{monitor.Bounds.Top} "
            + $"{monitor.Bounds.Width}x{monitor.Bounds.Height}");
        return $"{Monitors.Count} monitor(s): {string.Join("; ", parts)}";
    }
}

/// <summary>
/// A top-level window's placement in <c>GetWindowPlacement</c> terms. <see cref="NormalRect"/> is
/// <c>rcNormalPosition</c>, the restored rectangle that also decides which monitor a maximized window maximizes onto;
/// <see cref="WindowRect"/> is where the window actually is now, which catches a maximized window Windows moved to
/// another monitor without changing its restored rectangle.
/// </summary>
/// <param name="Hwnd">The window's handle.</param>
/// <param name="Pid">The owning process id; Windows recycles handles, so a record only matches with the same owner.</param>
/// <param name="Title">Its title.</param>
/// <param name="ProcessName">The owning process's image name, when known.</param>
/// <param name="ShowState">Minimized, maximized or neither.</param>
/// <param name="NormalRect">The restored rectangle.</param>
/// <param name="WindowRect">Where it is now.</param>
/// <param name="RestoreToMaximized">Whether restoring it maximizes it.</param>
public sealed record WindowPlacementRecord(
    long Hwnd,
    int Pid,
    string Title,
    string? ProcessName,
    ShowState ShowState,
    Rect NormalRect,
    Rect WindowRect,
    bool RestoreToMaximized = false);

/// <summary>Everything the display guard needs from the machine.</summary>
public interface IDisplayGuardPorts
{
    /// <summary>Wall-clock seconds. Unlike a monotonic clock it keeps advancing through system sleep.</summary>
    double Now { get; }

    /// <summary>The monitors attached now.</summary>
    MonitorSignature MonitorSignature();

    /// <summary>A counter that increases on every display-change, power-resume or session-unlock event.</summary>
    int DisplayEventCount();

    /// <summary>Whether a workspace restore holds its lock.</summary>
    bool RestoreInProgress();

    /// <summary>The placement of every window the guard manages, across every virtual desktop.</summary>
    IReadOnlyList<WindowPlacementRecord> CapturePlacements();

    /// <summary>Apply each record; the handles that were placed.</summary>
    IReadOnlyList<long> ApplyPlacements(IReadOnlyList<WindowPlacementRecord> records);

    /// <summary>Record a line for the guard's log.</summary>
    void Log(string message);
}

/// <summary>What phase the guard is in.</summary>
public enum GuardPhase
{
    /// <summary>A settled topology, taking snapshots.</summary>
    Watching,

    /// <summary>A topology changed and has not held long enough to trust yet.</summary>
    Unsettled,

    /// <summary>A restore is running, so the guard neither snapshots nor moves anything.</summary>
    Paused,
}

/// <summary>How the display guard behaves.</summary>
public sealed record DisplayGuardSettings
{
    /// <summary>A topology must hold this long before the guard trusts it.</summary>
    public double SettleSeconds { get; init; } = 6.0;

    /// <summary>Seconds between placement snapshots while displays are stable.</summary>
    public double SnapshotIntervalSeconds { get; init; } = 5.0;

    /// <summary>A gap between steps longer than this means the process was suspended (system sleep).</summary>
    public double SuspendGapSeconds { get; init; } = 30.0;

    /// <summary>Re-check placements this many times after a recovery.</summary>
    public int RecoveryPasses { get; init; } = 2;

    /// <summary>Seconds between recovery passes.</summary>
    public double RecoveryPassIntervalSeconds { get; init; } = 3.0;

    /// <summary>Log what would move instead of moving it.</summary>
    public bool DryRun { get; init; }
}

/// <summary>
/// Put windows back after a monitor drops out and returns. Windows moves every window off a monitor that disappears
/// and does not reliably move them back, so the guard keeps the latest placements for each monitor topology it has
/// seen settle, and re-applies the snapshot for a returning topology to every window that moved.
/// </summary>
/// <remarks>
/// The platform-independent core; a Windows port supplies the machine. A system sleep counts as a topology change even
/// when the monitor set looks the same, because the guard cannot see what happened while it was suspended.
/// </remarks>
public sealed class DisplayGuard(IDisplayGuardPorts ports, DisplayGuardSettings? settings = null)
{
    /// <summary>Small frame and DPI rounding differences are not worth a placement call.</summary>
    public const int PlacementTolerancePx = 8;

    private readonly Dictionary<MonitorSignature, IReadOnlyList<WindowPlacementRecord>> _snapshots = [];
    private MonitorSignature? _signature;
    private int? _eventCount;
    private double? _lastStepAt;
    private double _changedAt;
    private double? _lastSnapshotAt;
    private int _pendingRecoveryPasses;
    private double _nextRecoveryPassAt;

    /// <summary>The machine.</summary>
    public IDisplayGuardPorts Ports { get; } = ports;

    /// <summary>How the guard behaves.</summary>
    public DisplayGuardSettings Settings { get; } = settings ?? new DisplayGuardSettings();

    /// <summary>The phase the guard is in.</summary>
    public GuardPhase Phase { get; private set; } = GuardPhase.Unsettled;

    /// <summary>The snapshot held for each topology, by signature.</summary>
    public IReadOnlyDictionary<MonitorSignature, IReadOnlyList<WindowPlacementRecord>> Snapshots => _snapshots;

    /// <summary>Take one step: read the machine, and snapshot or recover as the phase allows.</summary>
    public void Step()
    {
        var now = Ports.Now;
        var suspended = _lastStepAt is { } last && now - last > Settings.SuspendGapSeconds;
        _lastStepAt = now;

        var signature = Ports.MonitorSignature();
        var eventCount = Ports.DisplayEventCount();

        if (Ports.RestoreInProgress())
        {
            if (Phase is not GuardPhase.Paused)
            {
                Ports.Log("Workspace restore running; pausing until it finishes.");
            }

            Phase = GuardPhase.Paused;
            Observe(signature, eventCount, now);

            // The restore leaves the layout it wants, so it supersedes every snapshot, including those for topologies
            // that are not current. The next settled snapshot for each topology reflects it.
            _snapshots.Clear();
            _pendingRecoveryPasses = 0;
            return;
        }

        if (Phase is GuardPhase.Paused)
        {
            Ports.Log("Workspace restore finished; resuming.");
            Phase = GuardPhase.Unsettled;
            _changedAt = now;
        }

        var changed = !Equals(signature, _signature) || eventCount != _eventCount || suspended;
        if (changed)
        {
            NoteChange(signature, suspended);
            Observe(signature, eventCount, now);
            Phase = GuardPhase.Unsettled;
            _pendingRecoveryPasses = 0;
            return;
        }

        if (Phase is GuardPhase.Unsettled)
        {
            if (now - _changedAt < Settings.SettleSeconds)
            {
                return;
            }

            Phase = GuardPhase.Watching;
            Ports.Log($"Display topology settled: {signature.Describe()}");
            if (_snapshots.ContainsKey(signature))
            {
                Recover(signature);
                _pendingRecoveryPasses = Settings.RecoveryPasses;
                _nextRecoveryPassAt = now + Settings.RecoveryPassIntervalSeconds;
            }
            else
            {
                TakeSnapshot(signature, eventCount, now);
            }

            return;
        }

        if (_pendingRecoveryPasses > 0)
        {
            if (now >= _nextRecoveryPassAt)
            {
                _pendingRecoveryPasses--;
                _nextRecoveryPassAt = now + Settings.RecoveryPassIntervalSeconds;
                Recover(signature);
            }

            return;
        }

        if (_lastSnapshotAt is null || now - _lastSnapshotAt >= Settings.SnapshotIntervalSeconds)
        {
            TakeSnapshot(signature, eventCount, now);
        }
    }

    /// <summary>Whether <paramref name="expected"/> and <paramref name="actual"/> differ beyond the placement tolerance.</summary>
    public static bool PlacementDiffers(WindowPlacementRecord expected, WindowPlacementRecord actual)
    {
        if (expected.ShowState != actual.ShowState)
        {
            return true;
        }

        if (!RectsClose(expected.NormalRect, actual.NormalRect))
        {
            return true;
        }

        // A minimized window's live rectangle is the off-screen parking spot, not a placement.
        return expected.ShowState is not ShowState.Minimized && !RectsClose(expected.WindowRect, actual.WindowRect);
    }

    /// <summary>The one-line account of a window the guard put back.</summary>
    public static string DescribeMove(WindowPlacementRecord expected, WindowPlacementRecord actual)
    {
        var label = expected.ProcessName ?? "?";
        var title = expected.Title.Length <= 60 ? expected.Title : expected.Title[..57] + "...";
        return $"{label} [{expected.Hwnd}] '{title}': {Wire(actual.ShowState)} {RectLabel(actual.WindowRect)} -> "
            + $"{Wire(expected.ShowState)} {RectLabel(expected.NormalRect)}";
    }

    /// <summary>Run <paramref name="guard"/> until <paramref name="shouldStop"/> says to, sleeping between steps.</summary>
    public static void Run(DisplayGuard guard, double pollSeconds, Action<double> sleep, Func<bool>? shouldStop = null)
    {
        while (!(shouldStop?.Invoke() ?? false))
        {
            guard.Step();
            sleep(pollSeconds);
        }
    }

    private static string Wire(ShowState state) => state switch
    {
        ShowState.Minimized => "minimized",
        ShowState.Maximized => "maximized",
        _ => "normal",
    };

    private static string RectLabel(Rect rect) =>
        string.Create(CultureInfo.InvariantCulture, $"{rect.Left},{rect.Top} {rect.Width}x{rect.Height}");

    private static bool RectsClose(Rect left, Rect right, int tolerance = PlacementTolerancePx) =>
        Math.Abs(left.Left - right.Left) <= tolerance
        && Math.Abs(left.Top - right.Top) <= tolerance
        && Math.Abs(left.Width - right.Width) <= tolerance
        && Math.Abs(left.Height - right.Height) <= tolerance;

    private void Observe(MonitorSignature signature, int eventCount, double now)
    {
        _signature = signature;
        _eventCount = eventCount;
        _changedAt = now;
    }

    private void NoteChange(MonitorSignature signature, bool suspended)
    {
        if (_signature is null)
        {
            Ports.Log($"Watching displays: {signature.Describe()}");
            return;
        }

        if (suspended)
        {
            Ports.Log("Resumed after a suspend gap; treating it as a display change.");
        }

        if (!Equals(signature, _signature))
        {
            Ports.Log($"Display topology changed: {_signature.Describe()} -> {signature.Describe()}");
        }
        else if (!suspended)
        {
            Ports.Log("Display change event with an unchanged topology; waiting for it to settle.");
        }
    }

    private void TakeSnapshot(MonitorSignature signature, int eventCount, double now)
    {
        var records = Ports.CapturePlacements();

        // A change that lands mid-capture would mix pre- and post-change placements.
        if (!Equals(Ports.MonitorSignature(), signature) || Ports.DisplayEventCount() != eventCount)
        {
            Ports.Log("Display changed during snapshot; discarding it.");
            return;
        }

        var first = !_snapshots.ContainsKey(signature);
        _snapshots[signature] = records;
        _lastSnapshotAt = now;
        if (first)
        {
            Ports.Log($"Snapshot taken: {records.Count} window(s) for {signature.Describe()}");
        }
    }

    private void Recover(MonitorSignature signature)
    {
        var expected = _snapshots[signature];
        var live = Ports.CapturePlacements().ToDictionary(record => (record.Hwnd, record.Pid));
        var moved = expected
            .Where(record => live.TryGetValue((record.Hwnd, record.Pid), out var actual) && PlacementDiffers(record, actual))
            .ToList();
        if (moved.Count == 0)
        {
            Ports.Log("Windows already in place; nothing to restore.");
            return;
        }

        foreach (var record in moved)
        {
            Ports.Log($"  {DescribeMove(record, live[(record.Hwnd, record.Pid)])}");
        }

        if (Settings.DryRun)
        {
            Ports.Log($"Dry run: would restore {moved.Count} window(s).");
            return;
        }

        var placed = Ports.ApplyPlacements(moved);
        var failed = moved.Count - placed.Count;
        var suffix = failed > 0 ? $", {failed} failed" : string.Empty;
        Ports.Log($"Restored {placed.Count} window(s){suffix}.");
    }
}

/// <summary>
/// The guard's and restore's cross-process locks, in one per-user folder whatever plan or runtime copy runs them, so
/// the guard always sees a running restore.
/// </summary>
public static class DisplayGuardLocks
{
    /// <summary>The file naming the single guard instance.</summary>
    public const string GuardLockFileName = "display-guard.lock";

    /// <summary>The file a restore holds for its whole run.</summary>
    public const string RestoreLockFileName = "workspace-restore.lock";

    /// <summary>The guard's log file name.</summary>
    public const string GuardLogFileName = "display-guard.log";

    /// <summary>The folder both locks live in.</summary>
    public static string Directory(string appData) => Path.Join(appData, "workspace-restore");

    /// <summary>Hold the lock at <paramref name="path"/> for the returned lifetime; null when another process holds it.</summary>
    public static IDisposable? TryHold(string path) => RunLock.Hold(path, TimeSpan.Zero, _ => { });

    /// <summary>Hold the lock at <paramref name="path"/> for the returned lifetime, retrying for <paramref name="timeout"/>.</summary>
    public static IDisposable? Hold(string path, TimeSpan timeout, Action<TimeSpan> sleep) => RunLock.Hold(path, timeout, sleep);

    /// <summary>Whether another process currently holds the lock at <paramref name="path"/>.</summary>
    public static bool IsHeld(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        using var acquired = TryHold(path);
        return acquired is null;
    }
}
