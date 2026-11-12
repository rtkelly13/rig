using System.Runtime.Versioning;
using Rig.Core.Chrome.Windows;
using Rig.Core.Desktop.Windows;
using Rig.Core.Desktop.Windows.Uia;

namespace Rig.Core.Tests.Chrome;

/// <summary>
/// The reader against the Chrome already running on the machine, read-only. Opt-in (<c>RIG_CHROME_LIVE=1</c>), because
/// it needs a Chrome with at least one window on the vertical tab strip, which is the only strip the reader knows.
/// </summary>
[SupportedOSPlatform("windows8.1")]
public sealed class ChromeUiaLiveTests
{
    [Fact]
    public void Reads_the_running_chrome()
    {
        Assert.SkipUnless(
            OperatingSystem.IsWindowsVersionAtLeast(6, 3) && Environment.GetEnvironmentVariable("RIG_CHROME_LIVE") == "1",
            "Set RIG_CHROME_LIVE=1 on a Windows machine running Chrome with vertical tabs to run it.");
        var reader = new ChromeUiaReader(new NativeUiAutomation(), new NativeWin32Windows(), new NoDesktops(), new NativeChromeWindowShell());

        var windows = reader.ListWindows();

        Assert.NotEmpty(windows);
        Assert.All(windows, window =>
        {
            Assert.NotEmpty(window.Tabs);
            Assert.NotNull(window.Rect);
            Assert.True(window.AgeRank < ChromeUiaReader.UnrankedAge);
            Assert.Equal(Enumerable.Range(0, window.Tabs.Count), window.Tabs.Select(tab => tab.Index));
        });
        Assert.Equal(windows.OrderBy(window => window.AgeRank).Select(window => window.Handle), windows.Select(window => window.Handle));
        var first = windows[0].Tabs[0];
        Assert.NotNull(reader.FindTabElement(first));
        Assert.Equal(windows[0].Handle, reader.ReadWindow(windows[0].Handle)?.Handle);
    }

    private sealed class NoDesktops : Rig.Core.Desktop.IWindowDesktops
    {
        public bool IsOnCurrentDesktop(long handle) => true;

        public int? DesktopNumberOf(long handle) => null;
    }
}
