using Rig.Core.Estate;
using Rig.Core.Processes;
using Rig.Core.Roots;
using Rig.Core.Tests.Orca;

namespace Rig.Core.Tests.Estate;

/// <summary>A project renamed, or moved between owners, found under its old name and moved to its new one.</summary>
public sealed class OrcaRenamesTests : IDisposable
{
    private readonly TempDir _temp = new();
    private readonly string _code;
    private readonly FakeOrca _fake = new();

    public OrcaRenamesTests()
    {
        _code = Path.Join(PathResolution.Resolve(_temp.Path), "code");
        _fake.Groups.Add(FakeOrca.Group("g-resultful", "Resultful"));
        _fake.AddedOwner = "Resultful";
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static DeclaredProject Union(params string[] formerly) =>
        new("Union.Assertions", "git@github.com:Resultful/Union.Assertions.git", "Resultful/Union.Assertions", "Resultful", Formerly: formerly.Length > 0 ? formerly : null);

    public void Dispose() => _temp.Dispose();

    private string Clone(string relative) => Directory.CreateDirectory(Path.Join(_code, relative)).FullName.Replace('\\', '/');

    private async Task<IReadOnlyList<LayoutFinding>> AuditAsync(params DeclaredProject[] declared) =>
        OrcaLayout.Audit(declared, await LayoutSnapshot.ReadAsync(_fake.Runtime(), Token), _code, owner => owner.Equals("Resultful", StringComparison.OrdinalIgnoreCase));

    [Fact]
    public async Task A_registration_under_an_old_remote_is_renamed_not_unmanaged()
    {
        var old = Clone("Resultful/FluentAssertions.Resultful");
        _fake.Repos.Add(FakeOrca.Project("r-old", old, "FluentAssertions.Resultful", "g-resultful", "Resultful"));

        var finding = Assert.Single(await AuditAsync(Union("resultful/fluentassertions.resultful")));

        Assert.Equal((OrcaLayout.Renamed, "Union.Assertions", old), (finding.Kind, finding.Repo, finding.Path));
        Assert.Contains($"move it to {_code.Replace('\\', '/')}/Resultful/Union.Assertions", finding.SuggestedAction, StringComparison.Ordinal);
        Assert.Equal("estate orca --apply", finding.Command);
    }

    [Fact]
    public async Task Without_its_old_name_the_same_registration_is_unmanaged_and_the_project_not_cloned()
    {
        _fake.Repos.Add(FakeOrca.Project("r-old", Clone("Resultful/FluentAssertions.Resultful"), "FluentAssertions.Resultful", "g-resultful", "Resultful"));

        var kinds = (await AuditAsync(Union())).Select(f => f.Kind).Order(StringComparer.Ordinal);

        Assert.Equal([OrcaLayout.NoClone, OrcaLayout.Unmanaged], kinds);
    }

    [Fact]
    public async Task An_unregistered_clone_with_an_old_name_is_renamed()
    {
        var old = Clone("Resultful/FluentAssertions.Resultful");

        var finding = Assert.Single(await AuditAsync(Union("resultful/fluentassertions.resultful")));

        Assert.Equal((OrcaLayout.Renamed, old), (finding.Kind, finding.Path));
        Assert.StartsWith("cloned as resultful/fluentassertions.resultful", finding.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Both_clones_present_is_left_for_a_human()
    {
        _fake.Repos.Add(FakeOrca.Project("r-old", Clone("Resultful/FluentAssertions.Resultful"), "FluentAssertions.Resultful", "g-resultful", "Resultful"));
        _ = Clone("Resultful/Union.Assertions");

        var finding = Assert.Single(await AuditAsync(Union("resultful/fluentassertions.resultful")));

        Assert.Equal((OrcaLayout.Renamed, string.Empty), (finding.Kind, finding.Command));
        Assert.Contains("exists too", finding.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Apply_moves_the_clone_registers_it_again_and_groups_it()
    {
        var old = Clone("Resultful/FluentAssertions.Resultful");
        _fake.Repos.Add(FakeOrca.Project("r-old", old, "FluentAssertions.Resultful", null, "Resultful"));
        var mover = new MovingMover();
        var log = new List<string>();

        _ = await OrcaLayout.ApplyAsync(
            _fake.Runtime(), [Union("resultful/fluentassertions.resultful")], _code, _ => true, _ => Task.FromResult<bool?>(false), log.Add, mover: mover, cancellationToken: Token);

        var target = Path.Join(_code, "Resultful", "Union.Assertions");
        var moved = Assert.Single(mover.Moves);
        Assert.Equal((target, "git@github.com:Resultful/Union.Assertions.git"), (moved.To, moved.Repo));
        Assert.Contains("resultful/fluentassertions.resultful", moved.Accepted);
        var repo = Assert.Single(_fake.Repos);
        Assert.Equal((target.Replace('\\', '/'), "g-resultful"), (((string)repo["path"]!).Replace('\\', '/'), (string?)repo["projectGroupId"]));
        Assert.Contains(log, line => line.StartsWith("moved Union.Assertions from", StringComparison.Ordinal));
        Assert.Empty(await AuditAsync(Union("resultful/fluentassertions.resultful")));
    }

    [Theory]
    [InlineData(true, "a process is working in it")]
    [InlineData(null, "could not check")]
    public async Task Apply_leaves_a_clone_a_process_may_be_working_in(bool? busy, string why)
    {
        _fake.Repos.Add(FakeOrca.Project("r-old", Clone("Resultful/FluentAssertions.Resultful"), "FluentAssertions.Resultful", "g-resultful", "Resultful"));
        var mover = new MovingMover();
        var log = new List<string>();

        _ = await OrcaLayout.ApplyAsync(
            _fake.Runtime(), [Union("resultful/fluentassertions.resultful")], _code, _ => true, _ => Task.FromResult(busy), log.Add, mover: mover, cancellationToken: Token);

        Assert.Empty(mover.Moves);
        Assert.Equal("r-old", (string?)Assert.Single(_fake.Repos)["id"]);
        Assert.Contains(log, line => line.Contains(why, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_refused_move_leaves_orca_as_it_was()
    {
        _fake.Repos.Add(FakeOrca.Project("r-old", Clone("Resultful/FluentAssertions.Resultful"), "FluentAssertions.Resultful", "g-resultful", "Resultful"));
        var log = new List<string>();

        _ = await OrcaLayout.ApplyAsync(
            _fake.Runtime(), [Union("resultful/fluentassertions.resultful")], _code, _ => true, _ => Task.FromResult<bool?>(false), log.Add, mover: new MovingMover("it has linked worktrees"), cancellationToken: Token);

        Assert.Equal("r-old", (string?)Assert.Single(_fake.Repos)["id"]);
        Assert.Contains(log, line => line.EndsWith("it has linked worktrees", StringComparison.Ordinal));
    }

    [Fact]
    public void Renames_github_reports_become_former_names_of_the_project_now_holding_them()
    {
        var renamed = new Dictionary<string, string> { ["resultful/fluentassertions.resultful"] = "Resultful/Union.Assertions", ["rtkelly13/old"] = "rtkelly13/elsewhere" };

        var project = Assert.Single(OrcaLayout.WithFormerNames([Union()], renamed));

        Assert.Equal(["resultful/fluentassertions.resultful"], project.FormerSlugs);
        Assert.Empty(OrcaLayout.RenamedOnGitHubFindings([project], renamed));
    }

    [Fact]
    public void A_declared_repo_github_serves_under_another_name_is_a_config_fix()
    {
        var stale = new DeclaredProject("FluentAssertions.Resultful", "git@github.com:Resultful/FluentAssertions.Resultful.git", "Resultful/FluentAssertions.Resultful", "Resultful");

        var finding = Assert.Single(OrcaLayout.RenamedOnGitHubFindings([stale], new Dictionary<string, string> { ["resultful/fluentassertions.resultful"] = "Resultful/Union.Assertions" }));

        Assert.Equal((OrcaLayout.RenamedOnGitHub, string.Empty), (finding.Kind, finding.Command));
        Assert.Contains("git@github.com:Resultful/Union.Assertions.git", finding.SuggestedAction, StringComparison.Ordinal);
        Assert.Contains("\"formerly\": [\"Resultful/FluentAssertions.Resultful\"]", finding.SuggestedAction, StringComparison.Ordinal);
    }

    [Fact]
    public void A_move_between_owners_lands_where_the_new_group_expects()
    {
        var project = new DeclaredProject("Diffra", "git@github.com:rtkelly-labs/Diffra.git", "rtkelly-labs/Diffra", "rtkelly-labs", Formerly: ["rtkelly13/diffra"]);
        var groups = new GroupTree([]);

        Assert.Equal(
            Path.Join(_code, "rtkelly-labs", "Diffra"),
            OrcaLayout.RenamedClone(project, new FormerPlace("rtkelly13/diffra", Path.Join(_code, "personal", "Diffra"), null), groups, _code));
        Assert.Equal(
            Path.Join(_code, "personal", "Union.Assertions"),
            OrcaLayout.RenamedClone(Union(), new FormerPlace("resultful/x", Path.Join(_code, "personal", "x"), null), groups, _code));
    }

    [Fact]
    public void Former_names_are_read_lowercase_and_a_bad_one_is_refused()
    {
        var good = _temp.File("good.json");
        System.IO.File.WriteAllText(good, """
            {"projects": [{"name": "Union.Assertions", "repo": "git@github.com:Resultful/Union.Assertions.git", "formerly": ["Resultful/FluentAssertions.Resultful"]}]}
            """);
        var bad = _temp.File("bad.json");
        System.IO.File.WriteAllText(bad, """
            {"projects": [{"name": "x", "repo": "git@github.com:a/x.git", "formerly": ["not a slug"]}]}
            """);

        Assert.Equal(["resultful/fluentassertions.resultful"], Assert.Single(OrcaLayout.LoadDeclared(good)).FormerSlugs);
        Assert.Contains("x.formerly", Assert.Throws<InvalidDataException>(() => OrcaLayout.LoadDeclared(bad)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_git_mover_points_origin_at_the_new_name_and_moves_the_folder()
    {
        var old = await Repos.InitAsync(Path.Join(_code, "Resultful", "FluentAssertions.Resultful"), "git@github.com:Resultful/FluentAssertions.Resultful.git");
        await File.WriteAllTextAsync(Path.Join(old, "draft.txt"), "uncommitted", Token);
        var target = Path.Join(_code, "Resultful", "Union.Assertions");
        var mover = new GitCloneMover(new Git(Repos.Proc, _code));

        var refused = await mover.MoveAsync(old, target, "git@github.com:Resultful/Union.Assertions.git", new HashSet<string> { "resultful/fluentassertions.resultful" }, Token);

        Assert.Null(refused);
        Assert.False(Directory.Exists(old));
        Assert.Equal("uncommitted", await File.ReadAllTextAsync(Path.Join(target, "draft.txt"), Token));
        Assert.Equal("git@github.com:Resultful/Union.Assertions.git", await Repos.GitAsync(target, "remote", "get-url", "origin"));
    }

    [Fact]
    public async Task The_git_mover_refuses_another_repo_and_one_with_linked_worktrees()
    {
        var other = await Repos.InitAsync(Path.Join(_code, "a", "FluentAssertions.Resultful"), "git@github.com:someone/else.git");
        var linked = await Repos.InitAsync(Path.Join(_code, "b", "FluentAssertions.Resultful"), "git@github.com:Resultful/FluentAssertions.Resultful.git");
        _ = await Repos.GitAsync(linked, "worktree", "add", "-q", "-b", "side", Path.Join(_code, "b", "side"));
        var mover = new GitCloneMover(new Git(Repos.Proc, _code));
        var accepted = new HashSet<string> { "resultful/fluentassertions.resultful" };

        Assert.Contains("not one of the project's names", await mover.MoveAsync(other, Path.Join(_code, "a", "Union.Assertions"), "git@github.com:Resultful/Union.Assertions.git", accepted, Token), StringComparison.Ordinal);
        Assert.Contains("linked worktrees", await mover.MoveAsync(linked, Path.Join(_code, "b", "Union.Assertions"), "git@github.com:Resultful/Union.Assertions.git", accepted, Token), StringComparison.Ordinal);
        Assert.True(Directory.Exists(other) && Directory.Exists(linked));
        Assert.Equal("git@github.com:Resultful/FluentAssertions.Resultful.git", await Repos.GitAsync(linked, "remote", "get-url", "origin"));
    }

    // Moves the folder as the git mover would, without git.
    private sealed class MovingMover(string? refusal = null) : ICloneMover
    {
        public List<(string From, string To, string Repo, IReadOnlySet<string> Accepted)> Moves { get; } = [];

        public Task<string?> MoveAsync(string source, string destination, string repo, IReadOnlySet<string> accepted, CancellationToken cancellationToken = default)
        {
            if (refusal is not null)
            {
                return Task.FromResult<string?>(refusal);
            }

            Moves.Add((source, destination, repo, accepted));
            Directory.Move(source, destination);
            return Task.FromResult<string?>(null);
        }
    }
}
