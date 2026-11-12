namespace Rig.Core.Desktop;

/// <summary>
/// The machine as restore sees it: every read, write, wait and sleep. Restore reaches the desktop only through this,
/// so it runs against a fake in tests and the Win32 implementation on Windows. Ported from
/// <c>desktop_manager.desktop_port.DesktopPort</c>.
/// </summary>
/// <remarks>
/// Desktops are numbered from 1 in position order. Time belongs to the port, so a fake's waits and sleeps move its own
/// clock and a test runs instantly. Expected failures throw <see cref="DesktopException"/>; what happens along the way
/// goes to the <see cref="IRunLog"/> passed in.
/// </remarks>
public interface IDesktopPort
{
    /// <summary>The time on the port's monotonic clock.</summary>
    TimeSpan Now { get; }

    /// <summary>Wait for <paramref name="duration"/>.</summary>
    void Sleep(TimeSpan duration);

    /// <summary>What is open on the first <paramref name="desktopCount"/> desktops, or on all of them when null.</summary>
    WorkspacePlan CurrentWorkspaceStatus(int? desktopCount = null);

    /// <summary>What is open on the desktops numbered <paramref name="desktopIndexes"/>, in that order.</summary>
    WorkspacePlan CurrentWorkspaceStatus(IReadOnlyList<int> desktopIndexes);

    /// <summary>What is open on the current desktop, named <paramref name="desktopName"/> or its own name.</summary>
    DesktopPlan CurrentDesktopStatus(string? desktopName = null);

    /// <summary>The windows restore must never close or move: the one driving it.</summary>
    IReadOnlySet<long> WindowHandlesToPreserve();

    /// <summary>The current desktop, or null when it cannot be told.</summary>
    int? CurrentDesktopNumber();

    /// <summary>Every window restore could reuse, apart from <paramref name="excluded"/>.</summary>
    IReadOnlyList<LiveWindow> InventoryReusableWindows(IReadOnlySet<long> excluded);

    /// <summary>The windows owned by the process named <paramref name="processName"/>.</summary>
    IReadOnlySet<long> WindowsMatchingProcess(string processName);

    /// <summary>The title of each window owned by the process named <paramref name="processName"/>.</summary>
    IReadOnlyDictionary<long, string> WindowTitlesForProcess(string processName);

    /// <summary>Whether a process named <paramref name="processName"/> is running, with a window or not.</summary>
    bool IsProcessRunning(string processName);

    /// <summary>Whether window <paramref name="handle"/> is on <paramref name="desktopIndex"/> in <paramref name="entry"/>'s place.</summary>
    bool IsWindowTargetConfirmed(long handle, int desktopIndex, LaunchEntry entry);

    /// <summary>Whether a window of <paramref name="entry"/> shows on <paramref name="desktopIndex"/> in its place within <paramref name="timeout"/>.</summary>
    bool IsEntryTargetConfirmed(LaunchEntry entry, int desktopIndex, TimeSpan timeout);

    /// <summary>Wait for <paramref name="entry"/>'s window and bring it to the target desktop.</summary>
    WindowMatch WaitForWindowForEntry(LaunchEntry entry, WindowWait wait, IRunLog log);

    /// <summary>Wait for window <paramref name="handle"/> to take a title other than <paramref name="title"/>; its title then, or null when it is gone.</summary>
    string? WaitForWindowTitleChange(long handle, string title, TimeSpan timeout, IRunLog log);

    /// <summary>Fail with <see cref="DesktopException"/> when this machine cannot run a restore.</summary>
    void EnsureAvailable();

    /// <summary>Let pending app updates finish before anything launches; whether any were waited on.</summary>
    bool SettleAppUpdates(WorkspacePlan plan, IRunLog log);

    /// <summary>Start upgrading the commands the plan's terminals run, in the background.</summary>
    void StartTerminalCommandUpgrades(WorkspacePlan plan);

    /// <summary>Add desktops until there are at least <paramref name="count"/>, waiting <paramref name="settle"/> after each.</summary>
    void EnsureDesktopCount(int count, TimeSpan settle);

    /// <summary>Switch to desktop <paramref name="desktopIndex"/>; whether it is now current.</summary>
    bool EnsureCurrentDesktop(int desktopIndex, TimeSpan settle);

    /// <summary>Name desktop <paramref name="desktopIndex"/> <paramref name="name"/>; whether that took.</summary>
    bool RenameDesktop(int desktopIndex, string name);

    /// <summary>
    /// Remove the desktops named in <paramref name="retiredNames"/>, never the last one, moving their windows to the
    /// last of the first <paramref name="keepCount"/>; the names removed, in position order.
    /// </summary>
    IReadOnlyList<string> RetireSwitchedOffDesktops(int keepCount, IReadOnlyList<string> retiredNames);

    /// <summary>Switch <paramref name="steps"/> desktops to the right, stopping at the last.</summary>
    void MoveRight(int steps);

    /// <summary>Switch <paramref name="steps"/> desktops to the left, stopping at the first.</summary>
    void SwitchLeftmost(int steps);

    /// <summary>Close every window on the current desktop apart from <paramref name="preserved"/>; how many closed.</summary>
    int CloseCurrentDesktopWindows(IReadOnlySet<long> preserved);

    /// <summary>Move an existing window onto <paramref name="desktopIndex"/> and into <paramref name="entry"/>'s place.</summary>
    WindowMove MoveExistingWindowToEntry(LiveWindow window, int desktopIndex, LaunchEntry entry);

    /// <summary>Launch <paramref name="entry"/>; the started process's id, when known.</summary>
    /// <param name="entry">The entry to launch.</param>
    /// <param name="substitutions">The plan's placeholders.</param>
    /// <param name="restoreToken">A token the launched terminal can report back, to tell this restore's windows apart.</param>
    int? Launch(LaunchEntry entry, IReadOnlyDictionary<string, string> substitutions, string? restoreToken = null);

    /// <summary>Put <paramref name="entry"/>'s window, <paramref name="handle"/> or the one that shows, in its place on the target desktop.</summary>
    PlacementOutcome ApplyWindowPlacement(LaunchEntry entry, WindowWait wait, IRunLog log, long? handle = null);

    /// <summary>Bring every window of the named processes to the front; how many were raised.</summary>
    int BringProcessWindowsToFront(IReadOnlySet<string> processNames);

    /// <summary>Open a note for each missing path the restore hit; the actions taken.</summary>
    IReadOnlyList<string> OpenMissingPathFallbacks(RestoreFailureFallbacks fallbacks, IRunLog log);

    /// <summary>Visit each desktop again and re-place windows that drifted while others launched; the actions taken.</summary>
    IReadOnlyList<string> ApplySecondPassPlacements(WorkspacePlan plan, IReadOnlyDictionary<int, IReadOnlyList<LaunchEntry>> entriesByDesktop, IRunLog log);
}
