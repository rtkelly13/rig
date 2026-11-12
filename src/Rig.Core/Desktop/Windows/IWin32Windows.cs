namespace Rig.Core.Desktop.Windows;

/// <summary>
/// The Win32 reads window inventory is built from, one call each, with no policy of their own. A failed call answers
/// null, zero or empty rather than throwing, as the Python's <c>WIN32_ERRORS</c> handlers did. The real one is
/// <see cref="NativeWin32Windows"/>; tests use a fake, so <see cref="WindowInventory"/> is tested on every OS.
/// </summary>
public interface IWin32Windows
{
    /// <summary>This process's id, whose own windows inventory leaves out.</summary>
    int CurrentProcessId { get; }

    /// <summary>Every top-level window, in <c>EnumWindows</c> order (top of the z-order first).</summary>
    IReadOnlyList<long> TopLevelWindows();

    /// <summary>Whether <paramref name="handle"/> is still a window.</summary>
    bool IsWindow(long handle);

    /// <summary>Whether the window has the visible style.</summary>
    bool IsVisible(long handle);

    /// <summary>Whether the window is minimized.</summary>
    bool IsMinimized(long handle);

    /// <summary>Whether the window's placement says maximized.</summary>
    bool IsMaximized(long handle);

    /// <summary>The window's title as Windows gives it, unstripped, or null when the call fails.</summary>
    string? Title(long handle);

    /// <summary>The window's class name, or null when the call fails.</summary>
    string? ClassName(long handle);

    /// <summary>Whether the window has the tool-window extended style, which keeps it off the taskbar.</summary>
    bool IsToolWindow(long handle);

    /// <summary>The id of the process that owns the window, or 0 when it cannot be told.</summary>
    int ProcessIdOf(long handle);

    /// <summary>The window's rectangle, frame included, or null when the call fails.</summary>
    Rect? WindowRect(long handle);

    /// <summary>The window's visible frame from DWM, without its invisible resize borders, or null when DWM will not say.</summary>
    Rect? ExtendedFrameBounds(long handle);

    /// <summary>The window with keyboard focus, or 0.</summary>
    long ForegroundWindow();

    /// <summary>This process's console window, or 0 when it has none.</summary>
    long ConsoleWindow();

    /// <summary>The window's root owner (<c>GA_ROOTOWNER</c>), or 0.</summary>
    long RootOwner(long handle);

    /// <summary>The window's root (<c>GA_ROOT</c>), or 0.</summary>
    long Root(long handle);

    /// <summary>The id of every process, or empty when they cannot be listed.</summary>
    IReadOnlyList<int> ProcessIds();

    /// <summary>The full path of the process's executable, or null when Windows will not say.</summary>
    string? ProcessImagePath(int pid);

    /// <summary>Every monitor, in <c>EnumDisplayMonitors</c> order.</summary>
    IReadOnlyList<MonitorInfo> Monitors();

    /// <summary>The monitor the window is on, or nearest to, or null when the call fails.</summary>
    MonitorInfo? MonitorOf(long handle);

    /// <summary>The primary monitor, or null when the call fails.</summary>
    MonitorInfo? PrimaryMonitor();
}

/// <summary>One monitor's <c>MONITORINFOEX</c>. Ported from <c>desktop_manager.win32.monitor_info.Win32MonitorInfo</c>.</summary>
/// <param name="Bounds">The whole monitor.</param>
/// <param name="WorkArea">The monitor less the taskbar and docked bars.</param>
/// <param name="IsPrimary">Whether it is the primary monitor.</param>
/// <param name="Device">Its device name, such as <c>\\.\DISPLAY1</c>.</param>
public sealed record MonitorInfo(Rect Bounds, Rect WorkArea, bool IsPrimary, string Device);

/// <summary>A process's image name and executable path; both null when Windows will not say.</summary>
/// <param name="Name">The image name, such as <c>chrome.exe</c>.</param>
/// <param name="ExePath">The executable's full path.</param>
public sealed record ProcessDetails(string? Name = null, string? ExePath = null);

/// <summary>How a window is shown. Ported from <c>desktop_manager.display_guard.ShowState</c>.</summary>
public enum ShowState
{
    /// <summary>Neither minimized nor maximized.</summary>
    Normal,

    /// <summary>Minimized.</summary>
    Minimized,

    /// <summary>Maximized.</summary>
    Maximized,
}
