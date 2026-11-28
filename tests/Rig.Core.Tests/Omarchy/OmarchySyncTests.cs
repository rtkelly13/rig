using System.Text.Json.Nodes;
using Rig.Core.Omarchy;
using Rig.Core.Processes;
using Rig.Core.Tests.Fakes;
using Rig.Core.Text;

namespace Rig.Core.Tests.Omarchy;

/// <summary>Reading this machine's Omarchy files, and the apply behind <c>rig omarchy sync --apply</c>.</summary>
public sealed class OmarchySyncTests : IDisposable
{
    private readonly TempDir _root = new();
    private readonly FakeProc _proc = new();

    public void Dispose() => _root.Dispose();

    private OmarchyPaths Paths => new(
        _root.Path,
        Path.Join(_root.Path, "config"),
        Path.Join(_root.Path, "state"),
        Path.Join(_root.Path, "etc"),
        Path.Join(_root.Path, "omarchy"));

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private void Install() => _ = Directory.CreateDirectory(Paths.InstallRoot);

    private static void Write(string path, string text)
    {
        _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    [Fact]
    public void Read_collects_every_file_and_the_sleep_lock_state()
    {
        Install();
        Write(Paths.ShellFile, """{"version": 1, "idle": {"screensaver": 150, "lock": 300}}""");
        Write(Paths.ThemeNameFile, "tokyo-night\n");
        Write(Paths.PowerProfileFile("ac"), "performance\n");
        Write(Paths.ToggleFile("suspend-off"), string.Empty);
        Write(Paths.ShellTomlFile, "[font]\nbase-size = 15\n");
        Write(Paths.LogindDropIn, "[Login]\nHandleLidSwitch=ignore\n");
        _ = _proc.Answer(new Ran(0, "enabled\n", string.Empty), "systemctl", "--user", "is-enabled");

        var state = OmarchyState.Read(Paths, _proc);

        Assert.True(state.Installed);
        Assert.Equal(150, JsonShape.AsInt32(state.Shell["idle"]?["screensaver"]));
        Assert.Equal("tokyo-night", state.Theme);
        Assert.Equal(("performance", (string?)null), (state.PowerAc, state.PowerBattery));
        Assert.True(state.SuspendOff);
        Assert.Equal(15, state.FontBaseSize);
        Assert.Contains("HandleLidSwitch=ignore", state.LogindText, StringComparison.Ordinal);
        Assert.True(state.SleepLockEnabled);
    }

    [Fact]
    public void A_missing_shell_file_reads_as_the_default_document()
    {
        var state = OmarchyState.Read(Paths, _proc);

        Assert.False(state.Installed);
        Assert.Equal(1, JsonShape.AsInt32(state.Shell["version"]));
        Assert.Null(state.SleepLockEnabled);
    }

    [Fact]
    public void A_disabled_sleep_lock_unit_reads_as_disabled_and_an_unanswered_one_as_unknown()
    {
        Install();
        _ = _proc.Answer(new Ran(1, "disabled\n", string.Empty), "systemctl", "--user", "is-enabled");

        Assert.False(OmarchyState.Read(Paths, _proc).SleepLockEnabled);

        var unanswered = new FakeProc { Unanswered = new Ran(Ran.NotFound, string.Empty, "no systemd") };
        Assert.Null(OmarchyState.Read(Paths, unanswered).SleepLockEnabled);
    }

    [Fact]
    public void A_user_manager_that_cannot_be_reached_reads_as_unknown_not_disabled()
    {
        Install();
        _ = _proc.Answer(new Ran(1, string.Empty, "Failed to connect to bus"), "systemctl", "--user", "is-enabled");

        Assert.Null(OmarchyState.Read(Paths, _proc).SleepLockEnabled);
    }

    [Fact]
    public void A_shell_toml_without_a_font_table_reads_as_no_font_size()
    {
        Write(Paths.ShellTomlFile, "[bar]\nheight = 30\n");

        Assert.Null(OmarchyState.Read(Paths, _proc).FontBaseSize);
    }

    [Fact]
    public async Task Apply_merges_over_the_shell_file_as_it_is_when_written()
    {
        Install();
        Write(Paths.ShellFile, """{"version": 1, "bar": {"position": "top"}}""");
        var policy = OmarchyPolicy.Parse("""{"shell": {"bar": {"position": "bottom"}}}""");
        var state = OmarchyState.Read(Paths, _proc);
        Write(Paths.ShellFile, """{"version": 1, "bar": {"position": "top"}, "dock": {"enabled": true}}""");

        _ = await OmarchySync.ApplyAsync(policy, state, Paths, _proc, apply: true, Token);

        var shell = JsonNode.Parse(await File.ReadAllTextAsync(Paths.ShellFile, Token))!;
        Assert.Equal("bottom", (string?)shell["bar"]?["position"]);
        Assert.True((bool?)shell["dock"]?["enabled"]);
    }

    [Fact]
    public async Task A_failed_logind_reload_restores_the_previous_drop_in_so_the_next_sync_retries()
    {
        Install();
        Write(Paths.LogindDropIn, "[Login]\nHandleLidSwitch=suspend\n");
        List<string> installed = [];
        var proc = new FakeProc
        {
            BeforeRun = request =>
            {
                if (request.Argv is ["sudo", "install", ..])
                {
                    installed.Add(File.ReadAllText(request.Argv[^2]));
                }
            },
        };
        _ = proc.Answer(new Ran(0, string.Empty, string.Empty), "sudo");
        _ = proc.Answer(new Ran(1, string.Empty, "reload refused"), "sudo", "systemctl", "reload");
        var policy = OmarchyPolicy.Parse("""{"suspend": {"lid": "ignore"}}""");

        var actions = await OmarchySync.ApplyAsync(policy, OmarchyState.Read(Paths, proc), Paths, proc, apply: true, Token);

        Assert.All(actions, action => Assert.Equal(OmarchySync.ErrorStatus, action.Status));
        Assert.All(actions, action => Assert.Contains("restored the previous drop-in", action.Detail, StringComparison.Ordinal));
        Assert.Equal(2, installed.Count);
        Assert.Contains("HandleLidSwitch=ignore", installed[0], StringComparison.Ordinal);
        Assert.Contains("HandleLidSwitch=suspend", installed[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failed_logind_reload_removes_a_drop_in_that_did_not_exist_before()
    {
        Install();
        _ = _proc.Answer(new Ran(0, string.Empty, string.Empty), "sudo");
        _ = _proc.Answer(new Ran(1, string.Empty, "reload refused"), "sudo", "systemctl", "reload");
        var policy = OmarchyPolicy.Parse("""{"suspend": {"lid": "ignore"}}""");

        var actions = await OmarchySync.ApplyAsync(policy, OmarchyState.Read(Paths, _proc), Paths, _proc, apply: true, Token);

        Assert.All(actions, action => Assert.Equal(OmarchySync.ErrorStatus, action.Status));
        Assert.Contains(_proc.Requests, request => request.Argv is ["sudo", "rm", "-f", var path] && path == Paths.LogindDropIn);
    }

    [Fact]
    public void A_malformed_shell_file_is_refused()
    {
        Write(Paths.ShellFile, "not json");

        Assert.Throws<InvalidDataException>(() => OmarchyState.Read(Paths, _proc));
    }

    [Fact]
    public async Task A_dry_run_plans_the_changes_without_touching_a_file()
    {
        Install();
        Write(Paths.ShellFile, """{"version": 1, "idle": {"screensaver": 150, "lock": 300}, "bar": {"position": "top"}}""");
        var policy = OmarchyPolicy.Parse("""
            {"idle": {"screensaver_seconds": 0, "lock_seconds": 0}, "shell": {"bar": {"position": "bottom"}}}
            """);

        var actions = await OmarchySync.ApplyAsync(policy, OmarchyState.Read(Paths, _proc), Paths, _proc, apply: false, Token);

        Assert.Equal(3, actions.Count);
        Assert.All(actions, action => Assert.Equal(OmarchySync.Planned, action.Status));
        Assert.Contains("150", await File.ReadAllTextAsync(Paths.ShellFile, Token), StringComparison.Ordinal);
        Assert.DoesNotContain(_proc.Requests, request => request.Argv[0] is "omarchy" or "sudo");
    }

    [Fact]
    public async Task Apply_writes_the_shell_font_menu_and_logind_drop_in()
    {
        Install();
        Write(Paths.ShellFile, """{"version": 1, "idle": {"screensaver": 150, "lock": 300}, "bar": {"position": "top"}}""");
        Write(Paths.ShellTomlFile, "[font]\nbase-size = 14\n");
        string? dropIn = null;
        var proc = new FakeProc
        {
            BeforeRun = request =>
            {
                if (request.Argv is ["sudo", "install", ..])
                {
                    dropIn = File.ReadAllText(request.Argv[^2]);
                }
            },
        };
        _ = proc.Answer(new Ran(0, string.Empty, string.Empty), "sudo");
        _ = proc.Answer(new Ran(0, string.Empty, string.Empty), "omarchy", "display", "text", "size");
        var policy = OmarchyPolicy.Parse("""
            {"idle": {"screensaver_seconds": 0, "lock_seconds": 0},
             "suspend": {"menu": false, "lid": "ignore"},
             "font": {"base_size": 16},
             "shell": {"bar": {"position": "bottom"}}}
            """);

        var actions = await OmarchySync.ApplyAsync(policy, OmarchyState.Read(Paths, proc), Paths, proc, apply: true, Token);

        Assert.All(actions, action => Assert.Equal(OmarchySync.AppliedStatus, action.Status));
        var shell = JsonNode.Parse(await File.ReadAllTextAsync(Paths.ShellFile, Token))!;
        Assert.Equal(1, JsonShape.AsInt32(shell["version"]));
        Assert.Equal(0, JsonShape.AsInt32(shell["idle"]?["screensaver"]));
        Assert.Equal(0, JsonShape.AsInt32(shell["idle"]?["lock"]));
        Assert.Equal("bottom", (string?)shell["bar"]?["position"]);
        Assert.Contains(proc.Requests, request => request.Argv is ["omarchy", "display", "text", "size", "16"]);
        Assert.True(File.Exists(Paths.ToggleFile("suspend-off")));
        Assert.Contains("[Login]", dropIn, StringComparison.Ordinal);
        Assert.Contains("HandleLidSwitch=ignore", dropIn, StringComparison.Ordinal);
        Assert.Contains(proc.Requests, request => request.Argv is ["sudo", "install", ..] && request.Argv[^1] == Paths.LogindDropIn);
        Assert.Contains(proc.Requests, request => request.Argv is ["sudo", "systemctl", "reload", "systemd-logind"]);
    }

    [Fact]
    public async Task Apply_removes_the_suspend_off_flag_when_the_menu_is_wanted()
    {
        Install();
        Write(Paths.ToggleFile("suspend-off"), string.Empty);
        var policy = OmarchyPolicy.Parse("""{"suspend": {"menu": true}}""");

        var action = Assert.Single(await OmarchySync.ApplyAsync(policy, OmarchyState.Read(Paths, _proc), Paths, _proc, apply: true, Token));

        Assert.Equal(OmarchySync.AppliedStatus, action.Status);
        Assert.False(File.Exists(Paths.ToggleFile("suspend-off")));
    }

    [Fact]
    public async Task Apply_remembers_both_power_profiles_then_restores_autodetect()
    {
        Install();
        _ = _proc.Answer(new Ran(0, string.Empty, string.Empty), "omarchy", "powerprofiles", "set");
        var policy = OmarchyPolicy.Parse("""{"power": {"ac": "performance", "battery": "power-saver"}}""");

        var actions = await OmarchySync.ApplyAsync(policy, OmarchyState.Read(Paths, _proc), Paths, _proc, apply: true, Token);

        Assert.Equal(2, actions.Count);
        Assert.All(actions, action => Assert.Equal(OmarchySync.AppliedStatus, action.Status));
        Assert.Equal(
            [["omarchy", "powerprofiles", "set", "ac", "performance"],
             ["omarchy", "powerprofiles", "set", "battery", "power-saver"],
             ["omarchy", "powerprofiles", "set", "autodetect"]],
            _proc.Requests.Where(request => request.Argv[0] == "omarchy").Select(request => request.Argv.ToArray()));
    }

    [Fact]
    public async Task Apply_reports_a_theme_the_machine_refuses()
    {
        Install();
        Write(Paths.ThemeNameFile, "old");
        _ = _proc.Answer(new Ran(1, string.Empty, "no such theme"), "omarchy", "theme", "set");
        var policy = OmarchyPolicy.Parse("""{"theme": "tokyo-night"}""");

        var action = Assert.Single(await OmarchySync.ApplyAsync(policy, OmarchyState.Read(Paths, _proc), Paths, _proc, apply: true, Token));

        Assert.True(action.Error);
        Assert.Contains("omarchy theme set", action.Detail, StringComparison.Ordinal);
        Assert.Contains("no such theme", action.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Apply_without_Omarchy_reports_every_change_as_an_error()
    {
        var policy = OmarchyPolicy.Parse("""{"idle": {"screensaver_seconds": 0, "lock_seconds": 0}}""");

        var actions = await OmarchySync.ApplyAsync(policy, OmarchyState.Read(Paths, _proc), Paths, _proc, apply: true, Token);

        Assert.All(actions, action => Assert.True(action.Error));
        Assert.All(actions, action => Assert.Contains("not installed", action.Detail, StringComparison.Ordinal));
    }

    [Fact]
    public void Read_asks_hyprctl_for_the_live_spacing()
    {
        Install();
        _ = _proc.Answer(new Ran(0, """{"option": "general:gaps_in", "css": "2 2 2 2", "set": true}""", string.Empty), "hyprctl", "getoption", "general:gaps_in");
        _ = _proc.Answer(new Ran(0, """{"option": "general:gaps_out", "css": "5 5 5 5", "set": true}""", string.Empty), "hyprctl", "getoption", "general:gaps_out");
        _ = _proc.Answer(new Ran(0, """{"option": "general:border_size", "int": 1, "set": true}""", string.Empty), "hyprctl", "getoption", "general:border_size");

        Assert.Equal(new HyprlandSpacing("2", "5", "1"), OmarchyState.Read(Paths, _proc).Hyprland);
    }

    [Fact]
    public void Read_without_a_running_hyprland_has_no_spacing()
    {
        Install();

        Assert.Null(OmarchyState.Read(Paths, _proc).Hyprland);
    }

    [Fact]
    public async Task Apply_appends_rigs_spacing_block_after_the_users_settings()
    {
        Install();
        Write(Paths.HyprLookAndFeelFile, "-- mine\nhl.config({ decoration = { rounding = 8 } })\n");
        var policy = OmarchyPolicy.Empty with { Hyprland = new HyprlandPolicy(2, 5, 1) };
        var state = OmarchyState.Read(Paths, _proc) with { Hyprland = new HyprlandSpacing("5", "10", "2") };

        var actions = await OmarchySync.ApplyAsync(policy, state, Paths, _proc, apply: true, Token);

        Assert.Equal(3, actions.Count);
        Assert.All(actions, action => Assert.Equal(OmarchySync.AppliedStatus, action.Status));
        var text = await File.ReadAllTextAsync(Paths.HyprLookAndFeelFile, Token);
        Assert.StartsWith("-- mine\nhl.config({ decoration = { rounding = 8 } })\n\n" + OmarchySync.HyprlandBlockBegin, text, StringComparison.Ordinal);
        Assert.Contains("gaps_in = 2,", text, StringComparison.Ordinal);
        Assert.Contains("gaps_out = 5,", text, StringComparison.Ordinal);
        Assert.Contains("border_size = 1,", text, StringComparison.Ordinal);
        Assert.EndsWith(OmarchySync.HyprlandBlockEnd + "\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Rewriting_the_block_replaces_it_and_keeps_what_surrounds_it()
    {
        var first = OmarchySync.WithHyprlandBlock("-- before\n", new HyprlandPolicy(2, 5, 1)) + "-- after\n";

        var second = OmarchySync.WithHyprlandBlock(first, new HyprlandPolicy(0, 0, 0));

        Assert.Equal(1, second.Split(OmarchySync.HyprlandBlockBegin).Length - 1);
        Assert.Contains("gaps_out = 0,", second, StringComparison.Ordinal);
        Assert.DoesNotContain("gaps_out = 5,", second, StringComparison.Ordinal);
        Assert.StartsWith("-- before\n", second, StringComparison.Ordinal);
        Assert.EndsWith("-- after\n", second, StringComparison.Ordinal);
        Assert.Equal(second, OmarchySync.WithHyprlandBlock(second, new HyprlandPolicy(0, 0, 0)));
    }

    [Fact]
    public void A_block_without_its_end_marker_is_refused() =>
        Assert.Throws<InvalidDataException>(() => OmarchySync.WithHyprlandBlock(OmarchySync.HyprlandBlockBegin + "\n", new HyprlandPolicy(2, 5, 1)));
}
