using System.Diagnostics;
using Rig.Core.Workspace;

namespace Rig.Core.Chrome;

/// <summary>How far a grouping run got.</summary>
/// <param name="Moved">Tabs moved.</param>
/// <param name="Stopped">Why it stopped; null when it finished.</param>
public sealed record GroupRun(int Moved, string? Stopped = null);

/// <summary>
/// Carries out a grouping plan: every ungroup first, then window by window with each window in front. Windows on other
/// virtual desktops are brought here for the run and sent home after, and the user is put back on the desktop they
/// were on. Ported from <c>desktop_manager.chrome.group_run</c>.
/// </summary>
/// <param name="actions">The platform's grouping steps.</param>
/// <param name="desktops">The virtual desktops the windows live on.</param>
/// <param name="sessionLocked">Whether the lock screen is up, to explain a window that would not come forward.</param>
/// <param name="sleep">Sleeps between looks while brought windows arrive.</param>
/// <param name="clock">Seconds on a monotonic clock.</param>
public sealed class GroupRunner(
    IChromeGroupActions actions,
    IChromeDesktops desktops,
    Func<bool>? sessionLocked = null,
    Action<TimeSpan>? sleep = null,
    Func<double>? clock = null)
{
    /// <summary>How long brought windows get to arrive: the move is asynchronous, and a window on its way refuses the foreground.</summary>
    public const double ArrivalSeconds = 2.0;

    private static readonly ActivitySource Source = new("Rig.Chrome");
    private static readonly TimeSpan ArrivalPoll = TimeSpan.FromMilliseconds(50);

    private readonly Func<bool> _sessionLocked = sessionLocked ?? (() => false);
    private readonly Action<TimeSpan> _sleep = sleep ?? Thread.Sleep;
    private readonly Func<double> _clock = clock ?? (() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);

    /// <summary>
    /// <see cref="TabGrouping.Plan"/> over windows in screen order, with site hosts looked up in each profile's
    /// history. Only titles that name no key and tabs without a URL are looked up: a tab with a URL names its own host.
    /// </summary>
    /// <param name="windows">The windows.</param>
    /// <param name="currentDesktop">The desktop the user is on.</param>
    /// <param name="minSize">The fewest tabs a new group needs.</param>
    /// <param name="thisDesktop">Group only windows on this desktop.</param>
    /// <param name="hostsByTitle">A profile's hosts for these titles, from its history.</param>
    /// <param name="owners">The projects' declared local ports and Vercel names.</param>
    /// <param name="phase">Which pass this plan is, for the trace.</param>
    public static GroupPlan Plan(
        IReadOnlyList<ChromeWindow> windows,
        int? currentDesktop,
        int minSize,
        bool thisDesktop,
        Func<string, IReadOnlyCollection<string>, IReadOnlyDictionary<string, string>> hostsByTitle,
        UrlOwners owners,
        string phase = "plan")
    {
        ArgumentNullException.ThrowIfNull(windows);
        ArgumentNullException.ThrowIfNull(hostsByTitle);
        using var activity = Source.StartActivity("chrome_tabs.plan_grouping");
        activity?.SetTag("group.phase", phase);
        Dictionary<string, HashSet<string>> unkeyed = new(StringComparer.Ordinal);
        foreach (var window in windows)
        {
            if (!unkeyed.TryGetValue(window.ProfileKey, out var titles))
            {
                titles = new HashSet<string>(StringComparer.Ordinal);
                unkeyed[window.ProfileKey] = titles;
            }

            titles.UnionWith(window.Tabs.Where(tab => tab.Url is null && TabGrouping.KeyOf(tab.Title) is null).Select(tab => tab.Title));
        }

        var hosts = unkeyed
            .Where(pair => pair.Key.Length > 0)
            .ToDictionary(pair => pair.Key, pair => hostsByTitle(pair.Key, pair.Value), StringComparer.Ordinal);
        var plan = TabGrouping.Plan(windows, currentDesktop, new GroupOptions(minSize, !thisDesktop, hosts, owners));
        activity?.SetTag("group.moves", plan.Moves.Count);
        activity?.SetTag("group.new_groups", plan.Moves.Count(move => move.New));
        activity?.SetTag("group.ungroups", plan.Ungroups.Count);
        activity?.SetTag("group.already_grouped", plan.AlreadyGrouped);
        activity?.SetTag("group.skipped_windows", plan.Skipped.Count);
        return plan;
    }

    /// <summary>
    /// Works through the plan: every ungroup first, then one window at a time. <paramref name="interrupted"/> is asked
    /// before every step; a reason stops the run there.
    /// </summary>
    public GroupRun Run(GroupPlan plan, Func<string?>? interrupted = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        using var activity = Source.StartActivity("chrome_tabs.group");
        activity?.SetTag("group.moves", plan.Moves.Count);
        activity?.SetTag("group.ungroups", plan.Ungroups.Count);
        var start = desktops.Current();
        GroupRun ran;
        try
        {
            ran = Steps(plan, interrupted ?? (() => null));
        }
        finally
        {
            desktops.ReturnTo(start);
        }

        activity?.SetTag("group.moved", ran.Moved);
        if (ran.Stopped is not null)
        {
            activity?.SetTag("group.stopped", ran.Stopped);
        }

        return ran;
    }

    private GroupRun Steps(GroupPlan plan, Func<string?> interrupted)
    {
        var handles = plan.Ungroups.Select(ungroup => ungroup.Tabs[0].WindowHandle)
            .Concat(plan.Moves.Select(move => move.Tab.WindowHandle))
            .Concat(plan.Moves.Select(move => move.HomeHandle))
            .ToHashSet();
        var brought = BringHere(handles);
        try
        {
            foreach (var number in plan.UngroupWindowNumbers())
            {
                if (UngroupWindow([.. plan.Ungroups.Where(ungroup => ungroup.WindowNumber == number)], interrupted) is { } error)
                {
                    return new GroupRun(0, error);
                }
            }

            // A confirmed ungroup changes only these tabs' membership. Later steps check each tab against its record,
            // so the record must say ungrouped.
            var ungrouped = plan.Ungroups.SelectMany(ungroup => ungroup.Tabs).Select(tab => tab.RuntimeId).ToHashSet();
            var moves = plan.Moves
                .Select(move => ungrouped.Contains(move.Tab.RuntimeId) ? move with { Tab = move.Tab with { Group = null } } : move)
                .ToList();
            var moved = 0;
            foreach (var number in plan.WindowNumbers())
            {
                var window = GroupWindow([.. moves.Where(move => move.WindowNumber == number)], interrupted);
                moved += window.Moved;
                if (window.Stopped is not null)
                {
                    return new GroupRun(moved, window.Stopped);
                }
            }

            return new GroupRun(moved);
        }
        finally
        {
            SendHome(brought);
        }
    }

    // Clicking into a window on another desktop would switch desktops; moving the window here does not.
    private List<(long Handle, int Home)> BringHere(IEnumerable<long> handles)
    {
        List<(long Handle, int Home)> brought = [];
        if (desktops.Current() is not { } current)
        {
            return brought;
        }

        try
        {
            foreach (var handle in handles.Order())
            {
                if (desktops.DesktopOf(handle) is { } home && home != current)
                {
                    try
                    {
                        desktops.MoveWindow(handle, current);
                        brought.Add((handle, home));
                    }
#pragma warning disable CA1031 // A window that will not move is left where it is; its steps report it.
                    catch (Exception)
#pragma warning restore CA1031
                    {
                    }
                }
            }

            var deadline = _clock() + ArrivalSeconds;
            while (brought.Count > 0 && _clock() < deadline && !brought.All(each => desktops.DesktopOf(each.Handle) == current))
            {
                _sleep(ArrivalPoll);
            }
        }
        catch
        {
            SendHome(brought);
            throw;
        }

        return brought;
    }

    private void SendHome(List<(long Handle, int Home)> brought)
    {
        foreach (var (handle, home) in brought)
        {
            if (desktops.WindowExists(handle))
            {
                try
                {
                    desktops.MoveWindow(handle, home);
                }
#pragma warning disable CA1031 // Sending a window home is best effort, as in Python.
                catch (Exception)
#pragma warning restore CA1031
                {
                }
            }
        }
    }

    private string DesktopNote(long handle)
    {
#pragma warning disable CA1031 // The note only explains a failure; a failing probe adds nothing.
        try
        {
            if (_sessionLocked())
            {
                return " (the session is locked)";
            }
        }
        catch (Exception)
        {
        }

        try
        {
            if (desktops.DesktopOf(handle) is { } home && desktops.Current() is { } current && home != current)
            {
                return $" (it is on desktop {home}, this is desktop {current})";
            }
        }
        catch (Exception)
        {
        }
#pragma warning restore CA1031

        return string.Empty;
    }

    private string? UngroupWindow(IReadOnlyList<Ungroup> ungroups, Func<string?> interrupted)
    {
        using var activity = Source.StartActivity("chrome_tabs.ungroup_window");
        activity?.SetTag("window.number", ungroups[0].WindowNumber);
        activity?.SetTag("group.ungroups", ungroups.Count);
        var error = UngroupSteps(ungroups, interrupted);
        if (error is not null)
        {
            activity?.SetTag("group.stopped", error);
        }

        return error;
    }

    private string? UngroupSteps(IReadOnlyList<Ungroup> ungroups, Func<string?> interrupted)
    {
        var first = ungroups[0];
        if (interrupted() is { } reason)
        {
            return reason;
        }

        var handle = first.Tabs[0].WindowHandle;
        using var lease = actions.Foreground(handle);
        if (!lease.Arrived)
        {
            return $"window {first.WindowNumber} would not come to the front{DesktopNote(handle)}";
        }

        foreach (var ungroup in ungroups)
        {
            if (interrupted() is { } stop)
            {
                return stop;
            }

            var result = actions.Ungroup(ungroup.Tabs[0], ungroup.Group.Title);
            if (!result.Grouped)
            {
                return $"ungrouping duplicate {ungroup.Group.Title} in window {ungroup.WindowNumber}: {result.Detail}";
            }
        }

        return null;
    }

    // Stops at the first surprise, because the mouse is then no longer doing what the plan assumed.
    private GroupRun GroupWindow(IReadOnlyList<GroupMove> moves, Func<string?> interrupted)
    {
        using var activity = Source.StartActivity("chrome_tabs.group_window");
        activity?.SetTag("window.number", moves[0].WindowNumber);
        activity?.SetTag("window.profile", moves[0].Profile);
        activity?.SetTag("group.moves", moves.Count);
        var ran = GroupSteps(moves, interrupted);
        activity?.SetTag("group.moved", ran.Moved);
        if (ran.Stopped is not null)
        {
            activity?.SetTag("group.stopped", ran.Stopped);
        }

        return ran;
    }

    private GroupRun GroupSteps(IReadOnlyList<GroupMove> moves, Func<string?> interrupted)
    {
        var first = moves[0];
        if (interrupted() is { } reason)
        {
            return new GroupRun(0, reason);
        }

        var handle = first.Tab.WindowHandle;
        using var lease = actions.Foreground(handle);
        if (!lease.Arrived)
        {
            return new GroupRun(0, $"window {first.WindowNumber} would not come to the front{DesktopNote(handle)}");
        }

        for (var done = 0; done < moves.Count; done++)
        {
            if (interrupted() is { } stop)
            {
                return new GroupRun(done, stop);
            }

            var move = moves[done];
            var where = $"at {move.Title} tab {move.Tab.Index + 1} in window {move.WindowNumber}";
            var tab = move.Tab;
            if (move.New && move.Crosses)
            {
                var carried = actions.MoveToWindow(move.Tab, move.HomeHandle);
                if (carried.Tab is null)
                {
                    return new GroupRun(done, $"{where}, moving it to window {move.HomeNumber}: {carried.Outcome.Detail}");
                }

                tab = carried.Tab;
            }

            var result = actions.GroupTab(tab, move.Title, move.New, move.HomeHandle);
            if (!result.Grouped)
            {
                return new GroupRun(done, $"{where}: {result.Detail}");
            }
        }

        return new GroupRun(moves.Count);
    }
}
