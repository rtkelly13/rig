using Rig.Core.Skills;

namespace Rig.Core.Tests.Skills;

/// <summary>Skill discovery, frontmatter and system names; ported from <c>tests/test_skills_symlinks.py</c>.</summary>
public sealed class SkillCatalogTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Discovers_skills_outside_ignored_dot_and_linked_folders()
    {
        var root = _dir.Folder("skills");
        SkillFixtures.Write(root, "engineering/tdd", "tdd", "Test-driven development");
        SkillFixtures.Write(root, "productivity/disk-cleanup", "disk-cleanup", "Disk Cleanup");
        SkillFixtures.Write(root, "node_modules/some-pkg", "bad-pkg");
        SkillFixtures.Write(root, ".hidden/secret", "secret");
        var elsewhere = SkillFixtures.Write(_dir.Path, "elsewhere/linked", "linked");
        Directory.CreateSymbolicLink(Path.Join(root, "link"), Path.GetDirectoryName(elsewhere)!);

        var skills = SkillFixtures.Catalog(root).Everything();

        Assert.Equal(["tdd", "disk-cleanup"], skills.Select(s => s.Name));
        Assert.Equal("Test-driven development", skills[0].Description);
        Assert.Equal("test", skills[0].Source);
    }

    [Fact]
    public void The_first_source_with_a_name_wins()
    {
        var first = _dir.Folder("first");
        var second = _dir.Folder("second");
        SkillFixtures.Write(first, "tdd", "tdd", "mine");
        SkillFixtures.Write(second, "tdd", "tdd", "theirs");
        SkillFixtures.Write(second, "other", "other");

        var skills = new SkillCatalog([new("a", first), new("b", second), new("gone", Path.Join(_dir.Path, "none"))], null, false).Everything();

        Assert.Equal(["tdd", "other"], skills.Select(s => s.Name));
        Assert.Equal(["mine", "Custom AI agent skill"], skills.Select(s => s.Description));
        Assert.Equal(["a", "b"], skills.Select(s => s.Source));
    }

    [Fact]
    public void An_allowlist_matches_names_or_folders_and_star_keeps_all()
    {
        var root = _dir.Folder("skills");
        SkillFixtures.Write(root, "skill-one", "skill-one");
        SkillFixtures.Write(root, "skill-two", "skill-two");
        SkillFixtures.Write(root, "folder-three", "named-three");

        Assert.Equal(3, SkillFixtures.Catalog(root).Discover(null).Count);
        Assert.Equal(["named-three", "skill-one"], SkillFixtures.Catalog(root).Discover(["skill-one", "folder-*"]).Select(s => s.Name));
        Assert.Equal(3, SkillFixtures.Catalog(root).Discover(["*"]).Count);
        var curated = SkillFixtures.Catalog(root, ["skill-two"]);
        Assert.Equal(["skill-two"], curated.Curated().Select(s => s.Name));
        Assert.True(curated.IsCurated("skill-two"));
        Assert.False(curated.IsCurated("skill-one"));
    }

    [Fact]
    public void A_vendored_skill_reports_its_vendor()
    {
        var root = _dir.Folder("skills");
        SkillFixtures.Write(root, "vendored/pstack/plan", "plan");

        Assert.Equal("pstack", Assert.Single(SkillFixtures.Catalog(root).Everything()).Source);
    }

    [Theory]
    [InlineData("no frontmatter", "folder", "Custom AI agent skill")]
    [InlineData("---\nname: [unclosed\n---\n", "folder", "Custom AI agent skill")]
    [InlineData("---\nname: x\n", "folder", "Custom AI agent skill")]
    [InlineData("---\n- a list\n---\n", "folder", "Custom AI agent skill")]
    [InlineData("---\nname: \"  \"\ndescription: \" \"\n---\n", "folder", "Custom AI agent skill")]
    [InlineData("---\nname: \"'quoted'\"\ndescription: \"one\\n  two\\tthree \"\n---\n", "quoted", "one two three")]
    [InlineData("---\r\nname: crlf\r\n---\r\n", "crlf", "Custom AI agent skill")]
    public void Frontmatter_is_read_or_falls_back(string content, string name, string description)
    {
        var path = _dir.File("folder/SKILL.md");
        File.WriteAllText(path, content);

        Assert.Equal((name, description), SkillCatalog.ReadFrontmatter(path));
    }

    [Fact]
    public void A_missing_skill_file_falls_back_to_its_folder()
    {
        Assert.Equal(("x", "Custom AI agent skill"), SkillCatalog.ReadFrontmatter(Path.Join(_dir.Path, "x", "SKILL.md")));
    }

    [Theory]
    [InlineData("[\"a\", \"b\"]", true)]
    [InlineData("{}", false)]
    [InlineData("[1]", false)]
    [InlineData("not json", false)]
    public void Names_load_only_from_a_list_of_strings(string json, bool loads)
    {
        var path = _dir.File("names.json");
        File.WriteAllText(path, json);

        Assert.Equal(loads, SkillCatalog.LoadNames(path) is not null);
        Assert.Null(SkillCatalog.LoadNames(Path.Join(_dir.Path, "missing.json")));
    }

    [Fact]
    public void Places_list_sources_in_priority_order()
    {
        var checkout = _dir.Folder("checkout");
        _dir.Folder("checkout/skills");
        _dir.Folder("work/skills");
        _dir.Folder("home/.config/devenv/skills");
        _dir.Folder("home/.config/devenv/vendors/zeta");
        _dir.Folder("home/.config/devenv/vendors/alpha");
        _dir.Folder("home/.config/devenv/vendors/.cache");
        File.WriteAllText(_dir.File("checkout/config/skills-allowlist.json"), "[\"a\"]");
        var places = new SkillPlaces(checkout, Path.Join(_dir.Path, "home"), Path.Join(_dir.Path, "work"), false);

        Assert.Equal(["workspace", "shared-utilities", "user-custom", "alpha", "zeta"], places.Sources().Select(s => s.Name));
        Assert.Equal(["a"], SkillCatalog.At(places).EstateAllowlist);
        Assert.Equal(Path.Join(checkout, "skills", "vendored"), places.Vendored);
        Assert.Equal(Path.Join(checkout, "config", "upstream-skills.json"), places.Vendors);
    }

    [Fact]
    public void The_working_directorys_skills_are_skipped_when_they_are_the_canonical_ones()
    {
        var checkout = _dir.Folder("checkout");
        _dir.Folder("checkout/skills");

        var places = new SkillPlaces(checkout, _dir.Folder("home"), checkout, false);

        Assert.Equal(["shared-utilities"], places.Sources().Select(s => s.Name));
    }

    [Theory]
    [InlineData(new string[0], new[] { "opencode", "claude", "codex", "antigravity" })]
    [InlineData(new[] { "claude", "all" }, new[] { "opencode", "claude", "codex", "antigravity", "agents" })]
    [InlineData(new[] { " Claude ", "agents" }, new[] { "claude", "agents" })]
    public void Systems_resolve_from_names(string[] names, string[] expected)
    {
        Assert.Equal(expected, SkillSystems.Resolve(names, out var unknown));
        Assert.Null(unknown);
    }

    [Fact]
    public void An_unknown_system_is_named()
    {
        Assert.Null(SkillSystems.Resolve(["claude", "vim"], out var unknown));
        Assert.Equal("vim", unknown);
        Assert.Throws<ArgumentOutOfRangeException>(() => SkillSystems.Directory("vim", _dir.Path));
        Assert.Equal("vim", SkillSystems.DisplayName("vim"));
    }

    [Theory]
    [InlineData("opencode", "OpenCode", ".config/opencode/skills")]
    [InlineData("claude", "Claude Code", ".claude/skills")]
    [InlineData("codex", "OpenAI Codex", ".codex/skills")]
    [InlineData("antigravity", "Google Antigravity", ".gemini/config/skills")]
    [InlineData("agents", "Cross-Agent Standard", ".agents/skills")]
    public void Each_system_has_a_name_and_folder(string system, string display, string folder)
    {
        Assert.Equal(display, SkillSystems.DisplayName(system));
        Assert.Equal(Path.Join(_dir.Path, folder).Replace('\\', '/'), SkillSystems.Directory(system, _dir.Path).Replace('\\', '/'));
    }
}
