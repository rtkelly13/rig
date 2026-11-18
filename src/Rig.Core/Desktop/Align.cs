using System.Globalization;
using Rig.Core.Vault;

namespace Rig.Core.Desktop;

/// <summary>What align decided to do with one plan entry.</summary>
public enum AlignOutcome
{
    /// <summary>Its window is already where the plan wants it.</summary>
    InPlace,

    /// <summary>It would move, in a dry run.</summary>
    Move,

    /// <summary>It was moved.</summary>
    Moved,

    /// <summary>No window at all matched it.</summary>
    Missing,

    /// <summary>It was left alone for a reason.</summary>
    Skipped,

    /// <summary>The move failed.</summary>
    Failed,
}

/// <summary>Why align left a window alone.</summary>
public enum SkipReason
{
    /// <summary>Minimized, which is the user's own choice for that window right now.</summary>
    Minimized,

    /// <summary>Maximized, likewise.</summary>
    Maximized,

    /// <summary>The user is working in it.</summary>
    Foreground,

    /// <summary>The desktop it belongs on is not connected.</summary>
    NoDesktop,

    /// <summary>It closed since the inventory.</summary>
    Closed,
}

/// <summary>A plan entry, the desktop it belongs on, and the live window matched to it, if any.</summary>
/// <param name="DesktopIndex">The desktop's number, from 1.</param>
/// <param name="Desktop">The desktop.</param>
/// <param name="Entry">The entry.</param>
/// <param name="Window">The window matched to it.</param>
public sealed record EntryMatch(int DesktopIndex, DesktopPlan Desktop, LaunchEntry Entry, LiveWindow? Window);

/// <summary>Live facts the alignment decision needs beyond the window inventory.</summary>
/// <param name="DesktopCount">How many virtual desktops exist.</param>
/// <param name="TargetRect">Where a placement sits in pixels on the live screens.</param>
/// <param name="ShowStates">Each window's show state, by handle; a window not in here counts as normal.</param>
/// <param name="ForegroundHwnd">The window the user is in, if any.</param>
public sealed record AlignContext(
    int DesktopCount,
    Func<PlacementSpec, Rect> TargetRect,
    IReadOnlyDictionary<long, ShowState> ShowStates,
    long? ForegroundHwnd = null);

/// <summary>What align should do with one plan entry's window.</summary>
/// <param name="DesktopIndex">The desktop's number, from 1.</param>
/// <param name="DesktopName">The desktop's name.</param>
/// <param name="Entry">The entry.</param>
/// <param name="Window">The window matched to it.</param>
/// <param name="Outcome">What align decided.</param>
public sealed record AlignDecision(int DesktopIndex, string DesktopName, LaunchEntry Entry, LiveWindow? Window, AlignOutcome Outcome)
{
    /// <summary>Whether the window has to change desktop.</summary>
    public bool MoveDesktop { get; init; }

    /// <summary>Whether the window has to change position.</summary>
    public bool MovePlacement { get; init; }

    /// <summary>Why it was skipped.</summary>
    public SkipReason? SkipReason { get; init; }

    /// <summary>A failure's explanation.</summary>
    public string Detail { get; init; } = string.Empty;

    /// <summary>The decision in one line.</summary>
    public string Describe()
    {
        var label = $"{Wire(Outcome)}: {DesktopName}/{Entry.Name}";
        var notes = new List<string>();
        if (SkipReason is { } reason)
        {
            notes.Add(Wire(reason));
        }

        if (MoveDesktop && Window is not null)
        {
            notes.Add($"desktop {Window.DesktopNumber?.ToString(CultureInfo.InvariantCulture) ?? "unknown"} -> {DesktopIndex.ToString(CultureInfo.InvariantCulture)}");
        }

        if (MovePlacement && Entry.Placement is not null)
        {
            notes.Add($"-> {Placement.FormatPlacement(Entry.Placement)}");
        }

        if (Detail.Length > 0)
        {
            notes.Add(Detail);
        }

        return notes.Count > 0 ? $"{label} ({string.Join("; ", notes)})" : label;
    }

    /// <summary>The outcome as Python names it.</summary>
    public static string Wire(AlignOutcome outcome) => outcome switch
    {
        AlignOutcome.InPlace => "in place",
        AlignOutcome.Move => "to move",
        AlignOutcome.Moved => "moved",
        AlignOutcome.Missing => "missing",
        AlignOutcome.Skipped => "skipped",
        AlignOutcome.Failed => "failed",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
    };

    /// <summary>The skip reason as Python names it.</summary>
    public static string Wire(SkipReason reason) => reason switch
    {
        Rig.Core.Desktop.SkipReason.Minimized => "minimized",
        Rig.Core.Desktop.SkipReason.Maximized => "maximized",
        Rig.Core.Desktop.SkipReason.Foreground => "foreground",
        Rig.Core.Desktop.SkipReason.NoDesktop => "desktop not present",
        Rig.Core.Desktop.SkipReason.Closed => "closed",
        _ => throw new ArgumentOutOfRangeException(nameof(reason)),
    };
}

/// <summary>What one align run decided, and (unless it was a dry run) did.</summary>
/// <param name="Decisions">One decision per enabled entry, in plan order.</param>
public sealed record AlignReport(IReadOnlyList<AlignDecision> Decisions)
{
    /// <summary>Why the run stopped before deciding anything.</summary>
    public string? Blocked { get; init; }

    /// <summary>Whether the user's desktop or foreground window was put back.</summary>
    public bool ViewRestored { get; init; }

    /// <summary>What the virtual desktop reconciliation did, if it ran.</summary>
    public DesktopReconciliationResult? DesktopReconciliation { get; init; }

    /// <summary>How many decisions ended in <paramref name="outcome"/>.</summary>
    public int Count(AlignOutcome outcome) => Decisions.Count(decision => decision.Outcome == outcome);
}

/// <summary>
/// The machine as align sees it. Every write must leave focus and the current desktop alone.
/// </summary>
public interface IAlignPorts
{
    /// <summary>The live virtual desktops' names, in position order.</summary>
    IReadOnlyList<string> DesktopNames();

    /// <summary>How many virtual desktops exist.</summary>
    int DesktopCount();

    /// <summary>The desktop showing now, from 1, or null when it cannot be told.</summary>
    int? CurrentDesktop();

    /// <summary>The window the user is in, or null.</summary>
    long? ForegroundHwnd();

    /// <summary>This process's console window, or null when it has none.</summary>
    long? ConsoleHwnd();

    /// <summary>Every user window align could move, apart from <paramref name="excluded"/>.</summary>
    IReadOnlyList<LiveWindow> Inventory(IReadOnlySet<long> excluded);

    /// <summary>How a window is shown.</summary>
    ShowState ShowState(long hwnd);

    /// <summary>Where a placement sits in pixels on the live screens.</summary>
    Rect TargetRect(PlacementSpec placement);

    /// <summary>Whether the window is still there.</summary>
    bool IsWindow(long hwnd);

    /// <summary>Move a window to a desktop; null on success, otherwise what went wrong.</summary>
    string? MoveToDesktop(long hwnd, int desktopIndex);

    /// <summary>Put a window in a rectangle; null on success, otherwise what went wrong.</summary>
    string? Place(long hwnd, Rect rect);

    /// <summary>Put the user's desktop and foreground window back after a move; whether that took.</summary>
    bool RestoreView(int? desktop, long? foreground, IReadOnlySet<long> movedHwnds);

    /// <summary>Add desktops until there are at least <paramref name="count"/>.</summary>
    void EnsureDesktopCount(int count, double settleSeconds = 0.65);

    /// <summary>Name a desktop; whether that took.</summary>
    bool RenameDesktop(int desktopIndex, string name);

    /// <summary>Remove switched-off desktops, moving their windows; the names removed.</summary>
    IReadOnlyList<string> RetireDesktops(int keepCount, IReadOnlyList<string> retiredNames);
}

/// <summary>
/// Put already-open plan windows back on their planned desktop and slot, without launching anything. The placement-only
/// sibling of restore: it reuses restore's window matching, decides per entry whether the matched window is in place,
/// needs moving or must be left alone, and only then touches the machine. Ported from <c>desktop_manager.align</c>.
/// </summary>
public static class Align
{
    /// <summary>The tolerance restore uses to call a window "already placed".</summary>
    public const double PlacementSimilarity = 0.92;

    /// <summary>A title (150+) or Chrome URL hint (170+) match; a process-name match plus the entry name only reaches 125.</summary>
    public const int StrongIdentityScore = 150;

    /// <summary>
    /// Match every enabled entry to at most one live window, in two passes. The first runs restore's own matching,
    /// desktop by desktop, over windows already on that desktop (or whose desktop is unknown), so a window sitting on
    /// its own desktop is never pulled away for another. The second lets a still-unmatched entry claim a window from
    /// another desktop, but only on strong identity, never on process name alone.
    /// </summary>
    public static IReadOnlyList<EntryMatch> MatchPlanWindows(
        WorkspacePlan plan,
        IReadOnlyList<LiveWindow> windows,
        IReadOnlyDictionary<string, string> substitutions)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(windows);
        ArgumentNullException.ThrowIfNull(substitutions);

        var remaining = windows.ToList();
        var slots = new List<EntryMatch>();
        for (var desktopIndex = 1; desktopIndex <= plan.Desktops.Count; desktopIndex++)
        {
            var desktop = plan.Desktops[desktopIndex - 1];
            foreach (var entry in desktop.Launch)
            {
                if (entry.Disabled)
                {
                    continue;
                }

                var claim = WindowReuse.ClaimReusableWindow(
                    entry,
                    remaining,
                    substitutions,
                    desktopIndex,
                    WindowReuse.CompetingSiblingTitles(desktop.Launch, entry));
                remaining = claim.Remaining.ToList();
                slots.Add(new(desktopIndex, desktop, entry, claim.Window));
            }
        }

        for (var position = 0; position < slots.Count; position++)
        {
            var slot = slots[position];
            if (slot.Window is not null)
            {
                continue;
            }

            var competing = WindowReuse.CompetingSiblingTitles(slot.Desktop.Launch, slot.Entry);
            LiveWindow? best = null;
            var bestScore = 0;
            foreach (var window in remaining)
            {
                if (!HasStrongIdentity(slot.Entry, window, substitutions, competing))
                {
                    continue;
                }

                var score = WindowReuse.ReusableWindowMatchScore(slot.Entry, window, substitutions, competingTitles: competing);
                if (score > bestScore)
                {
                    (best, bestScore) = (window, score);
                }
            }

            if (best is not null)
            {
                remaining.RemoveAll(window => window.Handle == best.Handle);
                slots[position] = slot with { Window = best };
            }
        }

        return slots;
    }

    /// <summary>Whether the window is already in the placement's slot, by inferred placement or by position.</summary>
    public static bool PlacementInPlace(LiveWindow window, PlacementSpec placement, Func<PlacementSpec, Rect> targetRect)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(placement);
        ArgumentNullException.ThrowIfNull(targetRect);

        var inferred = window.InferredPlacement;
        if (inferred is not null && WindowReuse.SamePlacement(inferred, placement))
        {
            return true;
        }

        // Two layouts can name the same slot, such as a full screen subdivided into one half.
        if (inferred is not null
            && inferred.Screen == placement.Screen
            && Geometry.NormalizedPlacementRect(inferred) == Geometry.NormalizedPlacementRect(placement))
        {
            return true;
        }

        return window.Bounds is { } bounds && Geometry.RectSimilarity(bounds, targetRect(placement)) >= PlacementSimilarity;
    }

    /// <summary>What align should do with one plan entry's window.</summary>
    public static AlignDecision Decide(EntryMatch match, AlignContext context)
    {
        ArgumentNullException.ThrowIfNull(match);
        ArgumentNullException.ThrowIfNull(context);
        var baseDecision = new AlignDecision(match.DesktopIndex, match.Desktop.Name, match.Entry, match.Window, AlignOutcome.Missing);
        if (match.Window is not { } window)
        {
            return baseDecision;
        }

        // An unknown desktop (a pinned window, say) is left where it is.
        var moveDesktop = window.DesktopNumber is { } current && current != match.DesktopIndex;
        var placement = match.Entry.Placement;
        var movePlacement = placement is not null && !PlacementInPlace(window, placement, context.TargetRect);
        if (!moveDesktop && !movePlacement)
        {
            return baseDecision with { Outcome = AlignOutcome.InPlace };
        }

        var moving = baseDecision with { MoveDesktop = moveDesktop, MovePlacement = movePlacement };
        var skip = SkipReasonFor(window, match.DesktopIndex, moveDesktop, context);
        return skip is { } reason
            ? moving with { Outcome = AlignOutcome.Skipped, SkipReason = reason }
            : moving with { Outcome = AlignOutcome.Move };
    }

    /// <summary>
    /// Decide every enabled entry, optionally keeping only one desktop's decisions. Matching always covers the whole
    /// plan, so a one-desktop run still cannot steal a window another desktop's entry owns.
    /// </summary>
    public static IReadOnlyList<AlignDecision> DecideAlignment(
        WorkspacePlan plan,
        IReadOnlyList<LiveWindow> windows,
        AlignContext context,
        IReadOnlyDictionary<string, string> substitutions,
        int? targetDesktopIndex = null) =>
        [.. MatchPlanWindows(plan, windows, substitutions)
            .Where(match => targetDesktopIndex is null || match.DesktopIndex == targetDesktopIndex)
            .Select(match => Decide(match, context))];

    /// <summary>
    /// Why the live desktops cannot be trusted to line up with the plan, or null. Desktops are addressed by position,
    /// so a leftover or user-inserted one shifts every later one. An unnamed live desktop is not a mismatch: Windows
    /// drops names, and restore puts them back.
    /// </summary>
    public static string? DesktopNameMismatch(WorkspacePlan plan, IReadOnlyList<string> liveNames)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(liveNames);
        for (var index = 1; index <= Math.Min(plan.Desktops.Count, liveNames.Count); index++)
        {
            var liveName = liveNames[index - 1];
            var desktop = plan.Desktops[index - 1];
            if (liveName.Length > 0 && UnicodeText.CaseFold(liveName) != UnicodeText.CaseFold(desktop.Name))
            {
                return $"desktop {index} is named '{liveName}' but the plan expects '{desktop.Name}'";
            }
        }

        return null;
    }

    /// <summary>The one summary line the command prints.</summary>
    public static string SummaryLine(AlignReport report, bool dryRun)
    {
        ArgumentNullException.ThrowIfNull(report);
        var prefix = dryRun ? "align (dry run)" : "align";
        if (report.Blocked is { } blocked)
        {
            return $"{prefix}: skipped ({blocked})";
        }

        var parts = new List<string>();
        if (report.DesktopReconciliation is { } reconciliation)
        {
            var actions = new List<string>();
            if (dryRun)
            {
                if (reconciliation.Plan.ToRetire.Count > 0)
                {
                    actions.Add($"{reconciliation.Plan.ToRetire.Count} to retire");
                }

                if (reconciliation.Plan.ToCreate.Count > 0)
                {
                    actions.Add($"{reconciliation.Plan.ToCreate.Count} to create");
                }

                if (reconciliation.Plan.ToRename.Count > 0)
                {
                    actions.Add($"{reconciliation.Plan.ToRename.Count} to rename");
                }
            }
            else
            {
                if (reconciliation.Retired.Count > 0)
                {
                    actions.Add($"{reconciliation.Retired.Count} retired");
                }

                if (reconciliation.Created.Count > 0)
                {
                    actions.Add($"{reconciliation.Created.Count} created");
                }

                if (reconciliation.Renamed.Count > 0)
                {
                    actions.Add($"{reconciliation.Renamed.Count} renamed");
                }
            }

            if (actions.Count > 0)
            {
                parts.Add($"desktops: {string.Join(", ", actions)}");
            }
        }

        var moved = dryRun ? report.Count(AlignOutcome.Move) : report.Count(AlignOutcome.Moved);
        parts.Add($"{moved} {(dryRun ? "to move" : "moved")}");
        parts.Add($"{report.Count(AlignOutcome.InPlace)} in place");
        var missing = report.Count(AlignOutcome.Missing);
        if (missing > 0)
        {
            parts.Add($"{missing} missing");
        }

        var skipped = report.Decisions.Where(decision => decision.SkipReason is not null).Select(decision => decision.SkipReason!.Value).ToList();
        if (skipped.Count > 0)
        {
            var reasons = skipped.Select(WireSkip).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
            parts.Add(reasons.Count == 1
                ? $"{skipped.Count} skipped ({reasons[0]})"
                : $"{skipped.Count} skipped ({string.Join(", ", reasons.Select(reason => $"{skipped.Count(item => WireSkip(item) == reason)} {reason}"))})");
        }

        var failed = report.Count(AlignOutcome.Failed);
        if (failed > 0)
        {
            parts.Add($"{failed} failed");
        }

        return $"{prefix}: {string.Join(", ", parts)}";
    }

    /// <summary>
    /// Decide, then (unless <paramref name="dryRun"/>) move the out-of-place windows. With
    /// <paramref name="reconcileDesktops"/> the virtual desktops are brought to the declared state instead of blocking
    /// when names drift.
    /// </summary>
    /// <exception cref="ArgumentException">No desktop has the name <paramref name="targetDesktop"/> asks for.</exception>
    public static AlignReport Run(
        WorkspacePlan plan,
        IAlignPorts ports,
        IReadOnlyDictionary<string, string> substitutions,
        string? targetDesktop = null,
        bool dryRun = false,
        bool reconcileDesktops = false,
        Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(ports);
        ArgumentNullException.ThrowIfNull(substitutions);
        log ??= _ => { };

        var targetIndex = RestoreExecutionPlan.ResolveTargetDesktopIndex(plan, targetDesktop);
        DesktopReconciliationResult? desktopResult = null;
        if (reconcileDesktops)
        {
            var targetNames = plan.Desktops.Select(desktop => desktop.Name).ToList();
            var desktopPlan = DesktopReconcile.Plan(targetNames, plan.RetiredDesktopNames, ports.DesktopNames());
            if (desktopPlan.HasDrift)
            {
                if (dryRun)
                {
                    desktopResult = new(desktopPlan)
                    {
                        Unchanged = desktopPlan.Unchanged,
                        Unmanaged = desktopPlan.Unmanaged,
                        DryRun = true,
                    };
                    log(
                        $"desktop drift (dry run): {desktopPlan.ToCreate.Count} to create, "
                        + $"{desktopPlan.ToRename.Count} to rename, {desktopPlan.ToRetire.Count} to retire");
                }
                else
                {
                    var retired = plan.RetiredDesktopNames.Count > 0
                        ? ports.RetireDesktops(targetNames.Count, plan.RetiredDesktopNames)
                        : [];
                    if (desktopPlan.ToCreate.Count > 0)
                    {
                        ports.EnsureDesktopCount(targetNames.Count);
                    }

                    var renamed = new List<DesktopRenameAction>();
                    var failed = new List<DesktopRenameAction>();
                    foreach (var action in desktopPlan.ToRename)
                    {
                        var ok = ports.RenameDesktop(action.Index, action.TargetName);
                        var result = new DesktopRenameAction(action.Index, action.CurrentName, action.TargetName, ok, ok ? null : "Rename failed");
                        renamed.Add(result);
                        if (!ok)
                        {
                            failed.Add(result);
                        }
                    }

                    desktopResult = new(desktopPlan)
                    {
                        Retired = retired,
                        Created = desktopPlan.ToCreate,
                        Renamed = renamed,
                        FailedRenames = failed,
                        Unchanged = desktopPlan.Unchanged,
                        Unmanaged = desktopPlan.Unmanaged,
                    };
                    log(
                        $"reconciled virtual desktops: {desktopPlan.ToCreate.Count} created, "
                        + $"{renamed.Count} renamed, {retired.Count} retired");
                }
            }
        }
        else if (DesktopNameMismatch(plan, ports.DesktopNames()) is { } mismatch)
        {
            return new AlignReport([]) { Blocked = $"{mismatch}; run workspace restore" };
        }

        var console = ports.ConsoleHwnd();
        var excluded = console is { } handle ? new HashSet<long> { handle } : [];
        var windows = ports.Inventory(excluded);
        var foreground = ports.ForegroundHwnd();
        var context = new AlignContext(
            ports.DesktopCount(),
            ports.TargetRect,
            windows.ToDictionary(window => window.Handle, window => ports.ShowState(window.Handle)),
            foreground);
        var decisions = DecideAlignment(plan, windows, context, substitutions, targetIndex);
        if (dryRun || !decisions.Any(decision => decision.Outcome is AlignOutcome.Move))
        {
            return new AlignReport(decisions) { DesktopReconciliation = desktopResult };
        }

        var startingDesktop = ports.CurrentDesktop();
        var applied = decisions
            .Select(decision => decision.Outcome is AlignOutcome.Move ? Apply(decision, ports, log) : decision)
            .ToList();
        var movedHandles = applied
            .Where(decision => decision.Outcome is AlignOutcome.Moved && decision.Window is not null)
            .Select(decision => decision.Window!.Handle)
            .ToHashSet();
        var viewRestored = movedHandles.Count > 0 && ports.RestoreView(startingDesktop, foreground, movedHandles);
        if (viewRestored)
        {
            log("Put the user's desktop or foreground window back after moving windows");
        }

        return new AlignReport(applied) { ViewRestored = viewRestored, DesktopReconciliation = desktopResult };
    }

    private static bool HasStrongIdentity(
        LaunchEntry entry,
        LiveWindow window,
        IReadOnlyDictionary<string, string> substitutions,
        IReadOnlySet<string> competing)
    {
        var score = WindowReuse.WindowIdentityMatchScore(entry, window.Title, window.ProcessName, substitutions, competing);
        if (score < StrongIdentityScore)
        {
            return false;
        }

        if (entry.Type != LaunchEntry.Terminal)
        {
            return true;
        }

        // A terminal's command hint ("claude" in the title) fits every agent terminal on the machine; only its
        // configured window title singles one out.
        var ownTitle = UnicodeText.CaseFold(UnicodeText.Strip(entry.WindowTitle ?? string.Empty));
        return ownTitle.Length > 0 && UnicodeText.CaseFold(window.Title).Contains(ownTitle, StringComparison.Ordinal);
    }

    private static SkipReason? SkipReasonFor(LiveWindow window, int desktopIndex, bool moveDesktop, AlignContext context)
    {
        var showState = context.ShowStates.GetValueOrDefault(window.Handle, ShowState.Normal);
        if (showState is ShowState.Minimized)
        {
            return SkipReason.Minimized;
        }

        if (showState is ShowState.Maximized)
        {
            return SkipReason.Maximized;
        }

        if (context.ForegroundHwnd is { } foreground && window.Handle == foreground)
        {
            return SkipReason.Foreground;
        }

        return moveDesktop && desktopIndex > context.DesktopCount ? SkipReason.NoDesktop : null;
    }

    private static AlignDecision Apply(AlignDecision decision, IAlignPorts ports, Action<string> log)
    {
        if (decision.Window is not { } window)
        {
            return decision;
        }

        var hwnd = window.Handle;

        // The user may have touched the window since the inventory; check again right before moving.
        if (!ports.IsWindow(hwnd))
        {
            return decision with { Outcome = AlignOutcome.Skipped, SkipReason = SkipReason.Closed };
        }

        if (ports.ForegroundHwnd() == hwnd)
        {
            return decision with { Outcome = AlignOutcome.Skipped, SkipReason = SkipReason.Foreground };
        }

        var showState = ports.ShowState(hwnd);
        if (showState is not ShowState.Normal)
        {
            return decision with
            {
                Outcome = AlignOutcome.Skipped,
                SkipReason = showState is ShowState.Minimized ? SkipReason.Minimized : SkipReason.Maximized,
            };
        }

        if (decision.MoveDesktop && ports.MoveToDesktop(hwnd, decision.DesktopIndex) is { } desktopError)
        {
            log($"FAILED desktop move {decision.DesktopName}/{decision.Entry.Name} [{hwnd}]: {desktopError}");
            return decision with { Outcome = AlignOutcome.Failed, Detail = desktopError };
        }

        if (decision.MovePlacement && decision.Entry.Placement is { } placement)
        {
            if (ports.Place(hwnd, ports.TargetRect(placement)) is { } placementError)
            {
                log($"FAILED placement {decision.DesktopName}/{decision.Entry.Name} [{hwnd}]: {placementError}");
                return decision with { Outcome = AlignOutcome.Failed, Detail = placementError };
            }
        }

        return decision with { Outcome = AlignOutcome.Moved };
    }

    private static string WireSkip(SkipReason reason) => AlignDecision.Wire(reason);
}
