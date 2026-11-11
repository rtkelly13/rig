using Rig.Core.Orca;
using Rig.Core.Workspace;
using static Rig.Core.Tests.Orca.OrcaFixtures;

namespace Rig.Core.Tests.Orca;

/// <summary>The policy file, repo decoding, icons and hook resolution. Ported from <c>tests/test_workspace_orca.py</c>.</summary>
public sealed class OrcaPolicyTests
{
    private static readonly string[] FlaggedModes = ["ask", "accept-edits", "auto", "full-access"];

    [Fact]
    public void The_checked_in_policy_decodes()
    {
        var policy = OrcaPolicy.Load(CheckedInPolicy);

        Assert.Equal(("", "--auto"), (policy.AgentArgs["claude"], policy.AgentArgs["opencode2"]));
        Assert.NotNull(policy.AgentPermissions);
        Assert.Equal("auto", policy.AgentPermissions.Wanted("claude"));
        Assert.Equal("full-access", policy.AgentPermissions.Wanted("antigravity"));
        Assert.Equal("auto", policy.AgentPermissions.Wanted("codex"));
        Assert.Equal(("show", "#ec4899", "Palette"), (policy.Repos["design-system"].ExternalWorktreeVisibility, policy.Repos["design-system"].Color, policy.Repos["design-system"].Icon));
        Assert.Equal(("#6366f1", "SquareTerminal"), (policy.Repos["rig"].Color, policy.Repos["rig"].Icon));
    }

    [Fact]
    public void The_checked_in_policy_badges_every_active_project_shown_in_Orca()
    {
        var policy = OrcaPolicy.Load(CheckedInPolicy);
        var registry = Path.Join(Path.GetDirectoryName(CheckedInPolicy), ManagedProjects.FileName);

        // hearth is managed outside Orca on purpose.
        var shown = ManagedProjects.Load(registry).Where(spec => spec.Enabled && spec.OrcaGroup is not null && spec.Name != "hearth").ToList();

        Assert.NotEmpty(shown);
        Assert.All(shown, spec =>
        {
            Assert.True(policy.Repos.TryGetValue(spec.Name, out var repo), $"{spec.Name} is missing from config/orca.json");
            Assert.False(string.IsNullOrEmpty(repo.Color), $"{spec.Name} has no badge color");
            Assert.False(string.IsNullOrEmpty(repo.Icon), $"{spec.Name} has no icon");
        });
    }

    [Fact]
    public void The_checked_in_policy_keeps_permission_flags_out_of_custom_args()
    {
        // A permission flag in custom arguments makes Orca read them as "mixed" and lock the mode.
        var policy = OrcaPolicy.Load(CheckedInPolicy);
        foreach (var (agent, args) in policy.AgentArgs)
        {
            var flags = FlaggedModes.SelectMany(mode => (AgentPermissions.FlagsFor(mode, agent) ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries));
            Assert.Empty(flags.Intersect(args.Split(' ', StringSplitOptions.RemoveEmptyEntries)));
        }
    }

    [Fact]
    public void The_checked_in_policy_asks_for_auto_only_where_orca_has_it()
    {
        var policy = OrcaPolicy.Load(CheckedInPolicy);
        Assert.NotNull(policy.AgentPermissions);
        var auto = policy.AgentPermissions.Agents.Where(pair => pair.Value == "auto").Select(pair => pair.Key).ToList();

        Assert.NotEmpty(auto);
        Assert.All(auto, agent => Assert.False(string.IsNullOrEmpty(AgentPermissions.FlagsFor("auto", agent))));
    }

    [Fact]
    public void Every_policy_icon_is_one_orca_draws()
    {
        var policy = OrcaPolicy.Load(CheckedInPolicy);

        Assert.All(policy.Repos.Values.Select(repo => repo.Icon).OfType<string>(), icon => Assert.True(OrcaRepoIcon.Renders(icon), icon));
    }

    [Theory]
    [InlineData("""{"repos": {"x": {"external_worktree_visibility": "sideways"}}}""")]
    [InlineData("""{"agent_permissions": {"agents": {"claude": "yolo"}}}""")]
    [InlineData("""{"agent_sleep": {"idle_minutes": 30}}""")]
    [InlineData("""{"agent_sleep": {"enabled": true, "idle_minutes": 0}}""")]
    [InlineData("""{"agent_args": {"claude": 1}}""")]
    [InlineData("[]")]
    [InlineData("not json")]
    public void A_malformed_policy_is_refused(string text) =>
        Assert.Throws<InvalidDataException>(() => OrcaPolicy.Parse(text));

    [Fact]
    public void A_policy_fills_its_defaults()
    {
        var policy = OrcaPolicy.Parse("""{"agent_permissions": {"agents": {}}, "agent_sleep": {"enabled": false}}""");

        Assert.Equal("own", policy.AgentPermissions?.Default);
        Assert.Equal(new AgentSleepPolicy(false, 30), policy.AgentSleep);
        Assert.Empty(policy.AgentArgs);
        Assert.Empty(policy.Repos);
    }

    [Fact]
    public void Live_repos_read_every_attribute()
    {
        var repo = Repo("rig", "/code/rig", "pnpm install", "pnpm clean", "show", "develop", "#6366f1", new("lucide", Name: "terminal"));

        Assert.Equal(new Scripts("pnpm install", "pnpm clean"), repo.HookSettings?.Scripts);
        Assert.Equal(("show", "develop", "#6366f1", "terminal"), (repo.ExternalWorktreeVisibility, repo.BaseRef, repo.Color, repo.IconIdentifier));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{"ok": false}""")]
    [InlineData("""{"result": {"repos": [1]}}""")]
    [InlineData("""{"result": {"repos": [{"displayName": "no path"}]}}""")]
    public void A_failed_repo_list_is_null(string? output) => Assert.Null(OrcaRepo.ParseList(output));

    [Fact]
    public void A_repo_with_no_icon_or_settings_reads_empty()
    {
        var repos = OrcaRepo.ParseList("""{"result": {"repos": [{"path": "/r"}]}}""");

        var repo = Assert.Single(repos!);
        Assert.Equal(("", "", "", false), (repo.BaseRef, repo.Color, repo.IconIdentifier, repo.IconMatches("Box")));
        Assert.Null(repo.HookSettings);
    }

    [Fact]
    public void Icon_payloads_and_matching()
    {
        Assert.Equal(new OrcaRepoIcon("lucide", Name: "terminal"), OrcaRepoIcon.For("terminal"));
        Assert.Equal(new OrcaRepoIcon("emoji", Emoji: "🚀"), OrcaRepoIcon.For("emoji:🚀"));
        Assert.Equal(new OrcaRepoIcon("image", Src: "https://github.com/rtkelly13.png", Source: "github"), OrcaRepoIcon.For("https://github.com/rtkelly13.png"));
        Assert.Equal("upload", OrcaRepoIcon.For("http://example.com/a.png").Source);

        var repo = Repo("test", "/path", icon: new("lucide", Name: "palette"));
        Assert.True(repo.IconMatches("palette"));
        Assert.True(repo.IconMatches("lucide:palette"));
        Assert.False(repo.IconMatches("terminal"));
        Assert.True(Repo("e", "/e", icon: OrcaRepoIcon.For("emoji:🚀")).IconMatches("emoji:🚀"));
        Assert.Equal("🚀", Repo("e", "/e", icon: OrcaRepoIcon.For("emoji:🚀")).IconIdentifier);
        Assert.True(Repo("i", "/i", icon: OrcaRepoIcon.For("https://x/a.png")).IconMatches("https://x/a.png"));
        Assert.Equal("https://x/a.png", Repo("i", "/i", icon: OrcaRepoIcon.For("https://x/a.png")).IconIdentifier);
    }

    [Fact]
    public void Only_lucide_names_from_orcas_picker_render()
    {
        Assert.True(OrcaRepoIcon.Renders("Palette"));
        Assert.True(OrcaRepoIcon.Renders("emoji:🚀"));
        Assert.False(OrcaRepoIcon.Renders("terminal"));
    }

    [Fact]
    public void Icon_json_leaves_out_null_fields()
    {
        Assert.Equal("""{"type":"lucide","name":"Box"}""", OrcaRepoIcon.For("Box").ToJson().ToJsonString());
    }

    [Theory]
    [InlineData(null, "", "shared-only")]
    [InlineData(null, "old", "local-only")]
    [InlineData("run-both", "old", "run-both")]
    [InlineData("shared-only", "old", "shared-only")]
    public void Hook_sources_resolve_as_orca_does(string? policy, string local, string effective)
    {
        var source = OrcaHooks.Resolve("setup", new HookSettings(Scripts: new(local), CommandSourcePolicy: policy), new Scripts("uv sync"));

        Assert.Equal(effective, source.Policy);
        Assert.Equal(("uv sync", local), (source.Shared, source.Local));
    }

    [Theory]
    [InlineData("scripts:\n  setup: uv sync\n  archive: x\n", "uv sync", "x")]
    [InlineData("other: 1\n", "", "")]
    [InlineData("scripts: [1]\n", "", "")]
    public void Orca_yaml_scripts_are_read(string yaml, string setup, string archive)
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Join(dir.Path, OrcaHooks.OrcaYaml), yaml);

        Assert.Equal(new Scripts(setup, archive), OrcaHooks.ReadOrcaYaml(dir.Path));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("- a list\n")]
    [InlineData("a: [unclosed\n")]
    public void An_unusable_orca_yaml_is_null(string? yaml)
    {
        using var dir = new TempDir();
        if (yaml is not null)
        {
            File.WriteAllText(Path.Join(dir.Path, OrcaHooks.OrcaYaml), yaml);
        }

        Assert.Null(OrcaHooks.ReadOrcaYaml(dir.Path));
    }

    [Fact]
    public void Deferring_to_orca_yaml_clears_scripts_and_keeps_the_rest()
    {
        var deferred = OrcaHooks.DeferToOrcaYaml(new HookSettings("auto", "ask", Scripts: new("old"), CommandSourcePolicy: "local-only"));

        Assert.Equal(new HookSettings("auto", "ask", null, new Scripts(), "shared-only"), deferred);
        Assert.Equal(
            """{"mode":"auto","setupRunPolicy":"ask","scripts":{"setup":"","archive":""},"commandSourcePolicy":"shared-only"}""",
            deferred.ToJson().ToJsonString());
        Assert.Equal(new HookSettings(Scripts: new(), CommandSourcePolicy: "shared-only"), OrcaHooks.DeferToOrcaYaml(null));
    }
}
