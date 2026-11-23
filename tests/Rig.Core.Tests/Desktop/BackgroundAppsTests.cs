using Rig.Core.Desktop;
using Rig.Core.Tests.Fakes;
using Rig.Core.Text;

namespace Rig.Core.Tests.Desktop;

/// <summary>
/// A plan's <c>background</c> apps: read and written strictly, and put away by a full restore once the planned windows
/// are placed.
/// </summary>
public sealed class BackgroundAppsTests
{
    private const string Docker = "Docker Desktop.exe";

    private static WorkspacePlan Plan(params BackgroundApp[] apps) =>
        new() { Desktops = [new DesktopPlan("Work")], SwitchDelayMs = 0, Background = apps };

    private static IReadOnlyList<string> Restore(WorkspacePlan plan, FakeDesktop machine, bool dryRun = false, string? target = null) =>
        WorkspaceRestore.Run(plan, new(new Dictionary<string, string>()) { DryRun = dryRun, TargetDesktop = target }, machine, RunLog.Discard);

    [Fact]
    public void A_background_app_round_trips_through_the_plan_file()
    {
        var plan = PlanFiles.Parse("""
            {"background": [
              {"name": "Docker", "window_process_name": "Docker Desktop.exe", "summary": "tray only"},
              {"name": "Quitter", "window_process_name": "q.exe", "action": "minimize", "window_wait_timeout_ms": 5000, "disabled": true}
            ]}
            """);

        Assert.Equal(
            [
                new BackgroundApp("Docker", Docker) { Summary = "tray only" },
                new BackgroundApp("Quitter", "q.exe") { Action = BackgroundApp.Minimize, WindowWaitTimeoutMs = 5000, Disabled = true },
            ],
            plan.Background);
        Assert.Equal(plan.Background, PlanFiles.Parse(PlanFiles.Format(plan.ToJson())).Background);
        Assert.Equal("""{"name":"Docker","window_process_name":"Docker Desktop.exe","summary":"tray only"}""", plan.Background[0].ToJson().ToJsonString());
    }

    [Fact]
    public void A_plan_with_no_background_apps_writes_no_background_field() =>
        Assert.False(Plan().ToJson().ContainsKey("background"));

    [Theory]
    [InlineData("""{"name": "d", "window_process_name": "d.exe", "action": "hide"}""", "$.background[0].action: expected one of close, minimize, found \"hide\"")]
    [InlineData("""{"name": "d"}""", "$.background[0].window_process_name: expected a string")]
    [InlineData("""{"name": "d", "window_process_name": "d.exe", "placement": {}}""", "$.background[0].placement: expected a known field")]
    public void A_bad_background_app_says_what_is_wrong(string item, string message) =>
        Assert.StartsWith(
            message,
            Assert.Throws<JsonInputException>(() => PlanFiles.Parse($$"""{"background": [{{item}}]}""")).Message["<memory>: ".Length..],
            StringComparison.Ordinal);

    [Fact]
    public void Restore_closes_a_background_app_window_after_placing_the_plan()
    {
        var machine = new FakeDesktop();
        var dashboard = machine.AddWindow("Docker Desktop", Docker);

        var actions = Restore(Plan(new BackgroundApp("Docker", Docker)), machine);

        Assert.Null(machine.Window(dashboard.Handle));
        Assert.Contains($"Close 1 window(s) of background app Docker ({Docker})", actions);
    }

    [Fact]
    public void Restore_minimizes_when_the_app_asks_for_it()
    {
        var machine = new FakeDesktop();
        var window = machine.AddWindow("Quitter", "q.exe");

        _ = Restore(Plan(new BackgroundApp("Quitter", "q.exe") { Action = BackgroundApp.Minimize }), machine);

        Assert.NotNull(machine.Window(window.Handle));
        Assert.Equal([window.Handle], machine.Minimized);
    }

    [Fact]
    public void Restore_waits_for_a_running_app_whose_window_shows_late()
    {
        var machine = new FakeDesktop();
        var late = new FakeWindow(77, "Docker Desktop", Docker, 1) { VisibleAt = TimeSpan.FromSeconds(3) };
        machine.Windows.Add(late);

        _ = Restore(Plan(new BackgroundApp("Docker", Docker) { WindowWaitTimeoutMs = 10_000 }), machine);

        Assert.Null(machine.Window(late.Handle));
    }

    [Fact]
    public void Without_a_wait_a_window_that_has_not_shown_yet_is_left()
    {
        var machine = new FakeDesktop();
        machine.Windows.Add(new FakeWindow(77, "Docker Desktop", Docker, 1) { VisibleAt = TimeSpan.FromSeconds(3) });
        var before = machine.Now;

        var actions = Restore(Plan(new BackgroundApp("Docker", Docker)), machine);

        Assert.NotNull(machine.Window(77));
        Assert.DoesNotContain(actions, action => action.Contains("background app", StringComparison.Ordinal));
        Assert.Equal(before, machine.Now);
    }

    [Fact]
    public void An_app_that_is_not_running_costs_no_wait()
    {
        var machine = new FakeDesktop();

        var actions = Restore(Plan(new BackgroundApp("Docker", Docker) { WindowWaitTimeoutMs = 10_000 }), machine);

        Assert.DoesNotContain(actions, action => action.Contains("background app", StringComparison.Ordinal));
        Assert.Equal(TimeSpan.Zero, machine.Now);
    }

    [Fact]
    public void Disabled_apps_one_desktop_restores_and_the_invoking_window_are_left_alone()
    {
        var machine = new FakeDesktop();
        var invoking = machine.AddWindow("Docker Desktop", Docker);
        _ = machine.InvokingHandles.Add(invoking.Handle);
        var disabled = machine.AddWindow("Other", "other.exe");

        _ = Restore(Plan(new BackgroundApp("Docker", Docker), new BackgroundApp("Other", "other.exe") { Disabled = true }), machine);
        _ = Restore(Plan(new BackgroundApp("Docker", Docker)), machine, target: "Work");

        Assert.NotNull(machine.Window(disabled.Handle));
        Assert.NotNull(machine.Window(invoking.Handle));
    }

    [Fact]
    public void A_dry_run_lists_the_apps_and_touches_nothing()
    {
        var machine = new FakeDesktop();
        var dashboard = machine.AddWindow("Docker Desktop", Docker);

        var actions = Restore(Plan(new BackgroundApp("Docker", Docker), new BackgroundApp("Quitter", "q.exe") { Action = BackgroundApp.Minimize }), machine, dryRun: true);

        Assert.Equal(
            [$"Close any window of background app Docker ({Docker})", "Minimize any window of background app Quitter (q.exe)"],
            actions.TakeLast(2));
        Assert.NotNull(machine.Window(dashboard.Handle));
    }
}
