using System.Diagnostics;
using System.Globalization;
using Rig.Core.GitHub;
using Rig.Core.Processes;
using Rig.Core.Workspace;

namespace Rig.Core.Chrome;

/// <summary><c>chrome-tabs organise</c>'s flags, with the defaults its command declares.</summary>
/// <param name="DryRun">Print both plans; change nothing.</param>
/// <param name="Profile">Only this profile: directory ("Profile 4") or display name.</param>
/// <param name="GitHub">Run the PR rules, asking GitHub.</param>
/// <param name="Sites">Close duplicate non-PR pages by per-site title rules.</param>
/// <param name="MinSize">Fewest tabs of one repo or site that start a new group.</param>
/// <param name="Unattended">A scheduled run: prune as <c>prune --unattended</c>, and group only while the machine is idle.</param>
/// <param name="IdleMinutes">With <paramref name="Unattended"/>, minutes without input before grouping may start.</param>
/// <param name="WaitMinutes">With <paramref name="Unattended"/>, how long to wait for idle before leaving grouping to the next run.</param>
/// <param name="ThisDesktop">Group only windows on the current desktop; never bring others over.</param>
/// <param name="Consolidate">When grouping created a group, delete closed duplicate saved groups afterwards.</param>
/// <param name="Trigger">What started the run, for its trace: <c>manual</c> or <c>scheduled</c>.</param>
public sealed record OrganiseOptions(
    bool DryRun = false,
    string? Profile = null,
    bool GitHub = true,
    bool Sites = true,
    int MinSize = 2,
    bool Unattended = false,
    double IdleMinutes = 1,
    double WaitMinutes = ChromeInstall.DefaultWaitMinutes,
    bool ThisDesktop = false,
    bool Consolidate = false,
    string Trigger = "manual");

/// <summary>How an organise run went, as its root span records it.</summary>
/// <param name="Failed">The run failed: grouping or consolidating stopped, or an attended run could not read Chrome.</param>
/// <param name="Outcome">The run's <c>run.outcome</c>: <c>ok</c>, <c>partial</c>, <c>dry_run</c>, <c>failed</c> or a skip.</param>
public sealed record OrganiseResult(bool Failed, string Outcome)
{
    /// <summary>Tabs the close plans held before the unattended hold.</summary>
    public int ClosePlanned { get; init; }

    /// <summary>Tabs closed.</summary>
    public int Closed { get; init; }

    /// <summary>Planned closes that failed; a warning, not a failure.</summary>
    public int CloseFailed { get; init; }

    /// <summary>How grouping went; null when the run never got there (a skip).</summary>
    public GroupingResult? Grouping { get; init; }

    /// <summary>How the wait for idle went; null when the run did not wait.</summary>
    public GroupingWait? Wait { get; init; }

    /// <summary>Why grouping was deferred, if it was.</summary>
    public string? Deferred { get; init; }

    /// <summary>Saved groups the consolidation planned to delete.</summary>
    public int SavedPlanned { get; init; }

    /// <summary>Saved groups the consolidation deleted.</summary>
    public int SavedDeleted { get; init; }

    /// <summary>The consolidation stopped on a failure.</summary>
    public bool ConsolidationStopped { get; init; }
}

/// <summary>
/// <c>chrome-tabs organise</c>: prune, then group, one window at a time, with the counts and verdicts recorded on a
/// <c>chrome_tabs.organise</c> span.
/// </summary>
/// <remarks>
/// <para>
/// Windows go in screen order: the current desktop first, then monitor by monitor, top to bottom. Each window has its
/// tabs closed and its active tab put back before the next starts. Then grouping (as <c>group</c>) runs window by window
/// over what is left; it brings each window forward and uses the mouse.
/// </para>
/// <para>
/// <c>Consolidate</c> deletes closed duplicate saved groups, but only when grouping created a new group or a profile is
/// still marked pending from an earlier run: joining an open group never saves a duplicate. A consolidation that failed
/// or deleted anything is looked at again next run, since a delete can bring chips out of the overflow.
/// </para>
/// <para>
/// <c>Unattended</c> does nothing at all while a call is live. Grouping then waits until the machine has been idle and
/// is not locked or on a call, stopping at the first key press or mouse move, opening a note and going again once left
/// alone. Every run is traced; the caller writes those spans to the Chrome traces directory.
/// </para>
/// </remarks>
/// <param name="chrome">The platform's Chrome.</param>
/// <param name="github">Answers the PR queries.</param>
/// <param name="owners">The projects' declared local ports and Vercel names; read afresh for each plan.</param>
/// <param name="pendingMarker">The file listing profiles an earlier consolidation left to look at again.</param>
/// <param name="say">Writes a line for the user.</param>
/// <param name="showPausedNote">Opens the note asking to be left alone so an interrupted grouping can retry.</param>
/// <param name="sleep">Sleeps for so many seconds while waiting for idle.</param>
/// <param name="clock">Seconds on a monotonic clock, for the idle wait.</param>
/// <param name="time">Times the run for its report.</param>
public sealed class OrganiseRunner(
    ChromeSession chrome,
    IGitHubApi github,
    Func<UrlOwners> owners,
    string pendingMarker,
    Action<string> say,
    Action showPausedNote,
    Action<double> sleep,
    Func<double> clock,
    TimeProvider time)
{
    /// <summary>The activity source every Chrome step's span comes from.</summary>
    public const string SourceName = "Rig.Chrome";

    private static readonly ActivitySource Source = new(SourceName);

    /// <summary>
    /// Runs <c>organise</c>: chrome read once for the close plans, then afresh for grouping and consolidation. A
    /// <see cref="ChromeReadException"/> is a skip when scheduled (nothing acted on) and a failure when attended, as
    /// each step re-finds and checks its tab before acting and the next run starts over.
    /// </summary>
    /// <param name="options">The command's flags.</param>
    /// <param name="cancellationToken">Cancels the GitHub lookups.</param>
    public async Task<OrganiseResult> RunAsync(OrganiseOptions options, CancellationToken cancellationToken = default)
    {
        Validate(options);
        using var run = Source.StartActivity(ChromeTraces.RunSpan);
        run?.SetTag("chrome.trigger", options.Trigger);
        run?.SetTag("chrome.unattended", options.Unattended);
        run?.SetTag("chrome.dry_run", options.DryRun);
        run?.SetTag("chrome.github", options.GitHub);
        run?.SetTag("chrome.sites", options.Sites);
        run?.SetTag("chrome.profile", options.Profile ?? string.Empty);
        try
        {
            return await OrganiseAsync(run, options, cancellationToken).ConfigureAwait(false);
        }
        catch (ChromeReadException error)
        {
            say($"Skipped: {error.Message}; left the rest for the next run");
            var outcome = options.Unattended ? "skipped_unreadable" : "failed";
            run?.SetTag("run.outcome", outcome);
            run?.SetTag("run.skipped", error.Message);
            return new OrganiseResult(!options.Unattended, outcome);
        }
        catch (Exception error) when (run is not null && Recorded(run, error))
        {
            throw;
        }
    }

    // Marks the run failed with the exception that ended it, then lets it go on: the filter never handles it.
    private static bool Recorded(Activity run, Exception error)
    {
        _ = run.SetStatus(ActivityStatusCode.Error, error.Message);
        _ = run.AddException(error);
        return false;
    }

    private UnattendedGrouping UnattendedGroupingFor() =>
        new(
            new GroupRunner(chrome.Grouping, chrome.Desktops, chrome.Presence.SessionLocked, pause => sleep(pause.TotalSeconds), clock),
            chrome.Presence,
            say,
            sleep,
            clock);

    private async Task<OrganiseResult> OrganiseAsync(Activity? run, OrganiseOptions options, CancellationToken cancellationToken)
    {
        var started = time.GetTimestamp();
        var unattended = UnattendedGroupingFor();
        var windows = ChromeReads.Read(chrome.ReadWindows, options.Profile, "initial");
        if (options.Unattended && unattended.SkipIfLive(windows, run))
        {
            return new OrganiseResult(false, "skipped_live");
        }

        var resolution = await ResolveAsync(windows, options.GitHub, cancellationToken).ConfigureAwait(false);
        var plans = ChromeClosePlans.Plan(windows, resolution, new ClosePlanOptions(options.GitHub, options.Sites), say);
        var current = chrome.Desktops.Current();
        var ordered = ChromeWindow.ScreenOrder(windows, current);
        var numbers = ordered.Select((window, index) => (window.Handle, Number: index + 1))
            .ToDictionary(item => item.Handle, item => item.Number);
        if (options.Unattended)
        {
            var runnable = ClosePlans.HoldUnattended(plans, chrome.Foreground());
            plans = [runnable];
            run?.SetTag("tabs.held", runnable.Held.Count);
            if (runnable.Held.Count > 0)
            {
                say($"Held back: active tab or foreground window ({runnable.Held.Count.ToString(CultureInfo.InvariantCulture)})");
                foreach (var close in runnable.Held)
                {
                    say(TabPlanText.PlanLine(close.Item, "left for an attended run"));
                }
            }
        }

        var closePlanned = plans.Sum(plan => plan.Closes.Count);
        run?.SetTag("tabs.close_planned", closePlanned);

        if (options.DryRun)
        {
            var closing = plans.SelectMany(plan => plan.Closes).Select(close => close.Item.Tab.RuntimeId).ToHashSet();
            say("Then grouping:");
            var after = PlanGrouping(Without(ordered, closing), current, options, "dry_run");
            SayAll(TabPlanText.Groups(after));
            say(string.Create(CultureInfo.InvariantCulture, $"Dry run: nothing changed. planned in {Seconds(started)}s"));
            run?.SetTag("run.outcome", "dry_run");
            run?.SetTag("group.outcome", "dry_run");
            run?.SetTag("group.planned", after.Moves.Count);
            return new OrganiseResult(false, "dry_run")
            {
                ClosePlanned = closePlanned,
                Grouping = new GroupingResult(after.Moves.Count),
            };
        }

        var (applied, failures) = CloseTabs(plans, windows, ordered, numbers, run);
        var closed = applied.Count - failures.Count;

        // Grouping reads the windows afresh: closes renumber tabs and can close whole windows.
        var regrouped = ReadOrdered(options.Profile, current, "regroup");
        var plan = PlanGrouping(regrouped, current, options, "plan");
        SayAll(TabPlanText.Groups(plan));
        string? deferred = null;
        GroupingWait? wait = null;
        var grouped = new GroupingResult(plan.Moves.Count);
        if (plan.Moves.Count > 0)
        {
            if (options.Unattended)
            {
                wait = unattended.Await(() => ReadOrdered(options.Profile, current, "wait_check"), options.IdleMinutes, options.WaitMinutes);
                TagWait(run, wait);
                deferred = wait.Deferred;
                if (deferred is null)
                {
                    // Tabs come and go while the run waits; group what is there now.
                    plan = PlanGrouping(ReadOrdered(options.Profile, current, "replan"), current, options, "replan");
                }
            }

            if (deferred is not null)
            {
                say($"Grouping deferred: {deferred}");
            }
            else if (plan.Moves.Count > 0)
            {
                grouped = unattended.GroupUntilLeftAlone(
                    plan,
                    phase => ReadOrdered(options.Profile, current, phase),
                    phase => PlanGrouping(ReadOrdered(options.Profile, current, phase), current, options, phase),
                    options.Unattended,
                    showPausedNote);
            }
        }

        if (grouped.Stopped is { } stopped)
        {
            say(grouped.ByInput
                ? $"Left grouping: {stopped}; the next run carries on"
                : $"Stopped grouping {stopped}");
        }

        var saved = Consolidate(options, grouped, run);
        var consolidated = saved.Ran
            ? string.Create(CultureInfo.InvariantCulture, $", deleted {saved.Deleted}/{saved.Planned} saved duplicates")
            : string.Empty;
        say(string.Create(
            CultureInfo.InvariantCulture,
            $"Closed {closed}/{applied.Count}, grouped {grouped.Moved}/{grouped.Planned}{consolidated} in {Seconds(started)}s"));

        // A tab that would not close is still open, so the next run plans it again: a warning, not a failure.
        // So is grouping someone's input stopped; grouping that failed by itself fails the run.
        var failed = saved.Stopped is not null || (grouped.Stopped is not null && !grouped.ByInput);
        if (failures.Count > 0 && !failed)
        {
            say($"Warning: {failures.Count} tab(s) not closed; the next run tries again");
        }

        var outcome = failed ? "failed" : failures.Count > 0 ? "partial" : "ok";
        run?.SetTag("run.outcome", outcome);
        run?.SetTag("group.outcome", grouped.Outcome(deferred));
        run?.SetTag("group.planned", grouped.Planned);
        run?.SetTag("group.moved", grouped.Moved);
        run?.SetTag("group.retries", grouped.Retries);
        if (grouped.Stopped is not null)
        {
            run?.SetTag("group.stopped", grouped.Stopped);
        }

        if (deferred is not null)
        {
            run?.SetTag("group.deferred", deferred);
        }

        return new OrganiseResult(failed, outcome)
        {
            ClosePlanned = closePlanned,
            Closed = closed,
            CloseFailed = failures.Count,
            Grouping = grouped,
            Wait = wait,
            Deferred = deferred,
            SavedPlanned = saved.Planned,
            SavedDeleted = saved.Deleted,
            ConsolidationStopped = saved.Stopped is not null,
        };
    }

    private async Task<Resolution> ResolveAsync(
        IReadOnlyList<ChromeWindow> windows, bool useGitHub, CancellationToken cancellationToken)
    {
        if (!useGitHub)
        {
            return Resolution.Empty;
        }

        var refs = PrPrune.PrTabs(windows).Select(item => item.Pr).Distinct().ToList();
        using var activity = Source.StartActivity("chrome_tabs.resolve_prs");
        activity?.SetTag("github.prs", refs.Count);
        activity?.SetTag("github.repos", refs.Select(pr => pr.Slug).Distinct(StringComparer.Ordinal).Count());
        var resolution = await PrResolver.ResolveAsync(github, refs, cancellationToken: cancellationToken).ConfigureAwait(false);
        activity?.SetTag("github.chains", resolution.Chains.Count);
        activity?.SetTag("github.failed_repos", resolution.FailedRepos.Keys.Order(StringComparer.Ordinal).ToArray());
        return resolution;
    }

    private (IReadOnlyList<AppliedClose> Applied, IReadOnlyList<AppliedClose> Failures) CloseTabs(
        IReadOnlyList<IClosePlan> plans,
        IReadOnlyList<ChromeWindow> windows,
        IReadOnlyList<ChromeWindow> ordered,
        IReadOnlyDictionary<long, int> numbers,
        Activity? run)
    {
        using var activity = Source.StartActivity("chrome_tabs.close_tabs");
        var start = chrome.Desktops.Current();
        IReadOnlyList<AppliedClose> applied;
        try
        {
            applied = ClosePlans.Apply(
                plans,
                windows,
                chrome.Actions,
                ordered.Select(window => window.Handle),
                (window, closes) => ReportCloses(window, closes, numbers));
        }
        finally
        {
            chrome.Desktops.ReturnTo(start);
        }

        var failures = applied.Where(result => !result.Closed).ToList();
        foreach (var failure in failures)
        {
            say($"Not closed {failure.Planned.Item.Label}: {failure.Detail}");
        }

        activity?.SetTag("tabs.closed", applied.Count - failures.Count);
        activity?.SetTag("tabs.close_failed", failures.Count);
        run?.SetTag("tabs.closed", applied.Count - failures.Count);
        run?.SetTag("tabs.close_failed", failures.Count);
        return (applied, failures);
    }

    private ConsolidationOutcome Consolidate(OrganiseOptions options, GroupingResult grouped, Activity? run)
    {
        if (!options.Consolidate)
        {
            return default;
        }

        var pending = SavedConsolidation.PendingProfiles(pendingMarker);
        run?.SetTag(
            "saved.consolidate",
            grouped.Created ? "ran" : pending.Count > 0 ? "pending" : "no_new_groups");
        if (!grouped.Created && pending.Count == 0)
        {
            return default;
        }

        using var activity = Source.StartActivity("chrome_tabs.consolidate_saved");
        var scanned = ChromeReads.Read(chrome.ReadWindows, options.Profile, "consolidate");
        var unscanned = pending.Except(scanned.Select(window => window.ProfileKey), StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);

        // Marked first, so a run that fails part way is looked at again.
        SavedConsolidation.SetPendingProfiles(pendingMarker, unscanned.Concat(scanned.Select(window => window.ProfileKey)));
        var consolidation = new SavedConsolidation(chrome.Saved, chrome.Desktops, say);
        var planned = consolidation.Plan(scanned);
        var saved = planned.Plan;
        var done = consolidation.Consolidate(planned);

        // A profile that failed anything goes again: a delete can bring chips out of the overflow.
        SavedConsolidation.SetPendingProfiles(pendingMarker, unscanned.Concat(saved.Deletes.Select(delete => delete.Profile)));
        activity?.SetTag("saved.planned", saved.Total);
        activity?.SetTag("saved.deleted", done.Deleted);
        run?.SetTag("saved.planned", saved.Total);
        run?.SetTag("saved.deleted", done.Deleted);
        if (done.Stopped is not null)
        {
            activity?.SetTag("saved.stopped", done.Stopped);
            run?.SetTag("saved.stopped", done.Stopped);
            say($"Stopped consolidating {done.Stopped}");
        }

        return new ConsolidationOutcome(true, saved.Total, done.Deleted, done.Stopped);
    }

    private void ReportCloses(ChromeWindow window, IReadOnlyList<AppliedClose> closes, IReadOnlyDictionary<long, int> numbers)
    {
        if (closes.Count == 0)
        {
            return;
        }

        var profile = string.IsNullOrEmpty(window.ProfileName) ? window.ProfileKey : window.ProfileName;
        say(string.Create(
            CultureInfo.InvariantCulture,
            $"Window {numbers[window.Handle]} [{profile}]: closed {closes.Count(close => close.Closed)}/{closes.Count}"));
    }

    private GroupPlan PlanGrouping(IReadOnlyList<ChromeWindow> windows, int? current, OrganiseOptions options, string phase) =>
        GroupRunner.Plan(windows, current, options.MinSize, options.ThisDesktop, chrome.HostsByTitle, owners(), phase);

    private IReadOnlyList<ChromeWindow> ReadOrdered(string? profile, int? current, string phase) =>
        ChromeWindow.ScreenOrder(ChromeReads.Read(chrome.ReadWindows, profile, phase), current);

    private static void TagWait(Activity? run, GroupingWait wait)
    {
        run?.SetTag("wait.outcome", wait.Outcome);
        run?.SetTag("wait.seconds", Math.Round(wait.WaitedSeconds, 1));
        run?.SetTag("wait.checks", wait.Checks);
        if (wait.LastBlock is not null)
        {
            run?.SetTag("wait.last_block", wait.LastBlock);
        }
    }

    /// <summary>The windows as they will be once <paramref name="closing"/> has closed, tabs renumbered.</summary>
    private static IReadOnlyList<ChromeWindow> Without(IEnumerable<ChromeWindow> windows, HashSet<RuntimeId> closing) =>
    [
        .. windows.Select(window => window with
        {
            Tabs =
            [
                .. window.Tabs
                    .Where(tab => !closing.Contains(tab.RuntimeId))
                    .Select((tab, index) => tab with { Index = index }),
            ],
        }),
    ];

    private void SayAll(IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            say(line);
        }
    }

    private string Seconds(long started) =>
        time.GetElapsedTime(started).TotalSeconds.ToString("F2", CultureInfo.InvariantCulture);

    private static void Validate(OrganiseOptions options)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MinSize, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(options.IdleMinutes);
        ArgumentOutOfRangeException.ThrowIfNegative(options.WaitMinutes);
    }

    private readonly record struct ConsolidationOutcome(bool Ran, int Planned, int Deleted, string? Stopped);
}
