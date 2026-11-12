using Rig.Core.Desktop;

namespace Rig.Core.Tests.Desktop;

/// <summary>
/// The plan against the live workspace, screen by screen. Replaces the WorkspaceDiff and truncate_text halves of
/// <c>tests/desktop_manager/test_visualization_helpers.py</c>; the mixed-drift render was checked line for line against
/// <c>diff_workspace</c>.
/// </summary>
public sealed class WorkspaceDiffTests
{
    private static readonly Dictionary<string, string> NoSubs = [];

    private static PlacementSpec Half(int screen, int slot) => new(screen, new("halves", slot, "vertical"));

    private static LaunchEntry App(string name, string exe, string title, PlacementSpec placement, bool disabled = false) =>
        new(name) { Path = exe, WindowProcessName = exe, WindowTitle = title, Placement = placement, Disabled = disabled };

    private static WorkspaceDiff One(ScreenDiff screen) => new([new DesktopDiff("Work", [screen])]);

    public static TheoryData<string, bool, bool> Fields => new()
    {
        { "matched", true, false },
        { "missing", false, true },
        { "misplaced", false, true },
        { "extra", false, false },
        { "borrowed", false, false },
    };

    [Theory]
    [MemberData(nameof(Fields))]
    public void Cleanliness_and_drift_read_the_fields_not_the_text(string field, bool clean, bool fixable)
    {
        string[] value = ["Notes <- extra: missing: notes [notes]"];
        var screen = field switch
        {
            "matched" => new ScreenDiff(1) { Matched = value },
            "missing" => new ScreenDiff(1) { Missing = value },
            "misplaced" => new ScreenDiff(1) { Misplaced = value },
            "extra" => new ScreenDiff(1) { Extra = value },
            _ => new ScreenDiff(1) { Borrowed = value },
        };

        var diff = One(screen);

        Assert.Equal(clean, diff.IsClean);
        Assert.Equal(fixable, diff.HasFixableDrift);
    }

    [Fact]
    public void Renders_the_restore_diff_layout()
    {
        var diff = new WorkspaceDiff(
        [
            new DesktopDiff("Work", [new ScreenDiff(1) { Planned = ["Notes"], Missing = ["Notes"], Extra = ["Slack [slack]"] }]),
            new DesktopDiff("Admin", [new ScreenDiff(2)]),
        ]);

        Assert.Equal(
            [
                "Workspace Diff", string.Empty, "Work", "Screen 1", "  planned: Notes", "  actual:  -", "  missing: Notes",
                "  extra:   Slack [slack]", string.Empty, "Admin", "Screen 2", "  planned: -", "  actual:  -", "  status:  ok",
            ],
            diff.Render().Split('\n'));
    }

    [Fact]
    public void Changed_since_keeps_only_the_screens_that_changed()
    {
        var okAdmin = new DesktopDiff("Admin", [new ScreenDiff(1)]);
        var previous = new WorkspaceDiff([new DesktopDiff("Work", [new ScreenDiff(1), new ScreenDiff(2)]), okAdmin]);
        var drifted = new WorkspaceDiff([new DesktopDiff("Work", [new ScreenDiff(1), new ScreenDiff(2) { Missing = ["Notes"] }]), okAdmin]);

        var changed = drifted.ChangedSince(previous);

        Assert.Equal(new WorkspaceDiff([new DesktopDiff("Work", [new ScreenDiff(2) { Missing = ["Notes"] }])]), changed);
    }

    [Fact]
    public void Changed_since_returns_the_whole_diff_without_a_previous_or_a_change()
    {
        var diff = One(new ScreenDiff(1));

        Assert.Same(diff, diff.ChangedSince(null));
        Assert.Same(diff, diff.ChangedSince(One(new ScreenDiff(1))));
    }

    [Fact]
    public void Equal_diffs_compare_by_content()
    {
        var left = One(new ScreenDiff(1) { Planned = ["A"] });
        var right = One(new ScreenDiff(1) { Planned = ["A"] });

        Assert.Equal(left, right);
        Assert.Equal(left.GetHashCode(), right.GetHashCode());
        Assert.Equal(left.Desktops[0].GetHashCode(), right.Desktops[0].GetHashCode());
        Assert.Equal(left.Desktops[0].Screens[0].GetHashCode(), right.Desktops[0].Screens[0].GetHashCode());
        Assert.NotEqual(left, One(new ScreenDiff(1) { Planned = ["B"] }));
        Assert.False(left.Equals(null));
        Assert.False(left.Desktops[0].Equals(null));
        Assert.False(left.Desktops[0].Screens[0].Equals(null));
    }

    [Theory]
    [InlineData("hello", 10, "hello")]
    [InlineData("hello", 5, "hello")]
    [InlineData("hello world", 8, "hello...")]
    public void Truncate_text_cuts_long_values_with_an_ellipsis(string value, int limit, string expected) =>
        Assert.Equal(expected, WorkspaceDiff.TruncateText(value, limit));

    [Fact]
    public void Truncated_text_is_exactly_the_limit()
    {
        for (var limit = 4; limit < 20; limit++)
        {
            Assert.Equal(limit, WorkspaceDiff.TruncateText(new string('a', 30), limit).Length);
        }
    }

    [Theory]
    [InlineData("Inbox", 7, "chrome.exe", "Inbox [chrome pid=7]")]
    [InlineData("Inbox", null, null, "Inbox")]
    [InlineData("Inbox", 7, null, "Inbox pid=7")]
    [InlineData("", 7, @"C:\Tools\x.tar.gz", "x.tar pid=7")]
    [InlineData("", null, ".bashrc", ".bashrc")]
    [InlineData("", null, null, "<unknown window>")]
    public void A_window_label_names_its_title_and_process(string title, int? pid, string? process, string expected) =>
        Assert.Equal(expected, WorkspaceDiff.WindowLabel(new(title, pid, process)));

    [Fact]
    public void A_long_window_label_is_cut_to_sixty_four()
    {
        var label = WorkspaceDiff.WindowLabel(new(new string('t', 80), 1, "code.exe"));

        Assert.Equal(64, label.Length);
        Assert.EndsWith("...", label, StringComparison.Ordinal);
    }

    [Fact]
    public void Of_reports_every_kind_of_drift_as_the_python_does()
    {
        var (plan, actual) = MixedDrift();

        var diff = WorkspaceDiff.Of(plan, actual, NoSubs);

        Assert.Equal(
            [
                "Workspace Diff",
                string.Empty,
                "Work",
                "Screen 1",
                "  planned: Notes, Mail",
                "  actual:  Slack [slack]",
                "  misplaced: Notes -> Notes [notepad pid=11] on screen 2 (planned 1)",
                "  missing: Mail",
                "  extra:   Slack [slack]",
                "Screen 2",
                "  planned: Browser",
                "  actual:  Notes [notepad pid=11], Inbox [chrome pid=12], Calc [calc]",
                "  matched: Browser <- Inbox [chrome pid=12]",
                "  borrowed: Notes [notepad pid=11] -> Notes on screen 1",
                "  extra:   Calc [calc]",
                string.Empty,
                "Admin",
                "Screen 1",
                "  planned: Solo",
                "  actual:  -",
                "  missing: Solo",
            ],
            diff.Render().Split('\n'));
        Assert.False(diff.IsClean);
        Assert.True(diff.HasFixableDrift);
    }

    [Fact]
    public void Only_entries_matched_on_their_planned_screen_count_as_satisfied()
    {
        var (plan, actual) = MixedDrift();

        Assert.Equal(["Browser"], WorkspaceDiff.EntriesMatchedOnPlannedScreen(plan.Desktops[0], actual.Desktops[0], NoSubs));
    }

    [Fact]
    public void A_tie_goes_to_the_later_entry()
    {
        var first = App("First", "code.exe", "Repo", Half(1, 1));
        var second = App("Second", "code.exe", "Repo", Half(1, 2));
        var planned = new DesktopPlan("Work") { Launch = [first, second] };
        var actual = new DesktopPlan("Desktop 1") { CapturedWindows = [new("Repo", ProcessName: "code.exe", ScreenNumber: 1)] };

        Assert.Equal(["Second"], WorkspaceDiff.EntriesMatchedOnPlannedScreen(planned, actual, NoSubs));
    }

    private static (WorkspacePlan Plan, WorkspacePlan Actual) MixedDrift()
    {
        var work = new DesktopPlan("Work")
        {
            Launch =
            [
                App("Notes", "notepad.exe", "Notes", Half(1, 1)),
                App("Mail", "outlook.exe", "Mail", Half(1, 2)),
                App("Browser", "chrome.exe", "Inbox", Half(2, 1)),
                App("Off", "calc.exe", "Calc", Half(2, 2), disabled: true),
            ],
        };
        var admin = new DesktopPlan("Admin") { Launch = [new LaunchEntry("Solo") { Path = "x.exe", Placement = Half(1, 1) }] };
        var live = new DesktopPlan("Desktop 1")
        {
            CapturedWindows =
            [
                new("Notes", 11, "notepad.exe", ScreenNumber: 2),
                new("Inbox", 12, "chrome.exe", ScreenNumber: 2),
                new("Slack", ProcessName: "slack.exe", ScreenNumber: 1),
                new("Calc", ProcessName: "calc.exe", ScreenNumber: 2),
                new(string.Empty),
            ],
        };
        return (new WorkspacePlan { Desktops = [work, admin] }, new WorkspacePlan { Desktops = [live] });
    }
}
