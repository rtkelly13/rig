using System.Text.Json.Nodes;
using Rig.Core.Governance;
using Rig.Core.Processes;
using Rig.Core.Roots;
using Rig.Core.Tests.Fakes;

namespace Rig.Core.Tests.Governance;

/// <summary>
/// <see cref="Gitignores"/> over a temp estate and a fake git. Ported from <c>tests/test_gitignore_manager.py</c> and
/// <c>tests/test_workspace_gitignore_cli.py</c>.
/// </summary>
public sealed class GitignoresTests : IDisposable
{
    // Temp and TODO by default, Python's bytecode by language, and no bespoke files unless a test asks for them.
    private const string Policy = """
        {
          "defaults": {
            "gitignore_entries": [
              {"name": "temp", "comment": "Local temp", "write": ["temp/"]},
              {"name": "todo", "comment": "Local TODO", "write": ["TODO.md", "todo.md"], "match_mode": "all"}
            ],
            "agents_md_hygiene": {"bespoke_agent_files": []}
          },
          "profiles": {"none": {"repos": ["bare"], "gitignore_entries": []}},
          "repo_overrides": {"agents": {"agents_md_hygiene": {"bespoke_agent_files": ["GEMINI.md"]}}},
          "language_presets": {
            "python": {"aliases": ["py"], "detect_files": ["pyproject.toml"],
                       "gitignore_entries": [{"name": "python_bytecode", "comment": "Bytecode", "write": ["__pycache__/"]}]}
          }
        }
        """;

    private static readonly EstateScopes Scopes = new(["o"], ["labs"]);
    private readonly TempDir _root = new();
    private readonly FakeProc _proc = new();

    public void Dispose() => _root.Dispose();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static RepoPolicies Policies => RepoPolicies.Parse(JsonNode.Parse(Policy), "policy.json");

    private string Estate => Path.Join(_root.Path, "personal");

    private Gitignores Subject => new(new Git(_proc, _root.Path), Scopes, Policies);

    private string Clone(string relative, string? remote = null)
    {
        var folder = Path.GetDirectoryName(_root.Folder(Path.Join(relative, ".git")))!;
        if (remote is not null)
        {
            _ = _proc.AnswerIn(folder, new(0, remote + "\n", string.Empty), "git", "config");
        }

        return folder;
    }

    private static string Gitignore(string repo, string? text = null)
    {
        var file = Path.Join(repo, ".gitignore");
        if (text is not null)
        {
            File.WriteAllText(file, text);
        }

        return file;
    }

    [Fact]
    public async Task The_policy_key_is_the_origin_then_the_org_folder_then_the_name()
    {
        var withOrigin = Clone("personal/a", "git@github.com:o/Rig.git");
        var inOrg = Clone("LABS/lake");
        var plain = _root.Folder("personal/plain");

        Assert.Equal("o/Rig", await Subject.PolicyKeyAsync(withOrigin, Token));
        Assert.Equal("labs/lake", await Subject.PolicyKeyAsync(inOrg + Path.DirectorySeparatorChar, Token));
        Assert.Equal("plain", await Subject.PolicyKeyAsync(plain, Token));
        Assert.DoesNotContain(_proc.Requests, r => r.WorkingDirectory == plain);
    }

    [Fact]
    public void The_bespoke_block_ends_the_entries_unless_the_policy_names_one_or_lists_none()
    {
        var named = RepoPolicies.Parse(
            JsonNode.Parse("""{"defaults": {"gitignore_entries": [{"name": "bespoke_agent_files", "write": ["x"]}]}}"""), "p");

        var entries = Subject.RequiredEntries("agents", Estate);

        Assert.Equal(["temp", "todo", BespokeAgentFiles.EntryName], entries.Select(e => e.Name));
        Assert.Equal(["GEMINI.md"], entries[^1].Write);
        Assert.Equal((true, BespokeAgentFiles.Comment), (entries[^1].MatchAll, entries[^1].Comment));
        Assert.Equal(["temp", "todo"], Subject.RequiredEntries("other", Estate).Select(e => e.Name));
        Assert.Equal(["x"], Assert.Single(new Gitignores(new Git(_proc, _root.Path), Scopes, named).RequiredEntries("r", Estate)).Write);
    }

    [Fact]
    public async Task Inspection_names_what_is_missing()
    {
        var repo = Clone("personal/r");
        var bare = Clone("personal/bare");

        var absent = await Subject.InspectAsync(repo, cancellationToken: Token);
        _ = Gitignore(repo, "temp/\nTODO.md\n");
        var partial = await Subject.InspectAsync(repo, ["py"], Token);

        Assert.Equal(("MISSING", false, Gitignore(repo)), (absent.Status, absent.IsAligned, absent.File));
        Assert.Equal(["temp", "todo"], absent.Missing);
        Assert.Equal(["temp/", "TODO.md", "todo.md"], absent.MissingLines);
        Assert.Equal("MISSING __pycache__/, todo.md", partial.Status);
        Assert.Equal(["python"], partial.Languages);
        Assert.Equal(["todo", "python_bytecode"], partial.Missing);
        Assert.Equal("NO POLICY", (await Subject.InspectAsync(bare, cancellationToken: Token)).Status);
    }

    [Fact]
    public async Task Configured_needs_git_to_ignore_the_state_folder_too()
    {
        var repo = Clone("personal/r");
        _ = Gitignore(repo, "temp/\nTODO.md\ntodo.md\n");

        var unknown = await Subject.InspectAsync(repo, cancellationToken: Token);
        _ = _proc.AnswerIn(repo, new(1, string.Empty, string.Empty), "git", "check-ignore");
        var reincluded = await Subject.InspectAsync(repo, cancellationToken: Token);

        Assert.Equal(("CONFIGURED", true), (unknown.Status, unknown.IsAligned));
        Assert.Equal("NOT IGNORED temp/.rig/ (fix the .gitignore rule that re-includes it)", reincluded.Status);
        Assert.Equal([Gitignores.WorktreeStateEntry], reincluded.Missing);
        Assert.False(reincluded.IsAligned);
    }

    [Fact]
    public async Task Real_git_sees_a_re_included_state_folder()
    {
        var proc = new SystemProc();
        var repo = _root.Folder("personal/real");
        Assert.True((await proc.RunAsync(new ProcRequest(["git", "init", "-q"]) { WorkingDirectory = repo }, Token)).Ok);
        var todoOnly = RepoPolicies.Parse(
            JsonNode.Parse("""{"defaults": {"gitignore_entries": [{"write": ["TODO.md"]}], "agents_md_hygiene": {"bespoke_agent_files": []}}}"""), "p");
        var subject = new Gitignores(new Git(proc, _root.Path), Scopes, todoOnly);
        var file = Gitignore(repo, "TODO.md\n");

        var unlisted = await subject.InspectAsync(repo, cancellationToken: Token);
        await File.WriteAllTextAsync(file, "TODO.md\ntemp/*\n!temp/.rig/\n", Token);
        var reincluded = await subject.InspectAsync(repo, cancellationToken: Token);
        await File.WriteAllTextAsync(file, "TODO.md\ntemp/\n", Token);
        var ignored = await subject.InspectAsync(repo, cancellationToken: Token);

        Assert.StartsWith("NOT IGNORED", unlisted.Status, StringComparison.Ordinal);
        Assert.StartsWith("NOT IGNORED", reincluded.Status, StringComparison.Ordinal);
        Assert.Equal("CONFIGURED", ignored.Status);
    }

    [Fact]
    public async Task Alignment_appends_only_the_missing_blocks_and_a_dry_run_writes_nothing()
    {
        var fresh = Clone("personal/fresh");
        var some = Clone("personal/some");
        _ = Gitignore(some, "bin/\ntodo.md");

        var dry = await Subject.AlignAsync(some, dryRun: true, ["python"], Token);
        var unchanged = await File.ReadAllTextAsync(Gitignore(some), Token);
        var wrote = await Subject.AlignAsync(some, languages: ["python"], cancellationToken: Token);
        var created = await Subject.AlignAsync(fresh, cancellationToken: Token);

        Assert.Equal("aligned", dry.Status);
        Assert.Equal(["temp/", "TODO.md", "__pycache__/"], dry.Added);
        Assert.Equal("bin/\ntodo.md", unchanged);
        Assert.Equal(dry.Added, wrote.Added);
        Assert.Equal(
            "bin/\ntodo.md\n# Local temp\ntemp/\n\n# Local TODO\nTODO.md\n\n# Bytecode\n__pycache__/\n",
            await File.ReadAllTextAsync(Gitignore(some), Token));
        Assert.Equal("# Local temp\ntemp/\n\n# Local TODO\nTODO.md\ntodo.md\n", await File.ReadAllTextAsync(Gitignore(fresh), Token));
        Assert.Equal("already_configured", (await Subject.AlignAsync(fresh, cancellationToken: Token)).Status);
        Assert.Equal("no_policy", (await Subject.AlignAsync(Clone("personal/bare"), cancellationToken: Token)).Status);
        Assert.Equal("aligned", created.Status);
    }

    [Fact]
    public void Only_git_clones_in_the_estate_and_its_org_folders_are_targets()
    {
        var a = Clone("personal/a");
        var lake = Clone("labs/lake");
        _ = _root.Folder("personal/notes");
        _ = _root.File("personal/wt/.git");
        _ = Clone("elsewhere/x");

        Assert.Equal([lake, a, Path.Join(Estate, "wt")], Subject.Clones(Estate));
        Assert.Equal([lake], Subject.Clones(Estate, ["lake", "missing", "notes"]));
        Assert.Equal([a], Subject.Clones(Estate, ["a"]));
        Assert.Empty(Subject.Clones(Path.Join(_root.Path, "nowhere", "personal")));
    }

    [Fact]
    public async Task An_estate_audit_fails_until_sync_fills_every_clone_in()
    {
        _ = Clone("personal/a");
        _ = Clone("labs/lake", "git@github.com:labs/lake.git");

        var before = await Subject.AuditEstateAsync(Estate, cancellationToken: Token);
        var dry = await Subject.SyncEstateAsync(Estate, dryRun: true, languages: ["py"], cancellationToken: Token);
        var synced = await Subject.SyncEstateAsync(Estate, cancellationToken: Token);
        var after = await Subject.AuditEstateAsync(Estate, cancellationToken: Token);

        Assert.Equal(["a", "lake"], before.Select(r => r.RepoName).Order(StringComparer.Ordinal));
        Assert.All(before, r => Assert.False(r.Result.IsAligned));
        Assert.All(dry, r => Assert.Contains("__pycache__/", r.Result.Added));
        Assert.All(synced, r => Assert.Equal("aligned", r.Result.Status));
        Assert.All(after, r => Assert.True(r.Result.IsAligned));
    }
}
