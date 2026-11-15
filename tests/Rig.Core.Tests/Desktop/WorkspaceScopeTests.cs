using Rig.Core.Desktop;

namespace Rig.Core.Tests.Desktop;

/// <summary>Narrowing a plan or status capture to one desktop or screen, and reading a plan's default paths.</summary>
public sealed class WorkspaceScopeTests
{
    private static readonly PlacementSpec Screen1 = new(1, new("full"));
    private static readonly PlacementSpec Screen2 = new(2, new("full"));

    private static WorkspacePlan Plan() => new()
    {
        Desktops =
        [
            new("Work")
            {
                Launch = [new("Notes") { Placement = Screen1 }, new("Mail") { Placement = Screen2 }],
                CapturedWindows = [new("Notes", ScreenNumber: 1), new("Mail", ScreenNumber: 2)],
            },
            new("Admin") { Launch = [new("Admin Notes") { Placement = Screen1 }] },
        ],
    };

    [Fact]
    public void Keeps_only_the_named_desktop_ignoring_case()
    {
        var scoped = WorkspaceScope.ToDesktop(Plan(), "admin");

        Assert.Equal("Admin", Assert.Single(scoped.Desktops).Name);
    }

    [Fact]
    public void Keeps_only_the_entries_and_windows_on_a_screen()
    {
        var scoped = WorkspaceScope.ToScreen(Plan(), 2);

        var work = scoped.Desktops[0];
        Assert.Equal(["Mail"], work.Launch.Select(entry => entry.Name));
        Assert.Equal(["Mail"], work.CapturedWindows.Select(window => window.Title));
        Assert.Empty(scoped.Desktops[1].Launch);
    }

    [Fact]
    public void Narrows_by_desktop_then_screen()
    {
        var scoped = WorkspaceScope.For(Plan(), "Work", 1);

        Assert.Equal("Work", Assert.Single(scoped.Desktops).Name);
        Assert.Equal(["Notes"], scoped.Desktops[0].Launch.Select(entry => entry.Name));
    }

    [Fact]
    public void No_scope_returns_the_plan_unchanged()
    {
        var plan = Plan();

        Assert.Same(plan, WorkspaceScope.For(plan, null, null));
    }
}
