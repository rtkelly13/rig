using Rig.Core.Governance;
using Rig.Core.Processes;
using Rig.Core.Tests.Fakes;

namespace Rig.Core.Tests.Governance;

/// <summary>
/// The Vercel deployment-creation audit. The planner is pure, so every rule is exercised by handing it facts; the
/// 2026-09-11 incident (83 deployments created in a day, 79 canceled by ignoreCommand) is the fixture that matters
/// most. Reading Vercel goes through a fake API and checkouts in a temp folder.
/// </summary>
public sealed class VercelGatingTests : IDisposable
{
    private const long Now = 1_789_160_000_000;
    private const long Day = 86_400_000;
    private const string Team = "team_x";

    private readonly TempDir _root = new();
    private readonly FakeProc _proc = new();
    private readonly FakeVercel _api = new();

    public void Dispose() => _root.Dispose();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static VercelProjectFacts Facts(
        string? create = "enabled",
        string[]? allow = null,
        string[]? deny = null,
        CreatedDeployment[]? deployments = null,
        string? head = null) =>
        new("design-system", "design-system", create, "main", allow, deny, deployments ?? [], head);

    private static CreatedDeployment Dep(string @ref, string? target = null, string state = "CANCELED", double ageDays = 0, string? sha = null) =>
        new(@ref, target, state, Now - (long)(ageDays * Day), sha);

    private static HashSet<string> Rules(IEnumerable<VercelFinding> findings) => [.. findings.Select(f => f.Rule)];

    private static IReadOnlyList<VercelFinding> Plan(VercelProjectFacts facts) => VercelGating.Plan(facts, Now, 3);

    private VercelGating Gating => new(_api, new Git(_proc, _root.Path));

    [Fact]
    public void Enabled_without_a_repo_allowlist_is_a_creation_gate_error()
    {
        var finding = Assert.Single(Plan(Facts()));

        Assert.Equal(("creation-gate", "error"), (finding.Rule, finding.Severity));
    }

    [Fact]
    public void Enabled_with_a_deny_all_allowlist_is_clean() =>
        Assert.Empty(Plan(Facts(allow: ["main", "preview", "slot/*"], deny: ["**"])));

    [Fact]
    public void An_allowlist_without_a_deny_all_is_only_a_warning()
    {
        var finding = Assert.Single(Plan(Facts(create: "disabled", allow: ["main", "preview"], deny: [])));

        Assert.Equal(("allowlist-has-no-deny-all", "warn"), (finding.Rule, finding.Severity));
    }

    [Fact]
    public void A_legacy_absent_flag_is_not_blamed() =>
        Assert.DoesNotContain("creation-gate", Rules(Plan(Facts(create: null, deployments: [Dep("main", "production", "READY")]))));

    [Fact]
    public void The_incident_is_caught_as_one_budget_leak_listing_each_ref()
    {
        var facts = Facts(
            allow: ["main", "preview", "slot/*"],
            deny: ["**"],
            deployments: [Dep("feat/some-pr"), Dep("feat/other-pr"), Dep("claude/scratch-branch"), Dep("feat/some-pr")]);

        var leak = Assert.Single(Plan(facts), f => f.Rule == "budget-leak");

        Assert.Equal("error", leak.Severity);
        Assert.Equal(
            "created 4 deployment(s) off the allowed refs in the last 3d — each one burns the daily budget: "
            + "claude/scratch-branch ×1, feat/other-pr ×1, feat/some-pr ×2",
            leak.Detail);
    }

    [Fact]
    public void Production_and_declared_slots_are_not_leaks()
    {
        var facts = Facts(
            allow: ["main", "preview", "slot/*"],
            deny: ["**"],
            deployments: [Dep("main", "production", "READY"), Dep("preview", state: "READY"), Dep("slot/2", state: "READY")]);

        Assert.DoesNotContain("budget-leak", Rules(Plan(facts)));
    }

    [Fact]
    public void Deployments_outside_the_window_do_not_count() =>
        Assert.DoesNotContain("budget-leak", Rules(Plan(Facts(allow: ["main"], deny: ["**"], deployments: [Dep("feat/old", ageDays: 30)]))));

    [Fact]
    public void Refs_match_as_fnmatch_does_so_a_bare_star_crosses_slashes()
    {
        Assert.True(VercelGating.RefIsAllowed("slot/3", Facts(allow: ["main", "slot/*"], deny: ["**"])));
        Assert.False(VercelGating.RefIsAllowed("feat/portal", Facts(allow: ["main", "preview", "slot/*"], deny: ["**"])));
        Assert.True(VercelGating.RefIsAllowed("feat/portal", Facts(allow: ["*"], deny: [])));
        Assert.True(VercelGating.RefIsAllowed("main", Facts(allow: [])));
    }

    [Fact]
    public void Production_behind_the_local_tip_is_stale()
    {
        var facts = Facts(allow: ["main"], deny: ["**"], deployments: [Dep("main", "production", "READY", 1, "aaaa1111")], head: "bbbb2222");

        var finding = Assert.Single(Plan(facts));

        Assert.Equal("latest Ready production deployment is aaaa111, origin/main is bbbb222 — production is behind.", finding.Detail);
    }

    [Fact]
    public void Production_at_the_tip_is_clean() =>
        Assert.Empty(Plan(Facts(allow: ["main"], deny: ["**"], deployments: [Dep("main", "production", "READY", sha: "bbbb222")], head: "bbbb222")));

    [Fact]
    public void No_ready_production_build_is_stale_against_a_checkout_or_after_pushes()
    {
        var withCheckout = Assert.Single(Plan(Facts(allow: ["main"], deny: ["**"], head: "bbbb2222")));
        var withoutCheckout = Assert.Single(Plan(Facts(allow: ["main"], deny: ["**"], deployments: [Dep("main", "production", "ERROR")])));

        Assert.Equal("no Ready production deployment in the window while origin/main is at bbbb222.", withCheckout.Detail);
        Assert.Equal("1 push(es) to main in the window, none with a Ready production deployment.", withoutCheckout.Detail);
        Assert.Empty(Plan(Facts(allow: ["main"], deny: ["**"])));
    }

    [Fact]
    public void The_deployment_map_reads_true_and_false_entries_and_a_false_switch()
    {
        var map = _root.File("a/vercel.json");
        File.WriteAllText(map, """{"git": {"deploymentEnabled": {"main": true, "*": false, "x": "nope"}}}""");
        var off = _root.File("b/vercel.json");
        File.WriteAllText(off, """{"git": {"deploymentEnabled": false}}""");
        var on = _root.File("c/vercel.json");
        File.WriteAllText(on, """{"git": {"deploymentEnabled": true}}""");

        var (allow, deny) = VercelGating.DeploymentMap(map);

        Assert.Equal(["main"], allow);
        Assert.Equal(["*"], deny);
        Assert.Empty(VercelGating.DeploymentMap(off).Allow!);
        Assert.Equal(["**"], VercelGating.DeploymentMap(off).Deny);
        Assert.Equal((null, null), VercelGating.DeploymentMap(on));
        Assert.Equal((null, null), VercelGating.DeploymentMap(_root.File("missing/vercel.json")));
    }

    [Fact]
    public async Task Audit_reads_each_linked_project_and_its_checkout()
    {
        _ = _root.Folder("personal/site/.git");
        await File.WriteAllTextAsync(_root.File("personal/site/web/vercel.json"), """{"git": {"deploymentEnabled": {"main": true, "**": false}}}""", Token);
        _ = _proc.Answer(new Ran(0, "cafe1234\n", string.Empty), "git", "rev-parse", "origin/main");
        _ = _api.On(
                "/v9/projects",
                """
                {"projects": [
                  {"id": "prj_1", "name": "site", "link": {"repo": "site", "org": "acme"}, "rootDirectory": "web", "gitProviderOptions": {"createDeployments": "enabled"}},
                  {"id": "prj_2", "name": "cli-only"},
                  {"id": "prj_3", "name": "other", "link": {"repo": "other"}, "productionBranch": "trunk"}
                ]}
                """)
            .On(
                "/v6/deployments",
                $$$"""{"deployments": [{"state": "READY", "createdAt": {{{Now - Day}}}, "target": "production", "meta": {"githubCommitRef": "main", "githubCommitSha": "cafe1234"}}, {"state": "CANCELED", "createdAt": {{{Now}}}, "meta": {}}]}""",
                ("projectId", "prj_1"))
            .On("/v6/deployments", """{"deployments": []}""", ("projectId", "prj_3"));

        var results = await Gating.AuditProjectsAsync(Team, 3, Now, Path.Join(_root.Path, "personal"), cancellationToken: Token);

        Assert.Equal(["site", "other"], results.Select(r => r.Facts.Name));
        var site = results[0];
        Assert.Equal(["main"], site.Facts.Allow);
        Assert.Equal("cafe1234", site.Facts.HeadSha);
        Assert.Equal("?", site.Facts.RecentDeployments[1].Ref);
        Assert.Equal(["budget-leak"], site.Findings.Select(f => f.Rule));
        Assert.Equal(("trunk", null), (results[1].Facts.ProductionRef, results[1].Facts.Allow));
        Assert.Contains(_api.Calls, c => c.Query.Contains(new("limit", "100")) && c.Path == "/v9/projects");
    }

    [Fact]
    public async Task Audit_narrowed_to_one_repo_skips_the_others()
    {
        _ = _api.On("/v9/projects", """{"projects": [{"id": "1", "name": "a", "link": {"repo": "a"}}, {"id": "2", "name": "b", "link": {"repo": "b"}}]}""")
            .On("/v6/deployments", """{"deployments": []}""");

        var results = await Gating.AuditProjectsAsync(Team, 3, Now, onlyRepo: "b", cancellationToken: Token);

        Assert.Equal("b", Assert.Single(results).Facts.Name);
    }

    [Fact]
    public async Task Unreadable_deployments_are_reported_and_the_audit_continues()
    {
        _ = _api.On("/v9/projects", """{"projects": [{"id": "prj_1", "name": "site", "link": {"repo": "site"}}]}""")
            .On("/v6/deployments", """{"deployments": "nope"}""");

        var audit = Assert.Single(await Gating.AuditProjectsAsync(Team, 3, Now, cancellationToken: Token));

        Assert.Empty(audit.Facts.RecentDeployments);
        Assert.Contains("deployments-unreadable", Rules(audit.Findings));
    }

    [Theory]
    [InlineData("""{"projects": "nope"}""")]
    [InlineData("""{"projects": [{"id": "1"}]}""")]
    [InlineData("""{"projects": [{"id": "1", "name": "a", "link": {"repo": 5}}]}""")]
    [InlineData("""{"projects": [{"id": "1", "name": "a", "link": "x"}]}""")]
    [InlineData("[]")]
    public async Task An_unreadable_project_list_fails_the_audit(string listing)
    {
        _ = _api.On("/v9/projects", listing);

        var error = await Assert.ThrowsAsync<VercelApiException>(() => Gating.AuditProjectsAsync(Team, 3, Now, cancellationToken: Token));

        Assert.Equal("Vercel /v9/projects returned an unreadable project list", error.Message);
    }

    [Fact]
    public async Task Project_links_are_sorted_and_name_the_repo_only_with_org_and_repo()
    {
        _ = _api.On("/v9/projects", """{"projects": [{"id": "2", "name": "zed", "link": {"repo": "z"}}, {"id": "1", "name": "alpha", "link": {"repo": "a", "org": "acme"}}]}""");

        var links = await Gating.ListProjectLinksAsync(Team, Token);

        Assert.Equal([new VercelProjectLink("alpha", "acme/a"), new VercelProjectLink("zed", null)], links);
    }

    [Fact]
    public async Task The_latest_commit_comes_from_git_metadata_then_the_cli_deploy()
    {
        _ = _api.On("/v6/deployments", """{"deployments": [{"state": "READY", "createdAt": 1, "meta": {"githubCommitSha": "abc", "gitCommitSha": "def"}}]}""", ("app", "git"))
            .On("/v6/deployments", """{"deployments": [{"state": "READY", "createdAt": 1, "meta": {"gitCommitSha": "def"}}]}""", ("app", "cli"))
            .On("/v6/deployments", """{"deployments": [{"state": "READY", "createdAt": 1, "meta": {"gitCommitSha": 7}}]}""", ("app", "odd"))
            .On("/v6/deployments", """{"deployments": []}""", ("app", "none"));

        Assert.Equal("abc", await Gating.LatestCommitAsync(Team, "git", Token));
        Assert.Equal("def", await Gating.LatestCommitAsync(Team, "cli", Token));
        Assert.Null(await Gating.LatestCommitAsync(Team, "odd", Token));
        Assert.Null(await Gating.LatestCommitAsync(Team, "none", Token));
    }
}
