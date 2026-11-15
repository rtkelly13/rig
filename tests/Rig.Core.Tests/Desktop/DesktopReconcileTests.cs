using Rig.Core.Desktop;
using Rig.Core.Tests.Fakes;

namespace Rig.Core.Tests.Desktop;

/// <summary>The virtual desktop drift planner and the reconciliation applied through the port.</summary>
public sealed class DesktopReconcileTests
{
    [Fact]
    public void A_matching_plan_has_no_drift()
    {
        var plan = DesktopReconcile.Plan(["Personal", "Work"], [], ["Personal", "Work"]);

        Assert.False(plan.HasDrift);
        Assert.Equal([new DesktopItem(1, "Personal"), new DesktopItem(2, "Work")], plan.Unchanged);
        Assert.Empty(plan.ToRename);
    }

    [Fact]
    public void Missing_desktops_are_created_and_names_renamed_in_place()
    {
        var plan = DesktopReconcile.Plan(["Personal", "Work", "Admin"], [], ["Personal", "Office"]);

        Assert.True(plan.HasDrift);
        Assert.Equal([new DesktopCreationAction(3, "Admin")], plan.ToCreate);
        Assert.Equal(
            [new DesktopRenameAction(2, "Office", "Work"), new DesktopRenameAction(3, "", "Admin")],
            plan.ToRename);
        Assert.Equal([new DesktopItem(1, "Personal")], plan.Unchanged);
    }

    [Fact]
    public void Switched_off_desktops_are_retired_before_the_rest_is_matched()
    {
        var plan = DesktopReconcile.Plan(["Work"], ["Personal"], ["Personal", "Work"]);

        Assert.Equal(["Personal"], plan.ToRetire);
        Assert.Empty(plan.ToCreate);
        Assert.Equal([new DesktopItem(1, "Work")], plan.Unchanged);
    }

    [Fact]
    public void A_plan_that_would_retire_everything_keeps_the_first_desktop()
    {
        var plan = DesktopReconcile.Plan([], ["Personal"], ["Personal"]);

        Assert.Equal(["Personal"], plan.ToRetire);
        Assert.Empty(plan.ToCreate);
        Assert.Empty(plan.ToRename);
        Assert.Equal([new DesktopItem(1, "Personal")], plan.Unmanaged);
    }

    [Fact]
    public void Surplus_desktops_are_reported_not_removed()
    {
        var plan = DesktopReconcile.Plan(["Work"], [], ["Work", "Spare"]);

        Assert.Equal([new DesktopItem(2, "Spare")], plan.Unmanaged);
        Assert.Empty(plan.ToRetire);
    }

    [Fact]
    public void Reconcile_retires_creates_and_renames_through_the_port()
    {
        var desktops = new FakeVirtualDesktops("Personal", "Office");

        var result = DesktopReconcile.Reconcile(["Personal", "Work", "Admin"], desktops, ["Personal"], TimeSpan.Zero);

        Assert.True(result.Converged);
        Assert.Equal(["Personal"], result.Retired);
        Assert.Equal(["Personal", "Work", "Admin"], desktops.Names());
        Assert.Contains(result.Created, action => action.Name == "Admin");
        Assert.Contains(result.Renamed, action => action is { Index: 2, TargetName: "Work", Success: true });
        Assert.Empty(result.Unchanged);
    }

    [Fact]
    public void A_dry_run_draws_the_plan_and_changes_nothing()
    {
        var desktops = new FakeVirtualDesktops("Personal", "Office");

        var result = DesktopReconcile.Reconcile(["Personal", "Work"], desktops, [], TimeSpan.Zero, dryRun: true);

        Assert.True(result.DryRun);
        Assert.Empty(result.Retired);
        Assert.Empty(result.Created);
        Assert.Empty(result.Renamed);
        Assert.Equal(["Personal", "Office"], desktops.Names());
        Assert.Single(result.Plan.ToRename);
    }

    [Fact]
    public void A_rename_that_does_not_take_is_reported()
    {
        var desktops = new FakeVirtualDesktops("Personal", "Office") { IgnoreRenames = true };

        var result = DesktopReconcile.Reconcile(["Personal", "Work"], desktops, [], TimeSpan.Zero);

        Assert.False(result.Converged);
        Assert.Equal([new DesktopRenameAction(2, "Office", "Work", false, "Rename failed")], result.FailedRenames);
    }
}
