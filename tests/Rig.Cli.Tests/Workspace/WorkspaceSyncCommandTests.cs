using System.CommandLine;
using System.Text.Json.Nodes;
using Rig.Cli.Workspace;
using Rig.Core.Roots;
using Rig.Core.Tests;
using Rig.Core.Tests.Fakes;
using Rig.Core.Workspace;

namespace Rig.Cli.Tests.Workspace;

/// <summary>
/// <c>rig workspace gitignore</c>, <c>rig workspace sync</c> and <c>rig sync</c> over a temp code root, a fake git
/// and a temp registry and policy. Ported from <c>tests/test_workspace_gitignore_cli.py</c> and
/// <c>tests/test_workspace_cli.py</c>'s sync cases.
/// </summary>
public sealed class WorkspaceSyncCommandTests : IDisposable
{
    private readonly TempDir _root = new();
    private readonly FakeProc _proc = new();

    public WorkspaceSyncCommandTests()
    {
        File.WriteAllText(_root.File(Path.Join("rig", "config", EstateScopes.FileName)), """{"personal": {"owners": ["o"], "orgs": ["labs"]}}""");
        File.WriteAllText(_root.File(Path.Join("rig", "config", ManagedProjects.FileName)), """
            {"projects": [
              {"name": "app", "repo": "git@github.com:o/app.git", "description": "App"},
              {"name": "lake", "repo": "git@github.com:labs/lake.git"},
              {"name": "gone", "repo": "git@github.com:o/gone.git"}
            ]}
            """);
        File.WriteAllText(_root.File(Path.Join("rig", "config", "repo_policies.json")), """
            {
              "defaults": {
                "gitignore_entries": [{"name": "temp", "comment": "Temp", "write": ["temp/"]}],
                "agents_md_hygiene": {"bespoke_agent_files": []}
              },
              "language_presets": {"python": {"aliases": ["py"], "gitignore_entries": [{"name": "pyc", "comment": "Bytecode", "write": ["__pycache__/"]}]}}
            }
            """);
        _ = _root.Folder("personal/app/.git");
        _ = _root.Folder("labs/lake/.git");
        _ = _root.Folder("personal/stray/.git");
    }

    private readonly HttpClient _http = new();

    public void Dispose()
    {
        _http.Dispose();
        _root.Dispose();
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private string Personal => Path.Join(PathResolution.Resolve(_root.Path), "personal");

    private string App => Path.Join(Personal, "app");

    private string Lake => Path.Join(PathResolution.Resolve(_root.Path), "labs", "lake");

    private WorkspaceCommand Command(TextWriter output) =>
        new(
            new WorkspaceHost(
                Personal,
                Path.Join(_root.Path, "rig", "config", EstateScopes.FileName),
                Path.Join(_root.Path, "home"),
                Path.Join(_root.Path, "rig", "config", ManagedProjects.FileName),
                Path.Join(_root.Path, "home", ".workspace", "config.json")),
            _proc,
            TimeProvider.System,
            output,
            _http);

    private async Task<(int Code, string Out)> RunAsync(params string[] args)
    {
        using var output = new StringWriter();
        var command = Command(output);
        var root = new RootCommand { command.Build(), command.BuildSync() };
        var code = await root.Parse(args).InvokeAsync(new InvocationConfiguration { Output = output, Error = output }, Token);
        return (code, output.ToString().ReplaceLineEndings("\n"));
    }

    private static string Text(string file) => File.Exists(file) ? File.ReadAllText(file) : string.Empty;

    [Fact]
    public async Task A_strict_audit_fails_until_sync_aligns_the_registered_clones()
    {
        var (strict, before) = await RunAsync("workspace", "gitignore", "audit", "--strict");
        var (dry, preview) = await RunAsync("workspace", "gitignore", "sync", "--dry-run");
        var untouched = Text(Path.Join(App, ".gitignore"));
        var (applied, synced) = await RunAsync("workspace", "gitignore", "sync");
        var (after, clean) = await RunAsync("workspace", "gitignore", "audit", "--strict");

        Assert.Equal((1, 0, 0, 0), (strict, dry, applied, after));
        Assert.StartsWith($"Estate .gitignore Audit: {Personal}\n", before, StringComparison.Ordinal);
        Assert.Contains("MISSING", before, StringComparison.Ordinal);
        Assert.EndsWith("\nRun 'workspace gitignore sync' or 'workspace sync' to align missing rules.\n", before, StringComparison.Ordinal);
        Assert.StartsWith($"Estate .gitignore Sync (DRY-RUN): {Personal}\n", preview, StringComparison.Ordinal);
        Assert.EndsWith("\n✓ Would update .gitignore in 2 repository/repositories.\n", preview, StringComparison.Ordinal);
        Assert.Empty(untouched);
        Assert.EndsWith("\n✓ Updated .gitignore in 2 repository/repositories.\n", synced, StringComparison.Ordinal);
        Assert.Equal("# Temp\ntemp/\n", Text(Path.Join(Lake, ".gitignore")));
        Assert.EndsWith("\n✓ All repositories have compliant common .gitignore entries.\n", clean, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Join(Personal, "stray", ".gitignore")));
    }

    [Fact]
    public async Task A_language_option_adds_its_entries_to_the_json_rows()
    {
        var (code, output) = await RunAsync("workspace", "gitignore", "audit", "--json", "--repo", "app", "-l", "py, python");

        Assert.Equal(0, code);
        var row = Assert.Single(JsonNode.Parse(output)!.AsArray())!.AsObject();
        Assert.Equal(["repo_name", "repo_path", "status", "is_aligned", "languages", "missing", "missing_lines", "file"], row.Select(p => p.Key));
        Assert.Equal(["python"], row["languages"]!.AsArray().Select(l => (string?)l));
        Assert.Equal(["temp/", "__pycache__/"], row["missing_lines"]!.AsArray().Select(l => (string?)l));
        Assert.Equal(App, (string?)row["repo_path"]);
    }

    [Fact]
    public async Task The_json_sync_lists_what_it_added()
    {
        var (code, output) = await RunAsync("workspace", "gitignore", "sync", "--json", "--repo", "lake", "--dry-run");

        Assert.Equal(0, code);
        var row = Assert.Single(JsonNode.Parse(output)!.AsArray())!.AsObject();
        Assert.Equal(["repo_name", "repo_path", "status", "file", "languages", "added"], row.Select(p => p.Key));
        Assert.Equal(("lake", "aligned"), ((string?)row["repo_name"], (string?)row["status"]));
        Assert.Equal(["temp/"], row["added"]!.AsArray().Select(l => (string?)l));
        Assert.False(File.Exists(Path.Join(Lake, ".gitignore")));
    }

    [Fact]
    public async Task An_aligned_estate_sync_says_so()
    {
        await File.WriteAllTextAsync(Path.Join(App, ".gitignore"), "temp/\n", Token);
        await File.WriteAllTextAsync(Path.Join(Lake, ".gitignore"), "/temp\n", Token);

        var (_, output) = await RunAsync("workspace", "gitignore", "sync");

        Assert.Contains("None (already compliant)", output, StringComparison.Ordinal);
        Assert.EndsWith("\n✓ All repositories already have compliant .gitignore files.\n", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("workspace", "sync")]
    [InlineData("sync")]
    public async Task Sync_writes_the_router_and_code_workspace_then_aligns_the_gitignores(params string[] args)
    {
        var (code, output) = await RunAsync(args);

        Assert.Equal(0, code);
        Assert.Equal(
            $"""
            Synchronizing estate orchestration in {Personal}...

              ✓ Generated AGENTS.md (3 managed projects mapped)
              ✓ Synchronized personal.code-workspace
              ✓ Synchronized .gitignore across 2 repos (2 updated: lake, app)

            Estate orchestration successfully synchronized.

            """,
            output);
        Assert.Contains("| [`lake`](../labs/lake) |", Text(Path.Join(Personal, "AGENTS.md")), StringComparison.Ordinal);
        Assert.Equal(["app", "lake"], JsonNode.Parse(Text(Path.Join(Personal, EstateSync.CodeWorkspaceName)))!["folders"]!.AsArray().Select(f => (string?)f!["name"]));
        Assert.Contains("  ✓ Verified .gitignore across 2 repos (all compliant)\n", (await RunAsync(args)).Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_gitignore_leaves_the_gitignores_alone()
    {
        var (_, output) = await RunAsync("workspace", "sync", "--no-gitignore");

        Assert.DoesNotContain(".gitignore", output, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Join(App, ".gitignore")));
        Assert.True(File.Exists(Path.Join(Personal, "AGENTS.md")));
    }
}
