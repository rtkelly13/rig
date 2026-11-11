using System.Text.Json.Nodes;
using Rig.Core.Estate;
using Rig.Core.Orca;
using Rig.Core.Roots;
using Rig.Core.Tests.Orca;

namespace Rig.Core.Tests.Estate;

/// <summary>The declared Orca layout against what Orca holds, and closing the gap. Ported from <c>tests/test_estate_orca.py</c>.</summary>
public sealed class OrcaLayoutTests : IDisposable
{
    private static readonly IReadOnlyList<DeclaredProject> Layout =
    [
        Declared("rig", "rtkelly13", string.Empty),
        Declared("blobflags", "rtkelly13", "personal/tooling"),
        Declared("Iceberg.NET", "rtkelly-labs", "rtkelly-labs"),
        Declared("Diffra", "rtkelly-labs", "rtkelly-labs"),
    ];

    private static readonly IReadOnlySet<string> IcebergArchived = new HashSet<string> { "rtkelly-labs/iceberg.net" };

    private readonly TempDir _temp = new();
    private readonly string _code;
    private readonly FakeOrca _fake = new();

    public OrcaLayoutTests()
    {
        _code = Path.Join(PathResolution.Resolve(_temp.Path), "code");
        foreach (var clone in new[] { "personal/rig", "personal/blobflags", "rtkelly-labs/Iceberg.NET", "rtkelly-labs/Diffra" })
        {
            _ = Directory.CreateDirectory(Path.Join(_code, clone));
        }

        _fake.Repos.AddRange([Repo("personal/rig", "rtkelly13", "g-tooling"), Repo("personal/blobflags", "rtkelly13", "g-tooling"), Repo("rtkelly-labs/Iceberg.NET", "rtkelly-labs", null)]);
        _fake.Groups.AddRange(
        [
            FakeOrca.Group("g-personal", "personal", parentPath: Posix("personal")),
            FakeOrca.Group("g-tooling", "tooling", "g-personal"),
            FakeOrca.Group("g-labs", "rtkelly-labs", parentPath: Posix("rtkelly-labs")),
        ]);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _temp.Dispose();

    private static DeclaredProject Declared(string name, string owner, string? group) => new(name, $"git@github.com:{owner}/{name}.git", $"{owner}/{name}", group);

    private static bool IsEstateOwner(string owner) => owner is "rtkelly13" or "rtkelly-labs";

    private string Posix(string clone) => Path.Join(_code, clone).Replace('\\', '/');

    private JsonObject Repo(string clone, string owner, string? groupId) => FakeOrca.Project($"r-{Path.GetFileName(clone)}", Posix(clone), Path.GetFileName(clone), groupId, owner);

    private static List<(string, string)> Kinds(IEnumerable<LayoutFinding> findings) => [.. findings.Select(f => (f.Kind, f.Repo))];

    private static Task<bool?> Idle(string path) => Task.FromResult<bool?>(false);

    private async Task<IReadOnlyList<LayoutFinding>> AuditAsync(
        IReadOnlyList<DeclaredProject> declared,
        FakeOrca? fake = null,
        IReadOnlySet<string>? archived = null,
        IReadOnlySet<string>? skipped = null) =>
        OrcaLayout.Audit(declared, await LayoutSnapshot.ReadAsync((fake ?? _fake).Runtime(), Token), _code, IsEstateOwner, archived: archived, skipped: skipped);

    private Task<IReadOnlyList<LayoutFinding>> ApplyAsync(
        IReadOnlyList<DeclaredProject> declared,
        List<string>? log = null,
        IReadOnlySet<string>? archived = null,
        IReadOnlySet<string>? skipped = null,
        IReadOnlySet<string>? only = null,
        Func<string, Task<bool?>>? inUse = null) =>
        OrcaLayout.ApplyAsync(_fake.Runtime(), declared, _code, IsEstateOwner, inUse ?? Idle, line => log?.Add(line), archived: archived, skipped: skipped, only: only, cancellationToken: Token);

    // ---------------------------------------------------------------- reading the layout

    [Fact]
    public async Task Projects_decode_through_keepalives_and_other_answers()
    {
        var repos = await _fake.Runtime().ProjectsAsync(Token);

        Assert.Equal([("rig", "g-tooling"), ("blobflags", "g-tooling"), ("Iceberg.NET", (string?)null)], repos.Select(r => (r.DisplayName, r.GroupId)));
        Assert.Equal([("repo.list", (JsonObject?)null)], _fake.Calls);
    }

    [Fact]
    public void Load_declared_reads_groups_top_level_and_skips_what_orca_cannot_hold()
    {
        var config = Write("managed-projects.json", """
            {"projects": [
              {"name": "rig", "repo": "git@github.com:rtkelly13/rig.git", "orca": {"group": ""}},
              {"name": "blobflags", "repo": "git@github.com:rtkelly13/blobflags.git", "orca": {"group": "/personal/tooling/"}},
              {"name": "reference", "repo": "git@github.com:someone/reference.git"},
              {"name": "old", "repo": "git@github.com:rtkelly13/old.git", "enabled": false, "orca": {"group": "personal"}},
              {"name": "backup", "repo": "local/backup", "orca": {"group": "personal"}}
            ]}
            """);

        var declared = OrcaLayout.LoadDeclared(config);

        Assert.Equal(
            [("rig", "rtkelly13/rig", string.Empty), ("blobflags", "rtkelly13/blobflags", "personal/tooling"), ("reference", "someone/reference", (string?)null)],
            declared.Select(d => (d.Name, d.Slug, d.Group)));
    }

    [Fact]
    public void Load_declared_reads_archived_projects_from_their_own_file()
    {
        var config = Write("managed-projects.json", """
            {"projects": [
              {"name": "live", "repo": "git@github.com:rtkelly13/live.git"},
              {"name": "gone", "repo": "git@github.com:rtkelly13/gone.git", "enabled": false}
            ]}
            """);
        _ = Write(OrcaLayout.ArchivedProjectsFile, """{"projects": [{"name": "old", "repo": "git@github.com:rtkelly13/old.git", "reason": "archived"}]}""");

        var declared = OrcaLayout.LoadDeclared(config);

        Assert.Equal([("live", false), ("old", true)], declared.Select(d => (d.Name, d.Archived)));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"projects": {}}""")]
    [InlineData("""{"projects": [{"name": "x"}]}""")]
    [InlineData("""{"projects": [{"name": "x", "repo": "git@github.com:o/x.git", "enabled": "yes"}]}""")]
    [InlineData("""{"projects": [{"name": "x", "repo": "git@github.com:o/x.git", "orca": "personal"}]}""")]
    public void Load_declared_refuses_a_malformed_file(string content) =>
        _ = Assert.Throws<InvalidDataException>(() => OrcaLayout.LoadDeclared(Write("managed-projects.json", content)));

    [Fact]
    public async Task Groups_resolve_nested_paths_and_their_scanned_folders()
    {
        var groups = (await LayoutSnapshot.ReadAsync(_fake.Runtime(), Token)).Groups;

        Assert.Equal("personal/tooling", groups.PathOf("g-tooling"));
        Assert.Null(groups.PathOf(null));
        Assert.Null(groups.PathOf("gone"));
        Assert.Equal("g-labs", groups.IdOf("rtkelly-labs"));
        Assert.False(groups.Has("personal/apps"));
        Assert.Equal(Posix("rtkelly-labs"), groups.Folder("rtkelly-labs"));
        Assert.Null(groups.Folder("tooling"));
    }

    // ---------------------------------------------------------------- the audit

    [Fact]
    public async Task The_audit_names_every_difference_and_what_closes_it()
    {
        _fake.Repos.Add(Repo("personal/ops", "rtkelly13", "g-personal"));
        _fake.Repos.Add(Repo("acme/platform", "acme-corp", null));
        IReadOnlyList<DeclaredProject> declared = [.. Layout, Declared("Parquet", "rtkelly-labs", "rtkelly-labs"), Declared("apps", "rtkelly13", "personal/apps")];

        var findings = await AuditAsync(declared);

        Assert.Equal(
            [
                (OrcaLayout.WrongGroup, "rig"),
                (OrcaLayout.WrongGroup, "Iceberg.NET"),
                (OrcaLayout.Unregistered, "Diffra"),
                (OrcaLayout.NoClone, "Parquet"),
                (OrcaLayout.MissingGroup, "personal/apps"),
                (OrcaLayout.NoClone, "apps"),
                (OrcaLayout.Unmanaged, "ops"),
            ],
            Kinds(findings));
        Assert.Equal("under personal/tooling, declared the top level", findings[0].Reason);
        Assert.Equal("under no group, declared rtkelly-labs", findings[1].Reason);
        var labs = Posix("rtkelly-labs");
        Assert.Equal($"{labs}/Diffra", findings[2].Path);
        Assert.Equal($"git clone git@github.com:rtkelly-labs/Parquet.git {labs}/Parquet", findings[3].Command);
    }

    [Fact]
    public async Task An_entry_without_a_placement_is_undeclared_only_when_orca_has_it()
    {
        var findings = await AuditAsync([Declared("blobflags", "rtkelly13", null), Declared("reference", "someone", null)]);

        Assert.Contains((OrcaLayout.Undeclared, "blobflags"), Kinds(findings));
        Assert.DoesNotContain(findings, f => f.Repo == "reference");
    }

    [Fact]
    public async Task A_registration_whose_folder_is_gone_is_stale()
    {
        _fake.Repos.Add(Repo("personal/gone", "rtkelly13", "g-tooling"));

        var findings = await AuditAsync([.. Layout, Declared("gone", "rtkelly13", "personal/tooling")]);

        Assert.Equal([(OrcaLayout.Stale, "registered in Orca, but the folder is gone")], findings.Where(f => f.Repo == "gone").Select(f => (f.Kind, f.Reason)));
    }

    // ---------------------------------------------------------------- applying it

    [Fact]
    public async Task Apply_registers_creates_groups_and_moves_with_top_level_projects_last()
    {
        _ = Directory.CreateDirectory(Path.Join(_code, "personal", "new"));
        IReadOnlyList<DeclaredProject> declared = [.. Layout, Declared("new", "rtkelly-labs", "personal/apps/games")];
        _fake.Repos.Add(Repo("personal/new", "rtkelly-labs", null));
        var log = new List<string>();

        _ = await ApplyAsync(declared, log);

        Assert.Equal(
            [
                "registered Diffra",
                "created group personal/apps",
                "created group personal/apps/games",
                "moved Iceberg.NET to rtkelly-labs",
                "moved Diffra to rtkelly-labs",
                "moved new to personal/apps/games",
                "moved rig to the top level",
            ],
            log);
        Assert.Empty(await AuditAsync(declared));
        Assert.Equal(
            ["""{"name":"apps","parentGroupId":"g-personal"}""", """{"name":"games","parentGroupId":"g-apps"}"""],
            _fake.Calls.Where(c => c.Method == "projectGroup.create").Select(c => c.Params!.ToJsonString()));
        Assert.Contains(_fake.Calls, c => c.Method == "projectGroup.moveProject" && c.Params!.ToJsonString() == """{"repo":"id:r-rig","groupId":null}""");
    }

    [Fact]
    public async Task Apply_changes_nothing_when_orca_already_matches()
    {
        _ = await ApplyAsync(Layout);
        var before = _fake.Calls.Count;

        _ = await ApplyAsync(Layout);

        Assert.Equal(["folderWorkspace.list", "projectGroup.list", "repo.list"], _fake.Calls.Skip(before).Select(c => c.Method).Distinct().Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Apply_with_only_registers_and_groups_the_named_projects_and_leaves_the_rest()
    {
        _fake.Groups.Add(FakeOrca.Group("g-empty", "empty"));
        var log = new List<string>();

        _ = await ApplyAsync(Layout, log, only: new HashSet<string> { "Diffra" });

        Assert.Equal(["registered Diffra", "moved Diffra to rtkelly-labs"], log);
        Assert.Equal([(OrcaLayout.WrongGroup, "rig"), (OrcaLayout.WrongGroup, "Iceberg.NET"), (OrcaLayout.EmptyGroup, "empty")], Kinds(await AuditAsync(Layout)));
    }

    [Fact]
    public async Task Scoped_keeps_the_named_projects_and_the_groups_they_need()
    {
        IReadOnlyList<DeclaredProject> declared = [.. Layout, Declared("apps", "rtkelly13", "personal/apps")];
        var findings = await AuditAsync(declared);

        var scoped = OrcaLayout.Scoped(findings, declared, new HashSet<string> { "apps" });

        Assert.Equal([(OrcaLayout.MissingGroup, "personal/apps"), (OrcaLayout.NoClone, "apps")], Kinds(scoped));
    }

    // ---------------------------------------------------------------- archived repos

    [Fact]
    public async Task An_archived_repo_is_never_placed_only_flagged_for_removal()
    {
        var findings = await AuditAsync(Layout, archived: new HashSet<string> { "rtkelly-labs/iceberg.net", "rtkelly-labs/diffra" });

        var kinds = Kinds(findings);
        Assert.Contains((OrcaLayout.Archived, "Iceberg.NET"), kinds);
        Assert.Contains((OrcaLayout.ArchivedClone, "Iceberg.NET"), kinds);
        Assert.Contains((OrcaLayout.ArchivedClone, "Diffra"), kinds);
        Assert.DoesNotContain(kinds, k => k.Item2 is "Iceberg.NET" or "Diffra" && k.Item1 is not (OrcaLayout.Archived or OrcaLayout.ArchivedClone));
    }

    [Fact]
    public async Task Apply_removes_an_archived_registration_but_never_touches_its_clone()
    {
        _ = await ApplyAsync(Layout, archived: IcebergArchived);

        Assert.Contains(_fake.Calls, c => c.Method == "repo.rm" && c.Params!.ToJsonString() == """{"repo":"id:r-Iceberg.NET"}""");
        Assert.DoesNotContain(_fake.Repos, r => (string?)r["id"] == "r-Iceberg.NET");
        Assert.True(Directory.Exists(Path.Join(_code, "rtkelly-labs", "Iceberg.NET")));
    }

    [Theory]
    [InlineData(true, "a process is working in")]
    [InlineData(null, "could not check")]
    public async Task Apply_leaves_an_archived_repo_it_cannot_show_is_idle(bool? busy, string said)
    {
        var log = new List<string>();

        _ = await ApplyAsync(Layout, log, archived: IcebergArchived, inUse: _ => Task.FromResult(busy));

        Assert.DoesNotContain(_fake.Calls, c => c.Method == "repo.rm");
        Assert.Contains(log, line => line.Contains(said, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Archived_in_the_registry_counts_without_asking_github()
    {
        IReadOnlyList<DeclaredProject> layout = [.. Layout.Take(2), new("Iceberg.NET", "x", "rtkelly-labs/Iceberg.NET", "rtkelly-labs", Archived: true)];

        Assert.Contains((OrcaLayout.Archived, "Iceberg.NET"), Kinds(await AuditAsync(layout)));
    }

    [Fact]
    public async Task An_unlisted_archived_estate_repo_is_archived_not_unmanaged()
    {
        var kinds = Kinds(await AuditAsync([.. Layout.Take(2)], archived: IcebergArchived));

        Assert.Contains((OrcaLayout.Archived, "Iceberg.NET"), kinds);
        Assert.DoesNotContain((OrcaLayout.Unmanaged, "Iceberg.NET"), kinds);
    }

    [Fact]
    public async Task An_unlisted_archived_repos_clone_is_still_reported_after_removal()
    {
        IReadOnlyList<DeclaredProject> layout = [.. Layout.Take(2)];
        Assert.Contains((OrcaLayout.ArchivedClone, "Iceberg.NET"), Kinds(await AuditAsync(layout, archived: IcebergArchived)));

        var carried = await ApplyAsync(layout, archived: IcebergArchived);

        Assert.DoesNotContain((OrcaLayout.ArchivedClone, "Iceberg.NET"), Kinds(await AuditAsync(layout, archived: IcebergArchived)));
        Assert.Equal([(OrcaLayout.ArchivedClone, "Iceberg.NET")], Kinds(carried));
    }

    // ---------------------------------------------------------------- empty groups

    [Fact]
    public async Task An_undeclared_group_with_nothing_in_it_is_reported_once_at_its_outermost()
    {
        _fake.Groups.AddRange([FakeOrca.Group("g-old", "old", "g-personal"), FakeOrca.Group("g-older", "older", "g-old")]);

        var findings = await AuditAsync(Layout);

        Assert.Equal([(OrcaLayout.EmptyGroup, "personal/old")], Kinds(findings.Where(f => f.Kind == OrcaLayout.EmptyGroup)));
    }

    [Fact]
    public async Task A_declared_group_a_folder_workspace_group_and_an_occupied_group_are_kept()
    {
        _fake.Groups.AddRange([FakeOrca.Group("g-apps", "apps", "g-personal"), FakeOrca.Group("g-notes", "notes"), FakeOrca.Group("g-hand", "hand")]);
        _fake.FolderWorkspaces.Add(new() { ["id"] = "w-1", ["projectGroupId"] = "g-notes", ["name"] = "notes" });
        _fake.Repos.Add(Repo("acme/platform", "acme-corp", "g-hand"));

        var findings = await AuditAsync([.. Layout, Declared("apps", "rtkelly13", "personal/apps")]);

        Assert.DoesNotContain(findings, f => f.Kind == OrcaLayout.EmptyGroup);
    }

    [Fact]
    public async Task Apply_deletes_empty_groups_including_ones_it_just_emptied()
    {
        _fake.Groups.Add(FakeOrca.Group("g-old", "old"));
        _fake.Repos[2]["projectGroupId"] = "g-old";
        var log = new List<string>();

        _ = await ApplyAsync(Layout, log);

        Assert.Equal("deleted empty group old", log[^1]);
        Assert.Contains(_fake.Calls, c => c.Method == "projectGroup.delete" && c.Params!.ToJsonString() == """{"groupId":"g-old"}""");
        Assert.Empty(await AuditAsync(Layout));
    }

    // ---------------------------------------------------------------- the work profile

    private FakeOrca Work()
    {
        var work = new FakeOrca();
        work.Repos.AddRange(
        [
            Repo("rtkelly-labs/Diffra", "rtkelly-labs", "g-labs-deep"),
            Repo("personal/rig", "rtkelly13", null),
            Repo("acme/platform", "acme-corp", "g-acme"),
            Repo("acme/data", "acme-corp", "g-mixed"),
            Repo("personal/blobflags", "rtkelly13", "g-mixed"),
        ]);
        work.Groups.AddRange(
        [
            FakeOrca.Group("g-labs", "rtkelly-labs"),
            FakeOrca.Group("g-labs-deep", "parquet", "g-labs"),
            FakeOrca.Group("g-acme", "acme"),
            FakeOrca.Group("g-mixed", "mixed"),
            FakeOrca.Group("g-spare", "spare"),
        ]);
        return work;
    }

    [Fact]
    public async Task The_work_audit_names_estate_repos_and_the_groups_only_they_occupy()
    {
        var findings = OrcaLayout.AuditWork(await LayoutSnapshot.ReadAsync(Work().Runtime(), Token), IsEstateOwner);

        Assert.Equal(
            [(OrcaLayout.InWorkProfile, "Diffra"), (OrcaLayout.InWorkProfile, "rig"), (OrcaLayout.InWorkProfile, "blobflags"), (OrcaLayout.WorkEmptyGroup, "rtkelly-labs")],
            Kinds(findings));
    }

    [Fact]
    public async Task Apply_work_removes_estate_repos_and_their_groups_and_leaves_work_alone()
    {
        var work = Work();
        var log = new List<string>();

        await OrcaLayout.ApplyWorkAsync(work.Runtime(), IsEstateOwner, log.Add, Token);

        Assert.Equal(["platform", "data"], work.Repos.Select(r => (string)r["displayName"]!));
        Assert.Equal(["g-acme", "g-mixed", "g-spare"], work.Groups.Select(g => (string)g["id"]!));
        Assert.Equal("deleted work-profile group rtkelly-labs", log[^1]);
        Assert.Empty(OrcaLayout.AuditWork(await LayoutSnapshot.ReadAsync(work.Runtime(), Token), IsEstateOwner));
    }

    // ---------------------------------------------------------------- which profile

    [Fact]
    public void The_estate_has_its_own_profile_on_windows_and_shares_orcas_elsewhere()
    {
        var windows = new OrcaLocations(_ => null, "/home/u", OrcaOs.Windows);
        Assert.Equal("orca-personal", Path.GetFileName(windows.EstateUserData()));
        Assert.Equal(windows.DefaultUserData(), windows.WorkUserData());

        var mac = new OrcaLocations(_ => null, "/home/u", OrcaOs.MacOs);
        Assert.Equal(mac.DefaultUserData(), mac.EstateUserData());
        Assert.Null(mac.WorkUserData());
    }

    // ---------------------------------------------------------------- repos this machine skips

    [Fact]
    public async Task A_skipped_repo_is_never_unregistered_or_missing_and_is_reported_only_while_registered()
    {
        var findings = await AuditAsync([.. Layout, Declared("hearth", "rtkelly13", "personal/home")], skipped: new HashSet<string> { "Iceberg.NET", "hearth" });

        Assert.Equal([(OrcaLayout.WrongGroup, "rig"), (OrcaLayout.Skipped, "Iceberg.NET"), (OrcaLayout.Unregistered, "Diffra")], Kinds(findings));
        Assert.Equal("in Orca, but listed in tools.estate.skip_repos on this machine", findings[1].Reason);
    }

    [Fact]
    public async Task Apply_never_registers_moves_or_removes_a_skipped_repo_nor_creates_its_group()
    {
        _ = Directory.CreateDirectory(Path.Join(_code, "personal", "hearth"));
        var log = new List<string>();

        _ = await ApplyAsync([.. Layout, Declared("hearth", "rtkelly13", "personal/home")], log, skipped: new HashSet<string> { "Iceberg.NET", "hearth" });

        Assert.Equal(["registered Diffra", "moved Diffra to rtkelly-labs", "moved rig to the top level"], log);
        Assert.Equal(["r-Diffra", "r-Iceberg.NET", "r-blobflags", "r-rig"], _fake.Repos.Select(r => (string)r["id"]!).Order(StringComparer.Ordinal));
        Assert.Null((string?)_fake.Repos.Single(r => (string?)r["id"] == "r-Iceberg.NET")["projectGroupId"]);
    }

    private string Write(string name, string content)
    {
        var path = Path.Join(_temp.Path, name);
        File.WriteAllText(path, content);
        return path;
    }
}
