using System.Diagnostics;
using Rig.Core.Chrome;
using Rig.Core.Chrome.Windows;
using Rig.Core.Desktop;
using Rig.Core.Tests.Fakes;

namespace Rig.Core.Tests.Chrome;

/// <summary>
/// Deleting closed duplicate saved tab groups through each profile's fullest bar, the marker of profiles left to look
/// at again, and the Windows desktops the run loops use. Replaces the consolidation half of <c>test_chrome_organise</c>.
/// </summary>
public sealed class SavedConsolidationTests
{
    private readonly FakeChromeSavedGroupActions _actions = new();
    private readonly FakeChromeDesktops _desktops = new();
    private readonly List<string> _said = [];

    private SavedConsolidation Consolidation() => new(_actions, _desktops, _said.Add);

    private static ChromeWindow Window(long handle, string profile) =>
        new(handle, "w", profile, profile, handle, false, 1, []);

    [Fact]
    public void Each_profile_deletes_through_the_window_showing_the_most_saved_groups()
    {
        _actions.Bar(1, "A", "+rig", "-rig").Bar(2, "A", "+rig", "-rig", "-rig", "-docs").Bar(3, "B", "-docs", "-docs");

        var planned = Consolidation().Plan([Window(1, "A"), Window(2, "A"), Window(3, "B")]);

        Assert.Equal(new Dictionary<string, long> { ["A"] = 2, ["B"] = 3 }, planned.Fullest);
        Assert.Equal([("A", "rig", 2), ("B", "docs", 1)], planned.Plan.Deletes.Select(delete => (delete.Profile, delete.Title, delete.Count)));
        Assert.Equal(3, planned.Plan.Total);
    }

    [Fact]
    public void A_tie_keeps_the_first_window_read()
    {
        _actions.Bar(1, "A", "-rig", "-rig").Bar(2, "A", "-rig", "-rig");

        Assert.Equal(1, Consolidation().Plan([Window(1, "A"), Window(2, "A")]).Fullest["A"]);
    }

    [Fact]
    public void Every_planned_copy_is_deleted_and_reported_with_its_tabs()
    {
        _actions.Bar(1, "A", "+rig", "-rig", "-rig", "-docs", "-docs");
        _actions.Tabs["rig"] = ["GitHub", new string('x', 50)];
        var consolidation = Consolidation();

        var done = consolidation.Consolidate(consolidation.Plan([Window(1, "A")]));

        Assert.Equal(new Consolidation(3), done);
        Assert.Equal(["1 A rig", "1 A rig", "1 A docs"], _actions.Log);
        Assert.Equal($"  deleted rig (2 saved tabs: GitHub, {new string('x', 39)}…)", _said[0]);
        Assert.Equal("  deleted docs (0 saved tabs)", _said[2]);
    }

    [Fact]
    public void A_title_that_fails_is_kept_and_the_others_still_go()
    {
        _actions.Bar(1, "A", "-rig", "-rig", "-rig", "-docs", "-docs");
        _actions.Refuses["rig"] = "menu never opened";
        var consolidation = Consolidation();

        var done = consolidation.Consolidate(consolidation.Plan([Window(1, "A")]));

        Assert.Equal(new Consolidation(1, "deleting saved groups 'rig' [A]: menu never opened"), done);
        Assert.Equal(["1 A rig", "1 A docs"], _actions.Log);
        Assert.Equal("  kept rig: menu never opened", _said[0]);
    }

    [Fact]
    public void Failures_are_joined_in_plan_order()
    {
        _actions.Bar(1, "A", "-rig", "-rig", "-docs", "-docs");
        _actions.Refuses["rig"] = "a";
        _actions.Refuses["docs"] = "b";
        var consolidation = Consolidation();

        var done = consolidation.Consolidate(consolidation.Plan([Window(1, "A")]));

        Assert.Equal("deleting saved groups 'rig' [A]: a; 'docs' [A]: b", done.Stopped);
    }

    [Fact]
    public void The_user_is_put_back_on_their_desktop()
    {
        _actions.Bar(1, "A", "-rig", "-rig");
        _actions.DuringDelete = () => _desktops.Number = 3;
        var consolidation = Consolidation();

        consolidation.Consolidate(consolidation.Plan([Window(1, "A")]));

        Assert.Equal([1], _desktops.Went);
    }

    [Fact]
    public void Putting_the_user_back_is_traced_and_best_effort()
    {
        using var spans = new RecordedSpans("Rig.Chrome");
        _desktops.Number = 2;

        _desktops.ReturnTo(1);
        _desktops.Broken = true;
        _desktops.ReturnTo(1);
        _desktops.ReturnTo(null);

        var restore = Assert.Single(spans.Stopped, activity => activity.OperationName == "restore_desktop");
        Assert.Equal(2, restore.GetTagItem("desktop.from"));
        Assert.Equal(1, restore.GetTagItem("desktop.to"));
        Assert.Equal([1], _desktops.Went);
    }

    [Fact]
    public void The_marker_lives_beside_the_scheduled_run()
    {
        Assert.Equal(
            Path.Join("L", "shared-utilities", "chrome-tabs", "saved-groups.pending"),
            SavedConsolidation.MarkerPath(name => name == "LOCALAPPDATA" ? "L" : null, "H"));
        Assert.Equal(
            Path.Join("H", "AppData", "Local", "shared-utilities", "chrome-tabs", "saved-groups.pending"),
            SavedConsolidation.MarkerPath(_ => string.Empty, "H"));
        Assert.Equal(SavedConsolidation.MarkerPath(_ => string.Empty, "H"), SavedConsolidation.MarkerPath(_ => null, "H"));
    }

    [Fact]
    public void Pending_profiles_round_trip_through_the_marker()
    {
        using var temp = new TempDir();
        var marker = Path.Join(temp.Path, "deep", "saved-groups.pending");

        Assert.Empty(SavedConsolidation.PendingProfiles(marker));
        SavedConsolidation.SetPendingProfiles(marker, ["b", "A", "b"]);
        Assert.Equal("A\nb\n", File.ReadAllText(marker));
        Assert.Equal(["A", "b"], SavedConsolidation.PendingProfiles(marker).Order(StringComparer.Ordinal));
        SavedConsolidation.SetPendingProfiles(marker, []);
        Assert.False(File.Exists(marker));
        SavedConsolidation.SetPendingProfiles(marker, []);
    }

    [Fact]
    public void A_marker_beside_a_missing_folder_lists_nothing()
    {
        using var temp = new TempDir();

        Assert.Empty(SavedConsolidation.PendingProfiles(Path.Join(temp.Path, "gone", "saved-groups.pending")));
    }

    [Fact]
    public void Blank_lines_in_the_marker_are_ignored()
    {
        using var temp = new TempDir();
        var marker = temp.File("saved-groups.pending");
        File.WriteAllText(marker, "A\r\n\r\nB\n");

        Assert.Equal(["A", "B"], SavedConsolidation.PendingProfiles(marker).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Windows_desktops_answer_through_the_virtual_desktops()
    {
        var virtualDesktops = new FakeVirtualDesktops("one", "two");
        virtualDesktops.Windows[7] = virtualDesktops.Desktops[1];
        var desktops = new ChromeVirtualDesktops(virtualDesktops, handle => handle == 7);

        Assert.Equal(1, desktops.Current());
        Assert.Equal(2, desktops.DesktopOf(7));
        Assert.Null(desktops.DesktopOf(8));
        Assert.True(desktops.WindowExists(7));
        Assert.False(desktops.WindowExists(8));

        desktops.SwitchTo(2);
        desktops.SwitchTo(9);
        desktops.MoveWindow(7, 1);
        desktops.MoveWindow(7, 9);

        Assert.Equal(2, desktops.Current());
        Assert.Equal(1, desktops.DesktopOf(7));
    }

    [Fact]
    public void An_unreadable_current_desktop_is_null()
    {
        var virtualDesktops = new FakeVirtualDesktops("one") { FailOn = nameof(IVirtualDesktops.CurrentNumber) };

        Assert.Null(new ChromeVirtualDesktops(virtualDesktops, _ => true).Current());
    }
}
