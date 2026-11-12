using Rig.Core.Desktop;
using Rig.Core.Vault;

namespace Rig.Core.Tests.Fakes;

/// <summary>A window on the fake machine. Tests read and set its desktop and placement directly.</summary>
public sealed class FakeWindow(long handle, string title, string processName, int desktop, PlacementSpec? placement = null)
{
    /// <summary>The window's handle.</summary>
    public long Handle { get; } = handle;

    /// <summary>Its title.</summary>
    public string Title { get; set; } = title;

    /// <summary>The owning process's image name.</summary>
    public string ProcessName { get; } = processName;

    /// <summary>The desktop it is on, from 1.</summary>
    public int Desktop { get; set; } = desktop;

    /// <summary>The slot it fills, if any.</summary>
    public PlacementSpec? Placement { get; set; } = placement;

    /// <summary>The owning process's id.</summary>
    public int? Pid { get; init; }

    /// <summary>When it first shows; a launched window takes its startup time, a hung one never shows.</summary>
    public TimeSpan VisibleAt { get; init; }

    /// <summary>The screen its slot is on.</summary>
    public int? Screen => Placement?.Screen;

    /// <summary>The window as an inventory reports it.</summary>
    public LiveWindow Live() => new(Handle, Title, Pid, ProcessName, DesktopNumber: Desktop, ScreenNumber: Screen, InferredPlacement: Placement);

    /// <summary>The window as a status capture records it.</summary>
    public WindowSnapshot Snapshot() => new(Title, Pid, ProcessName, ScreenNumber: Screen, InferredPlacement: Placement);
}

/// <summary>
/// The machine restore sees, as a stateful <see cref="IDesktopPort"/>. Mirrors <c>tests/fakes/desktop.py</c>: every write
/// changes the desktops and windows the way Windows would. A launch starts a process whose window shows
/// <see cref="Startup"/> later, placing a window puts it in its slot, closing a desktop's windows removes them. Time only
/// moves when restore sleeps or waits, so a test runs instantly. Assert on the end state and <see cref="Launches"/>,
/// not on which members were called.
/// </summary>
public sealed class FakeDesktop : IDesktopPort
{
    private long _nextHandle = 1000;

    /// <summary>The desktops' names, in position order.</summary>
    public List<string> DesktopNames { get; } = ["Desktop 1"];

    /// <summary>The current desktop, from 1.</summary>
    public int Current { get; set; } = 1;

    /// <summary>Every window, visible yet or not.</summary>
    public List<FakeWindow> Windows { get; private set; } = [];

    /// <summary>The windows driving the restore (the invoking terminal).</summary>
    public HashSet<long> InvokingHandles { get; } = [];

    /// <summary>How long a launched window takes to show.</summary>
    public TimeSpan Startup { get; set; }

    /// <summary>Entry name to how many of its next launches start nothing at all.</summary>
    public Dictionary<string, int> WindowlessLaunches { get; } = [];

    /// <summary>Entries whose launch starts a process that runs but never shows a window.</summary>
    public HashSet<string> HungLaunches { get; } = [];

    /// <summary>Windows that vanish when restore tries to move them.</summary>
    public HashSet<long> Vanishing { get; } = [];

    /// <summary>The name of each entry launched, in order.</summary>
    public List<string> Launches { get; } = [];

    /// <summary>Entry name to the message its launch fails with.</summary>
    public Dictionary<string, string> FailingLaunches { get; } = [];

    /// <summary>Entries whose placement fails.</summary>
    public HashSet<string> FailingPlacements { get; } = [];

    /// <summary>Titles a terminal never takes over: waiting for one times out.</summary>
    public HashSet<string> StuckTitles { get; } = [];

    /// <summary>Desktops, from 1, that refuse a new name.</summary>
    public HashSet<int> Unrenamable { get; } = [];

    /// <summary>Whether the current desktop cannot be read.</summary>
    public bool CurrentUnknown { get; set; }

    /// <summary>The missing-path fallbacks restore asked to open, as entry names.</summary>
    public List<string> OpenedFallbacks { get; } = [];

    /// <inheritdoc/>
    public TimeSpan Now { get; set; }

    /// <summary>Open a window that is already there before restore runs.</summary>
    public FakeWindow AddWindow(string title, string processName, int desktop = 1, PlacementSpec? placement = null, long? handle = null)
    {
        var window = new FakeWindow(handle ?? NewHandle(), title, processName, desktop, placement);
        Windows.Add(window);
        return window;
    }

    /// <summary>The window with <paramref name="handle"/>, if it is still open.</summary>
    public FakeWindow? Window(long handle) => Windows.Find(window => window.Handle == handle);

    /// <inheritdoc/>
    public void Sleep(TimeSpan duration) => Now += duration;

    /// <inheritdoc/>
    public WorkspacePlan CurrentWorkspaceStatus(int? desktopCount = null) =>
        CurrentWorkspaceStatus([.. Enumerable.Range(1, desktopCount is { } count and not 0 ? count : DesktopNames.Count)]);

    /// <inheritdoc/>
    public WorkspacePlan CurrentWorkspaceStatus(IReadOnlyList<int> desktopIndexes) => new() { Desktops = [.. desktopIndexes.Select(DesktopStatus)] };

    /// <inheritdoc/>
    public DesktopPlan CurrentDesktopStatus(string? desktopName = null)
    {
        var status = DesktopStatus(Current);
        return status with { Name = string.IsNullOrEmpty(desktopName) ? status.Name : desktopName };
    }

    /// <inheritdoc/>
    public IReadOnlySet<long> WindowHandlesToPreserve() => new HashSet<long>(InvokingHandles);

    /// <inheritdoc/>
    public int? CurrentDesktopNumber() => CurrentUnknown ? null : Current;

    /// <inheritdoc/>
    public IReadOnlyList<LiveWindow> InventoryReusableWindows(IReadOnlySet<long> excluded) =>
        [.. Visible().Where(window => !excluded.Contains(window.Handle)).Select(window => window.Live())];

    /// <inheritdoc/>
    public IReadOnlySet<long> WindowsMatchingProcess(string processName) =>
        Visible().Where(window => SameProcess(window, processName)).Select(window => window.Handle).ToHashSet();

    /// <inheritdoc/>
    public IReadOnlyDictionary<long, string> WindowTitlesForProcess(string processName) =>
        Visible().Where(window => SameProcess(window, processName)).ToDictionary(window => window.Handle, window => window.Title);

    /// <inheritdoc/>
    public bool IsProcessRunning(string processName) =>
        // A launched process runs before its window shows, so windows still starting count.
        Windows.Exists(window => SameProcess(window, processName));

    /// <inheritdoc/>
    public bool IsWindowTargetConfirmed(long handle, int desktopIndex, LaunchEntry entry) =>
        Window(handle) is { } window && window.Desktop == desktopIndex && window.Placement == entry.Placement;

    /// <inheritdoc/>
    public bool IsEntryTargetConfirmed(LaunchEntry entry, int desktopIndex, TimeSpan timeout) =>
        Visible().Any(window => SameProcess(window, ProcessOf(entry)) && IsWindowTargetConfirmed(window.Handle, desktopIndex, entry));

    /// <inheritdoc/>
    public WindowMatch WaitForWindowForEntry(LaunchEntry entry, WindowWait wait, IRunLog log)
    {
        ArgumentNullException.ThrowIfNull(wait);
        if (WaitFor(() => NewestWindowFor(entry, wait.Excluded), wait.Timeout) is not { } window)
        {
            return new(null);
        }

        var moved = window.Desktop != wait.TargetDesktop;
        window.Desktop = wait.TargetDesktop;
        return new(window.Handle, moved);
    }

    /// <inheritdoc/>
    public string? WaitForWindowTitleChange(long handle, string title, TimeSpan timeout, IRunLog log)
    {
        if (StuckTitles.Contains(title))
        {
            Now += timeout;
            return null;
        }

        return Window(handle)?.Title;
    }

    /// <inheritdoc/>
    public void EnsureAvailable()
    {
    }

    /// <inheritdoc/>
    public bool SettleAppUpdates(WorkspacePlan plan, IRunLog log) => false;

    /// <inheritdoc/>
    public void StartTerminalCommandUpgrades(WorkspacePlan plan)
    {
    }

    /// <inheritdoc/>
    public void EnsureDesktopCount(int count, TimeSpan settle)
    {
        while (DesktopNames.Count < count)
        {
            DesktopNames.Add($"Desktop {DesktopNames.Count + 1}");
        }
    }

    /// <inheritdoc/>
    public bool EnsureCurrentDesktop(int desktopIndex, TimeSpan settle)
    {
        if (desktopIndex < 1 || desktopIndex > DesktopNames.Count)
        {
            return false;
        }

        Current = desktopIndex;
        return true;
    }

    /// <inheritdoc/>
    public bool RenameDesktop(int desktopIndex, string name)
    {
        if (Unrenamable.Contains(desktopIndex))
        {
            return false;
        }

        DesktopNames[desktopIndex - 1] = name;
        return true;
    }

    /// <inheritdoc/>
    public IReadOnlyList<string> RetireSwitchedOffDesktops(int keepCount, IReadOnlyList<string> retiredNames)
    {
        var wanted = retiredNames.Select(PyText.CaseFold).ToHashSet(StringComparer.Ordinal);
        var removed = new List<string>();
        for (var index = DesktopNames.Count; index > 0; index--)
        {
            var name = DesktopNames[index - 1];
            if (!wanted.Contains(PyText.CaseFold(name)) || DesktopNames.Count == 1)
            {
                continue;
            }

            DesktopNames.RemoveAt(index - 1);
            removed.Insert(0, name);
            var fallback = Math.Min(keepCount, DesktopNames.Count);
            foreach (var window in Windows)
            {
                window.Desktop = window.Desktop == index ? fallback : window.Desktop > index ? window.Desktop - 1 : window.Desktop;
            }

            Current = Current == index ? fallback : Current > index ? Current - 1 : Current;
        }

        return removed;
    }

    /// <inheritdoc/>
    public void MoveRight(int steps) => Current = Math.Min(Current + steps, DesktopNames.Count);

    /// <inheritdoc/>
    public void SwitchLeftmost(int steps) => Current = Math.Max(Current - steps, 1);

    /// <inheritdoc/>
    public int CloseCurrentDesktopWindows(IReadOnlySet<long> preserved)
    {
        var closing = Visible().Where(window => window.Desktop == Current && !preserved.Contains(window.Handle)).ToHashSet();
        Windows = [.. Windows.Where(window => !closing.Contains(window))];
        return closing.Count;
    }

    /// <inheritdoc/>
    public WindowMove MoveExistingWindowToEntry(LiveWindow window, int desktopIndex, LaunchEntry entry)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(entry);
        if (Window(window.Handle) is not { } target || Vanishing.Contains(window.Handle))
        {
            Windows.RemoveAll(item => item.Handle == window.Handle);
            throw new DesktopException($"window {window.Handle} vanished before it could be moved");
        }

        var movedToDesktop = target.Desktop != desktopIndex;
        var movedToPlacement = entry.Placement is not null && target.Placement != entry.Placement;
        target.Desktop = desktopIndex;
        target.Placement = entry.Placement ?? target.Placement;
        return new(movedToDesktop, movedToPlacement);
    }

    /// <inheritdoc/>
    public int? Launch(LaunchEntry entry, IReadOnlyDictionary<string, string> substitutions, string? restoreToken = null)
    {
        ArgumentNullException.ThrowIfNull(entry);
        Launches.Add(entry.Name);
        if (FailingLaunches.TryGetValue(entry.Name, out var failure))
        {
            throw new DesktopException(failure);
        }

        var pid = 5000 + Launches.Count;
        if (WindowlessLaunches.GetValueOrDefault(entry.Name) > 0)
        {
            WindowlessLaunches[entry.Name]--;
            return pid;
        }

        // A hung process is running, so restore can see it, but its window never shows.
        Windows.Add(new(NewHandle(), string.IsNullOrEmpty(entry.WindowTitle) ? entry.Name : entry.WindowTitle, ProcessOf(entry), Current)
        {
            Pid = pid,
            VisibleAt = HungLaunches.Contains(entry.Name) ? TimeSpan.MaxValue : Now + Startup,
        });
        return pid;
    }

    /// <inheritdoc/>
    public PlacementOutcome ApplyWindowPlacement(LaunchEntry entry, WindowWait wait, IRunLog log, long? handle = null)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(wait);
        if (FailingPlacements.Contains(entry.Name))
        {
            throw new DesktopException($"could not place {entry.Name}");
        }

        var window = (handle is { } known ? Window(known) : null) ?? WaitFor(() => NewestWindowFor(entry, wait.Excluded), wait.Timeout);
        if (window is null)
        {
            return new(false);
        }

        var moved = window.Desktop != wait.TargetDesktop;
        window.Desktop = wait.TargetDesktop;
        window.Placement = entry.Placement;
        return new(true, moved);
    }

    /// <inheritdoc/>
    public int BringProcessWindowsToFront(IReadOnlySet<string> processNames)
    {
        var wanted = processNames.Select(PyText.CaseFold).ToHashSet(StringComparer.Ordinal);
        return Visible().Count(window => wanted.Contains(PyText.CaseFold(window.ProcessName)));
    }

    /// <inheritdoc/>
    public IReadOnlyList<string> OpenMissingPathFallbacks(RestoreFailureFallbacks fallbacks, IRunLog log)
    {
        ArgumentNullException.ThrowIfNull(fallbacks);
        var names = fallbacks.MissingPathEntries.SelectMany(pair => pair.Value.Entries).Select(entry => entry.Name).ToList();
        OpenedFallbacks.AddRange(names);
        return [.. names.Select(name => $"Open missing-path note for {name}")];
    }

    /// <inheritdoc/>
    public Rect PlacementRect(PlacementSpec placement)
    {
        ArgumentNullException.ThrowIfNull(placement);
        return Geometry.PlacementRectIn(new((placement.Screen - 1) * 1920, 0, 1920, 1080), placement);
    }

    /// <inheritdoc/>
    public IReadOnlyList<string> ApplySecondPassPlacements(WorkspacePlan plan, IReadOnlyDictionary<int, IReadOnlyList<LaunchEntry>> entriesByDesktop, IRunLog log)
    {
        ArgumentNullException.ThrowIfNull(entriesByDesktop);
        var actions = new List<string>();
        foreach (var (desktopIndex, entries) in entriesByDesktop)
        {
            actions.Add($"Desktop {desktopIndex}: second-pass placement");
            foreach (var entry in entries.Where(entry => entry.Placement is not null && entry.Type != LaunchEntry.Terminal))
            {
                if (NewestWindowFor(entry, null) is { } window)
                {
                    window.Desktop = desktopIndex;
                    window.Placement = entry.Placement;
                }
            }
        }

        return actions;
    }

    private static string ProcessOf(LaunchEntry entry) =>
        !string.IsNullOrEmpty(entry.WindowProcessName) ? entry.WindowProcessName : WindowReuse.WindowsFileName(string.IsNullOrEmpty(entry.Path) ? entry.Name : entry.Path);

    private static bool SameProcess(FakeWindow window, string processName) => PyText.CaseFold(window.ProcessName) == PyText.CaseFold(processName);

    private long NewHandle() => ++_nextHandle;

    private IEnumerable<FakeWindow> Visible() => Windows.Where(window => window.VisibleAt <= Now);

    private DesktopPlan DesktopStatus(int index) =>
        new(index <= DesktopNames.Count ? DesktopNames[index - 1] : $"Desktop {index}")
        {
            CapturedWindows = [.. Visible().Where(window => window.Desktop == index).Select(window => window.Snapshot())],
        };

    private FakeWindow? WaitFor(Func<FakeWindow?> find, TimeSpan timeout)
    {
        if (find() is { } found)
        {
            return found;
        }

        Now += timeout > Startup ? timeout : Startup;
        return find();
    }

    private FakeWindow? NewestWindowFor(LaunchEntry entry, IReadOnlySet<long>? excluded) =>
        Visible().LastOrDefault(window => SameProcess(window, ProcessOf(entry)) && excluded?.Contains(window.Handle) != true);
}
