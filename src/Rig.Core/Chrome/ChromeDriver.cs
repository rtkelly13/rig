using System.Text.RegularExpressions;
using Rig.Core.Vault;

namespace Rig.Core.Chrome;

/// <summary>
/// Chrome's windows could not be read reliably: its UI kept changing under the read. A driver throws this instead of
/// returning a partial or guessed read, because a tab whose pinned state or group went unread could be closed or moved
/// by mistake. A run that gets it acts on nothing.
/// </summary>
public sealed class ChromeReadException : Exception
{
    /// <summary>Creates the exception.</summary>
    public ChromeReadException()
    {
    }

    /// <summary>Creates the exception with <paramref name="message"/>.</summary>
    public ChromeReadException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with <paramref name="message"/> and the failure behind it.</summary>
    public ChromeReadException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>How a close went.</summary>
/// <param name="Closed">The tab left the strip.</param>
/// <param name="Detail">What happened, for the run's report.</param>
/// <param name="Elapsed">How long it took.</param>
public sealed record CloseOutcome(bool Closed, string Detail, TimeSpan Elapsed);

/// <summary>How a group, ungroup or move went.</summary>
/// <param name="Grouped">The tab ended where it was asked to.</param>
/// <param name="Detail">What happened.</param>
/// <param name="Elapsed">How long it took.</param>
public sealed record GroupOutcome(bool Grouped, string Detail, TimeSpan Elapsed);

/// <summary>How a saved-group delete went.</summary>
/// <param name="Deleted">The chip left the bar.</param>
/// <param name="Detail">What happened.</param>
/// <param name="Tabs">The saved group's tab titles, as its menu listed them before the delete.</param>
/// <param name="Elapsed">How long it took.</param>
public sealed record SavedDeleteOutcome(bool Deleted, string Detail, IReadOnlyList<string> Tabs, TimeSpan Elapsed);

/// <summary>How a move to another window went.</summary>
/// <param name="Outcome">The move's outcome.</param>
/// <param name="Tab">The tab as it now sits in the target window, with its new identity; null when it did not move.</param>
public sealed record MoveOutcome(GroupOutcome Outcome, ChromeTab? Tab = null);

/// <summary>
/// The virtual desktops a platform's Chrome windows live on. Windows numbers its desktops and the run loops bring a
/// window over while they work on it; macOS Spaces are not addressable (<see cref="NoChromeDesktops"/>).
/// </summary>
public interface IChromeDesktops
{
    /// <summary>The desktop the user is on, or null when desktops are not addressable.</summary>
    int? Current();

    /// <summary>The desktop <paramref name="handle"/> is on.</summary>
    int? DesktopOf(long handle);

    /// <summary>Switch to desktop <paramref name="number"/>.</summary>
    void SwitchTo(int number);

    /// <summary>Move <paramref name="handle"/> to desktop <paramref name="number"/>.</summary>
    void MoveWindow(long handle, int number);

    /// <summary>Whether <paramref name="handle"/> is still a window.</summary>
    bool WindowExists(long handle);
}

/// <summary>Desktops that cannot be addressed: macOS Spaces.</summary>
public sealed class NoChromeDesktops : IChromeDesktops
{
    /// <inheritdoc/>
    public int? Current() => null;

    /// <inheritdoc/>
    public int? DesktopOf(long handle) => null;

    /// <inheritdoc/>
    public void SwitchTo(int number)
    {
    }

    /// <inheritdoc/>
    public void MoveWindow(long handle, int number)
    {
    }

    /// <inheritdoc/>
    public bool WindowExists(long handle) => false;
}

/// <summary>The Chrome profile a window belongs to, as its shell relaunch properties name it.</summary>
/// <param name="Directory">The profile directory ("Default", "Profile 4").</param>
/// <param name="Name">The profile's display name.</param>
public sealed partial record WindowProfile(string? Directory, string? Name)
{
    /// <summary>
    /// <c>--profile-directory</c> from a window's relaunch command. Chrome only stamps the flag when more than one
    /// profile exists, so a command without it relaunches "Default".
    /// </summary>
    public static string? DirectoryFromRelaunchCommand(string? command)
    {
        if (string.IsNullOrEmpty(command))
        {
            return null;
        }

        var match = ProfileDirectory().Match(command);
        if (!match.Success)
        {
            return "Default";
        }

        return match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
    }

    /// <summary><c>"Ryan (work) - Chrome"</c> is <c>"Ryan (work)"</c>.</summary>
    public static string? NameFromRelaunchDisplayName(string? displayName)
    {
        if (string.IsNullOrEmpty(displayName))
        {
            return null;
        }

        var name = PyText.Strip(RemoveSuffix(RemoveSuffix(displayName, " - Chrome"), " - Google Chrome"));
        return name.Length == 0 ? null : name;
    }

    private static string RemoveSuffix(string text, string suffix) =>
        text.EndsWith(suffix, StringComparison.Ordinal) ? text[..^suffix.Length] : text;

    [GeneratedRegex("""--profile-directory=(?:"([^"]+)"|(\S+))""")]
    private static partial Regex ProfileDirectory();
}

/// <summary>Reading the entries of Chrome's tab context menu. Ported from <c>desktop_manager.chrome.uia</c>.</summary>
public static class TabMenus
{
    /// <summary>
    /// Whether a "Move tab to another window" entry (lower-cased) names the window whose active tab is
    /// <paramref name="activeTitle"/> and which holds <paramref name="tabCount"/> tabs. Chrome elides a long title
    /// with an ellipsis.
    /// </summary>
    public static bool NamesWindow(string entry, string activeTitle, int tabCount)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(activeTitle);
        var others = tabCount - 1;
        var suffix = others == 0 ? string.Empty : $" and {others} other tab{(others == 1 ? string.Empty : "s")}";
        if (suffix.Length > 0 && !entry.EndsWith(suffix, StringComparison.Ordinal))
        {
            return false;
        }

        var head = entry[..(entry.Length - suffix.Length)];
        var title = PyText.Lower(PyText.Strip(activeTitle));
        if (head.EndsWith('…'))
        {
            return head.Length > 1 && title.StartsWith(head[..^1], StringComparison.Ordinal);
        }

        return head == title;
    }

    /// <summary>
    /// "Add tab to new group" (a window with no groups) or the "Add tab to group" submenu, not "Add tab to new split
    /// view", which sits right above them.
    /// </summary>
    public static bool IsGroupEntry(string name) => name is "add tab to new group" or "add tab to group";
}
