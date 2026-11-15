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

        public IReadOnlyList<string> DesktopNames() => Names;

        public int DesktopCount() => Names.Count;

        public int? CurrentDesktop() => 1;

        public long? ForegroundHwnd() => null;

        public long? ConsoleHwnd() => null;

        public IReadOnlyList<LiveWindow> Inventory(IReadOnlySet<long> excluded) => [.. Windows.Where(window => !excluded.Contains(window.Handle))];

        public ShowState ShowState(long hwnd) => Rig.Core.Desktop.ShowState.Normal;

        public Rect TargetRect(PlacementSpec placement) => placement.Layout.Slot == 1 ? Left : Right;

        public bool IsWindow(long hwnd) => Windows.Any(window => window.Handle == hwnd);

        public string? MoveToDesktop(long hwnd, int desktopIndex)
        {
            Moved.Add(hwnd);
            return null;
        }

        public string? Place(long hwnd, Rect rect)
        {
            Placed.Add(new PlacementSpec(1, new("halves", rect.Left == Left.Left ? 1 : 2, "vertical")));
            return null;
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
