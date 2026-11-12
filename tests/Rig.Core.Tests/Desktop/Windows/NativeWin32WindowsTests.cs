using System.Runtime.Versioning;
using Rig.Core.Desktop.Windows;

namespace Rig.Core.Tests.Desktop.Windows;

/// <summary>
/// The real Win32 reads, on the Windows CI job and Windows machines only: each call answers without throwing, and the
/// answers hang together (a listed window is a window, this process's image is the test host's).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class NativeWin32WindowsTests
{
    [Fact]
    public void Reads_the_live_machine()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Win32 exists only on Windows.");
        var win32 = new NativeWin32Windows();

        var windows = win32.TopLevelWindows();
        Assert.NotEmpty(windows);
        foreach (var handle in windows.Take(20))
        {
            Assert.True(win32.IsWindow(handle));
            _ = (win32.IsVisible(handle), win32.IsMinimized(handle), win32.IsMaximized(handle), win32.IsToolWindow(handle));
            _ = (win32.Title(handle), win32.ClassName(handle), win32.ProcessIdOf(handle));
            _ = (win32.WindowRect(handle), win32.ExtendedFrameBounds(handle), win32.RootOwner(handle), win32.Root(handle));
            _ = win32.MonitorOf(handle);
        }

        Assert.Contains(win32.CurrentProcessId, win32.ProcessIds());
        var path = win32.ProcessImagePath(win32.CurrentProcessId);
        Assert.False(string.IsNullOrEmpty(path));
        Assert.True(File.Exists(path));
        Assert.Null(win32.ProcessImagePath(-1));
        Assert.False(win32.IsWindow(0));
        _ = (win32.ForegroundWindow(), win32.ConsoleWindow());

        var monitors = win32.Monitors();
        Assert.NotEmpty(monitors);
        Assert.Single(monitors, monitor => monitor.IsPrimary);
        Assert.Equal(monitors.Single(monitor => monitor.IsPrimary), win32.PrimaryMonitor());
        Assert.All(monitors, monitor => Assert.StartsWith(@"\\.\DISPLAY", monitor.Device, StringComparison.Ordinal));
    }
}
