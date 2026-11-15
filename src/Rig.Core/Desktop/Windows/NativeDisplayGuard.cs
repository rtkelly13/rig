using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Rig.Core.Vault;
using System.Runtime.Versioning;

namespace Rig.Core.Desktop.Windows;

/// <summary>
/// The real machine as the display guard sees it: monitors, display events and window placements. Ported from
/// <c>desktop_manager.display_guard_windows</c>.
/// </summary>
[SupportedOSPlatform("windows")]
[ExcludeFromCodeCoverage(Justification = "Win32 interop; the Windows CI job smoke-tests it, the ubuntu coverage run cannot reach it.")]
public sealed class NativeDisplayGuardPorts : IDisplayGuardPorts
{
    private const uint WpfRestoreToMaximized = 0x2;
    private const uint SwShowMinimized = 2;
    private const uint SwShowMaximized = 3;
    private const uint SwShowNoActivate = 4;
    private const uint SwShowMinNoActive = 7;
    private const nint PerMonitorAwareV2 = -4;

    /// <summary>
    /// Read and write real pixel coordinates on every monitor. A DPI-unaware process gets virtualised coordinates that
    /// differ by monitor; asking for physical coordinates keeps snapshots and re-applies in the same space.
    /// </summary>
    public static void MakePerMonitorDpiAware()
    {
        try
        {
            _ = SetProcessDpiAwarenessContext(PerMonitorAwareV2);
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException)
        {
            // Older Windows: the process stays DPI-unaware, as Python's guard could when the call was missing.
        }
    }

    private readonly IWin32Windows _win32;
    private readonly IVirtualDesktops _desktops;
    private readonly WindowInventory _inventory;
    private readonly string _restoreLockPath;
    private readonly DisplayEventListener? _events;
    private readonly Action<string> _log;

    /// <summary>The guard's view of <paramref name="win32"/>, with the restore lock and event counter it watches.</summary>
    /// <param name="win32">The Win32 reads.</param>
    /// <param name="desktops">The virtual desktops, put back after a maximized window is reactivated.</param>
    /// <param name="restoreLockPath">The restoring process's lock file.</param>
    /// <param name="log">Where the guard writes.</param>
    /// <param name="events">The display-change listener, when one was started.</param>
    public NativeDisplayGuardPorts(
        IWin32Windows win32,
        IVirtualDesktops desktops,
        string restoreLockPath,
        Action<string> log,
        DisplayEventListener? events = null)
    {
        ArgumentNullException.ThrowIfNull(win32);
        ArgumentNullException.ThrowIfNull(desktops);
        ArgumentNullException.ThrowIfNull(restoreLockPath);
        ArgumentNullException.ThrowIfNull(log);
        _win32 = win32;
        _desktops = desktops;
        _inventory = new WindowInventory(win32, desktops);
        _restoreLockPath = restoreLockPath;
        _log = log;
        _events = events;
    }

    /// <inheritdoc/>
    public double Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

    /// <inheritdoc/>
    public MonitorSignature MonitorSignature()
    {
        var monitors = _win32.Monitors()
            .Select(monitor => new GuardMonitor(
                monitor.Device.Replace("\\\\.\\", string.Empty, StringComparison.Ordinal),
                new MonitorBounds(monitor.Bounds.Left, monitor.Bounds.Top, monitor.Bounds.Right, monitor.Bounds.Bottom),
                monitor.IsPrimary))
            .OrderBy(monitor => monitor.Bounds.Left)
            .ThenBy(monitor => monitor.Bounds.Top)
            .ToList();
        return new(monitors);
    }

    /// <inheritdoc/>
    public int DisplayEventCount() => _events?.Count ?? 0;

    /// <inheritdoc/>
    public bool RestoreInProgress() => DisplayGuardLocks.IsHeld(_restoreLockPath);

    /// <inheritdoc/>
    public IReadOnlyList<WindowPlacementRecord> CapturePlacements()
    {
        var ownPid = Environment.ProcessId;
        var processNames = new Dictionary<int, string?>();
        var records = new List<WindowPlacementRecord>();
        foreach (var handle in _win32.TopLevelWindows())
        {
            try
            {
                if (!_win32.IsVisible(handle))
                {
                    continue;
                }

                var title = PyText.Strip(_win32.Title(handle) ?? string.Empty);
                if (title.Length == 0 || WindowInventory.IgnoredWindowTitles.Contains(title) || _win32.IsToolWindow(handle))
                {
                    continue;
                }

                var pid = _win32.ProcessIdOf(handle);
                if (pid is 0 || pid == ownPid)
                {
                    continue;
                }

                if (!processNames.TryGetValue(pid, out var processName))
                {
                    processName = _inventory.ProcessDetailsOf(pid).Name;
                    processNames[pid] = processName;
                }

                if (processName is { Length: > 0 } && WindowInventory.IgnoredProcessNames.Contains(PyText.Lower(processName)))
                {
                    continue;
                }

                if (!GetPlacement(handle, out var placement))
                {
                    continue;
                }

                records.Add(new(
                    handle,
                    pid,
                    title,
                    processName,
                    ShowStateOf(placement.ShowCmd),
                    ToRect(placement.NormalPosition),
                    _win32.WindowRect(handle) is { } window ? window : ToRect(placement.NormalPosition),
                    (placement.Flags & WpfRestoreToMaximized) != 0));
            }
            catch (Exception error) when (error is ExternalException or InvalidOperationException)
            {
                // A window can close mid-enumeration; skip it.
            }
        }

        return records;
    }

    /// <inheritdoc/>
    public IReadOnlyList<long> ApplyPlacements(IReadOnlyList<WindowPlacementRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        var foreground = _inventory.ForegroundWindow();
        var startingDesktop = _desktops.CurrentNumber;
        var placed = new List<long>();
        foreach (var record in records)
        {
            if (!_win32.IsWindow(record.Hwnd))
            {
                continue;
            }

            if (SetPlacement(record, record.ShowState switch
                {
                    ShowState.Maximized => SwShowMaximized,
                    ShowState.Minimized => SwShowMinNoActive,
                    _ => SwShowNoActivate,
                }))
            {
                placed.Add(record.Hwnd);
                if (record.ShowState is ShowState.Maximized)
                {
                    // Restore onto the snapshot rectangle first, then maximize, so it maximizes on that monitor.
                    _ = SetPlacement(record, SwShowNoActivate);
                    _ = SetPlacement(record, SwShowMaximized);
                }
            }
            else
            {
                _log($"  Could not place [{record.Hwnd}] '{record.Title}'");
            }
        }

        if (startingDesktop is >= 1 && _desktops.CurrentNumber != startingDesktop)
        {
            _ = VirtualDesktopOperations.GoTo(_desktops, startingDesktop);
        }

        if (foreground is { } window && _win32.IsWindow(window))
        {
            _ = SetForegroundWindow((nint)window);
        }

        return placed;
    }

    /// <inheritdoc/>
    public void Log(string message) => _log(message);

    private static bool SetPlacement(WindowPlacementRecord record, uint showCommand)
    {
        if (!GetPlacement(record.Hwnd, out var placement))
        {
            return false;
        }

        placement.Flags = record.RestoreToMaximized ? WpfRestoreToMaximized : 0;
        placement.ShowCmd = showCommand;
        placement.NormalPosition = ToNative(record.NormalRect);
        return SetWindowPlacement((nint)record.Hwnd, ref placement);
    }

    private static ShowState ShowStateOf(uint showCommand) => showCommand switch
    {
        SwShowMinimized => ShowState.Minimized,
        SwShowMaximized => ShowState.Maximized,
        _ => ShowState.Normal,
    };

    private static Rect ToRect(NativeRect rect) => new(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);

    private static NativeRect ToNative(Rect rect) => new() { Left = rect.Left, Top = rect.Top, Right = rect.Right, Bottom = rect.Bottom };

    private static bool GetPlacement(long handle, out NativePlacement placement)
    {
        placement = new NativePlacement { Length = (uint)Marshal.SizeOf<NativePlacement>() };
        return GetWindowPlacement((nint)handle, ref placement);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativePlacement
    {
        public uint Length;
        public uint Flags;
        public uint ShowCmd;
        public NativePoint MinPosition;
        public NativePoint MaxPosition;
        public NativeRect NormalPosition;
    }

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowPlacement(nint window, ref NativePlacement placement);

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPlacement(nint window, ref NativePlacement placement);

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint window);

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessDpiAwarenessContext(nint context);
}

/// <summary>
/// Count display-change, resume and unlock broadcasts on a hidden window. Polling the monitor set alone can miss a
/// drop-and-return that completes between two polls; every such flap still broadcasts <c>WM_DISPLAYCHANGE</c>.
/// Ported from <c>desktop_manager.display_guard_windows.DisplayEventListener</c>.
/// </summary>
[SupportedOSPlatform("windows")]
[ExcludeFromCodeCoverage(Justification = "Win32 message loop; the Windows CI job smoke-tests it, the ubuntu coverage run cannot reach it.")]
public sealed class DisplayEventListener : IDisposable
{
    private const uint WmDisplayChange = 0x007E;
    private const uint WmPowerBroadcast = 0x0218;
    private const uint WmWtsSessionChange = 0x02B1;
    private const uint PbtApmResumeSuspend = 0x0007;
    private const uint PbtApmResumeAutomatic = 0x0012;
    private const uint WtsSessionUnlock = 0x8;
    private const string ClassName = "DesktopManagerDisplayGuard";

    private readonly Action<string> _log;
    private readonly Thread _thread;
    private readonly Lock _sync = new();
    private int _count;

    /// <summary>A listener for <paramref name="log"/>; nothing runs until <see cref="Start"/>.</summary>
    public DisplayEventListener(Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log;
        _thread = new Thread(Run) { Name = "display-guard-events", IsBackground = true };
    }

    /// <summary>How many display-change, resume or unlock events have been seen.</summary>
    public int Count
    {
        get
        {
            lock (_sync)
            {
                return _count;
            }
        }
    }

    /// <summary>Start the message loop on its own thread.</summary>
    public void Start() => _thread.Start();

    /// <inheritdoc/>
    public void Dispose()
    {
        // The loop belongs to the process's lifetime, as Python's daemon thread did.
    }

    private void Bump(string reason)
    {
        lock (_sync)
        {
            _count++;
        }

        _log($"Event: {reason}");
    }

    private void Run()
    {
        var wndProc = new WindowProcedure(Handler);
        var windowClass = new WindowClass
        {
            Procedure = wndProc,
            ClassName = ClassName,
            Instance = GetModuleHandle(null),
        };
        var atom = RegisterClass(ref windowClass);
        if (atom == 0)
        {
            _log("Display event listener unavailable; relying on polling only.");
            return;
        }

        var handle = CreateWindowEx(0, ClassName, "Desktop Manager Display Guard", 0, 0, 0, 0, 0, 0, 0, windowClass.Instance, 0);
        if (handle == 0)
        {
            _ = UnregisterClass(ClassName, windowClass.Instance);
            _log("Display event listener unavailable; relying on polling only.");
            return;
        }

        try
        {
            _ = WTSRegisterSessionNotification(handle, 0);
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException)
        {
            _log($"Session unlock notifications unavailable ({error.Message}).");
        }

        while (GetMessage(out var message, 0, 0, 0) > 0)
        {
            _ = TranslateMessage(ref message);
            _ = DispatchMessage(ref message);
        }

        _ = DestroyWindow(handle);
        _ = UnregisterClass(ClassName, windowClass.Instance);
        GC.KeepAlive(wndProc);
    }

    private nint Handler(nint window, uint message, nint wParam, nint lParam)
    {
        switch (message)
        {
            case WmDisplayChange:
                Bump($"WM_DISPLAYCHANGE {(long)lParam & 0xFFFF}x{((long)lParam >> 16) & 0xFFFF}");
                break;
            case WmPowerBroadcast when (uint)wParam is PbtApmResumeSuspend or PbtApmResumeAutomatic:
                Bump("power resume");
                break;
            case WmWtsSessionChange when (uint)wParam == WtsSessionUnlock:
                Bump("session unlock");
                break;
        }

        return DefWindowProc(window, message, wParam, lParam);
    }

    private delegate nint WindowProcedure(nint window, uint message, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        public uint Style;
        public WindowProcedure Procedure;
        public int ClassExtra;
        public int WindowExtra;
        public nint Instance;
        public nint Icon;
        public nint Cursor;
        public nint Background;
        public string? MenuName;
        public string ClassName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public nint Window;
        public uint Value;
        public nint WParam;
        public nint LParam;
        public uint Time;
        public NativePoint Point;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern ushort RegisterClass(ref WindowClass windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint CreateWindowEx(
        uint extendedStyle,
        string className,
        string windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        nint parent,
        nint menu,
        nint instance,
        nint parameter);

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int GetMessage(out Message message, nint window, uint filterMin, uint filterMax);

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref Message message);

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint DispatchMessage(ref Message message);

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint DefWindowProc(nint window, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterClass(string className, nint instance);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint GetModuleHandle(string? moduleName);

    [DllImport("wtsapi32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSRegisterSessionNotification(nint window, uint flags);
}
