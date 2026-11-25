using System.Globalization;

namespace Rig.Core.Chrome;

/// <summary>
/// Unattended runs: leave Chrome alone during calls, and wait for an idle machine before grouping.
/// </summary>
public static class Unattended
{
    /// <summary>
    /// Why an unattended run must not take the mouse now, and when it might; null when it may. A locked session comes
    /// first, then too little idle time, then a live call.
    /// </summary>
    public static Block? GroupBlock(IPresence presence, Func<IEnumerable<ChromeWindow>> readWindows, double idleMinutes)
    {
        ArgumentNullException.ThrowIfNull(presence);
        ArgumentNullException.ThrowIfNull(readWindows);
        if (presence.SessionLocked())
        {
            return new Block("the session is locked");
        }

        var idle = Presence.IdleSeconds(presence);
        var needed = idleMinutes * 60;
        if (idle < needed)
        {
            var ago = Math.Round(idle, MidpointRounding.ToEven).ToString("F0", CultureInfo.InvariantCulture);
            var minutes = idleMinutes.ToString(CultureInfo.InvariantCulture);
            return new Block($"the machine was used {ago}s ago (waits for {minutes} min idle)", needed - idle);
        }

        var windows = readWindows().ToList();
        return (LiveCall(presence, windows) ?? MeetingOpen(windows)) is { } reason ? new Block(reason) : null;
    }

    /// <summary>
    /// Why the tab strip cannot be read, from the read's own diagnosis; null when at least one tab carries native
    /// evidence. Closing still runs on scripting facts; grouping, which needs the strip, cannot.
    /// </summary>
    public static string? Unreadable(IReadOnlyCollection<ChromeWindow> windows)
    {
        ArgumentNullException.ThrowIfNull(windows);
        var tabs = windows.SelectMany(window => window.Tabs).ToList();
        if (tabs.Count == 0 || !tabs.All(tab => tab.EvidenceUnknown))
        {
            return null;
        }

        var why = tabs.Select(tab => tab.EvidenceProblem).FirstOrDefault(problem => problem is not null) ?? "accessibility could not pair Chrome's tab strips";
        return $"Chrome's tab strip is unreadable ({why})";
    }

    /// <summary>
    /// Why the user is on a call or sharing their screen; null when nothing native says so. A tab recording or
    /// sharing, or an audible call site, gives it away; failing that, Chrome holding the microphone or camera does (a
    /// muted call, a Meet app window). A tab without native evidence never counts: its audio flag is a placeholder, and
    /// a meeting page stays on its address after the call ends. A device check that fails says nothing.
    /// </summary>
    public static string? LiveCall(IPresence presence, IEnumerable<ChromeWindow> windows)
    {
        ArgumentNullException.ThrowIfNull(presence);
        ArgumentNullException.ThrowIfNull(windows);
        foreach (var tab in windows.SelectMany(window => window.Tabs).Where(tab => !tab.EvidenceUnknown))
        {
            if (tab.Alerts.FirstOrDefault(TabNames.LiveAlerts.Contains) is { } alert)
            {
                return $"a call is live: {alert.ToLowerInvariant()} ({TabPlanText.Clip(tab.Title)})";
            }

            if (tab.Audible && SiteRules.IsCall(tab.Title))
            {
                return $"a call is live ({TabPlanText.Clip(tab.Title)})";
            }
        }

        IReadOnlyList<string> devices;
        try
        {
            devices = presence.DevicesInUse();
        }
#pragma warning disable CA1031 // A device check is a hint; any failure means no hint.
        catch (Exception)
#pragma warning restore CA1031
        {
            return null;
        }

        return devices.Count > 0 ? $"a call is live: Chrome is using the {string.Join(" and ", devices)}" : null;
    }

    /// <summary>
    /// A meeting page whose audio is unknown; null when there is none. It may be a call or one already left, so it
    /// holds grouping back (which takes the mouse) but never stops closing.
    /// </summary>
    public static string? MeetingOpen(IEnumerable<ChromeWindow> windows)
    {
        ArgumentNullException.ThrowIfNull(windows);
        var tab = windows.SelectMany(window => window.Tabs).FirstOrDefault(tab => tab.EvidenceUnknown && InCallRoom(tab));
        return tab is null ? null : $"a meeting page is open ({TabPlanText.Clip(tab.Title)})";
    }

    // Without an address the title is all there is, so a call site's title still counts.
    private static bool InCallRoom(ChromeTab tab) => tab.Url is { } url ? SiteRules.IsCallRoom(url) : SiteRules.IsCall(tab.Title);
}

/// <summary>
/// An interruption check that stops grouping at the first key press or mouse move, and remembers it did. Grouping also
/// stops for its own failures; <see cref="Fired"/> tells the two apart.
/// </summary>
/// <param name="watch">The input watch, marked after each step that saw no input.</param>
public sealed class InputStop(InputWatch watch)
{
    /// <summary>Input stopped grouping.</summary>
    public bool Fired { get; private set; }

    /// <summary>Why to stop now, or null to go on.</summary>
    public string? Check()
    {
        if (watch.UserMoved())
        {
            Fired = true;
            return "keyboard or mouse input";
        }

        watch.Mark();
        return null;
    }
}
