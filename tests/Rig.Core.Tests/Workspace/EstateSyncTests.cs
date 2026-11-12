using System.Text.Json.Nodes;
using CsCheck;
using Rig.Core.Roots;
using Rig.Core.Workspace;

namespace Rig.Core.Tests.Workspace;

/// <summary>
/// <see cref="EstateSync"/> over a temp estate. Ported from <c>tests/test_workspace_cli.py</c>'s router and code
/// workspace cases and <c>tests/test_workspace_layout.py</c>.
/// </summary>
public sealed class EstateSyncTests : IDisposable
{
    private static readonly EstateScopes Scopes = new(["o"], ["Labs"]);
    private static readonly EstateSync Sync = new(Scopes);
    private readonly TempDir _root = new();

    public void Dispose() => _root.Dispose();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private string Estate => _root.Folder("personal");

    private static readonly ManagedRepoSpec[] Specs =
    [
        new("rig", "git@github.com:o/rig.git", "Tooling", "Dev", "Python"),
        new("lake", "git@github.com:labs/lake.git"),
        new("Alpha", "https://example.com/alpha.git", "Elsewhere"),
        new("old", "git@github.com:o/old.git", "Legacy", Enabled: false),
    ];

    [Fact]
    public void The_table_links_each_enabled_project_where_it_is_cloned()
    {
        Assert.Equal(
            """
            | Repository | Domain | Tech Stack | Description | Scope Guide |
            |---|---|---|---|---|
            | [`rig`](./rig) | Dev | Python | Tooling | [rig/AGENTS.md](./rig/AGENTS.md) |
            | [`lake`](../Labs/lake) | General | - | - | [../Labs/lake/AGENTS.md](../Labs/lake/AGENTS.md) |
            | [`Alpha`](./Alpha) | General | - | Elsewhere | [Alpha/AGENTS.md](./Alpha/AGENTS.md) |
            """.ReplaceLineEndings("\n"),
            Sync.ProjectsTable(Specs));
    }

    [Fact]
    public void The_router_puts_the_table_between_the_markers_and_keeps_the_rest()
    {
        const string template = "# Head\n\nIntro\n\n\n<!-- MANAGED_PROJECTS_START -->\nstale\n<!-- MANAGED_PROJECTS_END -->\n\n\nTail\n";

        var text = Sync.RootAgentsMd(Specs[..1], template);

        Assert.Equal(
            $"# Head\n\nIntro\n\n{EstateSync.TableStart}\n{Sync.ProjectsTable(Specs[..1])}\n{EstateSync.TableEnd}\n\nTail\n",
            text);
        Assert.Equal(text, Sync.RootAgentsMd(Specs[..1], text));
    }

    [Theory]
    [InlineData("no markers")]
    [InlineData("<!-- MANAGED_PROJECTS_END --> then <!-- MANAGED_PROJECTS_START -->")]
    public void A_template_without_its_markers_is_refused(string template) =>
        Assert.Throws<InvalidDataException>(() => Sync.RootAgentsMd(Specs, template));

    [Theory]
    [InlineData("Personal code estate virtual monorepo")]
    [InlineData("Brand Asset & Production Cuts Invariant")]
    [InlineData("production-cuts.csv")]
    [InlineData("Domain Skills Live in the Vault")]
    [InlineData("mcp__open-knowledge__skills")]
    [InlineData("rig gh pr land <PR>")]
    [InlineData("rig gh pr wait <PR>")]
    [InlineData(EstateSync.TableStart)]
    [InlineData(EstateSync.TableEnd)]
    public void The_embedded_template_keeps_the_estate_rules(string text) =>
        Assert.Contains(text, EstateSync.Template, StringComparison.Ordinal);

    [Fact]
    public async Task Syncing_the_router_writes_it_and_removes_only_a_claude_md_link()
    {
        var target = _root.File("elsewhere/CLAUDE.md");
        var claude = Path.Join(Estate, "CLAUDE.md");
        try
        {
            _ = File.CreateSymbolicLink(claude, target);
        }
        catch (Exception e) when (OperatingSystem.IsWindows() && e is IOException or UnauthorizedAccessException)
        {
            Assert.Skip("Creating a file symlink needs Developer Mode or elevation on Windows.");
        }

        var written = await Sync.SyncRouterAsync(Estate, Specs, Token);

        Assert.Equal(Path.Join(Estate, "AGENTS.md"), written);
        Assert.Equal(Sync.RootAgentsMd(Specs), await File.ReadAllTextAsync(written, Token));
        Assert.False(Path.Exists(claude));
        Assert.True(File.Exists(target));

        await File.WriteAllTextAsync(claude, "mine", Token);
        _ = await Sync.SyncRouterAsync(Estate, Specs, Token);

        Assert.Equal("mine", await File.ReadAllTextAsync(claude, Token));
    }

    [Fact]
    public async Task The_code_workspace_opens_cloned_enabled_projects_by_name_and_keeps_its_settings()
    {
        _ = _root.Folder("personal/rig");
        _ = _root.Folder("Labs/lake");
        _ = _root.Folder("personal/Alpha");
        _ = _root.Folder("personal/old");
        var file = Path.Join(Estate, EstateSync.CodeWorkspaceName);
        await File.WriteAllTextAsync(file, """{"folders": [{"path": "gone"}], "settings": {"files.exclude": {"temp": true}}}""", Token);

        _ = await Sync.SyncCodeWorkspaceAsync(Estate, Specs, Token);
        var text = await File.ReadAllTextAsync(file, Token);

        var workspace = JsonNode.Parse(text)!;
        Assert.Equal(["Alpha", "lake", "rig"], workspace["folders"]!.AsArray().Select(f => (string?)f!["name"]));
        Assert.Equal(["Alpha", "../Labs/lake", "rig"], workspace["folders"]!.AsArray().Select(f => (string?)f!["path"]));
        Assert.True((bool)workspace["settings"]!["files.exclude"]!["temp"]!);
        Assert.EndsWith("}\n", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not json")]
    [InlineData("""{"settings": []}""")]
    [InlineData("[]")]
    public async Task A_missing_or_odd_code_workspace_starts_with_no_settings(string? existing)
    {
        var file = Path.Join(Estate, EstateSync.CodeWorkspaceName);
        if (existing is not null)
        {
            await File.WriteAllTextAsync(file, existing, Token);
        }

        _ = await Sync.SyncCodeWorkspaceAsync(Estate, Specs, Token);

        Assert.Equal("{\n  \"folders\": [],\n  \"settings\": {}\n}\n", await File.ReadAllTextAsync(file, Token));
    }

    [Fact]
    public async Task Syncing_twice_changes_nothing_and_lists_the_present_projects()
    {
        var gen = Gen.Select(Gen.Int[0, 5], Gen.Bool, Gen.Bool, Gen.Bool).Array[0, 6];
        await gen.SampleAsync(CheckAsync, iter: 20, threads: 1);
    }

    private static async Task CheckAsync((int Name, bool Org, bool Enabled, bool Cloned)[] projects)
    {
        using var root = new TempDir();
        var estate = root.Folder("personal");
        var specs = projects
            .Select(p => new ManagedRepoSpec($"p{p.Name}", $"git@github.com:{(p.Org ? "labs" : "o")}/p{p.Name}.git", Enabled: p.Enabled))
            .ToList();
        foreach (var (spec, project) in specs.Zip(projects).Where(pair => pair.Second.Cloned))
        {
            _ = Directory.CreateDirectory(Scopes.ProjectDir(estate, spec.Name, spec.Repo));
        }

        var agents = await Sync.SyncRouterAsync(estate, specs, Token);
        var code = await Sync.SyncCodeWorkspaceAsync(estate, specs, Token);
        var (router, workspace) = (await File.ReadAllTextAsync(agents, Token), await File.ReadAllTextAsync(code, Token));
        _ = await Sync.SyncRouterAsync(estate, specs, Token);
        _ = await Sync.SyncCodeWorkspaceAsync(estate, specs, Token);

        Assert.Equal(router, await File.ReadAllTextAsync(agents, Token));
        Assert.Equal(workspace, await File.ReadAllTextAsync(code, Token));
        Assert.Equal(specs.Count(s => s.Enabled), router.Split('\n').Count(line => line.StartsWith("| [`", StringComparison.Ordinal)));
        var expected = specs.Where(s => s.Enabled && Path.Exists(Scopes.ProjectDir(estate, s.Name, s.Repo))).Select(s => s.Name).Distinct().Order(StringComparer.Ordinal);
        var listed = JsonNode.Parse(workspace)!["folders"]!.AsArray().Select(f => (string)f!["name"]!).ToList();
        Assert.Equal(expected, listed.Distinct().Order(StringComparer.Ordinal));
        Assert.All(listed, name => Assert.Contains(specs, s => s.Name == name && s.Enabled && Path.Exists(Scopes.ProjectDir(estate, name, s.Repo))));
    }
}
