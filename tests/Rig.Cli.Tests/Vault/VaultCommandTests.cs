using System.CommandLine;
using System.Text.Json;
using Rig.Cli.Vault;
using Rig.Core.Tests;
using Rig.Core.Tests.Fakes;

namespace Rig.Cli.Tests.Vault;

/// <summary>
/// <c>rig vault</c> through its command line, ported from the Python <c>tests/obsidian/test_graph_*.py</c> scenarios. Each
/// runs against a throwaway PARA vault named by <c>VAULT_PATH</c>; the home is a temp folder, so the desktop app's orphan
/// list is never asked for.
/// </summary>
public sealed class VaultCommandTests : IDisposable
{
    private static readonly string[] Para = ["Projects", "Areas", "Resources", "Archive", "Social Media Posts", "Daily"];

    private readonly TempDir _temp = new();
    private readonly FakeProc _proc = new();
    private readonly StringWriter _out = new();
    private readonly StringWriter _err = new();
    private readonly Dictionary<string, string> _environment = new(StringComparer.Ordinal);

    public VaultCommandTests()
    {
        foreach (var folder in Para)
        {
            _temp.Folder(Path.Join("Vault", folder));
        }

        _environment["VAULT_PATH"] = Vault;
    }

    private string Vault => Path.Join(_temp.Path, "Vault");

    private string Output => _out.ToString().ReplaceLineEndings("\n");

    public void Dispose()
    {
        _out.Dispose();
        _err.Dispose();
        _temp.Dispose();
    }

    [Fact]
    public async Task Paths_shows_the_maintained_vault_and_the_drive_copy()
    {
        _environment["DRIVE_VAULT_PATH"] = "~/Drive/Vault";

        Assert.Equal(0, await Run("vault", "paths"));

        Assert.Equal($"maintained  {Vault}\ndrive copy  {Path.Combine(Home, "Drive/Vault")}\n", Output);
    }

    [Fact]
    public async Task Paths_defaults_the_drive_copy_to_google_drive()
    {
        _environment.Clear();

        await Run("vault", "paths");

        Assert.Contains(Path.Combine(Home, "code", "personal", "obsidian-vault", "Vault"), Output, StringComparison.Ordinal);
        Assert.Contains(Path.Combine(Home, "Google Drive", "My Drive", "Obsidian", "Vault"), Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Links_list_reports_a_clean_vault()
    {
        Note("Home.md", "# Home\n\n- [[Areas/Career]]\n");
        Note("Areas/Career.md", "# Career\n\n[[Home]]\n");

        Assert.Equal(0, await Run("vault", "graph", "links", "list"));

        Assert.Contains("No broken links or unlinked mentions found", Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Links_list_reports_broken_links_and_unlinked_mentions()
    {
        Note("Projects/Plan.md", "# Plan\n\nSee [[Gone Forever]]\n");
        Note("Areas/Career Plan.md", "# Career Plan\n");
        Note("Home.md", "# Home\n\nThe Career Plan needs review. [[Areas/Career Plan]]\n");

        Assert.Equal(0, await Run("vault", "graph", "links", "list"));

        Assert.Contains("Broken Links:\n  Projects/Plan.md:3 -> [[Gone Forever]]", Output, StringComparison.Ordinal);
        Assert.Contains("Unlinked Mentions:", Output, StringComparison.Ordinal);
        Assert.DoesNotContain("No broken links", Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Links_fix_is_a_dry_run_without_apply()
    {
        Note("Projects/Plan.md", "# Plan\n\nSee [[Gone Forever]]\n");

        Assert.Equal(0, await Run("vault", "graph", "links", "fix"));

        Assert.Equal("Dry run only. Re-run with --apply to write fixes.\n", Output);
        Assert.Contains("[[Gone Forever]]", Read("Projects/Plan.md"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Links_fix_apply_rewrites_the_fixable_links()
    {
        Note("Resources/Deep Dive.md", "# Deep Dive\n\ncontent\n");
        Note("Projects/Plan.md", "# Plan\n\nSee [[Projects/Deep Dive]] and [[Gone Forever]]\n");

        Assert.Equal(0, await Run("vault", "graph", "links", "fix", "--apply"));

        Assert.Contains("✓ Fixed 1 of 2 broken links.", Output, StringComparison.Ordinal);
        Assert.Contains("[[Resources/Deep Dive]]", Read("Projects/Plan.md"), StringComparison.Ordinal);
        Assert.Contains("[[Gone Forever]]", Read("Projects/Plan.md"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Orphans_list_prints_each_orphan_or_says_there_are_none()
    {
        Note("Projects/Lonely Idea.md", "# Lonely Idea\n");

        Assert.Equal(0, await Run("vault", "graph", "orphans", "list"));
        Assert.Contains("Projects/Lonely Idea.md", Output, StringComparison.Ordinal);

        Note("Home.md", "# Home\n\n- [[Projects/Lonely Idea]]\n");
        _out.GetStringBuilder().Clear();
        await Run("vault", "graph", "orphans", "list");
        Assert.Equal("✓ No orphan notes found.\n", Output);
    }

    [Fact]
    public async Task Orphans_link_is_a_dry_run_without_apply()
    {
        Note("Projects/Lonely Idea.md", "# Lonely Idea\n");

        await Run("vault", "graph", "orphans", "link");

        Assert.Equal("Dry run only. Re-run with --apply to link orphan notes.\n", Output);
        Assert.Equal("# Lonely Idea\n", Read("Projects/Lonely Idea.md"));
    }

    [Fact]
    public async Task Orphans_link_apply_attaches_an_orphan_to_its_parent_hub()
    {
        Note("Resources/Python.md", "# Python Hub\n");
        Note("Resources/Python/Async Guide.md", "# Async Guide\n");

        Assert.Equal(0, await Run("vault", "graph", "orphans", "link", "--apply"));

        Assert.Contains("✓ Linked 1 of", Output, StringComparison.Ordinal);
        Assert.Contains("[[Resources/Python/Async Guide.md]]", Read("Resources/Python.md"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Hubs_is_a_dry_run_without_apply_and_idempotent_with_it()
    {
        Note("Projects/Roadmap.md", "# Roadmap\n");

        await Run("vault", "graph", "hubs");
        Assert.Equal("Dry run only. Re-run with --apply to create and update hub notes.\n", Output);
        Assert.False(File.Exists(Path.Join(Vault, "Home.md")));

        Assert.Equal(0, await Run("vault", "graph", "hubs", "--apply"));
        Assert.Contains("[[Projects/Roadmap.md]]", Read("Projects.md"), StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Join(Vault, "Home.md")));

        _out.GetStringBuilder().Clear();
        await Run("vault", "graph", "hubs", "--apply");
        Assert.Equal("✓ Created 0 hubs and added 0 root links.\n", Output);
    }

    [Fact]
    public async Task Lint_reports_a_clean_vault()
    {
        Resource("Resources/Python/Async Guide.md", "Async Guide", "https://example.com/a");

        Assert.Equal(0, await Run("vault", "graph", "lint"));

        Assert.Equal("✓ Vault is clean. (Required metadata present, no duplicates detected).\n", Output);
    }

    [Fact]
    public async Task Lint_prints_every_kind_of_finding_as_the_python_did_and_still_exits_0()
    {
        // Recorded from `uv run vault graph lint` (Python, COLUMNS=200) on this vault before the Python was deleted.
        Note("Resources/No Type.md", "# No Type\n\nplain body\n");
        Resource("Resources/One.md", "Same One", "https://example.com/dup");
        Resource("Resources/Two.md", "Same Two", "https://example.com/dup");
        Resource("Resources/Python/my_ugly.note.md", "My Ugly Note", "https://example.com/u");
        Note("Areas/Odd.md", "---\ntype: weird\n---\n\nx\n");

        Assert.Equal(0, await Run("vault", "graph", "lint"));

        Assert.Equal(
            """
            Found 1 metadata errors:
              - TVS001 Missing 'type' field (Suggested: 'resource-hub'): No Type.md

            Found 1 metadata warnings:
              - TVS005 Unknown note type: 'weird': Odd.md

            Found 3 filename issues:
              - TVS101, TVS103, TVS105 Resources/Python/my_ugly.note.md: filename is URL-shaped, filename uses inconsistent separators, filename includes stray dots -> suggest my-ugly-note.md

            Found 1 duplicate groups (excluding genuine links):

            https://example.com/dup
              - Resources/One.md
              - Resources/Two.md

            """.ReplaceLineEndings("\n"),
            Output);
    }

    [Fact]
    public async Task Lint_json_emits_the_structured_report()
    {
        Note("Resources/No Type.md", "# No Type\n\nplain body\n");

        Assert.Equal(0, await Run("vault", "graph", "lint", "--json"));

        using var report = JsonDocument.Parse(Output);
        Assert.False(report.RootElement.GetProperty("ok").GetBoolean());
        Assert.NotEqual(0, report.RootElement.GetProperty("metadata").GetProperty("errors").GetArrayLength());
    }

    [Fact]
    public async Task Repair_duplicates_previews_then_archives_and_relinks_social_posts()
    {
        Resource("Resources/Python/Async Guide.md", "Async Guide", "https://example.com/async", string.Concat(Enumerable.Repeat("many words ", 40)));
        Resource("Resources/Python/Async Guide (1).md", "Async Guide", "https://example.com/async", "short\n");
        Note(
            "Social Media Posts/Async Post.md",
            "---\ntype: social\ntitle: Async Post\nsource: \"https://example.com/async\"\nstatus: draft\ncreated: 2024-01-01\n---\n\nCheck this out\n");

        Assert.Equal(0, await Run("vault", "graph", "repair", "duplicates"));
        Assert.StartsWith("Phase 1: Social Media Metadata Cleanup\n", Output, StringComparison.Ordinal);
        Assert.Contains("\n\nPhase 2: Resource Consolidation\n", Output, StringComparison.Ordinal);
        Assert.Contains("Would archive duplicate", Output, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Join(Vault, "Resources", "Python", "Async Guide (1).md")));

        Assert.Equal(0, await Run("vault", "graph", "repair", "duplicates", "--apply"));
        Assert.Contains("related_article", Read("Social Media Posts/Async Post.md"), StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Join(Vault, "Resources", "Python", "Async Guide (1).md")));
        Assert.NotEmpty(Directory.EnumerateFiles(Path.Join(Vault, "Archive", "Duplicates"), "*.md"));
    }

    [Fact]
    public async Task Backfill_types_previews_then_writes_the_type()
    {
        Note("Resources/Python/Untyped.md", "# Untyped\n\nbody\n");

        await Run("vault", "graph", "repair", "backfill-types");
        Assert.Contains("Would update type", Output, StringComparison.Ordinal);
        Assert.DoesNotContain("type:", Read("Resources/Python/Untyped.md"), StringComparison.Ordinal);

        await Run("vault", "graph", "repair", "backfill-types", "--apply");
        Assert.Contains("Updated type", Output, StringComparison.Ordinal);
        Assert.Contains("type: resource", Read("Resources/Python/Untyped.md"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Repair_metadata_previews_then_backfills_status_and_created()
    {
        Note("Areas/Career.md", "---\ntype: area\n---\n\n# Career\n");

        await Run("vault", "graph", "repair", "metadata");
        Assert.Contains("Would repair metadata", Output, StringComparison.Ordinal);
        Assert.DoesNotContain("status:", Read("Areas/Career.md"), StringComparison.Ordinal);

        await Run("vault", "graph", "repair", "metadata", "--apply");
        Assert.Contains("status: active", Read("Areas/Career.md"), StringComparison.Ordinal);
        Assert.Contains("created:", Read("Areas/Career.md"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Normalize_applies_the_type_backfill_and_the_metadata_repair()
    {
        Note("Resources/Python/Untyped.md", "# Untyped\n\nSource: https://example.com/x\n");

        Assert.Equal(0, await Run("vault", "graph", "repair", "normalize", "--apply"));

        Assert.Contains("type: resource", Read("Resources/Python/Untyped.md"), StringComparison.Ordinal);
        Assert.Contains("status: active", Read("Resources/Python/Untyped.md"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Repair_filenames_previews_then_renames_and_rewrites_links()
    {
        Resource("Resources/Python/my_ugly.note.md", "My Ugly Note", "https://example.com/u");
        Note("Home.md", "# Home\n\n[[my_ugly.note]]\n");

        await Run("vault", "graph", "repair", "filenames");
        Assert.Contains("Would rename", Output, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Join(Vault, "Resources", "Python", "my_ugly.note.md")));

        await Run("vault", "graph", "repair", "filenames", "--apply");
        Assert.Contains("Renamed", Output, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Join(Vault, "Resources", "Python", "my-ugly-note.md")));
        Assert.Contains("[[my-ugly-note]]", Read("Home.md"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Folder_scores_lists_folders_worst_first()
    {
        Note("Resources/No Type.md", "# No Type\n\nplain body\n");
        Resource("Areas/Fine.md", "Fine", "https://example.com/f");

        Assert.Equal(0, await Run("vault", "graph", "metrics", "folder-scores"));

        var lines = Output.Split('\n');
        Assert.Equal("Vault Folder Health Scores", lines[0]);
        Assert.Equal(["Folder", "Notes", "Errors", "Dupes", "Score"], lines[1].Split(' ', StringSplitOptions.RemoveEmptyEntries));
        Assert.StartsWith("Resources", lines[2], StringComparison.Ordinal);
        Assert.EndsWith("100.0", lines[3], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Health_lists_each_folders_density()
    {
        Note("Projects/Roadmap.md", "# Roadmap\n");

        Assert.Equal(0, await Run("vault", "graph", "metrics", "health"));

        Assert.StartsWith("Vault Health by Folder\nFolder ", Output, StringComparison.Ordinal);
        Assert.Matches(@"\nProjects +1  Healthy\n", Output);
        Assert.DoesNotContain("Areas", Output, StringComparison.Ordinal); // empty folders are left out, as in the Python
    }

    [Theory]
    [InlineData(0, "Empty")]
    [InlineData(1, "Healthy")]
    [InlineData(50, "Healthy")]
    [InlineData(51, "Dense")]
    [InlineData(100, "Dense")]
    [InlineData(101, "Overcrowded")]
    public void Density_bands_match_the_python(int count, string status)
    {
        Assert.Equal(status, VaultCommand.Density(count));
    }

    [Fact]
    public void A_table_row_needs_a_cell_per_column()
    {
        Assert.Throws<ArgumentException>(() => new TextTable("t", [("a", false)]).Add("1", "2"));
    }

    private string Home => _temp.Path;

    private Task<int> Run(params string[] args)
    {
        var command = new VaultCommand(
            _proc,
            new VaultHost(v => _environment.GetValueOrDefault(v), Home),
            new KnowledgeServerCommand(_proc, _ => throw new InvalidOperationException("not used"), false, _ => null, _out, _err),
            _out,
            _err);
        return command.Build().Parse(args).InvokeAsync(new InvocationConfiguration { Output = _out, Error = _err }, TestContext.Current.CancellationToken);
    }

    private void Note(string relative, string body)
    {
        var path = Path.Join(Vault, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, body);
    }

    private void Resource(string relative, string title, string source, string body = "content\n") =>
        Note(relative, $"---\ntype: resource\ntitle: {title}\nsource: \"{source}\"\nstatus: active\ncreated: 2024-01-01\n---\n\n{body}");

    private string Read(string relative) => File.ReadAllText(Path.Join(Vault, relative));
}
