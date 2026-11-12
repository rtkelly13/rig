using System.Globalization;

namespace Rig.Core.Chrome;

/// <summary>
/// Unattended runs: leave Chrome alone during calls, and wait for an idle machine before grouping. Ported from the pure
/// part of <c>desktop_manager.chrome.unattended</c>; <see cref="UnattendedGrouping"/> runs the waits and retries.
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

        return LiveCall(presence, readWindows()) is { } live ? new Block(live) : null;
    }

    /// <summary>
    /// Why the user looks to be on a call or sharing their screen; null when they do not. A tab recording or sharing,
    /// or an audible call site, gives it away; failing that, Chrome holding the microphone or camera does (a muted
    /// call, a Meet app window). A device check that fails says nothing.
    /// </summary>
    public static string? LiveCall(IPresence presence, IEnumerable<ChromeWindow> windows)
    {
        ArgumentNullException.ThrowIfNull(presence);
        ArgumentNullException.ThrowIfNull(windows);
        foreach (var tab in windows.SelectMany(window => window.Tabs))
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
#pragma warning disable CA1031 // A device check is a hint; any failure means no hint, as in Python.
        catch (Exception)
#pragma warning restore CA1031
        {
            return null;
        }

        return devices.Count > 0 ? $"a call is live: Chrome is using the {string.Join(" and ", devices)}" : null;
    }
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
