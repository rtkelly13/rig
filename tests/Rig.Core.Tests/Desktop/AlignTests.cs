using Rig.Core.Desktop;

namespace Rig.Core.Tests.Desktop;

/// <summary>The placement-only align engine: matching, decisions and the moves it makes through its port.</summary>
public sealed class AlignTests
{
    private static readonly Dictionary<string, string> NoSubs = [];
    private static readonly Rect Left = new(0, 0, 960, 1080);
    private static readonly Rect Right = new(1920, 0, 960, 1080);
    private static readonly PlacementSpec LeftPlacement = new(1, new("halves", 1, "vertical"));
    private static readonly PlacementSpec RightPlacement = new(1, new("halves", 2, "vertical"));

    private static LaunchEntry Entry(string name, string process, PlacementSpec? placement, string? title = null) =>
        new(name) { Path = $@"C:\Apps\{process}", WindowProcessName = process, WindowTitle = title, Placement = placement };

    private static LiveWindow Window(long handle, string title, string process, int? desktop, PlacementSpec? placement = null, Rect? bounds = null) =>
        new(handle, title, 100, process, $@"C:\Apps\{process}", null, desktop, placement?.Screen, placement, bounds);

    private static WorkspacePlan Plan(params DesktopPlan[] desktops) => new() { Desktops = desktops };

    private static DesktopPlan Desktop(string name, params LaunchEntry[] entries) => new(name) { Launch = entries };

    [Fact]
    public void A_window_on_its_own_desktop_is_matched_before_another_desktops_entry_can_take_it()
    {
        var notes = Entry("Notes", "notes.exe", LeftPlacement, "Notes");
        var plan = Plan(Desktop("Work", notes), Desktop("Admin", Entry("Admin Notes", "notes.exe", RightPlacement, "Notes")));
        var windows = new[] { Window(1, "Notes", "notes.exe", 1, LeftPlacement) };

        var matches = Align.MatchPlanWindows(plan, windows, NoSubs);

        Assert.Equal(1, matches[0].Window?.Handle);
        Assert.Null(matches[1].Window);
    }

    [Fact]
    public void A_strong_title_match_may_claim_a_window_from_another_desktop()
    {
        var plan = Plan(Desktop("Work"), Desktop("Admin", Entry("Notes", "notes.exe", RightPlacement, "Notes")));
        var windows = new[] { Window(1, "Notes", "notes.exe", 1, LeftPlacement) };

        var matches = Align.MatchPlanWindows(plan, windows, NoSubs);

        Assert.Equal(1, matches[0].Window?.Handle);
    }

    [Fact]
    public void A_process_name_alone_is_too_weak_to_pull_a_window_across_desktops()
    {
        var plan = Plan(Desktop("Work"), Desktop("Admin", Entry("Notes", "notes.exe", RightPlacement, "Notes")));
        var windows = new[] { Window(1, "Some other window", "notes.exe", 1, LeftPlacement) };

        var matches = Align.MatchPlanWindows(plan, windows, NoSubs);

        Assert.Null(matches[0].Window);
    }

    [Fact]
    public void An_entry_already_in_place_needs_no_move()
    {
        var entry = Entry("Notes", "notes.exe", LeftPlacement, "Notes");
        var match = new EntryMatch(1, Desktop("Work", entry), entry, Window(1, "Notes", "notes.exe", 1, LeftPlacement));

        var decision = Align.Decide(match, Context());

        Assert.Equal(AlignOutcome.InPlace, decision.Outcome);
        Assert.False(decision.MoveDesktop);
        Assert.False(decision.MovePlacement);

        // Window with inferred=null but matching Bounds
        var matchBounds = new EntryMatch(1, Desktop("Work", entry), entry, Window(2, "Notes", "notes.exe", 1, placement: null, bounds: Left));
        var decBounds = Align.Decide(matchBounds, Context());
        Assert.Equal(AlignOutcome.InPlace, decBounds.Outcome);

        // Window with inferred=null and differing Bounds
        var matchDiffBounds = new EntryMatch(1, Desktop("Work", entry), entry, Window(3, "Notes", "notes.exe", 1, placement: null, bounds: new Rect(5000, 5000, 10, 10)));
        var decDiffBounds = Align.Decide(matchDiffBounds, Context());
        Assert.Equal(AlignOutcome.Move, decDiffBounds.Outcome);
        Assert.True(decDiffBounds.MovePlacement);
    }

    [Fact]
    public void An_entry_on_the_wrong_desktop_is_to_move()
    {
        var entry = Entry("Notes", "notes.exe", RightPlacement, "Notes");
        var match = new EntryMatch(2, Desktop("Work"), entry, Window(1, "Notes", "notes.exe", 1, LeftPlacement));

        var decision = Align.Decide(match, Context(desktopCount: 2));

        Assert.Equal(AlignOutcome.Move, decision.Outcome);
        Assert.True(decision.MoveDesktop);
        Assert.True(decision.MovePlacement);
    }

    [Theory]
    [InlineData(ShowState.Minimized, SkipReason.Minimized)]
    [InlineData(ShowState.Maximized, SkipReason.Maximized)]
    public void A_window_the_user_is_using_is_skipped(ShowState state, SkipReason reason)
    {
        var entry = Entry("Notes", "notes.exe", RightPlacement, "Notes");
        var match = new EntryMatch(2, Desktop("Work"), entry, Window(1, "Notes", "notes.exe", 1, LeftPlacement));

        var decision = Align.Decide(match, Context(states: new Dictionary<long, ShowState> { [1] = state }));

        Assert.Equal(AlignOutcome.Skipped, decision.Outcome);
        Assert.Equal(reason, decision.SkipReason);
    }

    [Fact]
    public void The_foreground_window_is_skipped()
    {
        var entry = Entry("Notes", "notes.exe", RightPlacement, "Notes");
        var match = new EntryMatch(2, Desktop("Work"), entry, Window(1, "Notes", "notes.exe", 1, LeftPlacement));

        var decision = Align.Decide(match, Context(foreground: 1));

        Assert.Equal(SkipReason.Foreground, decision.SkipReason);
    }

    [Fact]
    public void A_desktop_the_machine_does_not_have_is_skipped()
    {
        var entry = Entry("Notes", "notes.exe", RightPlacement, "Notes");
        var match = new EntryMatch(2, Desktop("Work"), entry, Window(1, "Notes", "notes.exe", 1, LeftPlacement));

        var decision = Align.Decide(match, Context(desktopCount: 1));

        Assert.Equal(SkipReason.NoDesktop, decision.SkipReason);
    }

    [Fact]
    public void A_mismatched_live_desktop_blocks_alignment()
    {
        Assert.Equal(
            "desktop 2 is named 'Office' but the plan expects 'Work'",
            Align.DesktopNameMismatch(Plan(Desktop("Personal"), Desktop("Work")), ["Personal", "Office"]));
        Assert.Null(Align.DesktopNameMismatch(Plan(Desktop("Personal"), Desktop("Work")), ["Personal", ""]));
    }

    [Fact]
    public void A_dry_run_decides_and_moves_nothing()
    {
        var entry = Entry("Notes", "notes.exe", RightPlacement, "Notes");
        var plan = Plan(Desktop("Work"), Desktop("Admin", entry));
        var ports = new FakePorts(plan, [Window(1, "Notes", "notes.exe", 1, LeftPlacement)]);

        var report = Align.Run(plan, ports, NoSubs, dryRun: true, reconcileDesktops: true);

        Assert.Empty(ports.Moved);
        Assert.Equal(1, report.Count(AlignOutcome.Move));
        Assert.Equal("align (dry run): 1 to move, 0 in place", Align.SummaryLine(report, dryRun: true));
    }

    [Fact]
    public void A_run_moves_the_window_and_puts_the_view_back()
    {
        var entry = Entry("Notes", "notes.exe", RightPlacement, "Notes");
        var plan = Plan(Desktop("Work"), Desktop("Admin", entry));
        var ports = new FakePorts(plan, [Window(1, "Notes", "notes.exe", 1, LeftPlacement)]);

        var report = Align.Run(plan, ports, NoSubs, reconcileDesktops: true);

        Assert.Equal([1], ports.Moved);
        Assert.Equal([RightPlacement], ports.Placed);
        Assert.True(report.ViewRestored);
        Assert.Equal(1, report.Count(AlignOutcome.Moved));
        Assert.StartsWith("align: 1 moved, 0 in place", Align.SummaryLine(report, dryRun: false), StringComparison.Ordinal);
    }

    [Fact]
    public void A_drifting_name_blocks_instead_of_reconciling()
    {
        var plan = Plan(Desktop("Personal"), Desktop("Work"));
        var ports = new FakePorts(plan, []) { Names = ["Personal", "Office"] };

        var report = Align.Run(plan, ports, NoSubs);

        Assert.Equal("desktop 2 is named 'Office' but the plan expects 'Work'; run workspace restore", report.Blocked);
        Assert.Empty(report.Decisions);
    }

    [Fact]
    public void Summary_counts_missing_skipped_and_failed()
    {
        var entry = Entry("Notes", "notes.exe", LeftPlacement, "Notes");
        var report = new AlignReport(
        [
            new(1, "Work", entry, null, AlignOutcome.Missing),
            new(1, "Work", entry, null, AlignOutcome.Skipped) { SkipReason = SkipReason.Minimized },
            new(1, "Work", entry, null, AlignOutcome.Skipped) { SkipReason = SkipReason.Foreground },
            new(1, "Work", entry, null, AlignOutcome.Failed) { Detail = "refused" },
        ]);

        Assert.Equal(
            "align: 0 moved, 0 in place, 1 missing, 2 skipped (1 foreground, 1 minimized), 1 failed",
            Align.SummaryLine(report, dryRun: false));
    }

    [Fact]
    public void AlignDecision_Describe_covers_all_outcomes_and_reasons()
    {
        var entry = Entry("Notes", "notes.exe", LeftPlacement, "Notes");
        var win = Window(1, "Notes", "notes.exe", 2, LeftPlacement);

        var d1 = new AlignDecision(1, "Work", entry, win, AlignOutcome.Moved)
        {
            MoveDesktop = true,
            MovePlacement = true,
            Detail = "ok",
        };
        Assert.Contains("moved: Work/Notes", d1.Describe(), StringComparison.Ordinal);
        Assert.Contains("desktop 2 -> 1", d1.Describe(), StringComparison.Ordinal);

        var d2 = new AlignDecision(1, "Work", entry, win, AlignOutcome.Skipped)
        {
            SkipReason = SkipReason.Maximized,
        };
        Assert.Contains("maximized", d2.Describe(), StringComparison.Ordinal);

        var d3 = new AlignDecision(1, "Work", entry, null, AlignOutcome.Skipped)
        {
            SkipReason = SkipReason.NoDesktop,
        };
        Assert.Contains("desktop not present", d3.Describe(), StringComparison.Ordinal);

        var d4 = new AlignDecision(1, "Work", entry, null, AlignOutcome.Skipped)
        {
            SkipReason = SkipReason.Closed,
        };
        Assert.Contains("closed", d4.Describe(), StringComparison.Ordinal);

        Assert.Equal("to move", AlignDecision.Wire(AlignOutcome.Move));
        Assert.Equal("in place", AlignDecision.Wire(AlignOutcome.InPlace));
        Assert.Equal("missing", AlignDecision.Wire(AlignOutcome.Missing));
        Assert.Equal("failed", AlignDecision.Wire(AlignOutcome.Failed));
        Assert.Equal("minimized", AlignDecision.Wire(SkipReason.Minimized));
        Assert.Equal("foreground", AlignDecision.Wire(SkipReason.Foreground));
    }

    [Fact]
    public void Run_with_dry_run_and_desktop_reconciliation()
    {
        var plan = new WorkspacePlan
        {
            Desktops = [new("Work"), new("Personal")],
            RetiredDesktopNames = ["OldDesktop"],
        };
        var ports = new FakePorts(plan, []) { Names = ["Work", "OldDesktop"] };

        var report = Align.Run(plan, ports, NoSubs, dryRun: true, reconcileDesktops: true);
        Assert.NotNull(report.DesktopReconciliation);
        Assert.True(report.DesktopReconciliation.DryRun);
    }

    [Fact]
    public void Run_filters_by_target_desktop()
    {
        var notes = Entry("Notes", "notes.exe", LeftPlacement, "Notes");
        var plan = Plan(Desktop("Work", notes), Desktop("Personal", Entry("PersonalNotes", "notes.exe", RightPlacement)));
        var ports = new FakePorts(plan, [Window(1, "Notes", "notes.exe", 1, LeftPlacement)]);

        var report = Align.Run(plan, ports, NoSubs, targetDesktop: "Work");
        Assert.Single(report.Decisions);
        Assert.Equal("Work", report.Decisions[0].DesktopName);
    }

    [Fact]
    public void Run_applies_desktop_reconciliation_live()
    {
        var plan = new WorkspacePlan
        {
            Desktops = [new("Work"), new("Personal"), new("Ops")],
            RetiredDesktopNames = ["OldDesktop"],
        };
        var ports = new FakePorts(plan, [])
        {
            Names = ["OldDesktop", "OriginalWork"],
            RenameFunc = (index, name) => index != 2, // fail rename for index 2
        };

        var logs = new List<string>();
        var report = Align.Run(plan, ports, NoSubs, dryRun: false, reconcileDesktops: true, log: logs.Add);

        Assert.NotNull(report.DesktopReconciliation);
        Assert.False(report.DesktopReconciliation.DryRun);
        Assert.Single(report.DesktopReconciliation.Retired);
        Assert.Contains("OldDesktop", report.DesktopReconciliation.Retired);
        Assert.Single(report.DesktopReconciliation.FailedRenames);
        Assert.NotEmpty(logs);
    }

    [Fact]
    public void Run_applies_decisions_live_moving_and_placing_windows()
    {
        var notes = Entry("Notes", "notes.exe", LeftPlacement, "Notes");
        var plan = Plan(Desktop("Work", notes));
        var ports = new FakePorts(plan, [Window(1, "Notes", "notes.exe", 2, RightPlacement)]);

        var report = Align.Run(plan, ports, NoSubs, dryRun: false);

        Assert.Single(report.Decisions);
        Assert.Equal(AlignOutcome.Moved, report.Decisions[0].Outcome);
        Assert.Contains(1L, ports.Moved);
        Assert.Single(ports.Placed);
        Assert.True(ports.View);
    }

    [Fact]
    public void Apply_decision_skips_when_window_is_closed()
    {
        var notes = Entry("Notes", "notes.exe", LeftPlacement, "Notes");
        var plan = Plan(Desktop("Work", notes));
        var ports = new FakePorts(plan, [Window(1, "Notes", "notes.exe", 2, RightPlacement)])
        {
            IsWindowFunc = _ => false,
        };

        var report = Align.Run(plan, ports, NoSubs, dryRun: false);

        Assert.Single(report.Decisions);
        Assert.Equal(AlignOutcome.Skipped, report.Decisions[0].Outcome);
        Assert.Equal(SkipReason.Closed, report.Decisions[0].SkipReason);
    }

    [Fact]
    public void Apply_decision_skips_when_window_is_in_foreground()
    {
        var notes = Entry("Notes", "notes.exe", LeftPlacement, "Notes");
        var plan = Plan(Desktop("Work", notes));
        var ports = new FakePorts(plan, [Window(1, "Notes", "notes.exe", 2, RightPlacement)])
        {
            ForegroundHwndFunc = () => 1L,
        };

        var report = Align.Run(plan, ports, NoSubs, dryRun: false);

        Assert.Single(report.Decisions);
        Assert.Equal(AlignOutcome.Skipped, report.Decisions[0].Outcome);
        Assert.Equal(SkipReason.Foreground, report.Decisions[0].SkipReason);
    }

    [Theory]
    [InlineData(ShowState.Minimized, SkipReason.Minimized)]
    [InlineData(ShowState.Maximized, SkipReason.Maximized)]
    public void Apply_decision_skips_when_window_is_minimized_or_maximized(ShowState state, SkipReason reason)
    {
        var notes = Entry("Notes", "notes.exe", LeftPlacement, "Notes");
        var plan = Plan(Desktop("Work", notes));
        var ports = new FakePorts(plan, [Window(1, "Notes", "notes.exe", 2, RightPlacement)])
        {
            ShowStateFunc = _ => state,
        };

        var report = Align.Run(plan, ports, NoSubs, dryRun: false);

        Assert.Single(report.Decisions);
        Assert.Equal(AlignOutcome.Skipped, report.Decisions[0].Outcome);
        Assert.Equal(reason, report.Decisions[0].SkipReason);
    }

    [Fact]
    public void Apply_decision_reports_failed_when_move_or_place_fails()
    {
        var notes = Entry("Notes", "notes.exe", LeftPlacement, "Notes");
        var plan = Plan(Desktop("Work", notes));

        // Test desktop move failure
        var moveFailPorts = new FakePorts(plan, [Window(1, "Notes", "notes.exe", 2, RightPlacement)])
        {
            MoveToDesktopFunc = (_, _) => "Desktop move rejected",
        };
        var r1 = Align.Run(plan, moveFailPorts, NoSubs, dryRun: false);
        Assert.Equal(AlignOutcome.Failed, r1.Decisions[0].Outcome);
        Assert.Equal("Desktop move rejected", r1.Decisions[0].Detail);

        // Test placement failure
        var placeFailPorts = new FakePorts(plan, [Window(2, "Notes", "notes.exe", 1, RightPlacement)])
        {
            PlaceFunc = (_, _) => "Placement failed",
        };
        var r2 = Align.Run(plan, placeFailPorts, NoSubs, dryRun: false);
        Assert.Equal(AlignOutcome.Failed, r2.Decisions[0].Outcome);
        Assert.Equal("Placement failed", r2.Decisions[0].Detail);
    }

    [Fact]
    public void Describe_formats_dry_run_and_live_reports_with_full_reconciliation()
    {
        var entry = Entry("Notes", "notes.exe", LeftPlacement);
        var moveDecision = new AlignDecision(1, "Work", entry, Window(1, "Notes", "notes.exe", 2), AlignOutcome.Move);
        var inPlaceDecision = new AlignDecision(1, "Work", entry, Window(2, "Notes", "notes.exe", 1), AlignOutcome.InPlace);

        var dryReconPlan = new DesktopReconciliationPlan(["Ops", "Dev"], ["OldDev", "StaleDesktop"])
        {
            ToCreate = [new(3, "Ops")],
            ToRename = [new(2, "OldDev", "Dev")],
            ToRetire = ["StaleDesktop"],
        };
        var dryReport = new AlignReport([moveDecision, inPlaceDecision])
        {
            DesktopReconciliation = new(dryReconPlan) { DryRun = true },
        };
        var dryDesc = Align.SummaryLine(dryReport, dryRun: true);
        Assert.Contains("align (dry run)", dryDesc, StringComparison.Ordinal);
        Assert.Contains("1 to retire", dryDesc, StringComparison.Ordinal);
        Assert.Contains("1 to create", dryDesc, StringComparison.Ordinal);
        Assert.Contains("1 to rename", dryDesc, StringComparison.Ordinal);
        Assert.Contains("1 to move", dryDesc, StringComparison.Ordinal);
        Assert.Contains("1 in place", dryDesc, StringComparison.Ordinal);

        var liveMoved = new AlignDecision(1, "Work", entry, Window(1, "Notes", "notes.exe", 1), AlignOutcome.Moved);
        var liveRecon = new DesktopReconciliationResult(dryReconPlan)
        {
            Retired = ["StaleDesktop"],
            Created = [new(3, "Ops")],
            Renamed = [new(2, "OldDev", "Dev", true, null)],
        };
        var liveReport = new AlignReport([liveMoved, inPlaceDecision])
        {
            DesktopReconciliation = liveRecon,
        };
        var liveDesc = Align.SummaryLine(liveReport, dryRun: false);
        Assert.Contains("align: desktops:", liveDesc, StringComparison.Ordinal);
        Assert.Contains("1 retired", liveDesc, StringComparison.Ordinal);
        Assert.Contains("1 created", liveDesc, StringComparison.Ordinal);
        Assert.Contains("1 renamed", liveDesc, StringComparison.Ordinal);
        Assert.Contains("1 moved", liveDesc, StringComparison.Ordinal);

        var blockedReport = new AlignReport([]) { Blocked = "Desktop count mismatch" };
        Assert.Equal("align: skipped (Desktop count mismatch)", Align.SummaryLine(blockedReport, dryRun: false));
    }

    [Fact]
    public void Terminal_entry_matches_only_when_window_title_matches()
    {
        var termEntry = new LaunchEntry("Shell")
        {
            Type = LaunchEntry.Terminal,
            WindowTitle = "rig-terminal",
            WindowProcessName = "windowsterminal.exe",
            Placement = LeftPlacement,
        };
        var plan = Plan(Desktop("Work"), Desktop("Admin", termEntry));

        // Matching window title can pull across desktops
        var matchWin = Window(1, "rig-terminal - session", "windowsterminal.exe", 1, LeftPlacement);
        var m1 = Align.MatchPlanWindows(plan, [matchWin], NoSubs);
        Assert.NotNull(m1[0].Window);

        // Non-matching window title cannot pull terminal across desktops
        var otherWin = Window(2, "other-terminal", "windowsterminal.exe", 1, LeftPlacement);
        var m2 = Align.MatchPlanWindows(plan, [otherWin], NoSubs);
        Assert.Null(m2[0].Window);

        // Terminal with empty WindowTitle never matches across desktops
        var emptyTitleTerm = new LaunchEntry("Shell")
        {
            Type = LaunchEntry.Terminal,
            WindowTitle = "",
            WindowProcessName = "windowsterminal.exe",
        };
        var m3 = Align.MatchPlanWindows(Plan(Desktop("Work"), Desktop("Admin", emptyTitleTerm)), [matchWin], NoSubs);
        Assert.Null(m3[0].Window);
    }

    [Fact]
    public void Desktop_name_mismatch_blocks_live_align_when_reconcile_disabled()
    {
        var plan = new WorkspacePlan
        {
            Desktops = [new("ExpectedWork"), new("ExpectedAdmin")],
        };
        var ports = new FakePorts(plan, []) { Names = ["DifferentName1", "DifferentName2"] };

        var report = Align.Run(plan, ports, NoSubs, dryRun: false, reconcileDesktops: false);
        Assert.NotNull(report.Blocked);
        Assert.Contains("run workspace restore", report.Blocked, StringComparison.Ordinal);
    }

    private static AlignContext Context(
        int desktopCount = 2,
        IReadOnlyDictionary<long, ShowState>? states = null,
        long? foreground = null) =>
        new(
            desktopCount,
            placement => placement.Screen == 1 && placement.Layout.Slot == 1 ? Left : Right,
            states ?? new Dictionary<long, ShowState>(),
            foreground);

    private sealed class FakePorts : IAlignPorts
    {
        public FakePorts(WorkspacePlan plan, IReadOnlyList<LiveWindow> windows)
        {
            Windows = windows;
            Names = [.. plan.Desktops.Select(desktop => desktop.Name)];
        }

        public IReadOnlyList<LiveWindow> Windows { get; }

        public List<string> Names { get; set; }

        public List<long> Moved { get; } = [];

        public List<PlacementSpec?> Placed { get; } = [];

        public bool View { get; private set; }

        public Func<long, bool>? IsWindowFunc { get; set; }

        public Func<long?>? ForegroundHwndFunc { get; set; }

        public Func<long, ShowState>? ShowStateFunc { get; set; }

        public Func<long, int, string?>? MoveToDesktopFunc { get; set; }

        public Func<long, Rect, string?>? PlaceFunc { get; set; }

        public Func<int, string, bool>? RenameFunc { get; set; }

        public IReadOnlyList<string> DesktopNames() => Names;

        public int DesktopCount() => Names.Count;

        public int? CurrentDesktop() => 1;

        public long? ForegroundHwnd() => ForegroundHwndFunc != null ? ForegroundHwndFunc() : null;

        public long? ConsoleHwnd() => null;

        public IReadOnlyList<LiveWindow> Inventory(IReadOnlySet<long> excluded) => [.. Windows.Where(window => !excluded.Contains(window.Handle))];

        public ShowState ShowState(long hwnd) => ShowStateFunc != null ? ShowStateFunc(hwnd) : Rig.Core.Desktop.ShowState.Normal;

        public Rect TargetRect(PlacementSpec placement) => placement.Layout.Slot == 1 ? Left : Right;

        public bool IsWindow(long hwnd) => IsWindowFunc != null ? IsWindowFunc(hwnd) : Windows.Any(window => window.Handle == hwnd);

        public string? MoveToDesktop(long hwnd, int desktopIndex)
        {
            Moved.Add(hwnd);
            return MoveToDesktopFunc != null ? MoveToDesktopFunc(hwnd, desktopIndex) : null;
        }

        public string? Place(long hwnd, Rect rect)
        {
            Placed.Add(new PlacementSpec(1, new("halves", rect.Left == Left.Left ? 1 : 2, "vertical")));
            return PlaceFunc != null ? PlaceFunc(hwnd, rect) : null;
        }

        public bool RestoreView(int? desktop, long? foreground, IReadOnlySet<long> movedHwnds)
        {
            View = desktop == 1;
            return View;
        }

        public void EnsureDesktopCount(int count, double settleSeconds = 0.65)
        {
            while (Names.Count < count)
            {
                Names.Add($"Desktop {Names.Count + 1}");
            }
        }

        public bool RenameDesktop(int desktopIndex, string name)
        {
            if (RenameFunc != null)
            {
                var ok = RenameFunc(desktopIndex, name);
                if (ok)
                {
                    Names[desktopIndex - 1] = name;
                }
                return ok;
            }

            Names[desktopIndex - 1] = name;
            return true;
        }

        public IReadOnlyList<string> RetireDesktops(int keepCount, IReadOnlyList<string> retiredNames)
        {
            Names.RemoveAll(name => retiredNames.Contains(name, StringComparer.OrdinalIgnoreCase));
            return retiredNames;
        }
    }
}
