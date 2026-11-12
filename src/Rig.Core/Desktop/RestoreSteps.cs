using System.Diagnostics;
using System.Globalization;
using Rig.Core.Vault;

namespace Rig.Core.Desktop;

/// <summary>What one restore run carries into every desktop's steps.</summary>
/// <param name="Plan">The plan being restored.</param>
/// <param name="Port">The machine.</param>
/// <param name="Log">Where the run is recorded.</param>
/// <param name="Substitutions">The plan's placeholders, resolved by the caller.</param>
/// <param name="Preserved">Windows driving the restore, never reused or closed.</param>
/// <param name="Fallbacks">Missing paths the run hit, for the fallback notes.</param>
public sealed record RestoreRun(
    WorkspacePlan Plan,
    IDesktopPort Port,
    IRunLog Log,
    IReadOnlyDictionary<string, string> Substitutions,
    IReadOnlySet<long> Preserved,
    RestoreFailureFallbacks Fallbacks)
{
    /// <summary>How long a desktop switch settles.</summary>
    public TimeSpan SwitchSettle => TimeSpan.FromMilliseconds(Plan.SwitchDelayMs);

    /// <summary>The run with every log line dropped, as a dry run plans.</summary>
    public RestoreRun Quiet() => this with { Log = RunLog.Discard };
}

/// <summary>
/// One desktop's restore: reuse or launch each entry, retry the desktop, verify it. Ported from
/// <c>desktop_manager.handlers.restore_steps</c>. Only a <see cref="DesktopException"/> fails an entry; anything else
/// is a bug and ends the run.
/// </summary>
public static class RestoreSteps
{
    /// <summary>How many times a desktop is attempted before restore moves on.</summary>
    public const int MaxDesktopAttempts = 3;

    /// <summary>The <c>scope</c> tag on restore's activities.</summary>
    public const string Scope = "desktop_manager.restore";

    private static readonly TimeSpan AnchorSettleCeiling = TimeSpan.FromSeconds(0.2);
    private static readonly TimeSpan TerminalReadyTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Plan <paramref name="desktop"/>'s steps. Without <paramref name="diffExisting"/>, or when the desktop is closed
    /// first, every enabled entry launches. Otherwise each entry claims the best open window, and the windows no entry
    /// claims are its orphans. Entries already confirmed in place stay satisfied, and still claim their window so a
    /// later entry cannot take it.
    /// </summary>
    public static void RefreshPlan(RestoreRun run, PlannedDesktopExecution desktop, bool diffExisting)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(desktop);
        desktop.OrphanWindows = [];
        var entries = desktop.Desktop.Launch.Where(entry => !entry.Disabled).ToList();
        if (!diffExisting || desktop.CloseExisting)
        {
            desktop.EntrySteps = [.. entries.Select(entry => Step(run, desktop, entry, desktop.SatisfiedEntryNames.Contains(entry.Name) ? EntryAction.Satisfied : EntryAction.Launch))];
            return;
        }

        // The invoking window (the session driving the restore) matches a terminal entry on process and place; reusing
        // or closing it would end that session, so it never enters the inventory.
        var reusable = run.Port.InventoryReusableWindows(run.Preserved);

        // A window an entry pins by recorded pid is that entry's own instance even on another desktop: move it, don't relaunch.
        var pinned = entries.Where(entry => !string.IsNullOrEmpty(entry.WindowPidFile)).ToList();
        var remaining = reusable
            .Where(window => window.DesktopNumber == desktop.DesktopIndex
                || window.DesktopNumber is null
                || pinned.Exists(entry => WindowPidFile.PidVerified(entry, window.Pid, run.Substitutions)))
            .ToList();
        run.Log.Log($"Desktop {desktop.DesktopIndex} reusable inventory refresh collected {remaining.Count} candidate window(s)");

        IReadOnlyList<LiveWindow> unclaimed = remaining;
        var steps = new List<PlannedEntryExecution>();
        foreach (var entry in entries)
        {
            var claim = WindowReuse.ClaimReusableWindow(
                entry,
                unclaimed,
                run.Substitutions,
                desktop.DesktopIndex,
                WindowReuse.CompetingSiblingTitles(desktop.Desktop.Launch, entry),
                run.Port.PlacementRect);
            unclaimed = claim.Remaining;
            steps.Add(desktop.SatisfiedEntryNames.Contains(entry.Name)
                ? Step(run, desktop, entry, EntryAction.Satisfied)
                : Step(run, desktop, entry, claim.Window is null ? EntryAction.Launch : EntryAction.Reuse, claim.Window));
        }

        desktop.EntrySteps = steps;
        desktop.OrphanWindows =
        [
            .. unclaimed
                .OrderBy(window => (window.ProcessName ?? string.Empty).ToLowerInvariant(), StringComparer.Ordinal)
                .ThenBy(window => window.Title.ToLowerInvariant(), StringComparer.Ordinal)
                .ThenBy(window => window.Handle),
        ];
        if (desktop.OrphanWindows.Count > 0)
        {
            run.Log.Log($"Desktop {desktop.DesktopIndex} orphan open windows: {string.Join(", ", desktop.OrphanWindows.Select(window => window.Title))}");
        }
    }

    /// <summary>
    /// Run <paramref name="desktop"/>'s steps until it matches its plan, up to <see cref="MaxDesktopAttempts"/> times.
    /// A retry replans against what is open now and leaves alone what is already in place. The entries still waiting
    /// on the second pass; none when the desktop completed.
    /// </summary>
    public static IReadOnlyList<LaunchEntry> ExecuteWithRetries(RestoreRun run, PlannedDesktopExecution desktop)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(desktop);
        IReadOnlyList<LaunchEntry> pending = [];
        for (var attempt = 1; attempt <= MaxDesktopAttempts; attempt++)
        {
            if (attempt > 1)
            {
                desktop.CloseExisting = false;
                RefreshPlan(run, desktop, diffExisting: true);
                run.Log.Log($"Retrying desktop {desktop.DesktopIndex}: attempt {attempt} of {MaxDesktopAttempts}");
                if (desktop.SatisfiedEntryNames.Count > 0)
                {
                    run.Log.Log($"Leaving already-placed entries alone: {PythonList(desktop.SatisfiedEntryNames.Order(StringComparer.Ordinal).Select(PyText.Repr))}");
                }
            }

            DesktopCompletion completion;
            using (var span = Span("restore.desktop_attempt", ("desktop.index", desktop.DesktopIndex), ("restore.attempt", attempt)))
            {
                pending = ExecuteDesktop(run, desktop);
                using (Span("restore.verify_desktop", ("desktop.index", desktop.DesktopIndex)))
                {
                    completion = Verify(run, desktop);
                }

                span?.SetTag("desktop.complete", completion.IsComplete);
            }

            // Whatever is already in its slot is not relaunched or rematched on the next attempt.
            desktop.SatisfiedEntryNames.Clear();
            desktop.SatisfiedEntryNames.UnionWith(completion.SatisfiedEntryNames);
            if (completion.IsComplete)
            {
                run.Log.Log($"Desktop {desktop.DesktopIndex} completion check passed on attempt {attempt}");
                RaisePlannedWindows(run, desktop);
                return [];
            }

            run.Log.Log($"Desktop {desktop.DesktopIndex} completion check failed on attempt {attempt}");
            foreach (var line in completion.Diff.Split('\n').Skip(1))
            {
                run.Log.Log($"VERIFY {line}");
            }
        }

        run.Log.Log($"Desktop {desktop.DesktopIndex} incomplete after {MaxDesktopAttempts} attempt(s); continuing");
        RaisePlannedWindows(run, desktop);
        return pending;
    }

    /// <summary>
    /// The windows on <paramref name="sibling"/>'s desktop that plainly belong to another entry of the same process,
    /// so an adopting retry cannot take them. A sibling's window is known by its explicit title, so only siblings that
    /// declare one reserve anything.
    /// </summary>
    public static IReadOnlySet<long> SiblingOwnedHandles(RestoreRun run, PlannedDesktopExecution sibling, LaunchEntry entry)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(sibling);
        ArgumentNullException.ThrowIfNull(entry);
        var none = new HashSet<long>();
        if (string.IsNullOrEmpty(entry.WindowProcessName))
        {
            return none;
        }

        var process = PyText.CaseFold(entry.WindowProcessName);
        var titles = sibling.Desktop.Launch
            .Where(other => other.Name != entry.Name
                && !other.Disabled
                && !string.IsNullOrEmpty(other.WindowTitle)
                && !string.IsNullOrEmpty(other.WindowProcessName)
                && PyText.CaseFold(other.WindowProcessName) == process)
            .Select(other => PyText.CaseFold(other.WindowTitle!.Trim()))
            .ToHashSet(StringComparer.Ordinal);
        titles.Remove(PyText.CaseFold((entry.WindowTitle ?? string.Empty).Trim()));
        if (titles.Count == 0)
        {
            return none;
        }

        IReadOnlyDictionary<long, string> live;
        try
        {
            live = run.Port.WindowTitlesForProcess(entry.WindowProcessName);
        }
        catch (DesktopException)
        {
            return none;
        }

        var reserved = live
            .Where(pair => titles.Any(title => PyText.CaseFold(pair.Value).Contains(title, StringComparison.Ordinal)))
            .Select(pair => pair.Key)
            .ToHashSet();
        if (reserved.Count > 0)
        {
            run.Log.Log($"Reserved sibling-owned window(s) {Handles(reserved)} against adoption by {entry.Name}");
        }

        return reserved;
    }

    private static PlannedEntryExecution Step(RestoreRun run, PlannedDesktopExecution desktop, LaunchEntry entry, EntryAction action, LiveWindow? window = null) =>
        new(desktop.Desktop, desktop.DesktopIndex, entry, WindowReuse.DescribeLaunch(entry, run.Substitutions), action, window);

    private static List<LaunchEntry> ExecuteDesktop(RestoreRun run, PlannedDesktopExecution desktop)
    {
        var pending = new List<LaunchEntry>();
        run.Log.Log($"Processing desktop {desktop.DesktopIndex}: {desktop.Desktop.Name}");
        if (desktop.CloseExisting)
        {
            var closed = run.Port.CloseCurrentDesktopWindows(run.Preserved);
            run.Log.Log($"Requested close for {closed} existing window(s) on {desktop.Desktop.Name}");
            run.Port.Sleep(TimeSpan.FromSeconds(0.5));
        }

        foreach (var step in desktop.EntrySteps)
        {
            if (step.Action == EntryAction.Satisfied)
            {
                run.Log.Log($"Skipping {step.Entry.Name}: already verified in its planned slot");
                continue;
            }

            var confirmed = run.Port.EnsureCurrentDesktop(step.DesktopIndex, TimeSpan.FromSeconds(0.1));
            run.Log.Log($"Desktop {step.DesktopIndex} pre-entry check for {step.Entry.Name}: {Anchored(confirmed)}");
            using var span = Span(
                $"restore.entry.{(step.Action == EntryAction.Reuse ? "reuse" : "launch")}",
                ("desktop.index", step.DesktopIndex),
                ("entry.name", step.Entry.Name),
                ("entry.type", step.Entry.Type),
                ("entry.process", step.Entry.WindowProcessName ?? string.Empty));
            List<LaunchEntry> entryPending;
            if (step is { Action: EntryAction.Reuse, ReusableWindow: { } window })
            {
                span?.SetTag("window.hwnd", window.Handle);
                entryPending = Reuse(run, step, window);
            }
            else
            {
                entryPending = Launch(
                    run,
                    step,
                    recordMissingPath: true,
                    alreadyLaunched: desktop.LaunchedEntryNames.Contains(step.Entry.Name),
                    reserved: SiblingOwnedHandles(run, desktop, step.Entry));
                desktop.LaunchedEntryNames.Add(step.Entry.Name);
            }

            span?.SetTag("entry.deferred_second_pass", entryPending.Count > 0);
            pending.AddRange(entryPending);
        }

        run.Log.Log(pending.Count > 0
            ? $"Desktop {desktop.DesktopIndex} first pass incomplete; {pending.Count} entry(s) queued for second pass"
            : $"Desktop {desktop.DesktopIndex} first pass fully successful");
        return pending;
    }

    private static List<LaunchEntry> Reuse(RestoreRun run, PlannedEntryExecution step, LiveWindow window)
    {
        var entry = step.Entry;
        run.Log.Log(
            $"Reusing window {window.Handle} for {entry.Name} pid={PidOrUnknown(window.Pid)} title={PyText.Repr(window.Title)} "
            + $"process={ReprOrNone(window.ProcessName)} desktop={NumberOrNone(window.DesktopNumber)}");
        try
        {
            var move = run.Port.MoveExistingWindowToEntry(window, step.DesktopIndex, entry);
            if (move.MovedToDesktop)
            {
                run.Log.Log($"Moved reused window {window.Handle} for {entry.Name} from desktop {PidOrUnknown(window.DesktopNumber)} to {step.DesktopIndex}");
            }

            if (move.MovedToPlacement && entry.Placement is { } moved)
            {
                run.Log.Log($"Repositioned reused window {window.Handle} for {entry.Name} to {moved.ToJson().ToJsonString()}");
            }

            if (entry.Placement is null || entry.Type == LaunchEntry.Terminal)
            {
                return [];
            }

            if (run.Port.IsWindowTargetConfirmed(window.Handle, step.DesktopIndex, entry))
            {
                run.Log.Log($"First-pass placement verified for {entry.Name}");
                return [];
            }

            run.Log.Log($"Deferred second-pass placement for {entry.Name}");
            return [entry];
        }
        catch (DesktopException failure)
        {
            run.Log.LogFailure($"FAILED reuse {entry.Name}: {failure.Message}; falling back to relaunch", failure);
            return Launch(run, step with { Action = EntryAction.Launch, ReusableWindow = null }, recordMissingPath: false, alreadyLaunched: false, reserved: null);
        }
    }

    private static List<LaunchEntry> Launch(RestoreRun run, PlannedEntryExecution step, bool recordMissingPath, bool alreadyLaunched, IReadOnlySet<long>? reserved)
    {
        var entry = step.Entry;
        run.Log.Log(step.Description);

        // A retry means the last attempt's window never showed in time. If the instance it started is still alive, wait
        // for that one: three attempts against a slow Fork once spawned three processes and filled no slot.
        var adopt = alreadyLaunched && ProcessIsRunning(run.Port, entry);
        if (adopt)
        {
            run.Log.Log($"Not relaunching {entry.Name}: the instance launched earlier this run is still starting; waiting for its window instead");
        }

        // Only exclude the process's open windows when about to start a new one; an adopted window is one of them.
        var excluded = new HashSet<long>(reserved ?? new HashSet<long>());
        var token = StepTimer.ShouldForceTerminalRelaunch(entry) ? StepTimer.NewTerminalRestoreToken(entry) : null;
        if (!string.IsNullOrEmpty(entry.WindowProcessName) && !adopt)
        {
            excluded.UnionWith(run.Port.WindowsMatchingProcess(entry.WindowProcessName));
            if (excluded.Count > 0)
            {
                run.Log.Log($"Snapshot existing process windows for {entry.Name}: {Handles(excluded)}");
            }
        }

        if (!adopt)
        {
            try
            {
                if (run.Port.Launch(entry, run.Substitutions, token) is { } pid)
                {
                    run.Log.Log($"Launched {entry.Name} pid={pid}");
                }

                if (token is not null)
                {
                    run.Log.Log($"Launched {entry.Name} with restore token {token}");
                }
            }
            catch (DesktopException failure)
            {
                run.Log.LogFailure($"FAILED {entry.Name}: {failure.Message}", failure);
                if (recordMissingPath && RestoreFailureFallbacks.IsMissingPathError(failure.Message))
                {
                    run.Fallbacks.RecordMissingPath(step.DesktopIndex, entry, failure.Message);
                }

                return [];
            }
        }

        long? ready;
        try
        {
            ready = WaitUntilReady(run, entry, step.DesktopIndex, excluded);
        }
        catch (DesktopException failure)
        {
            run.Log.LogFailure($"FAILED readiness {entry.Name}: {failure.Message}", failure);
            return [];
        }

        if (entry.Placement is not { } placement)
        {
            return [];
        }

        try
        {
            var started = run.Port.Now;
            var outcome = run.Port.ApplyWindowPlacement(entry, new(StepTimer.EntryWindowWaitTimeout(entry), step.DesktopIndex, excluded), run.Log, ready);
            if (outcome.MovedToTargetDesktop)
            {
                run.Log.Log($"Moved launched window for {entry.Name} onto desktop {step.DesktopIndex} before placement");
            }

            run.Log.Log($"Applied placement {placement.ToJson().ToJsonString()} to {entry.Name} after {Seconds(run.Port.Now - started, "F2")}s");
            EnsureAnchor(run, step.DesktopIndex, entry.Name, "post-entry status");
            LogPostEntryState(run, entry, step.Desktop);
            if (entry.Type == LaunchEntry.Terminal)
            {
                return [];
            }

            if (run.Port.IsEntryTargetConfirmed(entry, step.DesktopIndex, TimeSpan.FromSeconds(0.35)))
            {
                run.Log.Log($"First-pass placement verified for {entry.Name}");
                return [];
            }

            run.Log.Log($"Deferred second-pass placement for {entry.Name}");
            return [entry];
        }
        catch (DesktopException failure)
        {
            run.Log.LogFailure($"FAILED placement {entry.Name}: {failure.Message}", failure);
            return [];
        }
    }

    // A Windows Terminal PowerShell entry is ready when its window shows and, if it runs a command, retitles itself.
    // Anything else gets its configured delay.
    private static long? WaitUntilReady(RestoreRun run, LaunchEntry entry, int desktopIndex, IReadOnlySet<long> excluded)
    {
        var started = run.Port.Now;
        if (!StepTimer.ShouldForceTerminalRelaunch(entry))
        {
            if (entry.DelayMs > 0)
            {
                run.Port.Sleep(TimeSpan.FromMilliseconds(entry.DelayMs));
            }

            return null;
        }

        run.Log.Log($"Waiting for launch readiness for {entry.Name} on desktop {desktopIndex} excluded_hwnds={Handles(excluded)}");
        var handle = run.Port.WaitForWindowForEntry(entry, new(TerminalReadyTimeout, desktopIndex, excluded, AllowProcessFallbackForTerminals: true), run.Log).Handle
            ?? throw new DesktopException($"Could not detect launched window for {entry.Name}");
        run.Log.Log($"Detected launched window {handle} for {entry.Name} after {Seconds(run.Port.Now - started, "F2")}s");

        // A bare shell (command deferred at logon) runs nothing that would retitle the window.
        if (!string.IsNullOrEmpty(entry.WindowTitle) && !string.IsNullOrEmpty(entry.Command))
        {
            var title = run.Port.WaitForWindowTitleChange(handle, entry.WindowTitle, StepTimer.EntryTitleTakeoverTimeout(entry), run.Log)
                ?? throw new DesktopException($"Timed out waiting for UI title takeover for {entry.Name}");
            run.Log.Log($"Confirmed UI title takeover for {entry.Name}: {PyText.Repr(title)}");
        }

        return handle;
    }

    private static DesktopCompletion Verify(RestoreRun run, PlannedDesktopExecution desktop)
    {
        EnsureAnchor(run, desktop.DesktopIndex, desktop.Desktop.Name, "desktop completion check");
        var actual = run.Port.CurrentDesktopStatus(desktop.Desktop.Name);
        var diff = WorkspaceDiff.Of(new WorkspacePlan { Desktops = [desktop.Desktop] }, new WorkspacePlan { Desktops = [actual] }, run.Substitutions);

        // Only missing or misplaced entries are fixable by another attempt. Extra and borrowed windows (Docker Desktop,
        // a stray browser tab, an already-open Slack) never resolve, so they must not burn every attempt.
        return new(!diff.HasFixableDrift, diff.Render(), WorkspaceDiff.EntriesMatchedOnPlannedScreen(desktop.Desktop, actual, run.Substitutions));
    }

    private static void LogPostEntryState(RestoreRun run, LaunchEntry entry, DesktopPlan desktop)
    {
        var actual = run.Port.CurrentDesktopStatus(desktop.Name);
        run.Log.Log($"Post-entry status for {entry.Name}: {actual.CapturedWindows.Count} visible window(s)");
        var diff = WorkspaceDiff.Of(new WorkspacePlan { Desktops = [desktop] }, new WorkspacePlan { Desktops = [actual] }, run.Substitutions);
        foreach (var line in diff.Render().Split('\n').Skip(1))
        {
            run.Log.Log($"STATE {line}");
        }
    }

    // Auto-started apps and auth popups can cover the tiled layout after placement, so the desktop's last act is to lift
    // every window of a planned entry's process.
    private static void RaisePlannedWindows(RestoreRun run, PlannedDesktopExecution desktop)
    {
        var processes = desktop.Desktop.Launch
            .Where(entry => !entry.Disabled && !string.IsNullOrEmpty(entry.WindowProcessName))
            .Select(entry => entry.WindowProcessName!)
            .ToHashSet(StringComparer.Ordinal);
        if (processes.Count == 0)
        {
            return;
        }

        var raised = run.Port.BringProcessWindowsToFront(processes);
        run.Log.Log($"Desktop {desktop.DesktopIndex} raised {raised} planned window(s) above orphan windows");
    }

    private static void EnsureAnchor(RestoreRun run, int desktopIndex, string name, string reason)
    {
        var current = run.Port.CurrentDesktopNumber();
        if (current == desktopIndex)
        {
            return;
        }

        var settle = run.SwitchSettle < AnchorSettleCeiling ? run.SwitchSettle : AnchorSettleCeiling;
        var confirmed = run.Port.EnsureCurrentDesktop(desktopIndex, settle);
        if (current is not null)
        {
            run.Log.Log($"Desktop drift detected before {reason} for {name}: on desktop {current}, expected {desktopIndex}; re-anchored ({Anchored(confirmed)})");
        }
    }

    private static bool ProcessIsRunning(IDesktopPort port, LaunchEntry entry)
    {
        if (string.IsNullOrEmpty(entry.WindowProcessName))
        {
            return false;
        }

        try
        {
            return port.IsProcessRunning(entry.WindowProcessName);
        }
        catch (DesktopException)
        {
            return false;
        }
    }

    private static Activity? Span(string name, params (string Key, object? Value)[] tags)
    {
        var activity = StepTimer.Source.StartActivity(name);
        activity?.SetTag("scope", Scope);
        foreach (var (key, value) in tags)
        {
            activity?.SetTag(key, value);
        }

        return activity;
    }

    internal static string Anchored(bool confirmed) => confirmed ? "confirmed" : "best-effort only";

    internal static string Seconds(TimeSpan elapsed, string format) => elapsed.TotalSeconds.ToString(format, CultureInfo.InvariantCulture);

    // Python's `value or '?'`: a missing or zero number reads as a question mark.
    internal static string PidOrUnknown(int? value) => value is { } known and not 0 ? known.ToString(CultureInfo.InvariantCulture) : "?";

    private static string NumberOrNone(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "None";

    private static string ReprOrNone(string? value) => value is null ? "None" : PyText.Repr(value);

    private static string Handles(IEnumerable<long> handles) => PythonList(handles.Order().Select(handle => handle.ToString(CultureInfo.InvariantCulture)));

    private static string PythonList(IEnumerable<string> items) => $"[{string.Join(", ", items)}]";

    private sealed record DesktopCompletion(bool IsComplete, string Diff, IReadOnlySet<string> SatisfiedEntryNames);
}
