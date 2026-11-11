using System.Text.Json.Nodes;
using Rig.Core.Governance;
using Rig.Core.Processes;
using Rig.Core.Roots;
using Rig.Core.Tests.Fakes;

namespace Rig.Core.Tests.Governance;

/// <summary><see cref="BranchCleanup"/> with git and gh faked: what a plan says, and that it never deletes on its own.</summary>
public sealed class BranchCleanupTests : IDisposable
{
    private const string Admin = """{"nameWithOwner": "acme/app", "viewerPermission": "ADMIN", "isFork": false}""";

    private readonly TempDir _root = new();
    private readonly FakeProc _proc = new();

    public BranchCleanupTests()
    {
        _ = _proc.Answer(new Ran(0, "git@github.com:acme/app.git\n", string.Empty), "git", "remote", "get-url", "origin")
            .Answer(new Ran(0, Admin, string.Empty), "gh", "repo", "view")
            .Answer(new Ran(0, string.Empty, string.Empty), "git", "fetch")
            .Answer(new Ran(0, "origin\norigin/HEAD -> origin/main\norigin/main\norigin/feat\norigin/open\norigin/stray\norigin/renovate/x\norigin/feat\n", string.Empty), "git", "branch", "-r")
            .Answer(new Ran(0, """[{"number": 3, "headRefName": "feat"}, {"number": 9, "headRefName": "open"}, {"number": 2, "headRefName": "feat"}]""", string.Empty), "gh", "pr", "list", "--state", "merged")
            .Answer(new Ran(0, """[{"number": 12, "headRefName": "open"}]""", string.Empty), "gh", "pr", "list", "--state", "open");
    }

    public void Dispose() => _root.Dispose();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private string Repo => _root.Folder("personal/app");

    private BranchCleanup Subject(string policy = """{"defaults": {"branch_cleanup": {"protect_prefixes": ["renovate/"]}}}""")
    {
        var policies = RepoPolicies.Parse(JsonNode.Parse(policy)!.AsObject(), "policy.json");
        var git = new Git(_proc, _root.Path);
        return new BranchCleanup(_proc, git, new GovernedRepos(_proc, git, new EstateScopes([], []), policies), policies);
    }

    [Fact]
    public async Task Each_branch_gets_one_verdict_and_a_reused_branch_follows_its_open_pr()
    {
        var plan = await Subject().PlanAsync(Repo, Token);

        Assert.Equal(new CleanupPlan("app", plan.Verdicts, "acme/app"), plan);
        Assert.Equal(
            [new("feat", "merged", 3), new("main", "protected"), new("open", "open-pr", 12), new("renovate/x", "protected"), new("stray", "no-pr")],
            plan.Verdicts);
        Assert.Equal(["feat"], plan.Deletable.Select(v => v.Branch));
        Assert.Equal(["stray"], plan.Of("no-pr").Select(v => v.Branch));
        Assert.DoesNotContain(_proc.Requests, r => r.Argv.Contains("--delete"));
        Assert.Contains(_proc.Requests, r => r.Argv.SequenceEqual(["gh", "pr", "list", "--state", "merged", "--limit", "500", "--json", "number,headRefName"]));
    }

    [Fact]
    public async Task Open_pr_heads_can_be_left_to_their_merged_pr()
    {
        var plan = await Subject("""{"defaults": {"branch_cleanup": {"protect_open_pr_heads": false}}}""").PlanAsync(Repo, Token);

        Assert.Contains(new BranchVerdict("open", "merged", 9), plan.Verdicts);
    }

    [Theory]
    [InlineData("""{"nameWithOwner": "them/app", "viewerPermission": "READ", "isFork": false}""", "no push access to them/app")]
    [InlineData("not json", "could not resolve origin")]
    [InlineData("""{"nameWithOwner": "acme/app"}""", "could not resolve origin")]
    public async Task A_repo_you_cannot_push_to_is_skipped_without_reading_branches(string view, string reason)
    {
        _ = _proc.Answer(new Ran(0, view, string.Empty), "gh", "repo", "view");

        var plan = await Subject().PlanAsync(Repo, Token);

        Assert.Equal(reason, plan.Skipped);
        Assert.Empty(plan.Verdicts);
        Assert.DoesNotContain(_proc.Requests, r => r.Argv.Contains("fetch"));
    }

    [Fact]
    public async Task Cleanup_disabled_in_policy_skips_the_repo()
    {
        var plan = await Subject("""{"defaults": {"branch_cleanup": {"enabled": false}}}""").PlanAsync(Repo, Token);

        Assert.Equal(new CleanupPlan("app", [], "acme/app", "cleanup disabled in policy"), plan);
    }

    [Theory]
    [InlineData(1, "[]")]
    [InlineData(0, "")]
    [InlineData(0, "{}")]
    [InlineData(0, """[{"number": "3", "headRefName": "feat"}]""")]
    [InlineData(0, """[{"number": 3}]""")]
    public async Task An_unreadable_pr_list_is_not_an_empty_one(int code, string output)
    {
        _ = _proc.Answer(new Ran(code, output, "boom"), "gh", "pr", "list", "--state", "open");

        var plan = await Subject().PlanAsync(Repo, Token);

        Assert.Equal(("could not read pull requests from gh", 0), (plan.Skipped, plan.Verdicts.Count));
    }

    [Fact]
    public async Task Unlistable_branches_plan_nothing()
    {
        _ = _proc.Answer(new Ran(128, string.Empty, "fatal"), "git", "branch", "-r");

        Assert.Empty((await Subject().PlanAsync(Repo, Token)).Verdicts);
    }

    [Fact]
    public async Task Deleting_reports_the_push_outcome()
    {
        _ = _proc.Answer(new Ran(0, string.Empty, string.Empty), "git", "push", "origin", "--delete", "feat")
            .Answer(new Ran(1, string.Empty, " error: unable to delete 'gone'\n"), "git", "push", "origin", "--delete", "gone");

        Assert.Equal(new BranchDeletion("feat", true), await Subject().DeleteAsync(Repo, "feat", Token));
        Assert.Equal(new BranchDeletion("gone", false, "error: unable to delete 'gone'"), await Subject().DeleteAsync(Repo, "gone", Token));
    }

    [Theory]
    [InlineData("write", true)]
    [InlineData("MAINTAIN", true)]
    [InlineData("TRIAGE", false)]
    [InlineData("", false)]
    public void Only_push_permissions_can_prune(string permission, bool expected) => Assert.Equal(expected, BranchCleanup.CanPrune(permission));

    [Fact]
    public async Task The_identity_is_asked_once_per_checkout()
    {
        var policies = RepoPolicies.Empty;
        var repos = new GovernedRepos(_proc, new Git(_proc, _root.Path), new EstateScopes([], []), policies);

        var first = await repos.IdentityAsync(Repo, Token);
        var second = await repos.IdentityAsync(Repo, Token);

        Assert.Same(first, second);
        Assert.Equal(new RepoIdentity("acme/app", "ADMIN", false), first);
        Assert.Single(_proc.Requests, r => r.Argv[0] == "gh");
    }
}
