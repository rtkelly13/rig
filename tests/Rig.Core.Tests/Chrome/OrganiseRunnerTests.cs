using System.Diagnostics;
using Rig.Core.Chrome;
using Rig.Core.Processes;
using Rig.Core.Tests.Fakes;
using Rig.Core.Workspace;

namespace Rig.Core.Tests.Chrome;

/// <summary>
/// <c>organise</c> end to end over fakes: a dry run, prune before group, windows and desktops, unattended waits and
/// refusals, input retries, saved consolidation, profile isolation and the trace it records. Replaces the whole-command
/// half of <c>test_chrome_organise</c>.
/// </summary>
public sealed class OrganiseRunnerTests : IDisposable
{
    private const long W1 = 1000;
    private const long W2 = 2000;
    private const string Profile = "Profile 4";
    private const string Ryan = "Ryan";

    /// <summary>A multi-word page title the site rules key exactly, so a second copy closes.</summary>
    private const string Handbook = "Merge Queue Handbook";

    private readonly TempDir _temp = new();
    private readonly FakeChromeTabActions _tabActions = new();
    private readonly FakeChromeGroupActions _grouping = new();
    private readonly FakeChromeSavedGroupActions _saved = new();
    private readonly FakeChromeDesktops _desktops = new() { Number = 1 };
    private readonly FakePresence _presence = new FakePresence().Idle(600);
    private readonly FakeGitHub _github = new();
    private readonly List<string> _said = [];
    private int _notes;
    private int _ids;
    private long _foreground;
    private IReadOnlyList<ChromeWindow> _windows = [];
    private Func<IReadOnlyList<ChromeWindow>>? _read;

    public void Dispose() => _temp.Dispose();

    private string Marker => Path.Join(_temp.Path, "saved-groups.pending");

    private ChromeSession Session() => new(
        () => _read?.Invoke() ?? _windows,
        _tabActions,
        _grouping,
        _saved,
        _desktops,
        () => _foreground,
        _presence,
        (_, _) => new Dictionary<string, string>());

    private OrganiseRunner Runner() => new(
        Session(),
        _github,
        () => UrlOwners.None,
        Marker,
        _said.Add,
        () => _notes++,
        seconds => _presence.Now += (uint)(seconds * 1000),
        () => _presence.Now / 1000.0,
        TimeProvider.System);

    private static OrganiseOptions Options(
        bool dryRun = false,
        bool unattended = false,
        bool consolidate = false,
        bool github = false,
        double waitMinutes = ChromeInstall.DefaultWaitMinutes,
        double idleMinutes = 1,
        string? profile = Profile) =>
        new(
            DryRun: dryRun,
            Profile: profile,
            GitHub: github,
            Sites: true,
            MinSize: 2,
            Unattended: unattended,
            IdleMinutes: idleMinutes,
            WaitMinutes: waitMinutes,
            ThisDesktop: false,
            Consolidate: consolidate,
            Trigger: unattended ? "scheduled" : "manual");

    private ChromeWindow Window(long handle, string directory, int? desktop, params TabSpec[] specs)
    {
        var tabs = specs.Select((spec, index) => new ChromeTab(
            new RuntimeId([42, (int)handle, ++_ids]),
            handle,
            index,
            spec.Title,
            spec.Title,
            spec.Selected,
            Pinned: false,
            spec.Loaded,
            Audible: false)).ToList();
        _desktops.Windows[handle] = desktop ?? 1;
        return new ChromeWindow(handle, "w", directory, directory == Profile ? Ryan : null, handle, false, desktop, tabs);
    }

    private static TabSpec Tab(string title, bool selected = false, bool loaded = true) => new(title, selected, loaded);

    private static string Pr(int number) => TabBrowser.PrTitle(number);

    private static Activity Root(IReadOnlyList<Activity> stopped) =>
        stopped.Single(activity => activity.OperationName == ChromeTraces.RunSpan);

    private static IReadOnlyList<string> TracedPhases(IReadOnlyList<Activity> stopped) =>
        [.. stopped.Where(activity => activity.OperationName == "chrome_tabs.read_windows").Select(activity => (string)activity.GetTagItem("read.phase")!)];

    [Fact]
    public async Task A_dry_run_plans_both_passes_and_changes_nothing()
    {
        using var spans = new RecordedSpans("Rig.Chrome");
        _windows =
        [
            Window(W1, Profile, 1, Tab(Handbook), Tab(Handbook), Tab(Pr(1)), Tab(Pr(2))),
        ];

        var result = await Runner().RunAsync(Options(dryRun: true), TestContext.Current.CancellationToken);

        Assert.False(result.Failed);
        Assert.Equal("dry_run", result.Outcome);
        Assert.Equal(1, result.ClosePlanned);
        Assert.Empty(_tabActions.Closed);
        Assert.Empty(_grouping.Log);
        Assert.Empty(_saved.Log);
        Assert.False(File.Exists(Marker));
        Assert.Contains("Then grouping:", _said);
        Assert.StartsWith("Dry run: nothing changed. planned in ", _said[^1], StringComparison.Ordinal);
        var root = Root(spans.Stopped);
        Assert.Equal("dry_run", root.GetTagItem("run.outcome"));
        Assert.Equal("dry_run", root.GetTagItem("group.outcome"));
        Assert.Equal(2, root.GetTagItem("group.planned"));
        Assert.Equal(["chrome_tabs.plan_closes", "chrome_tabs.plan_grouping"], spans.Stopped
            .Select(activity => activity.OperationName)
            .Where(name => name.StartsWith("chrome_tabs.", StringComparison.Ordinal))
            .Where(name => name is not (ChromeTraces.RunSpan or "chrome_tabs.read_windows")));
    }

    [Fact]
    public async Task Pruning_runs_before_grouping_and_each_window_puts_its_active_tab_back()
    {
        using var spans = new RecordedSpans("Rig.Chrome");
        _windows =
        [
            Window(W1, Profile, 1, Tab(Handbook, selected: true), Tab(Handbook), Tab(Pr(1)), Tab(Pr(2))),
        ];

        var result = await Runner().RunAsync(Options(), TestContext.Current.CancellationToken);

        Assert.False(result.Failed);
        Assert.Equal("ok", result.Outcome);
        Assert.Equal(1, result.Closed);
        Assert.Equal([Handbook], _tabActions.Closed.Select(tab => tab.Title));
        Assert.Equal(2, _grouping.Grouped.Count);
        var names = spans.Stopped.Select(activity => activity.OperationName).ToList();
        Assert.True(names.IndexOf("chrome_tabs.close_tabs") < names.IndexOf("chrome_tabs.group_window"));
        Assert.Equal([W1], Assert.Single(_tabActions.Restored).Windows);
        Assert.Empty(_tabActions.Restored[0].Fallbacks!);
        Assert.Contains("Closed 1/1, grouped 2/2", _said[^1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Windows_close_in_screen_order_current_desktop_first()
    {
        _windows =
        [
            Window(W2, "Default", 2, Tab(Handbook), Tab(Handbook)),
            Window(W1, Profile, 1, Tab(Handbook), Tab(Handbook)),
        ];

        await Runner().RunAsync(Options(profile: null), TestContext.Current.CancellationToken);

        Assert.Equal([W1, W2], _tabActions.Closed.Select(tab => tab.WindowHandle));
        Assert.Equal([[W1], [W2]], _tabActions.Restored.Select(restored => restored.Windows));
    }

    [Fact]
    public async Task A_profile_filter_leaves_every_other_profile_alone()
    {
        _windows =
        [
            Window(W1, Profile, 1, Tab(Handbook), Tab(Handbook), Tab(Pr(1)), Tab(Pr(2))),
            Window(W2, "Default", 1, Tab(Handbook), Tab(Handbook), Tab(Pr(3)), Tab(Pr(4))),
        ];

        var result = await Runner().RunAsync(Options(), TestContext.Current.CancellationToken);

        Assert.Equal("ok", result.Outcome);
        Assert.Equal([W1], _tabActions.Closed.Select(tab => tab.WindowHandle));
        Assert.All(_grouping.Grouped, tab => Assert.Equal(W1, tab.WindowHandle));
        Assert.Equal(2, result.Grouping!.Moved);
    }

    [Fact]
    public async Task A_window_on_another_desktop_is_brought_over_to_group_and_sent_home()
    {
        _windows =
        [
            Window(W1, Profile, 1, Tab(Pr(1))),
            Window(W2, Profile, 2, Tab(Pr(2))),
        ];

        var result = await Runner().RunAsync(Options(), TestContext.Current.CancellationToken);

        Assert.False(result.Failed);
        Assert.Equal(new GroupingResult(2, 2, Created: true), result.Grouping);
        Assert.Equal([(W2, 1), (W2, 2)], _desktops.Moves);
        Assert.Equal(1, _desktops.Number);
    }

    [Fact]
    public async Task Focus_returns_to_the_desktop_grouping_switched_away_from()
    {
        using var spans = new RecordedSpans("Rig.Chrome");
        _grouping.DuringGroup = () => _desktops.Number = 3;
        _windows = [Window(W1, Profile, 1, Tab(Pr(1)), Tab(Pr(2)))];

        await Runner().RunAsync(Options(), TestContext.Current.CancellationToken);

        Assert.Equal(1, _desktops.Number);
        var restored = spans.Stopped.Single(activity => activity.OperationName == "chrome_tabs.restore_desktop");
        Assert.Equal((3, 1), ((int)restored.GetTagItem("desktop.from")!, (int)restored.GetTagItem("desktop.to")!));
    }

    [Fact]
    public async Task Unattended_leaves_chrome_alone_while_a_call_is_live()
    {
        using var spans = new RecordedSpans("Rig.Chrome");
        _presence.Devices = ["microphone"];
        _windows = [Window(W1, Profile, 1, Tab(Handbook), Tab(Handbook))];

        var result = await Runner().RunAsync(Options(unattended: true), TestContext.Current.CancellationToken);

        Assert.False(result.Failed);
        Assert.Equal("skipped_live", result.Outcome);
        Assert.Empty(_tabActions.Closed);
        Assert.Empty(_grouping.Log);
        Assert.Contains(_said, line => line.StartsWith("Skipped: a call is live", StringComparison.Ordinal));
        Assert.Equal("skipped_live", Root(spans.Stopped).GetTagItem("run.outcome"));
        Assert.Contains("microphone", (string)Root(spans.Stopped).GetTagItem("run.skipped")!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unattended_waits_for_idle_before_taking_the_mouse()
    {
        using var spans = new RecordedSpans("Rig.Chrome");
        _presence.Idle(0);
        _windows = [Window(W1, Profile, 1, Tab(Pr(1)), Tab(Pr(2)))];

        var result = await Runner().RunAsync(
            Options(unattended: true, waitMinutes: 5, idleMinutes: 0.5), TestContext.Current.CancellationToken);

        Assert.False(result.Failed);
        Assert.Equal("clear", result.Wait!.Outcome);
        Assert.Equal("grouped", result.Grouping!.Outcome(null));
        Assert.Equal(2, result.Grouping.Moved);
        Assert.Contains("Waiting up to 5 min to group: the machine was used 0s ago (waits for 0.5 min idle)", _said);
        var root = Root(spans.Stopped);
        Assert.Equal("clear", root.GetTagItem("wait.outcome"));
        Assert.Equal(5, root.GetTagItem("wait.checks"));
        Assert.Equal(["initial", "regroup", "wait_check", "replan"], TracedPhases(spans.Stopped));
    }

    [Fact]
    public async Task Unattended_gives_up_on_a_locked_machine_without_failing()
    {
        using var spans = new RecordedSpans("Rig.Chrome");
        _presence.Locked = true;
        _windows = [Window(W1, Profile, 1, Tab(Pr(1)), Tab(Pr(2)))];

        var result = await Runner().RunAsync(
            Options(unattended: true, consolidate: true, waitMinutes: 1, idleMinutes: 1), TestContext.Current.CancellationToken);

        Assert.False(result.Failed);
        Assert.Equal("deferred", result.Grouping!.Outcome(result.Deferred));
        Assert.Equal("gave_up", result.Wait!.Outcome);
        Assert.StartsWith("still not clear after 1 min", result.Deferred, StringComparison.Ordinal);
        Assert.DoesNotContain(spans.Stopped, activity => activity.OperationName == "chrome_tabs.group");
        var root = Root(spans.Stopped);
        Assert.Equal("ok", root.GetTagItem("run.outcome"));
        Assert.Equal("deferred", root.GetTagItem("group.outcome"));
        Assert.Equal("no_new_groups", root.GetTagItem("saved.consolidate"));
        Assert.DoesNotContain(spans.Stopped, activity => activity.OperationName == "chrome_tabs.consolidate_saved");
        Assert.Contains(_said, line => line.StartsWith("Grouping deferred: still not clear", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Unattended_with_no_wait_budget_defers_grouping_at_once()
    {
        using var spans = new RecordedSpans("Rig.Chrome");
        _presence.Locked = true;
        var started = _presence.Now;
        _windows = [Window(W1, Profile, 1, Tab(Pr(1)), Tab(Pr(2)))];

        var result = await Runner().RunAsync(
            Options(unattended: true, waitMinutes: 0), TestContext.Current.CancellationToken);

        Assert.False(result.Failed);
        Assert.Equal(new GroupingWait("the session is locked", "no_wait", LastBlock: "the session is locked"), result.Wait);
        Assert.Equal(started, _presence.Now);
        Assert.Equal("deferred", Root(spans.Stopped).GetTagItem("group.outcome"));
        Assert.DoesNotContain(spans.Stopped, activity => activity.OperationName == "chrome_tabs.group");
    }

    [Fact]
    public async Task Unattended_holds_the_active_tab_and_the_foreground_window()
    {
        using var spans = new RecordedSpans("Rig.Chrome");
        _foreground = W2;
        _windows =
        [
            Window(W1, Profile, 1, Tab(Handbook, selected: true), Tab(Handbook)),
            Window(W2, Profile, 1, Tab(Handbook), Tab(Handbook)),
        ];

        var result = await Runner().RunAsync(
            Options(unattended: true, waitMinutes: 0), TestContext.Current.CancellationToken);

        Assert.Equal(1, result.ClosePlanned);
        Assert.Equal(2, Root(spans.Stopped).GetTagItem("tabs.held"));
        Assert.Contains(_said, line => line.StartsWith("Held back: active tab or foreground window (2)", StringComparison.Ordinal));
        Assert.Equal([(W1, 1)], _tabActions.Closed.Select(tab => (tab.WindowHandle, tab.Index)));
    }

    [Fact]
    public async Task Input_that_stops_grouping_opens_a_note_and_retries_once_left_alone()
    {
        using var spans = new RecordedSpans("Rig.Chrome");
        var seen = 0;
        _grouping.DuringGroup = () =>
        {
            if (seen++ == 0)
            {
                _presence.LastInput = _presence.Now + 1000;
                _presence.Now += 1000;
            }
        };
        _windows = [Window(W1, Profile, 1, Tab(Pr(1)), Tab(Pr(2)))];

        var result = await Runner().RunAsync(Options(unattended: true), TestContext.Current.CancellationToken);

        Assert.False(result.Failed);
        Assert.Equal(1, _notes);
        Assert.Equal(new GroupingResult(2, 3, Created: true, Retries: 1), result.Grouping);
        Assert.Equal("grouped", result.Grouping!.Outcome(result.Deferred));
        Assert.Equal(1, Root(spans.Stopped).GetTagItem("group.retries"));
        Assert.Contains("wait_check", TracedPhases(spans.Stopped));
        Assert.Contains("retry", TracedPhases(spans.Stopped));
    }

    [Fact]
    public async Task Input_that_keeps_coming_leaves_grouping_to_the_next_run_without_failing()
    {
        _grouping.DuringGroup = () =>
        {
            _presence.LastInput = _presence.Now + 1000;
            _presence.Now += 1000;
        };
        _windows = [Window(W1, Profile, 1, Tab(Pr(1)), Tab(Pr(2)))];

        var result = await Runner().RunAsync(Options(unattended: true), TestContext.Current.CancellationToken);

        Assert.False(result.Failed);
        Assert.Equal("ok", result.Outcome);
        Assert.Equal(UnattendedGrouping.InputRetries, _notes);
        Assert.Equal("interrupted", result.Grouping!.Outcome(null));
        Assert.Equal("keyboard or mouse input again after 2 retries", result.Grouping.Stopped);
        Assert.Contains(_said, line => line.StartsWith("Left grouping: keyboard or mouse input", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Grouping_that_fails_by_itself_fails_the_run()
    {
        using var spans = new RecordedSpans("Rig.Chrome");
        _grouping.GroupFailure = "menu never opened";
        _windows = [Window(W1, Profile, 1, Tab(Pr(1)), Tab(Pr(2)))];

        var result = await Runner().RunAsync(Options(unattended: true), TestContext.Current.CancellationToken);

        Assert.True(result.Failed);
        Assert.Equal("failed", result.Outcome);
        Assert.Equal(0, _notes);
        Assert.Equal("stopped", result.Grouping!.Outcome(null));
        Assert.Equal("failed", Root(spans.Stopped).GetTagItem("run.outcome"));
        Assert.Contains(_said, line => line.StartsWith("Stopped grouping", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_tab_that_would_not_close_warns_without_failing_the_run()
    {
        using var spans = new RecordedSpans("Rig.Chrome");
        _tabActions.FailWith = "tab is still present";
        _windows = [Window(W1, Profile, 1, Tab(Handbook), Tab(Handbook), Tab(Pr(1)))];

        var result = await Runner().RunAsync(Options(unattended: true), TestContext.Current.CancellationToken);

        Assert.False(result.Failed);
        Assert.Equal("partial", result.Outcome);
        Assert.Equal((0, 1), (result.Closed, result.CloseFailed));
        var root = Root(spans.Stopped);
        Assert.Equal("partial", root.GetTagItem("run.outcome"));
        Assert.Equal((0, 1), (root.GetTagItem("tabs.closed"), root.GetTagItem("tabs.close_failed")));
        Assert.Equal("Warning: 1 tab(s) not closed; the next run tries again", _said[^1]);
    }

    [Fact]
    public async Task A_strip_without_evidence_traces_grouping_as_skipped_unreadable()
    {
        using var spans = new RecordedSpans("Rig.Chrome");
        var window = Window(W1, Profile, 1, Tab("Notes", selected: true), Tab("Docs"));
        _windows = [window with { Tabs = [.. window.Tabs.Select(tab => tab with { Pinned = true, Audible = true, Alerts = [Rig.Core.Chrome.MacOS.MacRead.MetadataUnavailable] })] }];

        var result = await Runner().RunAsync(Options(), TestContext.Current.CancellationToken);

        Assert.False(result.Failed);
        Assert.Equal("skipped_unreadable", Root(spans.Stopped).GetTagItem("group.outcome"));
        Assert.Contains(_said, line => line.StartsWith("Grouping skipped: Chrome's tab strip is unreadable", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Chrome_that_will_not_read_is_a_skip_unattended_and_a_failure_attended()
    {
        _read = () => throw new ChromeReadException("Chrome's tab strip kept changing during the read");

        var scheduled = await Runner().RunAsync(Options(unattended: true), TestContext.Current.CancellationToken);
        var attended = await Runner().RunAsync(Options(), TestContext.Current.CancellationToken);

        Assert.False(scheduled.Failed);
        Assert.Equal("skipped_unreadable", scheduled.Outcome);
        Assert.True(attended.Failed);
        Assert.Equal("failed", attended.Outcome);
        Assert.Contains(_said, line => line.StartsWith("Skipped: Chrome's tab strip kept changing", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_run_that_crashes_records_the_exception_on_its_span()
    {
        using var spans = new RecordedSpans(OrganiseRunner.SourceName);
        _read = () => throw new InvalidOperationException("driver went away");

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => Runner().RunAsync(Options(), TestContext.Current.CancellationToken));

        var run = Root(spans.Stopped);
        Assert.Equal((ActivityStatusCode.Error, "driver went away"), (run.Status, run.StatusDescription));
        var recorded = Assert.Single(run.Events, happened => happened.Name == "exception");
        Assert.Equal(thrown.Message, recorded.Tags.Single(tag => tag.Key == "exception.message").Value);
    }

    [Fact]
    public async Task An_unreadable_run_leaves_no_step_half_done()
    {
        _windows = [Window(W1, Profile, 1, Tab(Pr(1)), Tab(Pr(2)))];
        _read = () => throw new ChromeReadException("kept changing");

        await Runner().RunAsync(Options(unattended: true), TestContext.Current.CancellationToken);

        Assert.Empty(_tabActions.Closed);
        Assert.Empty(_grouping.Log);
    }

    [Fact]
    public async Task A_created_group_consolidates_closed_duplicate_saved_groups()
    {
        using var spans = new RecordedSpans("Rig.Chrome");
        _saved.Bar(W1, Profile, "+Google", "-Google");
        _windows = [Window(W1, Profile, 1, Tab(Pr(1)), Tab(Pr(2)))];

        var result = await Runner().RunAsync(Options(consolidate: true), TestContext.Current.CancellationToken);

        Assert.False(result.Failed);
        Assert.False(result.ConsolidationStopped);
        Assert.Equal((1, 1), (result.SavedPlanned, result.SavedDeleted));
        Assert.Equal(["1000 Profile 4 Google"], _saved.Log);
        Assert.Equal($"{Profile}\n", await File.ReadAllTextAsync(Marker, TestContext.Current.CancellationToken));
        var root = Root(spans.Stopped);
        Assert.Equal("ran", root.GetTagItem("saved.consolidate"));
        Assert.Equal((1, 1), (root.GetTagItem("saved.planned"), root.GetTagItem("saved.deleted")));
        var consolidating = spans.Stopped.Single(activity => activity.OperationName == "chrome_tabs.consolidate_saved");
        Assert.Equal((1, 1), (consolidating.GetTagItem("saved.planned"), consolidating.GetTagItem("saved.deleted")));
        Assert.Contains(", deleted 1/1 saved duplicates", _said[^1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_new_group_leaves_saved_groups_alone()
    {
        using var spans = new RecordedSpans("Rig.Chrome");
        _saved.Bar(W1, Profile, "+Google", "-Google");
        _windows = [Window(W1, Profile, 1, Tab(Pr(1)))];

        var result = await Runner().RunAsync(
            Options(consolidate: true, unattended: true, waitMinutes: 0), TestContext.Current.CancellationToken);

        Assert.Equal("nothing_to_group", result.Grouping!.Outcome(null));
        Assert.Equal("no_new_groups", Root(spans.Stopped).GetTagItem("saved.consolidate"));
        Assert.Empty(_saved.Log);
        Assert.DoesNotContain(spans.Stopped, activity => activity.OperationName == "chrome_tabs.consolidate_saved");
    }

    [Fact]
    public async Task A_pending_profile_is_consolidated_without_a_new_group_and_stays_marked_when_unscanned()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Marker)!);
        await File.WriteAllTextAsync(Marker, "Closed Profile\n", TestContext.Current.CancellationToken);
        using var spans = new RecordedSpans("Rig.Chrome");
        _windows = [Window(W1, Profile, 1, Tab(Pr(1)))];

        var result = await Runner().RunAsync(
            Options(consolidate: true, unattended: true, waitMinutes: 0), TestContext.Current.CancellationToken);

        Assert.False(result.Failed);
        Assert.Equal("pending", Root(spans.Stopped).GetTagItem("saved.consolidate"));
        Assert.NotNull(spans.Stopped.SingleOrDefault(activity => activity.OperationName == "chrome_tabs.consolidate_saved"));
        Assert.Equal("Closed Profile\n", await File.ReadAllTextAsync(Marker, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_failed_consolidation_fails_the_run_and_is_looked_at_again()
    {
        using var spans = new RecordedSpans("Rig.Chrome");
        _saved.Bar(W1, Profile, "+Google", "-Google");
        _saved.Refuses["Google"] = "chip still on the bar";
        _windows = [Window(W1, Profile, 1, Tab(Pr(1)), Tab(Pr(2)))];

        var result = await Runner().RunAsync(Options(consolidate: true), TestContext.Current.CancellationToken);

        Assert.True(result.Failed);
        Assert.Equal("failed", result.Outcome);
        Assert.Equal(0, result.SavedDeleted);
        Assert.True(result.ConsolidationStopped);
        Assert.Equal($"{Profile}\n", await File.ReadAllTextAsync(Marker, TestContext.Current.CancellationToken));
        Assert.Equal("failed", Root(spans.Stopped).GetTagItem("run.outcome"));
        Assert.Equal("deleting saved groups 'Google' [Profile 4]: chip still on the bar", Root(spans.Stopped).GetTagItem("saved.stopped"));
        Assert.Contains(_said, line => line.StartsWith("Stopped consolidating", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Github_lookups_are_traced_and_their_failures_reported()
    {
        using var spans = new RecordedSpans("Rig.Chrome");
        _github.Fail("POST", "graphql", 502);
        _windows = [Window(W1, Profile, 1, Tab(Pr(1)), Tab(Pr(2)), Tab(Pr(3)))];

        var result = await Runner().RunAsync(
            Options(dryRun: true, github: true), TestContext.Current.CancellationToken);

        Assert.False(result.Failed);
        var resolve = spans.Stopped.Single(activity => activity.OperationName == "chrome_tabs.resolve_prs");
        Assert.Equal((3, 1), (resolve.GetTagItem("github.prs"), resolve.GetTagItem("github.repos")));
        Assert.Equal(["acme-corp/data-platform"], (string[])resolve.GetTagItem("github.failed_repos")!);
        Assert.Contains(_said, line => line == "acme-corp/data-platform: open PRs unreadable, chain rules skipped: gh: HTTP 502");
        Assert.Empty(_tabActions.Closed);
    }

    [Fact]
    public async Task Options_outside_their_ranges_are_refused()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            Runner().RunAsync(Options() with { MinSize = 0 }, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            Runner().RunAsync(Options() with { IdleMinutes = -1 }, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            Runner().RunAsync(Options() with { WaitMinutes = -1 }, TestContext.Current.CancellationToken));
    }

    /// <summary>One tab to build: its title and flags.</summary>
    private sealed record TabSpec(string Title, bool Selected = false, bool Loaded = true);
}
