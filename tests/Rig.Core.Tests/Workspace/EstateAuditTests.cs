using Rig.Core.Processes;
using Rig.Core.Roots;
using Rig.Core.Skills;
using Rig.Core.Tests.Fakes;
using Rig.Core.Workspace;

namespace Rig.Core.Tests.Workspace;

/// <summary>
/// <see cref="EstateAudit"/> over a temp code root and a fake git. Ported from <c>tests/test_workspace_cli.py</c>,
/// <c>tests/test_workspace_layout.py</c>, <c>tests/test_repo_owner_governance.py</c> and the pinned defects in
/// <c>tests/test_estate_sync_state_machine.py</c>.
/// </summary>
public sealed class EstateAuditTests : IDisposable
{
    private static readonly EstateScopes Scopes = new(["rtkelly13"], ["labs"]);
    private readonly TempDir _root = new();
    private readonly FakeProc _proc = new();

    public void Dispose() => _root.Dispose();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private string Estate => Path.Join(_root.Path, "personal");

    private EstateAudit Audit => new(new Git(_proc, _root.Path), Scopes);

    private static ManagedRepoSpec Spec(string name, string owner = "rtkelly13", bool enabled = true, string description = "") =>
        new(name, $"git@github.com:{owner}/{name}.git", description, Enabled: enabled);

    // A clone under the code root whose origin is <paramref name="remote"/>, or that has none.
    private string Clone(string relative, string? remote)
    {
        var folder = _root.Folder(Path.Join(relative, ".git"));
        folder = Path.GetDirectoryName(folder)!;
        _ = _proc.AnswerIn(folder, remote is null ? new(1, string.Empty, string.Empty) : new(0, remote + "\n", string.Empty), "git", "config");
        return folder;
    }

    private Task<EstateAuditResult> RunAsync(params ManagedRepoSpec[] specs) => RunAsync(new HashSet<string>(), specs);

    private Task<EstateAuditResult> RunAsync(IReadOnlySet<string> skip, params ManagedRepoSpec[] specs)
    {
        _ = Directory.CreateDirectory(Estate);
        return Audit.RunAsync(Estate, specs, skip, Token);
    }

    private static string[] Names(IEnumerable<EstateItem> items) => [.. items.Select(i => i.Name)];

    [Fact]
    public async Task Every_kind_of_entry_is_classified()
    {
        _ = Clone("personal/rig", "git@github.com:rtkelly13/rig.git");
        _ = Clone("personal/old", "https://github.com/rtkelly13/old");
        _ = Clone("personal/stray", "git@github.com:x/stray.git");
        _ = _root.File("personal/wt/.git");
        _ = _root.Folder("personal/notes");
        _ = _root.Folder("personal/gone");
        _ = _root.File("personal/junk.txt");
        _ = _root.File("personal/AGENTS.md");
        _ = _root.Folder("personal/.claude");

        var audit = await RunAsync(Spec("rig"), Spec("old", enabled: false, description: "Legacy"), Spec("gone", enabled: false), Spec("missing"));

        Assert.Equal(9, audit.TotalItems);
        Assert.Equal(["rig"], Names(audit.ManagedRepos));
        Assert.Equal("git@github.com:rtkelly13/rig.git", audit.ManagedRepos[0].Remote);
        Assert.Equal(["wt"], Names(audit.LinkedWorktrees));
        Assert.Equal([".claude", "AGENTS.md"], Names(audit.KnownSystemItems));
        Assert.Equal(["gone", "old"], Names(audit.RetainedArchives));
        Assert.Equal(["Retained archive", "Retained archive (Legacy)"], audit.RetainedArchives.Select(i => i.Detail));
        Assert.Equal([null, "https://github.com/rtkelly13/old"], audit.RetainedArchives.Select(i => i.Remote));
        Assert.Equal(
            [
                ("junk.txt", EstateItemKind.UnmanagedFile),
                ("notes", EstateItemKind.UnmanagedDir),
                ("stray", EstateItemKind.UnmanagedRepo),
            ],
            audit.UnmanagedItems.Select(i => (i.Name, i.Kind)));
        Assert.Equal(["missing"], audit.MissingManagedRepos.Select(s => s.Name));
        Assert.False(audit.IsClean);
    }

    [Fact]
    public async Task A_clone_of_a_declared_project_with_another_remote_is_unmanaged()
    {
        _ = Clone("personal/rig", "git@github.com:someone-else/rig.git");

        var audit = await RunAsync(Spec("rig"));

        var item = Assert.Single(audit.UnmanagedItems);
        Assert.Equal(EstateItemKind.UnmanagedRepo, item.Kind);
        Assert.Equal("Remote URL mismatch: local 'git@github.com:someone-else/rig.git' != declared 'git@github.com:rtkelly13/rig.git'", item.Detail);
        Assert.Empty(audit.ManagedRepos);
        Assert.Empty(audit.MissingManagedRepos);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("https://GitHub.com/rtkelly13/rig/")]
    [InlineData("ssh://git@github.com:rtkelly13/rig.git")]
    public async Task A_clone_with_no_origin_or_an_equivalent_one_is_managed(string? remote)
    {
        _ = Clone("personal/rig", remote);

        var audit = await RunAsync(Spec("rig"));

        Assert.Equal(["rig"], Names(audit.ManagedRepos));
        Assert.True(audit.IsClean);
    }

    [Fact]
    public async Task A_local_project_never_mismatches()
    {
        _ = Clone("personal/scratch", "git@github.com:x/y.git");

        var audit = await RunAsync(new ManagedRepoSpec("scratch", "local/scratch"));

        Assert.Equal(["scratch"], Names(audit.ManagedRepos));
    }

    [Fact]
    public async Task An_org_project_is_found_in_its_org_folder_and_missing_when_absent()
    {
        _ = Clone("labs/lake", "git@github.com:labs/lake.git");

        var audit = await RunAsync(Spec("lake", "labs"), Spec("absent", "labs"));

        var lake = Assert.Single(audit.ManagedRepos);
        Assert.Equal(Path.Join(_root.Path, "labs", "lake"), lake.Path);
        Assert.Equal("Registered managed repository (labs org folder)", lake.Detail);
        Assert.Equal(["absent"], audit.MissingManagedRepos.Select(s => s.Name));
    }

    [Fact]
    public async Task A_root_clone_of_an_org_project_is_misplaced()
    {
        _ = Clone("personal/lake", "git@github.com:labs/lake.git");

        var audit = await RunAsync(Spec("lake", "labs"));

        var item = Assert.Single(audit.UnmanagedItems);
        Assert.Equal("Misplaced: declared at ../labs/lake", item.Detail);
        Assert.Equal(["lake"], audit.MissingManagedRepos.Select(s => s.Name));
    }

    [Fact]
    public async Task Whatever_else_occupies_an_org_location_blocks_it_and_is_never_unmanaged()
    {
        _ = Clone("labs/wrong", "git@github.com:x/wrong.git");
        _ = _root.File("labs/file");
        _ = _root.Folder("labs/plain");
        _ = _root.File("labs/wt/.git");

        var audit = await RunAsync(Spec("wrong", "labs"), Spec("file", "labs"), Spec("plain", "labs"), Spec("wt", "labs"));

        Assert.Equal(
            [
                ("wrong", EstateItemKind.UnmanagedRepo, true, "Remote URL mismatch: local 'git@github.com:x/wrong.git' != declared 'git@github.com:labs/wrong.git'"),
                ("file", EstateItemKind.UnmanagedFile, false, "Not a git clone; declared as git@github.com:labs/file.git"),
                ("plain", EstateItemKind.UnmanagedDir, true, "Not a git clone; declared as git@github.com:labs/plain.git"),
                ("wt", EstateItemKind.LinkedWorktree, true, "Not a git clone; declared as git@github.com:labs/wt.git"),
            ],
            audit.BlockedManagedRepos.Select(i => (i.Name, i.Kind, i.IsDir, i.Detail)));
        Assert.Empty(audit.UnmanagedItems);
        Assert.Empty(audit.MissingManagedRepos);
        Assert.False(audit.IsClean);
    }

    [Fact]
    public async Task A_skipped_project_is_never_missing_and_disabled_ones_never_are()
    {
        var audit = await RunAsync(new HashSet<string> { "laptop-only" }, Spec("laptop-only"), Spec("retired", enabled: false));

        Assert.Empty(audit.MissingManagedRepos);
        Assert.True(audit.IsClean);
    }

    [Fact]
    public async Task A_missing_estate_is_empty_and_asks_nothing()
    {
        var audit = await Audit.RunAsync(Path.Join(_root.Path, "nowhere"), [Spec("rig")], new HashSet<string>(), Token);

        Assert.Equal(0, audit.TotalItems);
        Assert.Empty(audit.MissingManagedRepos);
        Assert.Empty(_proc.Requests);
    }

    [Theory]
    [InlineData("git@github.com:Owner/Repo.git", "github.com/owner/repo")]
    [InlineData("https://github.com/owner/repo/", "github.com/owner/repo")]
    [InlineData("http://github.com/owner/repo.git", "github.com/owner/repo")]
    [InlineData("ssh://git@github.com:owner/repo.git", "github.com/owner/repo")]
    [InlineData("  git@host:a:b.git  ", "host/a:b")]
    [InlineData("local/thing", "local/thing")]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Remotes_normalize_for_comparison(string? remote, string? expected) =>
        Assert.Equal(expected, EstateAudit.NormalizeRemote(remote));

    [Theory]
    [InlineData(EstateItemKind.ManagedRepo)]
    [InlineData(EstateItemKind.KnownSystem)]
    [InlineData(EstateItemKind.RetainedArchive)]
    public async Task Managed_system_and_retained_items_are_refused(EstateItemKind kind)
    {
        var path = _root.Folder("personal/keep");

        var outcome = await Audit.CleanAsync(new("keep", path, kind, true, null, string.Empty), dryRun: false, Token);

        Assert.Equal(new CleanOutcome(false, "Refused: keep is a managed, system, or retained archive item"), outcome);
        Assert.True(Directory.Exists(path));
    }

    [Fact]
    public async Task A_dry_run_says_what_it_would_remove_and_removes_nothing()
    {
        var path = _root.File("personal/junk.txt");

        var outcome = await Audit.CleanAsync(new("junk.txt", path, EstateItemKind.UnmanagedFile, false, null, string.Empty), dryRun: true, Token);

        Assert.Equal(new CleanOutcome(true, $"[dry-run] Would remove unmanaged_file: {path}"), outcome);
        Assert.True(File.Exists(path));
        Assert.Empty(_proc.Requests);
    }

    [Fact]
    public async Task Unmanaged_files_and_folders_are_removed()
    {
        var file = _root.File("personal/junk.txt");
        var dir = Path.GetDirectoryName(_root.File("personal/notes/a/b.md"))!;
        dir = Path.GetDirectoryName(dir)!;

        var removedFile = await Audit.CleanAsync(new("junk.txt", file, EstateItemKind.UnmanagedFile, false, null, string.Empty), dryRun: false, Token);
        var removedDir = await Audit.CleanAsync(new("notes", dir, EstateItemKind.UnmanagedDir, true, null, string.Empty), dryRun: false, Token);

        Assert.Equal(new CleanOutcome(true, "Removed junk.txt"), removedFile);
        Assert.Equal(new CleanOutcome(true, "Removed notes"), removedDir);
        Assert.False(Path.Exists(file));
        Assert.False(Path.Exists(dir));
    }

    [Theory]
    [InlineData(0, " M a.txt\n")]
    [InlineData(128, "")]
    [InlineData(null, "")]
    public async Task A_dirty_or_unreadable_clone_is_refused(int? code, string output)
    {
        var clone = Clone("personal/stray", null);
        if (code is { } exit)
        {
            _ = _proc.AnswerIn(clone, new(exit, output, string.Empty), "git", "status");
        }

        var outcome = await Audit.CleanAsync(new("stray", clone, EstateItemKind.UnmanagedRepo, true, null, string.Empty), dryRun: false, Token);

        Assert.Equal(new CleanOutcome(false, "Refused to delete dirty or unreadable unmanaged repo: stray"), outcome);
        Assert.True(Directory.Exists(clone));
    }

    [Fact]
    public async Task A_clean_clone_is_removed()
    {
        var clone = Clone("personal/stray", null);
        _ = _proc.AnswerIn(clone, new(0, string.Empty, string.Empty), "git", "status");

        var outcome = await Audit.CleanAsync(new("stray", clone, EstateItemKind.UnmanagedRepo, true, null, string.Empty), dryRun: false, Token);

        Assert.Equal(new CleanOutcome(true, "Removed stray"), outcome);
        Assert.False(Path.Exists(clone));
    }

    [Fact]
    public async Task A_linked_folder_is_refused_and_its_target_kept()
    {
        var target = Path.GetDirectoryName(_root.File("elsewhere/precious.txt"))!;
        var link = Path.Join(_root.Folder("personal"), "link");
        new SystemDirectoryLinker().Link(link, target);

        var outcome = await Audit.CleanAsync(new("link", link, EstateItemKind.UnmanagedDir, true, null, string.Empty), dryRun: false, Token);

        Assert.False(outcome.Ok);
        Assert.StartsWith("Failed to remove link:", outcome.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Join(target, "precious.txt")));
        Assert.True(Path.Exists(link));
    }
}
