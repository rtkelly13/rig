using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Rig.Core.Desktop.Windows;

/// <summary>
/// The Win32 reads, through user32, kernel32 and dwmapi. Process paths come from <c>QueryFullProcessImageNameW</c> with
/// limited query rights, which names more processes than the Python's <c>GetModuleFileNameEx</c> could (elevated ones
/// among them). The process is DPI-unaware, as the Python was, so coordinates match what the Python read.
/// </summary>
[SupportedOSPlatform("windows")]
[ExcludeFromCodeCoverage(Justification = "Win32 interop; the Windows CI job smoke-tests it, the ubuntu coverage run cannot reach it.")]
public sealed unsafe partial class NativeWin32Windows : IWin32Windows
{
    private const int ExStyleIndex = -20;
    private const long ToolWindowStyle = 0x80;
    private const uint ExtendedFrameBoundsAttribute = 9;
    private const uint RootOwnerFlag = 3;
    private const uint RootFlag = 2;
    private const uint ShowMaximized = 3;
    private const uint QueryLimitedInformation = 0x1000;
    private const uint MonitorPrimaryFlag = 1;
    private const uint MonitorDefaultToNearest = 2;
    private const uint MonitorDefaultToPrimary = 1;
    private const int ClassNameCapacity = 256;
    private const int PathCapacity = 32768;

    /// <inheritdoc/>
    public int CurrentProcessId { get; } = Environment.ProcessId;

    /// <inheritdoc/>
    public IReadOnlyList<long> TopLevelWindows()
    {
        var handles = new List<long>();
        var gc = GCHandle.Alloc(handles);
        try
        {
            _ = EnumWindows(&CollectWindow, GCHandle.ToIntPtr(gc));
        }
        finally
        {
            gc.Free();
        }

        return handles;
    }

    /// <inheritdoc/>
    public bool IsWindow(long handle) => IsWindowNative((nint)handle);

    /// <inheritdoc/>
    public bool IsVisible(long handle) => IsWindowVisible((nint)handle);

    /// <inheritdoc/>
    public bool IsMinimized(long handle) => IsIconic((nint)handle);

    /// <inheritdoc/>
    public bool IsMaximized(long handle)
    {
        var placement = new WindowPlacement { Length = (uint)sizeof(WindowPlacement) };
        return GetWindowPlacement((nint)handle, &placement) && placement.ShowCmd == ShowMaximized;
    }

    /// <inheritdoc/>
    public string? Title(long handle)
    {
        var length = GetWindowTextLengthW((nint)handle);
        if (length <= 0)
        {
            return "";
        }

        var buffer = new char[length + 1];
        fixed (char* text = buffer)
        {
            var copied = GetWindowTextW((nint)handle, text, buffer.Length);
            return new string(text, 0, Math.Max(copied, 0));
        }
    }

    /// <inheritdoc/>
    public string? ClassName(long handle)
    {
        var buffer = stackalloc char[ClassNameCapacity];
        var copied = GetClassNameW((nint)handle, buffer, ClassNameCapacity);
        return copied > 0 ? new string(buffer, 0, copied) : null;
    }

    /// <inheritdoc/>
    public bool IsToolWindow(long handle) => (GetWindowLongPtrW((nint)handle, ExStyleIndex) & ToolWindowStyle) != 0;

    /// <inheritdoc/>
    public int ProcessIdOf(long handle)
    {
        uint pid = 0;
        _ = GetWindowThreadProcessId((nint)handle, &pid);
        return (int)pid;
    }

    /// <inheritdoc/>
    public Rect? WindowRect(long handle)
    {
        NativeRect rect;
        return GetWindowRect((nint)handle, &rect) ? rect.ToRect() : null;
    }

    /// <inheritdoc/>
    public Rect? ExtendedFrameBounds(long handle)
    {
        NativeRect rect;
        return DwmGetWindowAttribute((nint)handle, ExtendedFrameBoundsAttribute, &rect, (uint)sizeof(NativeRect)) == 0
            ? rect.ToRect()
            : null;
    }

    /// <inheritdoc/>
    public long ForegroundWindow() => GetForegroundWindow();

    /// <inheritdoc/>
    public long ConsoleWindow() => GetConsoleWindow();

    /// <inheritdoc/>
    public long RootOwner(long handle) => GetAncestor((nint)handle, RootOwnerFlag);

    /// <inheritdoc/>
    public long Root(long handle) => GetAncestor((nint)handle, RootFlag);

    /// <inheritdoc/>
    public IReadOnlyList<int> ProcessIds()
    {
        for (var capacity = 1024; ; capacity *= 2)
        {
            var pids = new uint[capacity];
            uint needed;
            fixed (uint* buffer = pids)
            {
                if (!K32EnumProcesses(buffer, (uint)(capacity * sizeof(uint)), &needed))
                {
                    return [];
                }
            }

            var count = (int)(needed / sizeof(uint));
            if (count < capacity)
            {
                return [.. pids.Take(count).Select(pid => (int)pid)];
            }
        }
    }

    /// <inheritdoc/>
    public string? ProcessImagePath(int pid)
    {
        var process = OpenProcess(QueryLimitedInformation, false, (uint)pid);
        if (process == 0)
        {
            return null;
        }

        try
        {
            var buffer = new char[PathCapacity];
            var size = (uint)buffer.Length;
            fixed (char* path = buffer)
            {
                return QueryFullProcessImageNameW(process, 0, path, &size) && size > 0 ? new string(path, 0, (int)size) : null;
            }
        }
        finally
        {
            _ = CloseHandle(process);
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<MonitorInfo> Monitors()
    {
        var monitors = new List<MonitorInfo>();
        var gc = GCHandle.Alloc(monitors);
        try
        {
            _ = EnumDisplayMonitors(0, null, &CollectMonitor, GCHandle.ToIntPtr(gc));
        }
        finally
        {
            gc.Free();
        }

        return monitors;
    }

    /// <inheritdoc/>
    public MonitorInfo? MonitorOf(long handle) => Describe(MonitorFromWindow((nint)handle, MonitorDefaultToNearest));

    /// <inheritdoc/>
    public MonitorInfo? PrimaryMonitor() => Describe(MonitorFromPoint(default, MonitorDefaultToPrimary));

    private static MonitorInfo? Describe(nint monitor)
    {
        if (monitor == 0)
        {
            return null;
        }

        var info = new MonitorInfoEx { Size = (uint)sizeof(MonitorInfoEx) };
        if (!GetMonitorInfoW(monitor, &info))
        {
            return null;
        }

        var device = new string(info.Device, 0, 32);
        var end = device.IndexOf('\0', StringComparison.Ordinal);
        return new(
            info.Monitor.ToRect(),
            info.Work.ToRect(),
            (info.Flags & MonitorPrimaryFlag) != 0,
            end < 0 ? device : device[..end]);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int CollectWindow(nint window, nint state)
    {
        ((List<long>)GCHandle.FromIntPtr(state).Target!).Add(window);
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int CollectMonitor(nint monitor, nint deviceContext, NativeRect* clip, nint state)
    {
        if (Describe(monitor) is { } info)
        {
            ((List<MonitorInfo>)GCHandle.FromIntPtr(state).Target!).Add(info);
        }

        return 1;
    }

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumWindows(delegate* unmanaged[Stdcall]<nint, nint, int> callback, nint state);

    [LibraryImport("user32.dll", EntryPoint = "IsWindow")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowNative(nint window);

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowVisible(nint window);

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsIconic(nint window);

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowPlacement(nint window, WindowPlacement* placement);

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int GetWindowTextLengthW(nint window);

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int GetWindowTextW(nint window, char* text, int capacity);

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int GetClassNameW(nint window, char* name, int capacity);

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint GetWindowLongPtrW(nint window, int index);

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint GetWindowThreadProcessId(nint window, uint* pid);

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(nint window, NativeRect* rect);

    [LibraryImport("dwmapi.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int DwmGetWindowAttribute(nint window, uint attribute, void* value, uint size);

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint GetForegroundWindow();

    [LibraryImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint GetConsoleWindow();

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint GetAncestor(nint window, uint flags);

    [LibraryImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool K32EnumProcesses(uint* pids, uint size, uint* needed);

    [LibraryImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);

    [LibraryImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryFullProcessImageNameW(nint process, uint flags, char* path, uint* size);

    [LibraryImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumDisplayMonitors(
        nint deviceContext,
        NativeRect* clip,
        delegate* unmanaged[Stdcall]<nint, nint, NativeRect*, nint, int> callback,
        nint state);

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfoW(nint monitor, MonitorInfoEx* info);

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint MonitorFromWindow(nint window, uint flags);

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint MonitorFromPoint(NativePoint point, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public readonly Rect ToRect() => new(Left, Top, Right - Left, Bottom - Top);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPlacement
    {
        public uint Length;
        public uint Flags;
        public uint ShowCmd;
        public NativePoint MinPosition;
        public NativePoint MaxPosition;
        public NativeRect NormalPosition;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public uint Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
        public fixed char Device[32];
    }
}
