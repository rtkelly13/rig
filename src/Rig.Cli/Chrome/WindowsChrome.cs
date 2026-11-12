using System.Diagnostics.CodeAnalysis;
using System.Runtime.Versioning;
using Rig.Core.Chrome;
using Rig.Core.Chrome.Windows;
using Rig.Core.Desktop;
using Rig.Core.Desktop.Windows;
using Rig.Core.Desktop.Windows.Uia;
using Rig.Core.Processes;
using Rig.Core.Workspace;

namespace Rig.Cli.Chrome;

/// <summary>
/// What the <c>chrome-tabs</c> commands need: a Chrome session, opened when a command runs, GitHub for the PRs, and the
/// projects' declared URLs for grouping.
/// </summary>
/// <param name="Open">Opens the platform's Chrome session.</param>
/// <param name="GitHub">Answers the PR queries.</param>
/// <param name="Owners">The projects' declared local ports and Vercel names; none when the registry cannot be read.</param>
internal sealed record ChromeTabsDriver(Func<ChromeSession> Open, IGitHubApi GitHub, Func<UrlOwners> Owners)
{
    /// <summary>
    /// The owners declared in <paramref name="registry"/>, or none when it cannot be read, as <c>_url_owners</c> does.
    /// A malformed declaration still throws.
    /// </summary>
    public static UrlOwners LoadOwners(string registry)
    {
        try
        {
            return UrlOwners.Load(registry);
        }
        catch (IOException)
        {
            return UrlOwners.None;
        }
    }
}

/// <summary>One platform's Chrome: reading it, closing and grouping tabs, its desktops, and whether the user is around.</summary>
/// <param name="ReadWindows">Reads every Chrome window and its tabs, oldest window first.</param>
/// <param name="Actions">Closes tabs and puts each window's selection back.</param>
/// <param name="Grouping">Groups, ungroups and moves tabs.</param>
/// <param name="Saved">Reads and deletes saved tab groups.</param>
/// <param name="Desktops">The virtual desktops the windows live on.</param>
/// <param name="Foreground">The window with the focus.</param>
/// <param name="Presence">Whether the user is on a call or the session is locked.</param>
/// <param name="HostsByTitle">A profile's hosts for these titles, from its history.</param>
internal sealed record ChromeSession(
    Func<IReadOnlyList<ChromeWindow>> ReadWindows,
    IChromeTabActions Actions,
    IChromeGroupActions Grouping,
    IChromeSavedGroupActions Saved,
    IChromeDesktops Desktops,
    Func<long> Foreground,
    IPresence Presence,
    Func<string, IReadOnlyCollection<string>, IReadOnlyDictionary<string, string>> HostsByTitle);

/// <summary>The Windows Chrome driver: UI Automation for the tabs, the shell's virtual desktops for where windows are.</summary>
[SupportedOSPlatform("windows8.1")]
[ExcludeFromCodeCoverage(Justification = "Wiring of native readers; the reader is covered by its fakes and the live test.")]
internal static class WindowsChrome
{
    /// <summary>
    /// Opens the native session. Desktop numbers are unknown, and windows are never moved between desktops, when the
    /// shell's desktops cannot be reached, as in the Python.
    /// </summary>
    public static ChromeSession Open()
    {
        IWindowDesktops placement;
        IChromeDesktops desktops;
        var win32 = new NativeWin32Windows();
        try
        {
            var shell = ComVirtualDesktops.Open();
            placement = shell;
            desktops = new ChromeVirtualDesktops(shell, win32.IsWindow);
        }
        catch (DesktopException)
        {
            placement = new NoDesktops();
            desktops = new NoChromeDesktops();
        }

        var uia = new NativeUiAutomation();
        var reader = new ChromeUiaReader(uia, win32, placement, new NativeChromeWindowShell());
        var actions = new ChromeUiaActions(reader, uia, win32, new NativeWin32Actions());
        return new ChromeSession(
            reader.ListWindows,
            actions,
            actions,
            actions,
            desktops,
            actions.ForegroundWindow,
            new NativePresence(),
            (profile, titles) => ChromeHistory.HostsByTitle(ChromeHistory.HistoryPath(Environment.GetEnvironmentVariable, profile), titles));
    }

    private sealed class NoDesktops : IWindowDesktops
    {
        public bool IsOnCurrentDesktop(long handle) => true;

        public int? DesktopNumberOf(long handle) => null;
    }
}
