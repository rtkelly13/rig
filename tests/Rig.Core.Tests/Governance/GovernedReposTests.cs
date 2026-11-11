using Rig.Core.Governance;
using Rig.Core.Processes;
using Rig.Core.Roots;
using Rig.Core.Tests.Fakes;

namespace Rig.Core.Tests.Governance;

/// <summary>Which local checkouts repo governance acts on, and the pure decision behind it.</summary>
public sealed class GovernedReposTests : IDisposable
{
    private const string Policy = """
        {
          "defaults": {},
          "profiles": {
            "owned": {"repos": ["resultful"]},
            "parked": {"repos": ["shelf"], "repo_selection": {"exempt": true}},
            "loose": {"repos": [], "repo_selection": {"manage_forks": true, "require_push_access": false}}
          },
          "repo_overrides": {"open": {"repo_selection": {"manage_forks": true, "require_push_access": false}}}
        }
        """;

    private readonly TempDir _dir = new();
    private readonly FakeProc _proc = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private string Personal => Path.Join(_dir.Path, "personal");

    private GovernedRepos Subject => new(
        _proc,
        new Git(_proc, _dir.Path),
        new EstateScopes(["rtkelly13"], ["labs"]),
        RepoPolicies.Parse(System.Text.Json.Nodes.JsonNode.Parse(Policy), "policy.json"));

    public void Dispose() => _dir.Dispose();

    [Theory]
    [InlineData(true, false, false, "READ", true, "")]
    [InlineData(true, true, false, "READ", false, "exempt (its profile opts out of governance)")]
    [InlineData(false, false, true, "ADMIN", false, "fork (add it to a profile to manage it)")]
    [InlineData(false, false, false, "read", false, "no push access (read)")]
    [InlineData(false, false, false, "", false, "no push access (unknown)")]
    [InlineData(false, false, false, "maintain", true, "")]
    public void Registration_wins_then_forks_then_push_access(bool registered, bool exempt, bool fork, string permission, bool managed, string reason) =>
        Assert.Equal(new ManageDecision(managed, reason), RepoSelection.ShouldManage(new(Exempt: exempt), registered, fork, permission));

    [Fact]
    public void Rules_can_admit_forks_and_repos_without_push_access() =>
        Assert.True(RepoSelection.ShouldManage(new(ManageForks: true, RequirePushAccess: false), false, true, "READ").Managed);

    [Fact]
    public void Estate_clones_are_each_folder_s_checkouts_in_name_order()
    {
        _ = _dir.Folder("personal/zeta/.git");
        _ = _dir.Folder("personal/alpha/.git");
        _ = _dir.Folder("personal/not-a-clone");
        _ = _dir.File("personal/stray.txt");
        _ = _dir.Folder("labs/lake/.git");

        Assert.Equal(
            [Path.Join(Personal, "alpha"), Path.Join(Personal, "zeta"), Path.Join(_dir.Path, "labs", "lake")],
            Subject.EstateClones(Personal));
    }

    [Theory]
    [InlineData("resultful", """{"nameWithOwner": "o/resultful", "viewerPermission": "READ", "isFork": true}""", true, "")]
    [InlineData("shelf", """{"nameWithOwner": "o/shelf", "viewerPermission": "ADMIN", "isFork": false}""", false, "exempt (its profile opts out of governance)")]
    [InlineData("open", """{"nameWithOwner": "o/open", "viewerPermission": "READ", "isFork": true}""", true, "")]
    [InlineData("theirs", """{"nameWithOwner": "x/theirs", "viewerPermission": "READ", "isFork": false}""", false, "no push access (read)")]
    [InlineData("theirs", """{"nameWithOwner": "x/theirs", "viewerPermission": "READ"}""", false, "could not resolve origin")]
    [InlineData("theirs", """{"nameWithOwner": 3, "viewerPermission": "READ", "isFork": false}""", false, "could not resolve origin")]
    [InlineData("theirs", "[]", false, "could not resolve origin")]
    [InlineData("theirs", "not json", false, "could not resolve origin")]
    [InlineData("theirs", "", false, "could not resolve origin")]
    public async Task A_checkout_is_managed_by_what_github_says_about_it(string name, string view, bool managed, string reason)
    {
        var repo = _dir.Folder($"personal/{name}");
        _ = _proc.Answer(new Ran(0, $"git@github.com:o/{name}.git\n", string.Empty), "git", "remote", "get-url", "origin");
        _ = _proc.Answer(new Ran(0, view, string.Empty), "gh", "repo", "view");

        Assert.Equal(new ManageDecision(managed, reason), await Subject.IsManagedAsync(repo, Token));
        Assert.Equal(["gh", "repo", "view", $"git@github.com:o/{name}.git", "--json", "nameWithOwner,viewerPermission,isFork"], _proc.Requests[^1].Argv);
        Assert.Equal(repo, _proc.Requests[^1].WorkingDirectory);
    }

    [Fact]
    public async Task Without_an_origin_gh_is_asked_about_the_folder_and_a_failure_is_unresolved()
    {
        var repo = _dir.Folder("personal/local");
        _ = _proc.Answer(new Ran(1, """{"nameWithOwner": "o/local", "viewerPermission": "ADMIN", "isFork": false}""", "boom"), "gh");

        Assert.Equal(new ManageDecision(false, "could not resolve origin"), await Subject.IsManagedAsync(repo, Token));
        Assert.Equal(["gh", "repo", "view", "--json", "nameWithOwner,viewerPermission,isFork"], _proc.Requests[^1].Argv);
    }

    [Fact]
    public async Task All_targets_the_managed_clones()
    {
        var kept = _dir.Folder("personal/resultful");
        _ = _dir.Folder("personal/resultful/.git");
        _ = _dir.Folder("personal/theirs/.git");
        _ = _proc.Answer(new Ran(0, """{"nameWithOwner": "o/r", "viewerPermission": "READ", "isFork": false}""", string.Empty), "gh", "repo", "view");

        Assert.Equal([kept], await Subject.ResolveTargetsAsync(null, all: true, Personal, _dir.Path, Token));
    }

    [Fact]
    public async Task A_path_resolves_against_the_working_directory_and_a_missing_one_targets_nothing()
    {
        var repo = PathResolution.Resolve(_dir.Folder("personal/blog"));

        Assert.Equal([repo], await Subject.ResolveTargetsAsync("blog", all: false, Personal, Personal, Token));
        Assert.Empty(await Subject.ResolveTargetsAsync("absent", all: false, Personal, Personal, Token));
        Assert.Equal([Personal], await Subject.ResolveTargetsAsync(null, all: false, Personal, Personal, Token));
        Assert.Empty(_proc.Requests);
    }
}
