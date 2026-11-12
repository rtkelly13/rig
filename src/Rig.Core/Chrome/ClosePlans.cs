using System.Diagnostics;

namespace Rig.Core.Chrome;

/// <summary>A tab with where it sits, as every planner sees it.</summary>
public interface IPlacedTab
{
    /// <summary>The tab.</summary>
    ChromeTab Tab { get; }

    /// <summary>The profile scope it is planned in (<see cref="ChromeWindow.ProfileKey"/>).</summary>
    string Profile { get; }

    /// <summary>Its window's <see cref="ChromeWindow.AgeRank"/>.</summary>
    long WindowAge { get; }

    /// <summary>Its window's 1-based number within the profile, oldest first, for human-readable reasons.</summary>
    int WindowNumber { get; }

    /// <summary>What the planner calls it: a PR ref, a site key or the title.</summary>
    string Label { get; }
}

/// <summary>One tab a plan closes.</summary>
public interface IPlannedTabClose
{
    /// <summary>The tab to close.</summary>
    IPlacedTab Item { get; }

    /// <summary>The tab that stays in its place, selected instead of it when it was active and both share a window.</summary>
    ChromeTab? Keeper { get; }
}

/// <summary>A plan that closes tabs.</summary>
public interface IClosePlan
{
    /// <summary>The tabs to close, in plan order.</summary>
    IReadOnlyList<IPlannedTabClose> Closes { get; }
}

/// <summary>What closing needs from a driver: the UI Automation adapter in production, a fake in tests.</summary>
public interface IChromeTabActions
{
    /// <summary>Closes <paramref name="tab"/>, found again by its runtime id.</summary>
    CloseOutcome CloseTab(ChromeTab tab);

    /// <summary>Re-selects each window's selected tab, or its fallback when that tab closed.</summary>
    IReadOnlyDictionary<long, ChromeTab?> RestoreSelection(
        IEnumerable<ChromeWindow> before,
        IReadOnlyDictionary<long, ChromeTab>? fallbacks = null);
}

/// <summary>How one planned close went.</summary>
/// <param name="Planned">The close.</param>
/// <param name="Closed">The tab left the strip.</param>
/// <param name="Detail">What happened.</param>
/// <param name="Elapsed">How long it took.</param>
public sealed record AppliedClose(IPlannedTabClose Planned, bool Closed, string Detail, TimeSpan Elapsed);

/// <summary>What is left of the plans once <see cref="ClosePlans.HoldUnattended"/> has taken out what nobody watched.</summary>
/// <param name="Closes">The closes safe to run unattended.</param>
/// <param name="Held">The closes taken out, left for an attended run.</param>
public sealed record HeldPlan(IReadOnlyList<IPlannedTabClose> Closes, IReadOnlyList<IPlannedTabClose> Held) : IClosePlan;

/// <summary>
/// What the tab planners share: the keeper choice, how a tab is described, and executing plans window by window.
/// Ported from the shared half of <c>desktop_manager.chrome.pr_prune</c>.
/// </summary>
public static class ClosePlans
{
    private static readonly ActivitySource Source = new("Rig.Chrome");

    /// <summary>Orders the tab to keep first: the active tab, then a loaded one, then the leftmost in the oldest window.</summary>
    public static IComparer<IPlacedTab> KeeperOrder { get; } = Comparer<IPlacedTab>.Create((left, right) =>
    {
        var bySelected = right.Tab.Selected.CompareTo(left.Tab.Selected);
        if (bySelected != 0)
        {
            return bySelected;
        }

        var byLoaded = right.Tab.Loaded.CompareTo(left.Tab.Loaded);
        if (byLoaded != 0)
        {
            return byLoaded;
        }

        var byAge = left.WindowAge.CompareTo(right.WindowAge);
        return byAge != 0 ? byAge : left.Tab.Index.CompareTo(right.Tab.Index);
    });

    /// <summary>The first tab to keep of <paramref name="items"/>, by <see cref="KeeperOrder"/>.</summary>
    public static T Keeper<T>(IEnumerable<T> items)
        where T : IPlacedTab => items.MinBy(item => (IPlacedTab)item, KeeperOrder)!;

    /// <summary><c>"2 in window 1"</c>: the tab's 1-based position and its window's number.</summary>
    public static string Describe(IPlacedTab item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return $"{item.Tab.Index + 1} in window {item.WindowNumber}";
    }

    /// <summary>Window to the tab to select because the window's active tab closed and its keeper is there.</summary>
    public static IReadOnlyDictionary<long, ChromeTab> Activations(IEnumerable<IPlannedTabClose> closes)
    {
        ArgumentNullException.ThrowIfNull(closes);
        var chosen = new Dictionary<long, ChromeTab>();
        foreach (var close in closes)
        {
            if (close.Item.Tab.Selected && close.Keeper is { } keeper && keeper.WindowHandle == close.Item.Tab.WindowHandle)
            {
                chosen.TryAdd(keeper.WindowHandle, keeper);
            }
        }

        return chosen;
    }

    /// <summary>
    /// Closes every planned tab, one window at a time, and puts that window's selection back before moving to the next.
    /// </summary>
    /// <remarks>
    /// Closing selects each tab first, so working window by window means each window changes once instead of
    /// flickering as closes hop between windows. Windows go in <paramref name="order"/> (handles), then the others in
    /// <paramref name="windows"/> order; <paramref name="afterWindow"/> runs once per window after its selection is
    /// restored. Each close re-finds its tab by runtime id, so the index shift every close causes cannot redirect a
    /// later close onto a keeper.
    /// </remarks>
    public static IReadOnlyList<AppliedClose> Apply(
        IEnumerable<IClosePlan> plans,
        IEnumerable<ChromeWindow> windows,
        IChromeTabActions actions,
        IEnumerable<long>? order = null,
        Action<ChromeWindow, IReadOnlyList<AppliedClose>>? afterWindow = null)
    {
        ArgumentNullException.ThrowIfNull(plans);
        ArgumentNullException.ThrowIfNull(windows);
        ArgumentNullException.ThrowIfNull(actions);
        var byWindow = plans.SelectMany(plan => plan.Closes).GroupBy(close => close.Item.Tab.WindowHandle)
            .ToDictionary(group => group.Key, group => group.ToList());
        var known = new Dictionary<long, ChromeWindow>();
        var handles = new List<long>();
        foreach (var window in windows)
        {
            if (known.TryAdd(window.Handle, window))
            {
                handles.Add(window.Handle);
            }
            else
            {
                known[window.Handle] = window;
            }
        }

        var first = (order ?? []).Where(known.ContainsKey).Distinct().ToList();
        var applied = new List<AppliedClose>();
        foreach (var handle in first.Concat(handles.Where(handle => !first.Contains(handle))))
        {
            var window = known[handle];
            var results = new List<AppliedClose>();
            if (byWindow.GetValueOrDefault(handle) is { } closes)
            {
                using var activity = Source.StartActivity("chrome_tabs.close_window");
                activity?.SetTag("window.profile", window.ProfileName ?? window.ProfileKey);
                activity?.SetTag("close.planned", closes.Count);
                foreach (var close in closes)
                {
                    var outcome = actions.CloseTab(close.Item.Tab);
                    results.Add(new AppliedClose(close, outcome.Closed, outcome.Detail, outcome.Elapsed));
                }

                actions.RestoreSelection([window], Activations(closes));
                activity?.SetTag("close.closed", results.Count(result => result.Closed));
            }

            applied.AddRange(results);
            afterWindow?.Invoke(window, results);
        }

        return applied;
    }

    /// <summary>
    /// Splits planned closes into those safe to run with nobody watching and those held back: every tab that is some
    /// window's active tab (what the user last looked at there) and everything in the foreground window, where closing
    /// selects each tab first and would flash under the user's hands.
    /// </summary>
    public static HeldPlan HoldUnattended(IEnumerable<IClosePlan> plans, long? foregroundHandle)
    {
        ArgumentNullException.ThrowIfNull(plans);
        var kept = new List<IPlannedTabClose>();
        var held = new List<IPlannedTabClose>();
        foreach (var close in plans.SelectMany(plan => plan.Closes))
        {
            var tab = close.Item.Tab;
            (tab.Selected || tab.WindowHandle == foregroundHandle ? held : kept).Add(close);
        }

        return new HeldPlan(kept, held);
    }
}
