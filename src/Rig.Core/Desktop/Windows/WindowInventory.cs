using Rig.Core.Vault;

namespace Rig.Core.Desktop.Windows;

/// <summary>
/// The windows, processes and monitors on a Windows machine, as restore reads them. Ported from
/// <c>desktop_manager.win32.inventory</c> and the read half of <c>desktop_manager.win32.geometry</c>.
/// </summary>
/// <param name="win32">The Win32 reads.</param>
/// <param name="desktops">Which virtual desktop each window is on.</param>
public sealed class WindowInventory(IWin32Windows win32, IWindowDesktops desktops)
{
    /// <summary>Window titles that are never one of the user's windows.</summary>
    public static IReadOnlySet<string> IgnoredWindowTitles { get; } = new HashSet<string>(StringComparer.Ordinal) { "Program Manager" };

    /// <summary>Shell processes whose windows are never one of the user's, as lowercase image names.</summary>
    public static IReadOnlySet<string> IgnoredProcessNames { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "applicationframehost.exe",
        "lockapp.exe",
        "searchhost.exe",
        "shellexperiencehost.exe",
        "startmenuexperiencehost.exe",
        "textinputhost.exe",
        "widgets.exe",
    };

    /// <summary>The image name and executable of process <paramref name="pid"/>.</summary>
    public ProcessDetails ProcessDetailsOf(int pid)
    {
        var path = win32.ProcessImagePath(pid);
        return string.IsNullOrEmpty(path) ? new() : new(ImageName(path), path);
    }

    /// <summary>The window's rectangle, or null when it cannot be read.</summary>
    public Rect? Bounds(long handle) => win32.WindowRect(handle);

    /// <summary>The window's title, stripped, or null when it cannot be read.</summary>
    public string? Title(long handle) => win32.Title(handle) is { } title ? PyText.Strip(title) : null;

    /// <summary>Whether <paramref name="handle"/> is still a window.</summary>
    public bool IsWindow(long handle) => win32.IsWindow(handle);

    /// <summary>Each monitor's work area, numbered from 1 left to right, then top to bottom.</summary>
    public IReadOnlyList<DisplayArea> DisplayAreas()
    {
        var ordered = win32.Monitors().Select(monitor => monitor.WorkArea).OrderBy(area => area.Left).ThenBy(area => area.Top);
        return [.. ordered.Select((area, index) => new DisplayArea(index + 1, area))];
    }

    /// <summary>Each monitor's work area, in screen-number order.</summary>
    public IReadOnlyList<Rect> WorkAreas() => [.. DisplayAreas().Select(area => area.Bounds)];

    /// <summary>The work area of the <c>left</c>, <c>center</c> or <c>right</c> monitor.</summary>
    /// <exception cref="DesktopException">No monitor was found.</exception>
    /// <exception cref="ArgumentException"><paramref name="slot"/> is not one of the three.</exception>
    public Rect WorkAreaBySlot(string slot)
    {
        var areas = WorkAreas();
        if (areas.Count == 0)
        {
            throw new DesktopException("No monitor work areas detected");
        }

        return slot switch
        {
            "left" => areas[0],
            "right" => areas[^1],
            "center" => areas[areas.Count / 2],
            _ => throw new ArgumentException($"Unknown monitor slot: {slot}", nameof(slot)),
        };
    }

    /// <summary>The work area of screen <paramref name="screenNumber"/>, from 1.</summary>
    /// <exception cref="ArgumentOutOfRangeException">There is no such screen.</exception>
    public Rect WorkAreaByNumber(int screenNumber)
    {
        var areas = WorkAreas();
        if (screenNumber < 1 || screenNumber > areas.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(screenNumber), screenNumber, $"Screen number out of range: {screenNumber}");
        }

        return areas[screenNumber - 1];
    }

    /// <summary>The primary monitor's work area.</summary>
    /// <exception cref="DesktopException">The primary monitor cannot be read.</exception>
    public Rect PrimaryWorkArea() =>
        win32.PrimaryMonitor()?.WorkArea ?? throw new DesktopException("The primary monitor could not be read");

    /// <summary>The screen holding the window's centre, or null when it is off every screen or cannot be read.</summary>
    public int? ScreenNumberOf(long handle) => Bounds(handle) is { } bounds ? ScreenNumberAt(bounds, DisplayAreas()) : null;

    /// <summary>The placement the window most closely fills on the live monitors, if any is close enough.</summary>
    public PlacementSpec? InferredPlacementOf(long handle) =>
        Bounds(handle) is { } bounds ? Geometry.InferPlacement(bounds, DisplayAreas()) : null;

    /// <summary>
    /// <paramref name="target"/> grown by the window's invisible resize borders, so that its visible frame lands on
    /// <paramref name="target"/>. Unchanged when either rectangle cannot be read.
    /// </summary>
    public Rect AdjustForFrame(long handle, Rect target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (win32.ExtendedFrameBounds(handle) is not { } frame || win32.WindowRect(handle) is not { } window)
        {
            return target;
        }

        var leftAdjust = frame.Left - window.Left;
        var topAdjust = frame.Top - window.Top;
        var rightAdjust = window.Right - frame.Right;
        var bottomAdjust = window.Bottom - frame.Bottom;
        return new(
            target.Left - leftAdjust,
            target.Top - topAdjust,
            target.Width + leftAdjust + rightAdjust,
            target.Height + topAdjust + bottomAdjust);
    }

    /// <summary>The windows restore must never close or move: the one the user is in and this process's console.</summary>
    public IReadOnlySet<long> HandlesToPreserve()
    {
        var handles = new HashSet<long>();
        if (ForegroundWindow() is { } foreground)
        {
            handles.Add(foreground);
        }

        if (ConsoleWindow() is { } console)
        {
            handles.Add(console);
        }

        return handles;
    }

    /// <summary>The top-level window the user is working in, or null when nothing has focus.</summary>
    public long? ForegroundWindow() => NonZero(TopLevel(win32.ForegroundWindow()));

    /// <summary>This process's top-level console window, or null when it has none.</summary>
    public long? ConsoleWindow() => NonZero(TopLevel(win32.ConsoleWindow()));

    /// <summary>Whether the window is minimized, maximized or neither.</summary>
    public ShowState ShowStateOf(long handle) =>
        win32.IsMinimized(handle) ? ShowState.Minimized : win32.IsMaximized(handle) ? ShowState.Maximized : ShowState.Normal;

    /// <summary>The user's windows on the current desktop, by process name and then title.</summary>
    public IReadOnlyList<WindowSnapshot> CurrentDesktopWindows()
    {
        var screens = DisplayAreas();
        var snapshots = new List<WindowSnapshot>();
        foreach (var handle in win32.TopLevelWindows())
        {
            if (Listed(handle, currentDesktopOnly: true) is not { } listed)
            {
                continue;
            }

            var bounds = Bounds(handle);
            var placement = bounds is null ? null : Geometry.InferPlacement(bounds, screens);
            snapshots.Add(new(
                listed.Title,
                listed.Pid,
                listed.Process.Name,
                listed.Process.ExePath,
                win32.ClassName(handle),
                placement?.Screen ?? (bounds is null ? null : ScreenNumberAt(bounds, screens)),
                placement,
                bounds));
        }

        return [.. snapshots
            .OrderBy(snapshot => snapshot.ProcessName ?? "", PyText.CodePointOrder)
            .ThenBy(snapshot => PyText.Lower(snapshot.Title), PyText.CodePointOrder)];
    }

    /// <summary>The user's windows on the current desktop, apart from <paramref name="preserved"/>, in z-order.</summary>
    public IReadOnlyList<long> CurrentDesktopWindowHandles(IReadOnlySet<long>? preserved = null) =>
        [.. win32.TopLevelWindows().Where(handle =>
            preserved?.Contains(handle) != true && Listed(handle, currentDesktopOnly: true) is not null)];

    /// <summary>Every user window restore could reuse, on any desktop, apart from <paramref name="excluded"/>.</summary>
    public IReadOnlyList<LiveWindow> ReusableWindows(IReadOnlySet<long>? excluded = null)
    {
        var screens = DisplayAreas();
        var windows = new List<LiveWindow>();
        foreach (var handle in win32.TopLevelWindows())
        {
            if (excluded?.Contains(handle) == true || Listed(handle, currentDesktopOnly: false) is not { } listed)
            {
                continue;
            }

            var bounds = Bounds(handle);
            var placement = bounds is null ? null : Geometry.InferPlacement(bounds, screens);
            windows.Add(new(
                handle,
                listed.Title,
                listed.Pid,
                listed.Process.Name,
                listed.Process.ExePath,
                win32.ClassName(handle),
                desktops.DesktopNumberOf(handle),
                placement?.Screen ?? (bounds is null ? null : ScreenNumberAt(bounds, screens)),
                placement,
                bounds));
        }

        return [.. windows
            .OrderBy(window => PyText.Lower(window.ProcessName ?? ""), PyText.CodePointOrder)
            .ThenBy(window => PyText.Lower(window.Title), PyText.CodePointOrder)
            .ThenBy(window => window.Handle)];
    }

    /// <summary>
    /// Visible windows whose casefolded title is <paramref name="target"/> (when <paramref name="exact"/>) or contains
    /// it. <paramref name="target"/> must already be casefolded, as the Python's callers did.
    /// </summary>
    public IReadOnlyList<long> MatchingWindows(string target, bool exact = false, bool currentDesktopOnly = true)
    {
        ArgumentNullException.ThrowIfNull(target);
        return [.. win32.TopLevelWindows().Where(handle =>
        {
            if (!OnScope(handle, currentDesktopOnly) || !win32.IsVisible(handle) || Title(handle) is not { Length: > 0 } title)
            {
                return false;
            }

            var normalized = PyText.CaseFold(title);
            return exact ? normalized == target : normalized.Contains(target, StringComparison.Ordinal);
        })];
    }

    /// <summary>The visible windows owned by a process named <paramref name="processName"/>, in z-order.</summary>
    public IReadOnlyList<long> WindowsMatchingProcess(string processName, bool currentDesktopOnly = true) =>
        [.. ProcessWindows(processName, currentDesktopOnly).Select(match => match.Handle)];

    /// <summary>The ids of processes named <paramref name="processName"/> that own a visible window.</summary>
    public IReadOnlySet<int> ProcessIdsForProcessName(string processName, bool currentDesktopOnly = false) =>
        ProcessWindows(processName, currentDesktopOnly).Select(match => match.Pid).ToHashSet();

    /// <summary>The title of each visible window owned by a process named <paramref name="processName"/>.</summary>
    public IReadOnlyDictionary<long, string> WindowTitlesForProcess(string processName, bool currentDesktopOnly = false)
    {
        var titles = new Dictionary<long, string>();
        foreach (var handle in WindowsMatchingProcess(processName, currentDesktopOnly))
        {
            if (win32.Title(handle) is { } title)
            {
                titles[handle] = title;
            }
        }

        return titles;
    }

    /// <summary>
    /// The casefolded image name of every process Windows will name, windowed or not. Unlike
    /// <see cref="ProcessIdsForProcessName"/> this sees an app that is still starting and has no window yet.
    /// </summary>
    public IReadOnlySet<string> RunningProcessNames()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pid in win32.ProcessIds())
        {
            if (pid != 0 && ProcessDetailsOf(pid).Name is { } name)
            {
                names.Add(PyText.CaseFold(name));
            }
        }

        return names;
    }

    /// <summary>The id of every process named <paramref name="processName"/>, windowed or not, helpers included.</summary>
    public IReadOnlySet<int> ProcessIdsForImageName(string processName)
    {
        ArgumentNullException.ThrowIfNull(processName);
        var target = PyText.CaseFold(processName);
        return win32.ProcessIds()
            .Where(pid => pid != 0 && ProcessDetailsOf(pid).Name is { } name && PyText.CaseFold(name) == target)
            .ToHashSet();
    }

    /// <summary>Whether a process named <paramref name="processName"/> is running, with a window or not.</summary>
    public bool IsProcessRunning(string processName)
    {
        ArgumentNullException.ThrowIfNull(processName);
        return RunningProcessNames().Contains(PyText.CaseFold(processName));
    }

    /// <summary>The image name in a Windows path: what follows the last separator.</summary>
    private static string ImageName(string path) => path[(path.LastIndexOfAny(['\\', '/']) + 1)..];

    private static int? ScreenNumberAt(Rect bounds, IReadOnlyList<DisplayArea> screens) =>
        screens.FirstOrDefault(screen =>
            screen.Bounds.Left <= bounds.CenterX && bounds.CenterX < screen.Bounds.Right
            && screen.Bounds.Top <= bounds.CenterY && bounds.CenterY < screen.Bounds.Bottom)?.ScreenNumber;

    private static long? NonZero(long handle) => handle == 0 ? null : handle;

    private long TopLevel(long handle)
    {
        if (handle == 0)
        {
            return 0;
        }

        var rootOwner = win32.RootOwner(handle);
        if (rootOwner != 0)
        {
            return rootOwner;
        }

        var root = win32.Root(handle);
        return root != 0 ? root : handle;
    }

    private bool OnScope(long handle, bool currentDesktopOnly) => !currentDesktopOnly || desktops.IsOnCurrentDesktop(handle);

    private ListedWindow? Listed(long handle, bool currentDesktopOnly)
    {
        if (!OnScope(handle, currentDesktopOnly) || !win32.IsVisible(handle))
        {
            return null;
        }

        if (Title(handle) is not { Length: > 0 } title || IgnoredWindowTitles.Contains(title) || win32.IsToolWindow(handle))
        {
            return null;
        }

        var pid = win32.ProcessIdOf(handle);
        if (pid == 0 || pid == win32.CurrentProcessId)
        {
            return null;
        }

        var process = ProcessDetailsOf(pid);
        return process.Name is { } name && IgnoredProcessNames.Contains(PyText.Lower(name)) ? null : new(title, pid, process);
    }

    private List<ProcessWindow> ProcessWindows(string processName, bool currentDesktopOnly)
    {
        ArgumentNullException.ThrowIfNull(processName);
        var target = PyText.CaseFold(processName);
        var names = new Dictionary<int, string?>();
        var matches = new List<ProcessWindow>();
        foreach (var handle in win32.TopLevelWindows())
        {
            if (!OnScope(handle, currentDesktopOnly) || !win32.IsVisible(handle))
            {
                continue;
            }

            var pid = win32.ProcessIdOf(handle);
            if (pid == 0)
            {
                continue;
            }

            if (!names.TryGetValue(pid, out var name))
            {
                name = ProcessDetailsOf(pid).Name;
                names[pid] = name;
            }

            if (name is not null && PyText.CaseFold(name) == target)
            {
                matches.Add(new(handle, pid));
            }
        }

        return matches;
    }

    private sealed record ListedWindow(string Title, int Pid, ProcessDetails Process);

    private sealed record ProcessWindow(long Handle, int Pid);
}
