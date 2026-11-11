using System.CommandLine;
using System.Text.Json.Nodes;
using Rig.Cli.Workspace;
using Rig.Core.Processes;
using Rig.Core.Roots;
using Rig.Core.Tests;
using Rig.Core.Tests.Fakes;

namespace Rig.Cli.Tests.Workspace;

/// <summary>
/// <c>rig workspace audit</c>, <c>status</c> and <c>clean</c> over a temp code root, a fake git, a temp registry and a
/// temp machine config. Ported from <c>tests/test_workspace_cli.py</c>'s command cases.
/// </summary>
public sealed class WorkspaceEstateCommandTests : IDisposable
{
    private readonly TempDir _root = new();
    private readonly FakeProc _proc = new();

    public WorkspaceEstateCommandTests()
    {
        File.WriteAllText(_root.File(Path.Join("rig", "config", EstateScopes.FileName)), """{"personal": {"owners": ["o"], "orgs": ["labs"]}}""");
        Registry("""
            {"projects": [
              {"name": "rig", "repo": "git@github.com:o/rig.git", "description": "Tooling"},
              {"name": "lake", "repo": "git@github.com:labs/lake.git"},
              {"name": "gone", "repo": "git@github.com:o/gone.git", "description": "Not cloned"},
              {"name": "laptop", "repo": "git@github.com:o/laptop.git"},
              {"name": "old", "repo": "git@github.com:o/old.git", "description": "Legacy", "enabled": false},
              {"name": "older", "repo": "git@github.com:o/older.git", "enabled": false}
            ]}
            """);
        File.WriteAllText(_root.File(Path.Join("home", ".workspace", "config.json")), """{"tools": {"estate": {"skip_repos": ["laptop"]}}}""");
        _ = Directory.CreateDirectory(Personal);
    }

    public void Dispose() => _root.Dispose();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private string Root => PathResolution.Resolve(_root.Path);

    private string Personal => Path.Join(Root, "personal");

    private string RegistryFile => Path.Join(_root.Path, "rig", "config", "managed-projects.json");

    private void Registry(string json) => File.WriteAllText(_root.File(Path.Join("rig", "config", "managed-projects.json")), json);

    private string Clone(string parent, string name, string remote)
    {
        var folder = Path.Join(Root, parent, name);
        _ = Directory.CreateDirectory(Path.Join(folder, ".git"));
        _ = _proc.AnswerIn(folder, new(0, remote + "\n", string.Empty), "git", "config");
        return folder;
    }

    // An estate with one of everything the report lists.
    private void Estate()
    {
        _ = Clone("personal", "rig", "git@github.com:o/rig.git");
        _ = Clone("labs", "lake", "git@github.com:labs/lake.git");
        _ = Clone("personal", "old", "git@github.com:o/old.git");
        _ = _root.File("personal/wt/.git");
        _ = _root.File("personal/AGENTS.md");
        _ = _root.File("personal/junk.txt");
    }

    private async Task<(int Code, string Out)> RunAsync(params string[] args)
    {
        using var output = new StringWriter();
        using var http = new HttpClient();
        var host = new WorkspaceHost(
            Personal,
            Path.Join(_root.Path, "rig", "config", EstateScopes.FileName),
            Path.Join(_root.Path, "home"),
            RegistryFile,
            Path.Join(_root.Path, "home", ".workspace", "config.json"));
        var root = new RootCommand { new WorkspaceCommand(host, _proc, TimeProvider.System, output, http).Build() };
        var code = await root.Parse(["workspace", .. args]).InvokeAsync(new InvocationConfiguration { Output = output, Error = output }, Token);
        return (code, output.ToString().ReplaceLineEndings("\n"));
    }

    [Fact]
    public async Task The_audit_lists_each_section_and_fails_only_when_strict()
    {
        Estate();

        var (lax, output) = await RunAsync("audit");
        var (strict, _) = await RunAsync("audit", "--strict");

        Assert.Equal(0, lax);
        Assert.Equal(1, strict);
        Assert.Equal(
            $"""
            Personal Virtual Monorepo Audit: {Personal}

            ✓ Managed Repositories (2)
              • rig (git@github.com:o/rig.git)
              • lake (git@github.com:labs/lake.git)

            📦 Retained Archives (1)
              • old (git@github.com:o/old.git) (Retained archive (Legacy))

            ⌥ Linked Git Worktrees (1)
              • wt (Linked git worktree sibling)

            ⚙ System Orchestration Items (1)
              • AGENTS.md

            ✗ Missing Declared Clones (1)
              • gone: git@github.com:o/gone.git (Not cloned)

            ! Unmanaged Items In Estate (1)
              • junk.txt (unmanaged_file: Orphaned unmanaged file in personal directory root)

            Run 'workspace clean --dry-run' to inspect or register them in config/managed-projects.json

            """,
            output);
    }

    [Fact]
    public async Task A_clean_estate_says_so_and_passes_strict()
    {
        _ = Clone("personal", "rig", "git@github.com:o/rig.git");
        Registry("""{"projects": [{"name": "rig", "repo": "git@github.com:o/rig.git"}]}""");

        var (code, output) = await RunAsync("audit", "--strict");

        Assert.Equal(0, code);
        Assert.EndsWith("  • rig (git@github.com:o/rig.git)\n\nSUCCESS: No unmanaged files or directories in estate root.\n", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_blocked_org_location_is_listed()
    {
        _ = _root.File("labs/lake");
        Registry("""{"projects": [{"name": "lake", "repo": "git@github.com:labs/lake.git"}]}""");

        var (code, output) = await RunAsync("audit", "--strict");

        Assert.Equal(1, code);
        Assert.Contains(
            $"✗ Blocked Declared Locations (1)\n  • lake at {Path.Join(Root, "labs", "lake")} (unmanaged_file: Not a git clone; declared as git@github.com:labs/lake.git)\n",
            output,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_json_audit_keeps_the_python_fields_in_order()
    {
        Estate();

        var (code, output) = await RunAsync("audit", "--json", "--strict");

        Assert.Equal(1, code);
        var report = JsonNode.Parse(output)!.AsObject();
        Assert.Equal(
            ["estate_root", "is_clean", "total_items", "managed_repos", "linked_worktrees", "unmanaged_items", "missing_managed_repos", "blocked_managed_repos", "known_system_items", "retained_archives"],
            report.Select(p => p.Key));
        Assert.Equal(Personal, (string?)report["estate_root"]);
        Assert.Equal(5, (int)report["total_items"]!);
        var junk = report["unmanaged_items"]![0]!.AsObject();
        Assert.Equal(["name", "path", "kind", "detail"], junk.Select(p => p.Key));
        Assert.Equal(
            ["junk.txt", Path.Join(Personal, "junk.txt"), "unmanaged_file", "Orphaned unmanaged file in personal directory root"],
            junk.Select(p => (string?)p.Value));
        Assert.Equal("""{"name":"gone","repo":"git@github.com:o/gone.git","description":"Not cloned"}""", report["missing_managed_repos"]![0]!.ToJsonString());
        Assert.Equal("Retained archive (Legacy)", (string?)report["retained_archives"]![0]!["detail"]);
        Assert.StartsWith("{\n  \"estate_root\": ", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_config_option_replaces_the_registry()
    {
        var other = _root.File("other.json");
        await File.WriteAllTextAsync(other, """{"projects": [{"name": "x", "repo": "git@github.com:o/x.git"}]}""", Token);

        var (_, output) = await RunAsync("audit", "--json", "--config", other);

        Assert.Equal("x", (string?)JsonNode.Parse(output)!["missing_managed_repos"]![0]!["name"]);
    }

    [Fact]
    public async Task Status_lists_every_project_in_registry_order()
    {
        Estate();
        _ = _root.File("labs/blocked");
        Registry((await File.ReadAllTextAsync(RegistryFile, Token)).Replace("""{"name": "gone",""", """{"name": "blocked", "repo": "git@github.com:labs/blocked.git"}, {"name": "gone",""", StringComparison.Ordinal));

        var (code, output) = await RunAsync("status");

        Assert.Equal(0, code);
        Assert.Equal(
            $"""
            Estate Inventory — {Personal}
            Repository  Status              Description
            rig         Present             Tooling
            lake        Present             --
            blocked     Blocked             --
            gone        Missing             Not cloned
            laptop      Missing             --
            old         Retained (Archive)  Legacy
            older       Archived (Absent)   --

            2/5 active managed repos present · 1 retained archives · 1 unmanaged items detected


            """,
            output);
    }

    [Fact]
    public async Task Clean_is_a_dry_run_by_default_and_skips_clones_without_force()
    {
        Estate();
        var stray = Clone("personal", "stray", "git@github.com:x/stray.git");

        var (code, output) = await RunAsync("clean");

        Assert.Equal(0, code);
        Assert.Equal(
            $"""
            [DRY-RUN] Found 2 unmanaged item(s) in {Personal}:

              [dry-run] Would remove unmanaged_file: {Path.Join(Personal, "junk.txt")}
              Skipped repo stray (requires --force to remove git clones)

            """,
            output);
        Assert.True(File.Exists(Path.Join(Personal, "junk.txt")));
        Assert.True(Directory.Exists(stray));
        Assert.DoesNotContain(_proc.Requests, r => r.Argv.Contains("status"));
    }

    [Fact]
    public async Task Apply_removes_unmanaged_items_and_force_removes_only_clean_clones()
    {
        Estate();
        var stray = Clone("personal", "stray", "git@github.com:x/stray.git");
        var dirty = Clone("personal", "dirty", "git@github.com:x/dirty.git");
        _ = _proc
            .AnswerIn(stray, new(0, string.Empty, string.Empty), "git", "status")
            .AnswerIn(dirty, new(0, "?? a\n", string.Empty), "git", "status");

        var (code, output) = await RunAsync("clean", "--apply", "--force");

        Assert.Equal(0, code);
        Assert.Equal(
            $"""
            [APPLY] Found 3 unmanaged item(s) in {Personal}:

              Refused to delete dirty or unreadable unmanaged repo: dirty
              Removed junk.txt
              Removed stray

            """,
            output);
        Assert.False(Path.Exists(stray));
        Assert.True(Directory.Exists(dirty));
        Assert.True(Directory.Exists(Path.Join(Personal, "rig")));
        Assert.True(Directory.Exists(Path.Join(Personal, "old")));
        Assert.True(File.Exists(Path.Join(Personal, "AGENTS.md")));
    }

    [Fact]
    public async Task A_root_inside_an_untrusted_folder_still_hardens_git()
    {
        var collection = Path.Join(Root, "other", "collection");
        var stray = Path.Join(collection, "stray");
        _ = Directory.CreateDirectory(Path.Join(stray, ".git"));

        _ = await RunAsync("clean", "--root", collection, "--apply", "--force");

        var status = Assert.Single(_proc.Requests, r => r.Argv.Contains("status"));
        Assert.Equal(["git", .. Git.HardeningArgs], status.Argv.Take(Git.HardeningArgs.Count + 1));
    }

    [Fact]
    public async Task Clean_with_nothing_unmanaged_says_so()
    {
        var (code, output) = await RunAsync("clean", "--apply");

        Assert.Equal(0, code);
        Assert.Equal("✓ Estate is clean. Nothing to remove.\n", output);
    }
}
