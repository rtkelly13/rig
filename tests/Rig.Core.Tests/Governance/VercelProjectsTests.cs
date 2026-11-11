using Rig.Core.Governance;
using Rig.Core.Processes;
using Rig.Core.Tests.Fakes;

namespace Rig.Core.Tests.Governance;

/// <summary>Vercel project names in managed-projects.json, and the ownership audit against Vercel's facts.</summary>
public sealed class VercelProjectsTests : IDisposable
{
    private static readonly KeyValuePair<string, string>[] Repos =
    [
        new("design-system", "rtkelly-labs/design-system"),
        new("rig", "rtkelly13/rig"),
        new("blog", "rtkelly13/blog"),
    ];

    private static readonly VercelClaim[] Claims =
    [
        new("design-system", "rtkelly-labs/design-system", ["design-system", "ds-review"]),
        new("rig", "rtkelly13/rig", ["shared-utilities"]),
    ];

    private readonly TempDir _root = new();

    public void Dispose() => _root.Dispose();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static Dictionary<string, List<string>> Verdicts(params Ownership[] owned)
    {
        var found = new Dictionary<string, List<string>>();
        foreach (var finding in VercelProjects.PlanFindings(owned, Claims, Repos))
        {
            if (!found.TryGetValue(finding.VercelProject, out var rules))
            {
                found[finding.VercelProject] = rules = [];
            }

            rules.Add(finding.Rule);
        }

        return found;
    }

    private string Registry(string projects)
    {
        var path = _root.File("config/managed-projects.json");
        File.WriteAllText(path, $$"""{"projects": {{projects}}}""");
        return path;
    }

    [Fact]
    public void Claims_and_slugs_load_from_the_registry()
    {
        var registry = Registry("""
            [{"name": "a", "repo": "git@github.com:o/a.git", "vercel": ["site", "site-docs"]},
             {"name": "b", "repo": "git@github.com:o/b.git", "enabled": false},
             {"name": "c", "repo": "local/c"}]
            """);

        var claim = Assert.Single(VercelProjects.LoadClaims(registry));

        Assert.Equal(("a", "o/a"), (claim.Project, claim.Slug));
        Assert.Equal(["site", "site-docs"], claim.Names);
        Assert.Equal([new KeyValuePair<string, string>("a", "o/a")], VercelProjects.LoadRepoSlugs(registry));
    }

    [Fact]
    public void A_name_declared_twice_is_rejected()
    {
        var registry = Registry("""
            [{"name": "a", "repo": "git@github.com:o/a.git", "vercel": ["site"]},
             {"name": "b", "repo": "git@github.com:o/b.git", "vercel": ["site"]}]
            """);

        Assert.Equal("b: vercel project 'site' is already declared by a", Assert.Throws<VercelClaimException>(() => VercelProjects.LoadClaims(registry)).Message);
    }

    [Theory]
    [InlineData("Site")]
    [InlineData("-site")]
    [InlineData("site_x")]
    [InlineData("")]
    public void Names_must_be_vercel_names(string name)
    {
        var registry = Registry($$"""[{"name": "a", "repo": "git@github.com:o/a.git", "vercel": ["{{name}}"]}]""");

        Assert.EndsWith("is not a Vercel project name", Assert.Throws<VercelClaimException>(() => VercelProjects.LoadClaims(registry)).Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""[{"name": "a", "repo": "local/a", "vercel": ["site"]}]""", "a: vercel projects need a GitHub repo to belong to, not 'local/a'")]
    [InlineData("""[{"name": "a"}]""", "a project entry is misshapen")]
    [InlineData("""[{"name": "a", "repo": "r", "vercel": [1]}]""", "a project entry is misshapen")]
    [InlineData("""[{"name": "a", "repo": "r", "enabled": "yes"}]""", "a project entry is misshapen")]
    [InlineData("\"nope\"", "projects is not a list")]
    public void A_malformed_registry_is_rejected(string projects, string message) =>
        Assert.EndsWith(message, Assert.Throws<VercelClaimException>(() => VercelProjects.LoadClaims(Registry(projects))).Message, StringComparison.Ordinal);

    [Fact]
    public void A_missing_registry_is_rejected() =>
        _ = Assert.Throws<VercelClaimException>(() => VercelProjects.LoadRepoSlugs(Path.Join(_root.Path, "missing.json")));

    [Fact]
    public void Matching_declarations_are_confirmed()
    {
        var found = Verdicts(new("shared-utilities", "rtkelly13/rig", "link"), new("design-system", "rtkelly-labs/design-system", "commit"));

        Assert.Equal(["ok"], found["shared-utilities"]);
        Assert.Equal(["ok"], found["design-system"]);
    }

    [Fact]
    public void Undeclared_projects_name_the_owner_vercel_reports_or_say_it_is_unknown()
    {
        var findings = VercelProjects.PlanFindings([new("blog", "rtkelly13/blog", "link"), new("stray", "else/where", "link")], Claims, Repos);

        Assert.Equal("deploys from blog (its git link): declare it under blog", findings.Single(f => f.VercelProject == "blog").Detail);
        Assert.Equal("owner unknown (else/where, not managed): declare it", findings.Single(f => f.VercelProject == "stray").Detail);
    }

    [Fact]
    public void A_link_naming_a_repos_old_owner_is_stale_but_still_confirms()
    {
        var findings = VercelProjects.PlanFindings([new("design-system", "rtkelly13/design-system", "link")], Claims, Repos);

        Assert.Equal(["stale-link", "ok"], findings.Where(f => f.VercelProject == "design-system").Select(f => f.Rule));
        Assert.Equal("Vercel names rtkelly13/design-system, which is now rtkelly-labs/design-system: relink the project", findings[0].Detail);
    }

    [Fact]
    public void Declared_under_another_repo_is_a_mismatch()
    {
        Assert.Equal(["mismatch"], Verdicts(new Ownership("shared-utilities", "rtkelly13/blog", "link"))["shared-utilities"]);
        Assert.Equal(["mismatch"], Verdicts(new Ownership("shared-utilities", "else/where", "commit"))["shared-utilities"]);
    }

    [Fact]
    public void No_link_or_commit_rests_on_the_declaration()
    {
        Assert.Equal(["unverified"], Verdicts(new Ownership("ds-review", null, "none"))["ds-review"]);
        Assert.Equal(["undeclared"], Verdicts(new Ownership("mystery", null, "none"))["mystery"]);
    }

    [Fact]
    public void A_declared_name_vercel_lacks_is_missing() => Assert.Equal(["missing"], Verdicts()["ds-review"]);

    [Fact]
    public async Task Commit_source_tries_the_declared_repo_first()
    {
        var asked = new List<string>();

        var found = await VercelProjects.CommitSourceAsync(
            "abc",
            Repos,
            "rig",
            (slug, sha, _) =>
            {
                asked.Add(slug);
                return Task.FromResult(slug == "rtkelly13/rig" && sha == "abc");
            },
            Token);

        Assert.Equal("rtkelly13/rig", found);
        Assert.Equal(["rtkelly13/rig"], asked);
        Assert.Null(await VercelProjects.CommitSourceAsync("zzz", Repos, null, (_, _, _) => Task.FromResult(false), Token));
    }

    [Fact]
    public async Task Gather_takes_the_link_else_the_repo_holding_the_newest_commit()
    {
        var vercel = new FakeVercel()
            .On("/v9/projects", """{"projects": [{"id": "1", "name": "shared-utilities", "link": {"org": "rtkelly13", "repo": "rig"}}, {"id": "2", "name": "ds-review"}, {"id": "3", "name": "orphan"}]}""")
            .On("/v6/deployments", """{"deployments": [{"state": "READY", "createdAt": 1, "meta": {"gitCommitSha": "abc"}}]}""", ("app", "ds-review"))
            .On("/v6/deployments", """{"deployments": []}""", ("app", "orphan"));
        var gitHub = new FakeGitHub()
            .On("GET", "repos/rtkelly-labs/design-system/commits/abc", """{"sha": "abc"}""");
        var gating = new VercelGating(vercel, new Git(new FakeProc(), _root.Path));

        var owned = await VercelProjects.GatherAsync(gating, gitHub, "team_x", Claims, Repos, Token);

        Assert.Equal(
            [
                new Ownership("ds-review", "rtkelly-labs/design-system", "commit"),
                new Ownership("orphan", null, "none"),
                new Ownership("shared-utilities", "rtkelly13/rig", "link"),
            ],
            owned);
        Assert.Equal("repos/rtkelly-labs/design-system/commits/abc", Assert.Single(gitHub.Calls).Route);
    }

    [Fact]
    public async Task A_commit_counts_only_when_github_answers_with_that_sha()
    {
        var gitHub = new FakeGitHub().On("GET", "repos/o/a/commits/abc", """{"sha": "abcdef"}""");

        Assert.False(await VercelProjects.HasCommitAsync(gitHub, "o/a", "abc", Token));
        Assert.False(await VercelProjects.HasCommitAsync(gitHub, "o/b", "abc", Token));
    }
}
