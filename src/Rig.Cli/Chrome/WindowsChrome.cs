using System.Diagnostics.CodeAnalysis;
using System.Runtime.Versioning;
using Rig.Core.Chrome;
using Rig.Core.Chrome.Windows;
using Rig.Core.Desktop;
using Rig.Core.Desktop.Windows;
using Rig.Core.Desktop.Windows.Uia;

namespace Rig.Cli.Chrome;

/// <summary>What the <c>chrome-tabs</c> commands that read Chrome need: its windows, and GitHub for the PRs they show.</summary>
/// <param name="ReadWindows">Reads every Chrome window and its tabs, oldest window first.</param>
/// <param name="GitHub">Answers the PR queries.</param>
internal sealed record ChromeTabsDriver(Func<IReadOnlyList<ChromeWindow>> ReadWindows, Rig.Core.Processes.IGitHubApi GitHub);

/// <summary>The Windows Chrome driver: UI Automation for the tabs, the shell's virtual desktops for where windows are.</summary>
[SupportedOSPlatform("windows8.1")]
[ExcludeFromCodeCoverage(Justification = "Wiring of native readers; the reader is covered by its fakes and the live test.")]
internal static class WindowsChrome
{
    /// <summary>Every Chrome window, read once. Desktop numbers are unknown when the shell's desktops cannot be reached, as in the Python.</summary>
    public static IReadOnlyList<ChromeWindow> ListWindows()
    {
        IWindowDesktops desktops;
        try
        {
            desktops = ComVirtualDesktops.Open();
        }
        catch (DesktopException)
        {
            desktops = new NoDesktops();
        }

        return new ChromeUiaReader(new NativeUiAutomation(), new NativeWin32Windows(), desktops, new NativeChromeWindowShell()).ListWindows();
    }

    private sealed class NoDesktops : IWindowDesktops
    {
        public bool IsOnCurrentDesktop(long handle) => true;

        public int? DesktopNumberOf(long handle) => null;
    }
}
