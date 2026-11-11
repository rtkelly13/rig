using Rig.Core.Orca;
using static Rig.Core.Tests.Orca.OrcaFixtures;

namespace Rig.Core.Tests.Orca;

/// <summary><see cref="OrcaSync.SyncReposAsync"/>. Ported from <c>tests/test_workspace_orca.py</c>.</summary>
public sealed class OrcaSyncTests : IDisposable
{
    private static readonly HashSet<string> None = [];

    private readonly TempDir _dir = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task A_dry_run_plans_without_calling_orca()
    {
        var repoPath = _dir.Folder("rig");
        var live = new FakeOrcaLive(ok: false);

        var actions = await Sync(Repos(("rig", new RepoPolicy(Setup: "uv sync", Color: "#ec4899", BaseRef: "develop"))), [Hooked(repoPath, "{}")], new FakeOrcaCli(), live);

        var byField = actions.ToDictionary(a => a.Field);
        Assert.Equal("manual", byField["setup script"].Action);
        Assert.Equal("update", byField["badge color"].Action);
        Assert.Equal("update", byField["base ref"].Action);
        Assert.DoesNotContain(actions, a => a.Applied);
        Assert.Empty(live.Updates);
    }

    [Fact]
    public async Task Sync_never_writes_scripts_from_the_policy()
    {
        var repoPath = _dir.Folder("rig");
        var live = new FakeOrcaLive();

        var actions = await Sync(
            Repos(("rig", new RepoPolicy(Setup: "uv sync", Color: "#ec4899", Icon: "terminal"))),
            [Hooked(repoPath, """{"scripts": {"setup": "old"}}""")],
            new FakeOrcaCli(),
            live,
            apply: true);

        var (path, updates) = Assert.Single(live.Updates);
        Assert.Equal(repoPath, path);
        Assert.Equal(new RepoUpdates("#ec4899"), updates); // the icon already matches
        var byField = actions.ToDictionary(a => a.Field);
        Assert.True(byField["badge color"].Applied);
        Assert.Equal(("manual", false), (byField["setup script"].Action, byField["setup script"].Applied));
    }

    [Fact]
    public async Task Sync_clears_a_shadowing_local_script_and_keeps_other_hook_settings()
    {
        var repoPath = Path.Join(_dir.Path, "rig");
        WriteOrcaYaml(repoPath, "uv sync");
        const string Hooks = """
            {"mode": "auto", "setupRunPolicy": "ask", "setupAgentStartupPolicy": "wait-for-setup",
             "scripts": {"setup": "old", "archive": ""}, "commandSourcePolicy": "local-only"}
            """;
        var live = new FakeOrcaLive();

        var actions = await Sync(Repos(("rig", new RepoPolicy(Setup: "uv sync"))), [Hooked(repoPath, Hooks)], new FakeOrcaCli(), live, apply: true);

        var (_, updates) = Assert.Single(live.Updates);
        Assert.Equal(new HookSettings("auto", "ask", "wait-for-setup", new Scripts(), "shared-only"), updates.HookSettings);
        Assert.Null(updates.BadgeColor);
        Assert.Equal([true], actions.Select(a => a.Applied));
    }

    [Fact]
    public async Task Sync_will_not_clear_a_local_script_orca_yaml_lacks()
    {
        var repoPath = Path.Join(_dir.Path, "rig");
        WriteOrcaYaml(repoPath, "uv sync");
        var live = new FakeOrcaLive();

        var actions = await Sync(
            Repos(("rig", new RepoPolicy(Setup: "uv sync"))),
            [Hooked(repoPath, """{"scripts": {"setup": "old", "archive": "uv cache prune"}}""")],
            new FakeOrcaCli(),
            live,
            apply: true);

        Assert.Empty(live.Updates);
        var action = Assert.Single(actions);
        Assert.Equal("manual", action.Action);
        Assert.Contains("archive", action.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unreachable_orca_is_an_error()
    {
        var repoPath = _dir.Folder("rig");

        var actions = await Sync(Repos(("rig", new RepoPolicy(Color: "#ec4899", Icon: "palette"))), [Hooked(repoPath, "{}")], new FakeOrcaCli(), new FakeOrcaLive(ok: false), apply: true);

        Assert.Equal([("badge color", "error", false), ("icon", "error", false)], actions.Select(a => (a.Field, a.Action, a.Applied)));
    }

    [Fact]
    public async Task Visibility_and_base_ref_are_set_through_the_cli()
    {
        var repoPath = _dir.Folder("rig");
        var cli = new FakeOrcaCli(args => args[1] == "set-base-ref" ? null : "{}");
        var live = new FakeOrcaLive();

        var actions = await Sync(Repos(("rig", new RepoPolicy(ExternalWorktreeVisibility: "show", BaseRef: "develop"))), [Hooked(repoPath, "{}")], cli, live, apply: true);

        var selector = $"path:{repoPath}";
        Assert.Equal(
            [
                ["repo", "set", "--repo", selector, "--external-worktree-visibility", "show"],
                ["repo", "set-base-ref", "--repo", selector, "--ref", "develop"],
            ],
            cli.Calls);
        Assert.Empty(live.Updates);
        Assert.Equal([("external worktrees", true), ("base ref", false)], actions.Select(a => (a.Field, a.Applied)));
    }

    [Fact]
    public async Task A_missing_repo_is_registered()
    {
        _dir.Folder("blog");
        var cli = new FakeOrcaCli(_ => "{}");

        var planned = await Sync(Repos(("blog", new RepoPolicy(Color: "#14b8a6"))), [], new FakeOrcaCli(), new FakeOrcaLive());
        var applied = await Sync(Repos(("blog", new RepoPolicy(Color: "#14b8a6"))), [], cli, new FakeOrcaLive(), apply: true);

        Assert.Equal([("register", false)], planned.Select(a => (a.Action, a.Applied)));
        Assert.Equal([("register", true)], applied.Select(a => (a.Action, a.Applied)));
        Assert.Equal(["repo", "add", "--path", OrcaPaths.Posix(Path.Join(_dir.Path, "blog"))], cli.Calls.Single().Take(4));
    }

    [Fact]
    public async Task A_registration_failure_is_an_error()
    {
        _dir.Folder("blog");

        var actions = await Sync(Repos(("blog", new RepoPolicy(Color: "#14b8a6"))), [], new FakeOrcaCli(_ => null), new FakeOrcaLive(), apply: true);

        Assert.Equal([("error", false)], actions.Select(a => (a.Action, a.Applied)));
    }

    [Fact]
    public async Task A_retired_project_the_policy_still_names_is_never_registered()
    {
        _dir.Folder("the-vault-system");

        var actions = await OrcaSync.SyncReposAsync(
            Repos(("the-vault-system", new RepoPolicy(Color: "#14b8a6"))),
            Snapshot(null, []),
            Under(_dir.Path),
            apply: true,
            null,
            new FakeOrcaCli(),
            new FakeOrcaLive(),
            new HashSet<string> { "the-vault-system" },
            Token);

        Assert.Equal([("the-vault-system", "manual", false)], actions.Select(a => (a.Repo, a.Action, a.Applied)));
    }

    [Fact]
    public async Task Unlisted_repos_are_an_error_and_a_target_repo_limits_the_rest()
    {
        var unlisted = await OrcaSync.SyncReposAsync(Repos(("rig", new RepoPolicy())), Snapshot(null, null), Under(_dir.Path), false, null, new FakeOrcaCli(), new FakeOrcaLive(), None, Token);
        var targeted = await OrcaSync.SyncReposAsync(
            Repos(("rig", new RepoPolicy(Color: "#000000")), ("blog", new RepoPolicy(Color: "#000000"))),
            Snapshot(null, [Hooked(_dir.Folder("rig"), "{}")]),
            Under(_dir.Path),
            false,
            "rig",
            new FakeOrcaCli(),
            new FakeOrcaLive(),
            None,
            Token);

        Assert.Equal([("all", "error")], unlisted.Select(a => (a.Repo, a.Action)));
        Assert.Equal(["rig"], targeted.Select(a => a.Repo));
    }

    [Fact]
    public async Task The_worktree_location_is_synced()
    {
        var repoPath = _dir.Folder("rig");
        var live = new FakeOrcaLive();

        var actions = await Sync(Repos(("rig", new RepoPolicy(WorktreeBasePath: "temp"))), [Hooked(repoPath, "{}")], new FakeOrcaCli(), live, apply: true);

        var (_, updates) = Assert.Single(live.Updates);
        Assert.Equal(new RepoUpdates(WorktreeBasePath: "temp"), updates);
        Assert.Equal([("worktree location", true)], actions.Select(a => (a.Field, a.Applied)));
    }

    [Fact]
    public async Task A_matching_worktree_location_needs_no_update()
    {
        var repo = Decode(new() { ["path"] = _dir.Folder("rig"), ["displayName"] = "rig", ["worktreeBasePath"] = "temp" });

        Assert.Empty(await Sync(Repos(("rig", new RepoPolicy(WorktreeBasePath: "temp"))), [repo], new FakeOrcaCli(), new FakeOrcaLive()));
    }

    [Fact]
    public async Task A_mismatched_icon_is_sent_whole()
    {
        var live = new FakeOrcaLive();

        await Sync(Repos(("rig", new RepoPolicy(Icon: "emoji:🚀"))), [Hooked(_dir.Folder("rig"), "{}")], new FakeOrcaCli(), live, apply: true);

        Assert.Equal(new RepoUpdates(RepoIcon: new("emoji", Emoji: "🚀")), Assert.Single(live.Updates).Updates);
        Assert.Equal("🚀", (string?)live.Updates[0].Updates.ToJson()["repoIcon"]?["emoji"]);
    }

    [Fact]
    public void Repo_updates_leave_out_null_fields_and_keep_empty_ones() =>
        Assert.Equal(
            """{"badgeColor":"#000","hookSettings":{"scripts":{"setup":"","archive":""}},"worktreeBasePath":""}""",
            new RepoUpdates("#000", HookSettings: new(Scripts: new()), WorktreeBasePath: "").ToJson().ToJsonString());

    private Task<IReadOnlyList<SyncAction>> Sync(OrcaPolicy policy, IReadOnlyList<OrcaRepo> repos, FakeOrcaCli cli, FakeOrcaLive live, bool apply = false) =>
        OrcaSync.SyncReposAsync(policy, Snapshot(null, repos), Under(_dir.Path), apply, null, cli, live, None, Token);
}
