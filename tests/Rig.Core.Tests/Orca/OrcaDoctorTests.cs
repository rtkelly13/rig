using Rig.Core.Orca;
using Rig.Core.Packages;
using static Rig.Core.Tests.Orca.OrcaFixtures;

namespace Rig.Core.Tests.Orca;

/// <summary><see cref="OrcaDoctor.Diagnose"/>. Ported from <c>tests/test_workspace_orca.py</c>.</summary>
public sealed class OrcaDoctorTests
{
    private static readonly Dictionary<string, string> NoArgs = [];

    [Fact]
    public void An_agent_args_mismatch_fails_with_its_fix()
    {
        var policy = PolicyOf(new Dictionary<string, string> { ["claude"] = "", ["opencode"] = "--auto", ["antigravity"] = "--skip" });
        var snapshot = Snapshot(Copy(new Dictionary<string, string> { ["claude"] = "--dangerously-skip-permissions", ["antigravity"] = "--skip" }), []);

        var bySubject = Diagnose(policy, snapshot).Where(f => f.Check == "custom args").ToDictionary(f => f.Subject);

        Assert.Equal(CheckStatus.Fail, bySubject["agent claude"].Status);
        Assert.Equal("Orca Settings → Agents → claude: set custom arguments to \"\"", bySubject["agent claude"].Fix);
        Assert.Equal(CheckStatus.Fail, bySubject["agent opencode"].Status);
        Assert.Contains("\"--auto\"", bySubject["agent opencode"].Detail, StringComparison.Ordinal);
        Assert.Equal((CheckStatus.Ok, "--skip"), (bySubject["agent antigravity"].Status, bySubject["agent antigravity"].Detail));
    }

    [Fact]
    public void A_stale_backup_warns()
    {
        var findings = Diagnose(OrcaPolicy.Empty, Snapshot(Copy(NoArgs, ageHours: 48), []));

        var source = Assert.Single(findings, f => f.Check == "source");
        Assert.Equal((CheckStatus.Warn, "profile-state.db.backup.1.db, saved 48.0h ago"), (source.Status, source.Detail));
    }

    [Fact]
    public void Missing_sources_skip()
    {
        var findings = Diagnose(OrcaPolicy.Empty, Snapshot(null, null));

        Assert.Equal([CheckStatus.Skip, CheckStatus.Skip], findings.Select(f => f.Status));
        Assert.Contains("/profile", findings[0].Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void The_workspace_dir_expands_home()
    {
        var policy = PolicyOf(workspaceDir: "~/orca/workspaces");

        var ok = Diagnose(policy, Snapshot(Copy(NoArgs, "/home/u/orca/workspaces"), []));
        var tilde = Diagnose(policy, Snapshot(Copy(NoArgs, "~/orca/workspaces"), []));
        var bad = Diagnose(policy, Snapshot(Copy(NoArgs, "/elsewhere"), []));
        var unset = Diagnose(policy, Snapshot(Copy(NoArgs, ""), []));

        Assert.Equal(CheckStatus.Ok, Assert.Single(ok, f => f.Check == "workspace dir").Status);
        Assert.Equal(CheckStatus.Ok, Assert.Single(tilde, f => f.Check == "workspace dir").Status);
        Assert.Equal(CheckStatus.Fail, Assert.Single(bad, f => f.Check == "workspace dir").Status);
        Assert.Equal("unset, want /home/u/orca/workspaces", Assert.Single(unset, f => f.Check == "workspace dir").Detail);
    }

    public static TheoryData<bool, bool?, long?, CheckStatus, string> SleepCases => new()
    {
        { true, true, 1_800_000, CheckStatus.Ok, "on, 30m idle" },
        // Orca reads a missing or out-of-range window as its 30-minute default.
        { true, true, null, CheckStatus.Ok, "on, 30m idle" },
        { true, true, 5, CheckStatus.Ok, "on, 30m idle" },
        { true, false, null, CheckStatus.Fail, "off, want on, 30m idle" },
        { true, true, 600_000, CheckStatus.Fail, "on, 10m idle, want on, 30m idle" },
        { false, null, null, CheckStatus.Ok, "off" },
    };

    [Theory]
    [MemberData(nameof(SleepCases))]
    public void Agent_sleep_is_checked_against_the_policy(bool want, bool? enabled, long? idleMs, CheckStatus status, string detail)
    {
        var findings = Diagnose(PolicyOf(sleep: new(want)), SleepSnapshot(enabled, idleMs));

        var finding = Assert.Single(findings, f => f.Check == "agent sleep");
        Assert.Equal((status, detail), (finding.Status, finding.Detail));
    }

    [Fact]
    public void Agent_sleep_is_not_checked_without_a_policy() =>
        Assert.DoesNotContain(Diagnose(OrcaPolicy.Empty, SleepSnapshot(false, null)), f => f.Check == "agent sleep");

    [Fact]
    public void Sync_reports_agent_sleep_as_manual()
    {
        var policy = PolicyOf(sleep: new(true, 30));

        var action = Assert.Single(OrcaSync.SettingsManualActions(policy, SleepSnapshot(false, null)));

        Assert.Equal(("manual", "off", "on, 30m idle", false), (action.Action, action.OldValue, action.NewValue, action.Applied));
        Assert.Contains("Settings → Experimental", action.Detail, StringComparison.Ordinal);
        Assert.Empty(OrcaSync.SettingsManualActions(policy, SleepSnapshot(true, 1_800_000)));
        Assert.Empty(OrcaSync.SettingsManualActions(policy, Snapshot(null, [])));
        Assert.Empty(OrcaSync.SettingsManualActions(OrcaPolicy.Empty, SleepSnapshot(false, null)));
    }

    [Fact]
    public void A_repo_policy_checks_every_attribute()
    {
        var policy = Repos(
            ("rig", new RepoPolicy(Setup: "uv sync && pnpm install", Archive: "uv cache prune", Color: "#6366f1", BaseRef: "main", Icon: "SquareTerminal")),
            ("design-system", new RepoPolicy(ExternalWorktreeVisibility: "show", Color: "#ec4899", Icon: "Palette")),
            ("absent", new RepoPolicy(Setup: "x")),
            ("elsewhere", new RepoPolicy(Path: "~/code/personal/elsewhere")));
        OrcaRepo[] repos =
        [
            Repo("rig", "/code/rig", setup: "pnpm install", color: "#737373", icon: new("lucide", Name: "Code2")),
            Repo("design-system", "/code/design-system", color: "#ec4899", icon: new("lucide", Name: "Palette")),
        ];

        var byKey = Diagnose(policy, Snapshot(null, repos)).ToDictionary(f => (f.Subject, f.Check));

        Assert.Equal(CheckStatus.Fail, byKey[("repo rig", "setup script")].Status);
        Assert.Equal(CheckStatus.Fail, byKey[("repo rig", "archive script")].Status);
        Assert.Equal(CheckStatus.Fail, byKey[("repo rig", "badge color")].Status);
        Assert.Equal(CheckStatus.Ok, byKey[("repo rig", "base ref")].Status);
        Assert.Equal(CheckStatus.Fail, byKey[("repo rig", "icon")].Status);
        Assert.Equal(CheckStatus.Fail, byKey[("repo design-system", "external worktrees")].Status);
        Assert.Equal(CheckStatus.Ok, byKey[("repo design-system", "badge color")].Status);
        Assert.Equal(CheckStatus.Ok, byKey[("repo design-system", "icon")].Status);
        Assert.Equal((CheckStatus.Fail, "orca repo add --path /code/absent"), (byKey[("repo absent", "registered")].Status, byKey[("repo absent", "registered")].Fix));
        Assert.Equal("orca repo add --path /code-root/personal/elsewhere", byKey[("repo elsewhere", "registered")].Fix);
    }

    public static TheoryData<string?, string, CheckStatus, string> SetupCases => new()
    {
        { "uv sync", "{}", CheckStatus.Ok, "from orca.yaml" },
        { "uv sync", """{"scripts": {"setup": "uv sync"}}""", CheckStatus.Warn, "overrides orca.yaml" },
        { "uv sync", """{"scripts": {"setup": "old"}, "commandSourcePolicy": "local-only"}""", CheckStatus.Fail, "overrides" },
        { "uv sync", """{"scripts": {"setup": "old"}, "commandSourcePolicy": "shared-only"}""", CheckStatus.Ok, "from orca.yaml" },
        { null, """{"scripts": {"setup": "uv sync"}}""", CheckStatus.Warn, "only in Orca's local settings" },
        { null, "{}", CheckStatus.Fail, "not in orca.yaml" },
        { "pnpm i", "{}", CheckStatus.Fail, "orca.yaml has \"pnpm i\"" },
    };

    [Theory]
    [MemberData(nameof(SetupCases))]
    public void The_setup_check_follows_orcas_source_resolution(string? yamlSetup, string hooks, CheckStatus status, string detail)
    {
        using var dir = new TempDir();
        var repoPath = dir.Folder("rig");
        if (yamlSetup is not null)
        {
            WriteOrcaYaml(repoPath, yamlSetup);
        }

        var findings = Diagnose(Repos(("rig", new RepoPolicy(Setup: "uv sync"))), Snapshot(null, [Hooked(repoPath, hooks)]), Under(dir.Path));

        var finding = Assert.Single(findings, f => f.Check == "setup script");
        Assert.Equal(status, finding.Status);
        Assert.Contains(detail, finding.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unregistered_managed_project_warns()
    {
        using var dir = new TempDir();
        dir.Folder(Path.Join("blog", ".git"));
        dir.Folder(Path.Join("rig", ".git"));
        dir.Folder(Path.Join("named", ".git"));
        var policy = Repos(("named", new RepoPolicy()));

        var findings = Diagnose(policy, Snapshot(null, [Repo("rig", Path.Join(dir.Path, "rig"))]), Under(dir.Path), "blog", "rig", "named", "not-cloned");

        Assert.Equal(["repo blog"], findings.Where(f => f.Status == CheckStatus.Warn).Select(f => f.Subject));
    }

    [Fact]
    public void The_worktree_location_is_checked()
    {
        using var dir = new TempDir();
        var repoPath = dir.Folder("rig");
        var policy = Repos(("rig", new RepoPolicy(WorktreeBasePath: "temp")));

        var finding = Assert.Single(Diagnose(policy, Snapshot(null, [Hooked(repoPath, "{}")]), Under(dir.Path)), f => f.Check == "worktree location");

        Assert.Equal(CheckStatus.Fail, finding.Status);
    }

    [Fact]
    public void Repo_paths_follow_the_machines_code_root()
    {
        var paths = new OrcaPaths("/estate", "/root", "/home/u");

        Assert.Equal("/root/personal/blog", paths.RepoPath("~/code/personal/blog"));
        Assert.Equal("/home/u/elsewhere/blog", paths.RepoPath("~/elsewhere/blog"));
        Assert.Equal("/abs", paths.RepoPath("/abs"));
        Assert.Equal("/home/u", paths.Expand("~"));
    }

    private static OrcaSnapshot SleepSnapshot(bool? enabled, long? idleMs)
    {
        var copy = Copy(NoArgs);
        return Snapshot(copy with { Settings = copy.Settings with { AgentSleep = enabled, AgentSleepIdleMs = idleMs } }, []);
    }
}
