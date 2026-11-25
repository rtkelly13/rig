namespace Rig.Core.Chrome;

/// <summary>What happened to a link handed to <see cref="LinkRouter"/>.</summary>
/// <param name="Landed">The page opened in the companion window, or in a new companion window.</param>
/// <param name="Detail">What happened, for the caller's log.</param>
public sealed record LinkOutcome(bool Landed, string Detail);

/// <summary>
/// Opens pages in the companion "Rig links" Chrome window, so a link from the rig dashboard (an <c>--app</c> window
/// with no tab strip) or a page a rig command opens lands beside the dashboard instead of in whichever Chrome window
/// was used last.
/// </summary>
/// <remarks>
/// Chrome's command line opens a URL in the profile's most recently active window, so the router brings the companion
/// window to the front, launches the URL with that window's profile, then reads the windows again to check where the
/// new tab went. A tab that landed elsewhere is moved into the companion window with the tab driver. Without a
/// companion window, it opens one named <see cref="WindowName"/>, which the Work desktop's plan places on screen 3.
/// </remarks>
/// <param name="readWindows">Reads every Chrome window and its tabs.</param>
/// <param name="actions">Brings a window to the front and moves tabs between windows.</param>
/// <param name="launch">Runs Chrome with these arguments; the user-data directory is the caller's to add.</param>
/// <param name="pause">Waits between reads while Chrome opens the tab.</param>
public sealed class LinkRouter(
    Func<IReadOnlyList<ChromeWindow>> readWindows,
    IChromeGroupActions actions,
    Action<IReadOnlyList<string>> launch,
    Action<TimeSpan> pause)
{
    /// <summary>The companion window's name: Chrome's <c>--window-name</c>, which becomes its title.</summary>
    public const string WindowName = "Rig links";

    /// <summary>How many times the windows are read again for the new tab.</summary>
    public const int Checks = 6;

    /// <summary>The wait before each read.</summary>
    public static readonly TimeSpan CheckInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>Whether <paramref name="url"/> is a page the router opens: an absolute http or https address.</summary>
    public static bool IsWebPage(Uri url) =>
        url is { IsAbsoluteUri: true } && (url.Scheme == Uri.UriSchemeHttp || url.Scheme == Uri.UriSchemeHttps);

    /// <summary>
    /// Opens <paramref name="url"/> in the companion window, creating that window with <paramref name="profile"/> when
    /// there is none.
    /// </summary>
    /// <param name="url">An http or https page.</param>
    /// <param name="profile">The Chrome profile directory a new companion window opens with.</param>
    /// <exception cref="ArgumentException">The address is not an http or https page.</exception>
    public LinkOutcome Open(Uri url, string profile)
    {
        ArgumentNullException.ThrowIfNull(url);
        if (!IsWebPage(url))
        {
            throw new ArgumentException($"Only http and https pages open in {WindowName}: {url}", nameof(url));
        }

        var windows = readWindows();
        var companion = FindCompanion(windows);
        if (companion is null)
        {
            launch([$"--profile-directory={profile}", "--new-window", $"--window-name={WindowName}", url.AbsoluteUri]);
            return new LinkOutcome(true, $"opened a new {WindowName} window");
        }

        var before = windows.SelectMany(window => window.Tabs).Select(tab => tab.RuntimeId).ToHashSet();
        using var lease = actions.Foreground(companion.Handle);
        launch([$"--profile-directory={companion.ProfileDir ?? profile}", url.AbsoluteUri]);
        for (var check = 0; check < Checks; check++)
        {
            pause(CheckInterval);
            var opened = readWindows()
                .SelectMany(window => window.Tabs)
                .Where(tab => !before.Contains(tab.RuntimeId))
                .OrderByDescending(tab => tab.Selected)
                .FirstOrDefault();
            if (opened is null)
            {
                continue;
            }

            if (opened.WindowHandle == companion.Handle)
            {
                return new LinkOutcome(true, $"opened in {WindowName}");
            }

            var moved = actions.MoveToWindow(opened, companion.Handle);
            return moved.Outcome.Grouped
                ? new LinkOutcome(true, $"moved into {WindowName}")
                : new LinkOutcome(false, $"opened in another window; moving it failed: {moved.Outcome.Detail}");
        }

        return new LinkOutcome(false, lease.Arrived
            ? "launched, but the new tab never showed up"
            : $"{WindowName} would not come to the front; Chrome chose the window");
    }

    /// <summary>The companion window: the oldest whose title carries <see cref="WindowName"/>.</summary>
    public static ChromeWindow? FindCompanion(IEnumerable<ChromeWindow> windows) =>
        windows.FirstOrDefault(window => window.Title.Contains(WindowName, StringComparison.OrdinalIgnoreCase));
}
