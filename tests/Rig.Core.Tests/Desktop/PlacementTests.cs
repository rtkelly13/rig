using Rig.Core.Desktop;

namespace Rig.Core.Tests.Desktop;

/// <summary>
/// Checking a plan's entries and placements, and fitting columns to the screens. Replaces the placement half of
/// <c>tests/desktop_manager/test_placement_pure.py</c>.
/// </summary>
public sealed class PlacementTests
{
    private static readonly Dictionary<string, string> NoSubstitutions = [];

    /// <summary>A layout and the one error reported for it, or none.</summary>
    public static TheoryData<PlacementLayout, string?> Layouts => new()
    {
        { new("full"), null },
        { new("thirds", 3), null },
        { new("quarters", 4), null },
        { new("columns", 4, MinWidth: 640), null },
        { new("bogus"), "D/E: invalid layout mode `bogus`" },
        { new("halves", 3, "vertical"), "D/E: halves slot must be 1 or 2" },
        { new("halves", 1), "D/E: halves orientation must be vertical or horizontal" },
        { new("thirds", 4), "D/E: thirds slot must be 1, 2, or 3" },
        { new("quarters", 5), "D/E: quarters slot must be 1, 2, 3, or 4" },
        { new("two-thirds", 3), "D/E: two-thirds slot must be 1 or 2" },
        { new("thirds", 1, "vertical"), "D/E: orientation only applies to halves" },
        { new("thirds", 1, MinWidth: 800), "D/E: min_width only applies to columns" },
        { new("full", MinWidth: 800), "D/E: min_width only applies to columns" },
        { new("halves", 1, "vertical", MinWidth: 800), "D/E: min_width only applies to columns" },
    };

    [Theory]
    [MemberData(nameof(Layouts))]
    public void Each_layout_is_checked_against_its_mode(PlacementLayout layout, string? error) =>
        Assert.Equal(error is null ? [] : [error], Placement.ValidatePlacementSpec(new(1, layout), "D", "E"));

    [Fact]
    public void A_columns_layout_needs_a_positive_slot_and_width() =>
        Assert.Equal(
            ["D/E: columns slot must be >= 1", "D/E: columns min_width must be >= 1"],
            Placement.ValidatePlacementSpec(new(1, new("columns", 0, MinWidth: 0)), "D", "E"));

    [Fact]
    public void Columns_cannot_split_a_slot() =>
        Assert.Equal(
            ["D/E: columns applies to the screen, not a subdivide"],
            Placement.ValidatePlacementSpec(new(1, new("full"), new("columns", 3, MinWidth: 800)), "D", "E"));

    [Fact]
    public void A_split_is_checked_like_a_layout() =>
        Assert.Equal(["D/E: halves slot must be 1 or 2"], Placement.ValidatePlacementSpec(new(1, new("full"), new("halves", 3, "vertical")), "D", "E"));

    [Fact]
    public void The_screen_must_exist_and_fit_the_monitors_when_their_count_is_known()
    {
        Assert.Equal(["D/E: placement screen must be >= 1"], Placement.ValidatePlacementSpec(new(0, new("full")), "D", "E"));
        Assert.Equal(["D/E: placement screen 3 exceeds detected monitor count 2"], Placement.ValidatePlacementSpec(new(3, new("full")), "D", "E", monitorCount: 2));
        Assert.Empty(Placement.ValidatePlacementSpec(new(3, new("full")), "D", "E"));
    }

    [Fact]
    public void A_placement_formats_on_one_line()
    {
        Assert.Equal("screen 1: full#1", Placement.FormatPlacement(new(1, new("full"))));
        Assert.Equal("screen 1: thirds#2", Placement.FormatPlacement(new(1, new("thirds", 2))));
        Assert.Equal(
            "screen 2: halves#1/vertical -> halves#2/horizontal",
            Placement.FormatPlacement(new(2, new("halves", 1, "vertical"), new("halves", 2, "horizontal"))));
        Assert.Equal("screen 1: full#1 -> thirds#3", Placement.FormatPlacement(new(1, new("full"), new("thirds", 3))));
    }

    [Fact]
    public void The_screen_count_is_the_expected_then_the_detected_then_the_highest_placed()
    {
        var plan = Plan(Desktop("Work", Entry("App", 2)));

        Assert.Equal(3, Placement.ResolveScreenCount(plan, 3, detectedScreenCount: 4));
        Assert.Equal(0, Placement.ResolveScreenCount(plan, -1));
        Assert.Equal(4, Placement.ResolveScreenCount(plan, detectedScreenCount: 4));
        Assert.Equal(2, Placement.ResolveScreenCount(plan));
        Assert.Equal(0, Placement.ResolveScreenCount(new WorkspacePlan()));
    }

    [Fact]
    public void A_plan_whose_screens_are_all_connected_or_whose_live_count_is_unknown_does_not_fold()
    {
        var plan = Plan(Desktop("Work", Entry("A", 1), Entry("B", 3), new LaunchEntry("Unplaced")));

        Assert.Same(plan, Placement.FoldScreens(plan, 3));
        Assert.Same(plan, Placement.FoldScreens(plan, 4));
        Assert.Same(plan, Placement.FoldScreens(plan, 0));
        Assert.Null(Placement.DescribeFold(plan));
    }

    [Fact]
    public void Missing_screens_fold_into_the_last_connected_one_as_columns_in_screen_order()
    {
        var plan = Plan(Desktop("Work", Entry("Orca", 1), Entry("Chrome", 2), Entry("Rig", 3, new("halves", 1, "vertical")), Entry("Links", 3, new("halves", 2, "vertical"))));

        var folded = Placement.FoldScreens(plan, 2).Desktops[0].Launch.Select(entry => entry.Placement!).ToList();

        Assert.Equal(new PlacementSpec(1, new("full")), folded[0]);
        Assert.Equal([2, 2, 2], folded.Skip(1).Select(placement => placement.Screen));
        Assert.Equal(new ScreenFold(2, new("columns", 1, Count: 2)), folded[1].Fold);
        Assert.Equal(new ScreenFold(3, new("columns", 2, Count: 2)), folded[2].Fold);
        Assert.Equal(new PlacementLayout("halves", 1, "vertical"), folded[2].Layout);
        Assert.Equal("screens 2-3 share screen 2", Placement.DescribeFold(Placement.FoldScreens(plan, 2)));
    }

    [Fact]
    public void One_screen_holds_three_planned_screens_as_thirds()
    {
        var plan = Plan(Desktop("Admin", Entry("Google", 1), Entry("Slack", 2), Entry("1Password", 3)));
        var screen = new Rect(0, 0, 3000, 1000);

        var rects = Placement.FoldScreens(plan, 1).Desktops[0].Launch.Select(entry => Geometry.PlacementRectIn(screen, entry.Placement!)).ToList();

        Assert.Equal([new Rect(0, 0, 1000, 1000), new Rect(1000, 0, 1000, 1000), new Rect(2000, 0, 1000, 1000)], rects);
        Assert.Equal("screens 1-3 share screen 1", Placement.DescribeFold(Placement.FoldScreens(plan, 1)));
    }

    [Fact]
    public void A_lone_missing_screen_takes_the_whole_connected_one()
    {
        var plan = Plan(Desktop("Work", Entry("A", 1), Entry("B", 3)));

        var folded = Placement.FoldScreens(plan, 2).Desktops[0].Launch[1].Placement!;

        Assert.Equal(2, folded.Screen);
        Assert.Equal(new Rect(0, 0, 1920, 1080), Geometry.PlacementRectIn(new Rect(0, 0, 1920, 1080), folded));
    }

    [Fact]
    public void Each_desktop_folds_by_the_screens_it_uses_and_inactive_entries_take_no_column()
    {
        var plan = Plan(
            Desktop("Work", Entry("A", 2), Entry("B", 3), Entry("Paused", 4) with { Disabled = true }),
            Desktop("IDE", Entry("Code", 1)));

        var folded = Placement.FoldScreens(plan, 2);

        Assert.Equal(2, folded.Desktops[0].Launch[1].Placement!.Fold!.Column.Count);
        Assert.Equal(2, folded.Desktops[0].Launch[2].Placement!.Screen);
        Assert.Null(folded.Desktops[0].Launch[2].Placement!.Fold);
        Assert.Same(plan.Desktops[1], folded.Desktops[1]);
    }

    [Fact]
    public void A_folded_window_already_in_its_column_counts_as_the_same_placement()
    {
        var folded = new PlacementSpec(1, new("full"), Fold: new ScreenFold(2, new("columns", 2, Count: 3)));

        Assert.False(WindowReuse.SamePlacement(new PlacementSpec(1, new("full")), folded));
        Assert.Equal(Geometry.NormalizedPlacementRect(new PlacementSpec(1, new("thirds", 2))), Geometry.NormalizedPlacementRect(folded));
    }

    [Fact]
    public void An_empty_desktop_and_an_unfilled_screen_are_reported()
    {
        var plan = Plan(Desktop("Empty"), Desktop("Work", Entry("A", 1, new("thirds", 1)), Entry("B", 1, new("thirds", 2))));

        Assert.Equal(
            ["Empty: virtual desktop is empty", "Work: screen 2 is empty", "Work: screen 1 leaves 33.3% empty"],
            Placement.ValidatePlanStructure(plan, expectedScreenCount: 2));
    }

    [Fact]
    public void A_screen_filled_once_has_no_issues() =>
        Assert.Empty(Placement.ValidatePlanStructure(Plan(Desktop("Work", Entry("App", 1))), expectedScreenCount: 1));

    [Fact]
    public void A_screen_past_the_count_is_reported_and_not_measured() =>
        Assert.Equal(
            ["Work: screen 1 is empty", "Work: screen 2 is empty", "Work: screen 3 exceeds expected screen count 2"],
            Placement.ValidatePlanStructure(Plan(Desktop("Work", Entry("App", 3, new("thirds", 1)))), expectedScreenCount: 2));

    [Fact]
    public void Overlap_is_reported_only_when_strict()
    {
        var plan = Plan(Desktop("Work", Entry("A", 1), Entry("B", 1)));

        Assert.Equal(["Work: screen 1 overlaps 100.0%"], Placement.ValidatePlanStructure(plan, 1, strict: true));
        Assert.Empty(Placement.ValidatePlanStructure(plan, 1));
    }

    [Fact]
    public void Entries_switched_off_by_the_plan_or_an_off_feature_are_not_measured()
    {
        var plan = Plan(Desktop("Work", Entry("A", 1), Entry("B", 1) with { Disabled = true }, Entry("C", 1) with { Feature = "off" }, Entry("D", 1) with { Feature = "unknown" }))
            with
        { Features = new Dictionary<string, bool> { ["off"] = false } };

        Assert.Empty(Placement.ValidatePlanStructure(plan, 1, strict: true));
        Assert.Empty(Placement.ValidatePlanStructure(Plan(Desktop("Work", Entry("Off", 1) with { Disabled = true })), 1));
    }

    [Fact]
    public void Without_a_count_the_highest_placed_screen_is_used()
    {
        var plan = Plan(Desktop("Work", Entry("A", 2)));

        Assert.Equal(["Work: screen 1 is empty"], Placement.ValidatePlanStructure(plan));
        Assert.Equal(["Work: screen 1 is empty", "Work: screen 3 is empty"], Placement.ValidatePlanStructure(plan, detectedScreenCount: 3));
        Assert.Empty(Placement.ValidatePlanStructure(plan, expectedScreenCount: 0));
    }

    [Fact]
    public void A_wide_screen_takes_every_column()
    {
        var fitted = Placement.FitColumns(Columns(1, 2, 3), new Dictionary<int, int> { [1] = 2560 });

        Assert.Equal([false, false, false], fitted.Desktops[0].Launch.Select(entry => entry.Disabled));
        Assert.Equal([3, 3, 3], fitted.Desktops[0].Launch.Select(entry => entry.Placement!.Layout.Count));
    }

    [Fact]
    public void A_narrow_screen_switches_off_the_column_that_does_not_fit()
    {
        var fitted = Placement.FitColumns(Columns(1, 2, 3), new Dictionary<int, int> { [1] = 1920 });

        Assert.Equal([false, false, true], fitted.Desktops[0].Launch.Select(entry => entry.Disabled));
        Assert.Equal(new UnitRect(Fraction.Zero, Fraction.Zero, new(1, 2), Fraction.One), Geometry.NormalizedPlacementRect(fitted.Desktops[0].Launch[0].Placement!));
    }

    [Fact]
    public void An_unknown_width_keeps_the_plan_as_written()
    {
        var fitted = Placement.FitColumns(Columns(1, 2, 3), new Dictionary<int, int>());

        Assert.Equal([false, false, false], fitted.Desktops[0].Launch.Select(entry => entry.Disabled));
        Assert.Equal(new Fraction(2, 3), Geometry.NormalizedPlacementRect(fitted.Desktops[0].Launch[2].Placement!).Left);
    }

    [Fact]
    public void Entries_and_desktops_without_columns_are_left_alone()
    {
        var other = Desktop("Other", Entry("Full", 1));
        var plan = Plan(other, Desktop("Mixed", Entry("Full", 1), new LaunchEntry("Unplaced"), Entry("Col", 1, new("columns", 1))));

        var fitted = Placement.FitColumns(plan, new Dictionary<int, int>());

        Assert.Same(other, fitted.Desktops[0]);
        Assert.Same(plan.Desktops[1].Launch[0], fitted.Desktops[1].Launch[0]);
        Assert.Same(plan.Desktops[1].Launch[1], fitted.Desktops[1].Launch[1]);
        Assert.Equal(1, fitted.Desktops[1].Launch[2].Placement!.Layout.Count);
    }

    [Fact]
    public void A_plan_read_from_a_file_gets_the_column_count_it_asks_for_which_is_never_saved()
    {
        var plan = PlanFiles.Parse(
            """{"desktops": [{"name": "Work", "launch": [{"name": "T1", "placement": {"screen": 1, "layout": {"mode": "columns", "slot": 1, "min_width": 800}}}, {"name": "T2", "placement": {"screen": 1, "layout": {"mode": "columns", "slot": 2}}}]}]}""");

        Assert.Equal([2, 2], plan.Desktops[0].Launch.Select(entry => entry.Placement!.Layout.Count));
        Assert.DoesNotContain("count", PlanFiles.Format(plan.ToJson()), StringComparison.Ordinal);
    }

    [Fact]
    public void Entries_are_checked_for_what_their_type_needs()
    {
        var plan = Plan(
            Desktop(
                "Work",
                new LaunchEntry("T") { Type = "terminal" },
                new LaunchEntry("A") { Type = "app" },
                new LaunchEntry("C") { Type = "command" },
                new LaunchEntry("Ok") { Type = "terminal", Command = "claude", Cwd = "/w" },
                new LaunchEntry("Ok") { Type = "command", Command = "x", Cwd = "/nowhere" },
                new LaunchEntry("App") { Type = "app", Path = "/nowhere/app" },
                new LaunchEntry("Other") { Type = "other" }),
            Desktop("Work"));

        Assert.Equal(
            [
                "Work/T: terminal missing command",
                "Work/T: terminal missing cwd",
                "Work/A: app missing path",
                "Work/C: command entry missing command",
                "Work: duplicate launch entry `Ok`",
                "Duplicate desktop name: Work",
            ],
            Placement.ValidatePlan(plan, NoSubstitutions));
    }

    [Fact]
    public void A_plan_without_desktops_is_an_error() =>
        Assert.Equal(["Workspace plan must define at least one desktop"], Placement.ValidatePlan(new WorkspacePlan(), NoSubstitutions));

    [Fact]
    public void Placements_are_checked_against_the_monitor_count() =>
        Assert.Equal(
            ["Work/A: placement screen 3 exceeds detected monitor count 2"],
            Placement.ValidatePlan(Plan(Desktop("Work", Entry("A", 3))), NoSubstitutions, monitorCount: 2));

    [Fact]
    public void An_entry_that_does_not_launch_is_not_held_to_the_screens_connected_now()
    {
        var plan = Plan(Desktop("Work", Entry("A", 1), Entry("B", 2), Entry("Paused", 3) with { Feature = "paused" }, Entry("Off", 3) with { Disabled = true })) with
        {
            Features = new Dictionary<string, bool>(StringComparer.Ordinal) { ["paused"] = false },
        };

        Assert.Empty(Placement.ValidatePlan(plan, NoSubstitutions, monitorCount: 2));
        Assert.Equal(2, Placement.ResolveScreenCount(plan));
        Assert.Empty(Placement.ValidatePlanStructure(plan));
        Assert.Equal(["Work/Paused: placement screen 3 exceeds detected monitor count 2"], Placement.ValidatePlan(plan with { Features = new Dictionary<string, bool>(StringComparer.Ordinal) { ["paused"] = true } }, NoSubstitutions, monitorCount: 2));
    }

    [Theory]
    [InlineData("run", null, null)]
    [InlineData("deferred", "terminal", null)]
    [InlineData("deferred", "terminal", "wt-pwsh")]
    [InlineData("later", "app", null)]
    [InlineData("deferred", "app", null)]
    [InlineData("deferred", "terminal", "warp")]
    public void Command_at_logon_is_a_known_mode_and_deferred_only_in_windows_terminal(string mode, string? type, string? shell)
    {
        var entry = new LaunchEntry("E") { Type = type ?? "command", Command = "x", Cwd = "/w", Path = "/p", CommandAtLogon = mode, Shell = shell };

        var errors = Placement.ValidatePlan(Plan(Desktop("D", entry)), NoSubstitutions);

        var expected = (mode, type, shell) switch
        {
            ("later", _, _) => "D/E: command_at_logon must be one of deferred, run, not 'later'",
            ("deferred", "terminal", "wt-pwsh") or ("run", _, _) => null,
            _ => "D/E: command_at_logon \"deferred\" is only for terminal entries with shell wt-pwsh",
        };
        Assert.Equal(expected is null ? [] : [expected], errors);
    }

    [Fact]
    public void With_a_path_probe_folders_apps_and_local_arguments_must_exist()
    {
        var probe = new FakePaths("C:/ws", "C:/ws/app.exe", "C:/ws/file.txt");
        var substitutions = new Dictionary<string, string> { ["workspace"] = "C:/ws" };
        var plan = Plan(Desktop(
            "Work",
            new LaunchEntry("Here") { Type = "terminal", Command = "x", Cwd = "{workspace}" },
            new LaunchEntry("Gone") { Type = "terminal", Command = "x", Cwd = "{workspace}/gone" },
            new LaunchEntry("Typo") { Type = "terminal", Command = "x", Cwd = "{wrkspace}" },
            new LaunchEntry("Cmd") { Type = "command", Command = "x", Cwd = "/nowhere" },
            new LaunchEntry("App") { Path = "{workspace}/app.exe", Args = ["{workspace}/file.txt", "--flag", "https://x", "./missing", "{nope}", "plain", "\"C:/ws/gone\""] },
            new LaunchEntry("Missing") { Path = "C:/ws/missing.exe" },
            new LaunchEntry("BadPath") { Path = "{nope}/app.exe" },
            new LaunchEntry("Vault") { Path = "C:/Obsidian/Obsidian.exe", Args = ["~/Notes Vault"] }));

        Assert.Equal(
            [
                "Work/Gone: cwd does not exist: C:/ws/gone",
                "Work/Typo: Unresolved placeholders: {wrkspace}",
                "Work/Cmd: cwd does not exist: /nowhere",
                "Work/App: Unresolved placeholders: {nope}",
                "Work/App: arg does not exist: ./missing",
                "Work/App: arg does not exist: \"C:/ws/gone\"",
                "Work/Missing: app path does not exist: C:/ws/missing.exe",
                "Work/BadPath: Unresolved placeholders: {nope}",
                "Work/Vault: app path does not exist: C:/Obsidian/Obsidian.exe",
            ],
            Placement.ValidatePlan(plan, substitutions, paths: probe));
    }

    [Fact]
    public void The_local_probe_finds_files_and_folders_and_expands_the_home_folder()
    {
        using var temp = new TempDir();
        var file = temp.File("app.exe");
        File.WriteAllText(file, string.Empty);
        var probe = new LocalPathProbe();

        Assert.True(probe.Exists(file));
        Assert.True(probe.Exists(temp.Path));
        Assert.False(probe.Exists(Path.Join(temp.Path, "missing")));
        Assert.True(probe.Exists("~"));
        Assert.False(probe.Exists("~/" + Guid.NewGuid().ToString("N")));
        Assert.Equal(file, probe.ResolveExecutable(file));
    }

    private static WorkspacePlan Plan(params DesktopPlan[] desktops) => new() { Desktops = desktops };

    private static DesktopPlan Desktop(string name, params LaunchEntry[] launch) => new(name) { Launch = launch };

    private static LaunchEntry Entry(string name, int screen, PlacementLayout? layout = null) =>
        new(name) { Path = "/usr/bin/app", Placement = new(screen, layout ?? new("full")) };

    private static WorkspacePlan Columns(params int[] slots) =>
        Plan(Desktop("Work", [.. slots.Select(slot => new LaunchEntry($"T{slot}") { Type = "terminal", Command = "claude", Cwd = "/w", Placement = new(1, new("columns", slot, MinWidth: 800)) })]));

    private sealed class FakePaths(params string[] existing) : IPathProbe
    {
        public bool Exists(string path) => existing.Contains(path, StringComparer.Ordinal);

        public string ResolveExecutable(string path) => path;
    }
}
