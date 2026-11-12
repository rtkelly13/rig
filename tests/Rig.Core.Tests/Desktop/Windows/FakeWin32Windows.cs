using Rig.Core.Desktop;
using Rig.Core.Desktop.Windows;

namespace Rig.Core.Tests.Desktop.Windows;

/// <summary>One window the fake knows; every field is what the matching Win32 read answers.</summary>
internal sealed record FakeWindow(long Handle, string? Title, int Pid)
{
    public bool Visible { get; init; } = true;

    public bool Minimized { get; init; }

    public bool Maximized { get; init; }

    public bool Tool { get; init; }

    public string? ClassName { get; init; } = "Window";

    public Rect? Rect { get; init; }

    public Rect? Frame { get; init; }

    public long RootOwner { get; init; }

    public long Root { get; init; }

    public int? Desktop { get; init; } = 1;
}

/// <summary>An in-memory Win32 and virtual-desktop layer: windows in z-order, processes by pid, monitors.</summary>
internal sealed class FakeWin32Windows : IWin32Windows, IWindowDesktops
{
    public List<FakeWindow> Windows { get; } = [];

    public Dictionary<int, string?> Processes { get; } = [];

    public List<MonitorInfo> MonitorList { get; } = [];

    public int CurrentDesktop { get; set; } = 1;

    public int CurrentProcessId { get; set; } = 999;

    public long Foreground { get; set; }

    public long Console { get; set; }

    public void Add(FakeWindow window) => Windows.Add(window);

    public void AddMonitor(Rect bounds, Rect work, bool primary = false) =>
        MonitorList.Add(new(bounds, work, primary, $@"\\.\DISPLAY{MonitorList.Count + 1}"));

    public IReadOnlyList<long> TopLevelWindows() => [.. Windows.Select(window => window.Handle)];

    public bool IsWindow(long handle) => Find(handle) is not null;

    public bool IsVisible(long handle) => Find(handle)?.Visible == true;

    public bool IsMinimized(long handle) => Find(handle)?.Minimized == true;

    public bool IsMaximized(long handle) => Find(handle)?.Maximized == true;

    public string? Title(long handle) => Find(handle)?.Title;

    public string? ClassName(long handle) => Find(handle)?.ClassName;

    public bool IsToolWindow(long handle) => Find(handle)?.Tool == true;

    public int ProcessIdOf(long handle) => Find(handle)?.Pid ?? 0;

    public Rect? WindowRect(long handle) => Find(handle)?.Rect;

    public Rect? ExtendedFrameBounds(long handle) => Find(handle)?.Frame;

    public long ForegroundWindow() => Foreground;

    public long ConsoleWindow() => Console;

    public long RootOwner(long handle) => Find(handle)?.RootOwner ?? 0;

    public long Root(long handle) => Find(handle)?.Root ?? 0;

    public IReadOnlyList<int> ProcessIds() => [0, .. Processes.Keys];

    public string? ProcessImagePath(int pid) => Processes.GetValueOrDefault(pid);

    public IReadOnlyList<MonitorInfo> Monitors() => MonitorList;

    public MonitorInfo? MonitorOf(long handle) => MonitorList.FirstOrDefault();

    public MonitorInfo? PrimaryMonitor() => MonitorList.FirstOrDefault(monitor => monitor.IsPrimary);

    public bool IsOnCurrentDesktop(long handle) => Find(handle)?.Desktop == CurrentDesktop;

    public int? DesktopNumberOf(long handle) => Find(handle)?.Desktop;

    private FakeWindow? Find(long handle) => Windows.FirstOrDefault(window => window.Handle == handle);
}
