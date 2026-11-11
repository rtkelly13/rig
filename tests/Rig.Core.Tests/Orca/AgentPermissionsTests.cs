using Rig.Core.Orca;
using Rig.Core.Packages;
using static Rig.Core.Tests.Orca.OrcaFixtures;

namespace Rig.Core.Tests.Orca;

/// <summary>Agent permission modes and launch arguments in the doctor and sync. Ported from <c>tests/test_workspace_orca_agent_permissions.py</c>.</summary>
public sealed class AgentPermissionsTests
{
    private static readonly AgentPermissionsPolicy Permissions = new("own", new Dictionary<string, string> { ["claude"] = "auto", ["antigravity"] = "full-access", ["opencode"] = "own" });

    private static readonly OrcaPolicy WithPermissions = PolicyOf(new Dictionary<string, string> { ["claude"] = "", ["opencode"] = "--auto" }, Permissions);

    // Orca 1.4.255 straight after its upgrade: one Full access default, custom arguments emptied.
    private static readonly OrcaSettings Upgraded = new(
        new Dictionary<string, string> { ["claude"] = "", ["opencode"] = "", ["antigravity"] = "" },
        PermissionDefault: "full-access",
        PermissionOverrides: new Dictionary<string, string> { ["opencode"] = "own", ["omp"] = "own" });

    private static readonly Dictionary<string, string> UpgradedLaunch = new()
    {
        ["claude"] = "--dangerously-skip-permissions",
        ["opencode"] = "",
        ["antigravity"] = "--dangerously-skip-permissions",
    };

    private static readonly OrcaSettings Wanted = new(
        new Dictionary<string, string> { ["claude"] = "", ["opencode"] = "--auto" },
        PermissionDefault: "own",
        PermissionOverrides: new Dictionary<string, string> { ["claude"] = "auto", ["antigravity"] = "full-access", ["omp"] = "own" });

    private static readonly Dictionary<string, string> WantedLaunch = new()
    {
        ["claude"] = "--permission-mode auto",
        ["opencode"] = "--auto",
        ["antigravity"] = "--dangerously-skip-permissions",
    };

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void The_full_access_upgrade_fails_claudes_permission_and_launch_line()
    {
        var statuses = Statuses(PermissionSnapshot(Upgraded, UpgradedLaunch));

        Assert.Equal(CheckStatus.Fail, statuses[("settings", "permission default")]);
        Assert.Equal(CheckStatus.Fail, statuses[("agent claude", "permission")]);
        Assert.Equal(CheckStatus.Fail, statuses[("agent claude", "launch args")]);
        Assert.Equal(CheckStatus.Fail, statuses[("agent opencode", "custom args")]);
        Assert.Equal(CheckStatus.Ok, statuses[("agent antigravity", "launch args")]);
    }

    [Fact]
    public void The_wanted_settings_pass()
    {
        var statuses = Statuses(PermissionSnapshot(Wanted, WantedLaunch));

        Assert.Equal([CheckStatus.Ok], statuses.Values.Distinct());
        Assert.DoesNotContain(("agent omp", "permission"), statuses.Keys); // Orca offers omp no mode
    }

    [Fact]
    public void Launch_order_does_not_matter()
    {
        var check = Assert.Single(AgentPermissions.LaunchChecks(
            Only("claude", "auto"),
            new Dictionary<string, string> { ["claude"] = "--verbose" },
            new Dictionary<string, string> { ["claude"] = "--verbose --permission-mode auto" }));

        Assert.True(check.Ok);
    }

    [Fact]
    public void A_mode_rig_has_no_flags_for_is_not_checked_at_launch() =>
        Assert.Empty(AgentPermissions.LaunchChecks(Only("copilot", "auto"), new Dictionary<string, string>(), new Dictionary<string, string> { ["copilot"] = "" }));

    [Fact]
    public void An_agent_orca_offers_no_mode_launches_with_its_custom_args_alone()
    {
        var args = new Dictionary<string, string> { ["opencode2"] = "--auto" };

        Assert.True(Assert.Single(AgentPermissions.LaunchChecks(new("own", new Dictionary<string, string>()), args, args)).Ok);
    }

    [Fact]
    public void An_unexpected_override_is_caught()
    {
        var checks = AgentPermissions.PermissionChecks(Permissions, ["gemini"], "own", new Dictionary<string, string> { ["gemini"] = "full-access" }).ToDictionary(c => c.Subject);

        Assert.False(checks["agent gemini"].Ok);
        Assert.Equal("Orca Settings → Agents → gemini: set permission to Own", checks["agent gemini"].Fix);
    }

    [Fact]
    public void Orca_down_skips_the_launch_check_but_still_reads_the_backup()
    {
        var byCheck = Diagnose(WithPermissions, PermissionSnapshot(Upgraded, null)).ToDictionary(f => (f.Subject, f.Check), f => f.Status);

        Assert.Equal(CheckStatus.Skip, byCheck[("agents", "launch args")]);
        Assert.Equal(CheckStatus.Fail, byCheck[("agent claude", "permission")]);
        Assert.DoesNotContain(("agent claude", "launch args"), byCheck.Keys);
    }

    [Fact]
    public void An_override_orca_offers_no_choice_for_is_not_checked()
    {
        // Orca kept OpenCode at Full access through the upgrade, but launches it the same in every mode.
        Assert.False(AgentPermissions.IsConfigurable("opencode"));

        var checks = AgentPermissions.PermissionChecks(Permissions, ["opencode2"], "own", new Dictionary<string, string> { ["opencode2"] = "full-access" });

        Assert.Equal(["agent antigravity", "agent claude", "settings"], checks.Select(c => c.Subject).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_missing_stored_default_reads_as_own()
    {
        var check = AgentPermissions.PermissionChecks(Permissions, [], null, null)[0];

        Assert.Equal(("settings", "own", true), (check.Subject, check.Have, check.Ok));
    }

    [Fact]
    public void Launch_lines_carry_the_mode_flags_and_custom_args_for_agents_that_differ()
    {
        Assert.Equal(
            new Dictionary<string, string> { ["claude"] = "--permission-mode auto", ["opencode"] = "--auto" },
            AgentPermissions.LaunchLines(Permissions, WithPermissions.AgentArgs, UpgradedLaunch));
        Assert.Empty(AgentPermissions.LaunchLines(Permissions, WithPermissions.AgentArgs, WantedLaunch));
    }

    [Fact]
    public async Task Sync_plans_launch_lines_and_writes_only_with_apply()
    {
        var live = new FakeOrcaLive(launch: lines => UpgradedLaunch.Concat(lines).GroupBy(p => p.Key).ToDictionary(g => g.Key, g => g.Last().Value));
        var snapshot = PermissionSnapshot(Upgraded, UpgradedLaunch);

        var planned = await OrcaSync.SyncAgentLaunchAsync(WithPermissions, snapshot, false, live, Token);

        Assert.Equal([("agent claude", "update", false), ("agent opencode", "update", false)], planned.Select(a => (a.Repo, a.Action, a.Applied)));
        Assert.Empty(live.Lines);

        var applied = await OrcaSync.SyncAgentLaunchAsync(WithPermissions, snapshot, true, live, Token);

        Assert.All(applied, a => Assert.True(a.Applied));
        Assert.Equal(new Dictionary<string, string> { ["claude"] = "--permission-mode auto", ["opencode"] = "--auto" }, Assert.Single(live.Lines));
    }

    [Fact]
    public async Task A_line_orca_does_not_take_is_an_error_naming_the_manual_fix()
    {
        var served = new Dictionary<string, string> { ["claude"] = "--permission-mode auto" };

        var action = Assert.Single(await OrcaSync.SyncAgentLaunchAsync(
            PolicyOf(permissions: Only("claude", "own")),
            PermissionSnapshot(Wanted, served),
            true,
            new FakeOrcaLive(launch: _ => served),
            Token));

        Assert.Equal(("error", false), (action.Action, action.Applied));
        Assert.Equal("Orca serves '--permission-mode auto'; set this agent in Orca Settings → Agents", action.Detail);
    }

    [Fact]
    public async Task Sync_reports_orca_going_away_mid_write()
    {
        var actions = await OrcaSync.SyncAgentLaunchAsync(WithPermissions, PermissionSnapshot(Upgraded, UpgradedLaunch), true, new FakeOrcaLive(launch: _ => null), Token);

        Assert.Equal(["error"], actions.Select(a => a.Action).Distinct());
    }

    [Fact]
    public async Task Sync_leaves_agents_alone_without_a_permission_policy_or_a_running_orca()
    {
        Assert.Empty(await OrcaSync.SyncAgentLaunchAsync(PolicyOf(new Dictionary<string, string> { ["claude"] = "" }), PermissionSnapshot(Upgraded, UpgradedLaunch), true, null, Token));
        Assert.Empty(await OrcaSync.SyncAgentLaunchAsync(WithPermissions, PermissionSnapshot(Upgraded, null), true, null, Token));
        Assert.Empty(await OrcaSync.SyncAgentLaunchAsync(WithPermissions, PermissionSnapshot(Wanted, WantedLaunch), true, null, Token));
        Assert.Empty(OrcaSync.SettingsManualActions(WithPermissions, PermissionSnapshot(Upgraded, UpgradedLaunch)));
    }

    [Fact]
    public void Every_mode_has_a_label_and_own_has_no_flags()
    {
        Assert.All(AgentPermissions.Modes, mode => Assert.False(string.IsNullOrEmpty(AgentPermissions.Label(mode))));
        Assert.Equal("", AgentPermissions.FlagsFor("own", "claude"));
        Assert.Null(AgentPermissions.FlagsFor("auto", "copilot"));
    }

    private static AgentPermissionsPolicy Only(string agent, string mode) => new("own", new Dictionary<string, string> { [agent] = mode });

    private static OrcaSnapshot PermissionSnapshot(OrcaSettings settings, IReadOnlyDictionary<string, string>? launch) =>
        Snapshot(new SettingsCopy("profile-state.db.backup.1.db", Now, settings), [], launch);

    private static Dictionary<(string Subject, string Check), CheckStatus> Statuses(OrcaSnapshot snapshot) =>
        Diagnose(WithPermissions, snapshot)
            .Where(f => f.Subject != "settings" || f.Check.Contains("permission", StringComparison.Ordinal))
            .ToDictionary(f => (f.Subject, f.Check), f => f.Status);
}
