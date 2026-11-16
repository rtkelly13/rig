namespace Rig.Core.Chrome;

/// <summary>One platform's Chrome: reading it, closing and grouping tabs, its desktops, and whether the user is around.</summary>
/// <param name="ReadWindows">Reads every Chrome window and its tabs, oldest window first.</param>
/// <param name="Actions">Closes tabs and puts each window's selection back.</param>
/// <param name="Grouping">Groups, ungroups and moves tabs.</param>
/// <param name="Saved">Reads and deletes saved tab groups.</param>
/// <param name="Desktops">The virtual desktops the windows live on.</param>
/// <param name="Foreground">The window with the focus.</param>
/// <param name="Presence">Whether the user is on a call or the session is locked.</param>
/// <param name="HostsByTitle">A profile's hosts for these titles, from its history.</param>
public sealed record ChromeSession(
    Func<IReadOnlyList<ChromeWindow>> ReadWindows,
    IChromeTabActions Actions,
    IChromeGroupActions Grouping,
    IChromeSavedGroupActions Saved,
    IChromeDesktops Desktops,
    Func<long> Foreground,
    IPresence Presence,
    Func<string, IReadOnlyCollection<string>, IReadOnlyDictionary<string, string>> HostsByTitle);
