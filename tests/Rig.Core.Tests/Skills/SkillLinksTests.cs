using Rig.Core.Skills;

namespace Rig.Core.Tests.Skills;

/// <summary>Linking, auditing, pruning and overlays; ported from <c>tests/test_skills_symlinks.py</c>. Homes are temp folders.</summary>
public sealed class SkillLinksTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly string _sources;
    private readonly string _home;
    private readonly SkillPlaces _places;

    public SkillLinksTests()
    {
        _sources = _dir.Folder("skills");
        _home = _dir.Folder("home");
        SkillFixtures.Write(_sources, "engineering/tdd", "tdd", "Test-driven development");
        SkillFixtures.Write(_sources, "productivity/disk-cleanup", "disk-cleanup", "Disk Cleanup");
        _places = new SkillPlaces(_dir.Folder("checkout"), _home, _dir.Path, false);
    }

    public void Dispose() => _dir.Dispose();

    private SkillLinks Links(IDirectoryLinker? linker = null) => new(_places, linker ?? new SymlinkLinker());

    private IReadOnlyList<Skill> Catalog => SkillFixtures.Catalog(_sources).Everything();

    private string Claude => SkillSystems.Directory("claude", _home);

    private SystemSkillsAudit AuditClaude() => Assert.Single(Links().Audit(Catalog, ["claude"]));

    [Fact]
    public void Sync_links_every_skill_and_leaves_systems_healthy()
    {
        var before = Links().Audit(Catalog, SkillSystems.Primary);
        Assert.All(before, a => Assert.Equal(["disk-cleanup", "tdd"], a.Missing.Order(StringComparer.Ordinal)));
        Assert.All(before, a => Assert.False(a.Healthy));

        var dry = Links().Sync(Catalog, SkillSystems.Primary, prune: true, dryRun: true);
        Assert.All(dry, c => Assert.Equal(2, c.Count));
        Assert.False(Directory.Exists(Claude));

        var counts = Links().Sync(Catalog, SkillSystems.Primary, prune: true, dryRun: false);
        Assert.Equal(SkillSystems.Primary, counts.Select(c => c.System));
        Assert.All(counts, c => Assert.Equal(2, c.Count));

        var after = Links().Audit(Catalog, SkillSystems.Primary);
        Assert.All(after, a => Assert.True(a.Healthy));
        Assert.All(after, a => Assert.Equal(2, a.Total));
        Assert.True(DirectoryLinks.IsLink(Path.Join(Claude, "tdd")));
    }

    [Fact]
    public void A_second_sync_keeps_good_links_and_replaces_wrong_ones()
    {
        Links().Sync(Catalog, ["claude"], prune: false, dryRun: false);
        var elsewhere = _dir.Folder("elsewhere");
        DirectoryLinks.RemoveLink(Path.Join(Claude, "tdd"));
        Directory.CreateSymbolicLink(Path.Join(Claude, "tdd"), elsewhere);
        DirectoryLinks.Remove(Path.Join(Claude, "disk-cleanup"));
        File.WriteAllText(Path.Join(Directory.CreateDirectory(Path.Join(Claude, "disk-cleanup")).FullName, "x"), "agent");

        var drifted = AuditClaude();
        Assert.Equal(["tdd"], drifted.Drifted);
        Assert.Equal(["disk-cleanup"], drifted.Collisions);

        Links().Sync(Catalog, ["claude"], prune: false, dryRun: false);

        Assert.True(AuditClaude().Healthy);
        Assert.True(Directory.Exists(elsewhere));
    }

    [Fact]
    public void A_refused_link_falls_back_to_a_copy()
    {
        Links(new RefusingLinker()).Sync(Catalog, ["claude"], prune: false, dryRun: false);

        var copy = Path.Join(Claude, "tdd");
        Assert.False(DirectoryLinks.IsLink(copy));
        Assert.True(File.Exists(Path.Join(copy, "SKILL.md")));
        Assert.Equal(["disk-cleanup", "tdd"], AuditClaude().Collisions);
    }

    [Fact]
    public void Prune_removes_broken_and_stale_links_but_never_their_targets()
    {
        Links().Sync(Catalog, ["claude"], prune: false, dryRun: false);
        var dangling = Path.Join(Claude, "dangling-skill");
        Directory.CreateSymbolicLink(dangling, Path.Join(_sources, "gone"));
        var external = _dir.Folder("home/external-skill");
        var stale = Path.Join(Claude, "stale-skill");
        Directory.CreateSymbolicLink(stale, external);

        var audit = AuditClaude();
        Assert.Equal(["dangling-skill"], audit.Broken);
        Assert.Equal(["stale-skill"], audit.Stale);
        Assert.False(audit.Healthy);

        Links().Sync(Catalog, ["claude"], prune: true, dryRun: false);

        Assert.False(DirectoryLinks.Occupied(dangling));
        Assert.False(DirectoryLinks.Occupied(stale));
        Assert.True(Directory.Exists(external));
        Assert.True(Directory.Exists(Path.Join(Claude, "tdd")));
    }

    [Fact]
    public void Prune_never_removes_agent_owned_entries_and_audit_ignores_them()
    {
        Links().Sync(Catalog, ["claude"], prune: false, dryRun: false);
        var synced = Directory.CreateDirectory(Path.Join(Claude, "synced")).FullName;
        File.WriteAllText(Path.Join(synced, "bucket-abc123"), "agent state");
        File.WriteAllText(Path.Join(Claude, "index.json"), "{}");
        File.WriteAllText(Path.Join(Claude, ".DS_Store"), string.Empty);

        Links().Sync(Catalog, ["claude"], prune: true, dryRun: false);

        Assert.Equal("agent state", File.ReadAllText(Path.Join(synced, "bucket-abc123")));
        Assert.True(File.Exists(Path.Join(Claude, "index.json")));
        var audit = AuditClaude();
        Assert.Empty(audit.Collisions);
        Assert.True(audit.Healthy);
    }

    [Fact]
    public void Prune_broken_counts_then_removes_dangling_links()
    {
        Links().Sync(Catalog, ["claude"], prune: false, dryRun: false);
        var dangling = Path.Join(Claude, "gone");
        Directory.CreateSymbolicLink(dangling, Path.Join(_sources, "gone"));
        var file = Path.Join(Claude, "file-link");
        File.CreateSymbolicLink(file, Path.Join(_sources, "gone.txt"));

        var dry = Links().PruneBroken(["claude", "codex"], dryRun: true);
        Assert.Equal([("claude", 2), ("codex", 0)], dry.Select(c => (c.System, c.Count)));
        Assert.True(DirectoryLinks.Occupied(dangling));

        Assert.Equal(2, Links().PruneBroken(["claude"], dryRun: false)[0].Count);
        Assert.False(DirectoryLinks.Occupied(dangling));
        Assert.False(DirectoryLinks.Occupied(file));
        Assert.True(AuditClaude().Healthy);
    }

    [Fact]
    public void Activation_is_reversible_idempotent_and_recorded()
    {
        SkillFixtures.Write(_sources, "hidden", "hidden");
        var catalog = SkillFixtures.Catalog(_sources, ["tdd"]);

        Links().Activate(catalog, ["hidden", " tdd "], all: false, ["opencode"], dryRun: false);
        Links().Activate(catalog, ["hidden"], all: false, ["opencode"], dryRun: false);

        var link = Path.Join(SkillSystems.Directory("opencode", _home), "hidden");
        Assert.True(DirectoryLinks.IsLink(link));
        Assert.Equal(["hidden"], Links().Activated());
        Assert.Equal("[\n  \"hidden\"\n]\n", File.ReadAllText(_places.Ledger));

        Assert.Equal(1, Links().Deactivate(catalog, ["hidden"], all: false, ["opencode"], dryRun: true)[0].Count);
        Assert.True(DirectoryLinks.IsLink(link));

        Links().Deactivate(catalog, ["hidden"], all: false, ["opencode"], dryRun: false);

        Assert.False(DirectoryLinks.Occupied(link));
        Assert.Empty(Links().Activated());
    }

    [Fact]
    public void Deactivating_one_system_keeps_another_systems_overlay()
    {
        SkillFixtures.Write(_sources, "hidden", "hidden");
        var catalog = SkillFixtures.Catalog(_sources, ["tdd"]);

        Links().Activate(catalog, [], all: true, ["opencode", "claude"], dryRun: false);
        Assert.Equal(["disk-cleanup", "hidden"], Links().Activated());

        Links().Deactivate(catalog, ["hidden"], all: false, ["opencode"], dryRun: false);
        Assert.False(DirectoryLinks.Occupied(Path.Join(SkillSystems.Directory("opencode", _home), "hidden")));
        Assert.True(DirectoryLinks.IsLink(Path.Join(Claude, "hidden")));
        Assert.Equal(["disk-cleanup", "hidden"], Links().Activated());

        Links().Deactivate(catalog, [], all: true, ["opencode", "claude"], dryRun: false);
        Assert.Empty(Links().Activated());
        Assert.True(DirectoryLinks.IsLink(Path.Join(Claude, "tdd")));
    }

    [Fact]
    public void A_dry_run_activation_records_nothing()
    {
        Links().Activate(SkillFixtures.Catalog(_sources, ["tdd"]), ["disk-cleanup"], all: false, ["claude"], dryRun: true);

        Assert.False(File.Exists(_places.Ledger));
        Assert.False(Directory.Exists(Claude));
    }

    [Fact]
    public void Activation_rejects_unknown_names()
    {
        var error = Assert.Throws<ArgumentException>(() => Links().Activate(SkillFixtures.Catalog(_sources), ["zz", "missing"], all: false, ["claude"], dryRun: false));

        Assert.Equal("Unknown skill(s): missing, zz", error.Message);
    }

    [Fact]
    public void Deactivation_leaves_a_link_that_leads_elsewhere()
    {
        File.WriteAllText(_dir.File("home/.config/devenv/skill-activations.json"), "[\"tdd\", \"tdd\"]");
        Assert.Equal(["tdd"], Links().Activated());
        _ = Directory.CreateDirectory(Claude);
        Directory.CreateSymbolicLink(Path.Join(Claude, "tdd"), _dir.Folder("elsewhere"));

        var counts = Links().Deactivate(SkillFixtures.Catalog(_sources), ["tdd"], all: false, ["claude", "codex"], dryRun: false);

        Assert.All(counts, c => Assert.Equal(0, c.Count));
        Assert.True(DirectoryLinks.IsLink(Path.Join(Claude, "tdd")));
        Assert.Empty(Links().Activated());
    }

    [Fact]
    public void Remove_and_copy_handle_files_folders_and_skips()
    {
        var source = _dir.Folder("src");
        File.WriteAllText(_dir.File("src/keep.txt"), "k");
        File.WriteAllText(_dir.File("src/sub/deep.txt"), "d");
        File.WriteAllText(_dir.File("src/node_modules/x.js"), "x");

        var copy = Path.Join(_dir.Path, "copy");
        DirectoryLinks.Copy(source, copy, n => n == "node_modules");

        Assert.Equal("d", File.ReadAllText(Path.Join(copy, "sub", "deep.txt")));
        Assert.False(Directory.Exists(Path.Join(copy, "node_modules")));

        DirectoryLinks.Remove(Path.Join(copy, "keep.txt"));
        DirectoryLinks.Remove(copy);
        Assert.False(Directory.Exists(copy));
        Assert.True(DirectoryLinks.Same("/A/b", "/a/B", windows: true));
        Assert.False(DirectoryLinks.Same("/A/b", "/a/B", windows: false));
    }
}
