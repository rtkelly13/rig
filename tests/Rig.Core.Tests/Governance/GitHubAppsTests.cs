using System.Text.Json.Nodes;
using Rig.Core.Governance;
using Rig.Core.Tests.Fakes;

namespace Rig.Core.Tests.Governance;

/// <summary><see cref="GitHubApps"/> over a fake GitHub. Ported from <c>tests/test_github_apps_governance.py</c>.</summary>
public sealed class GitHubAppsTests
{
    private const string Policy = """
        {
          "profiles": {"labs": {"expected_owner": "labs", "repos": ["a", "b", "c"]}},
          "github_apps": {
            "renovate": {"name": "Renovate", "accounts": {"labs": {"required": true, "repos": ["a", "b"], "disallow_repos": ["c"]}}},
            "codecov": {"accounts": {"labs": {"all_repos": true, "exclude_repos": ["c"]}}},
            "optional": {"accounts": {"labs": {"repos": ["a"]}}}
          }
        }
        """;

    private const string Installations = "orgs/labs/installations?per_page=100&page=1";
    private const string RenovateRepos = "user/installations/7/repositories?per_page=100&page=1";

    private readonly FakeGitHub _api = new();
    private readonly List<string> _discovered = [];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static RepoPolicies Policies => RepoPolicies.Parse(JsonNode.Parse(Policy), "policy.json");

    private GitHubApps Subject => new(_api, Policies, (_, _) => Task.FromResult<IReadOnlyList<string>>(_discovered));

    private GitHubApps Installed(string selection = "selected", params string[] repos)
    {
        _ = _api.On("GET", "users/labs", """{"type": "Organization"}""")
            .On("GET", Installations, $$"""{"installations": [{"id": 7, "app_slug": "Renovate", "repository_selection": "{{selection}}"}]}""")
            .On("GET", RenovateRepos, new JsonObject { ["repositories"] = new JsonArray([.. repos.Select(r => (JsonNode)new JsonObject { ["name"] = r })]) }.ToJsonString());
        return Subject;
    }

    [Fact]
    public void Apps_parse_their_names_install_pages_and_disallowed_repos()
    {
        var apps = Policies.Apps;

        Assert.Equal(["renovate", "codecov", "optional"], apps.Select(a => a.Slug));
        Assert.Equal("Renovate", apps[0].Name);
        Assert.Equal("codecov", apps[1].Name);
        Assert.Equal("https://github.com/apps/codecov/installations/new", apps[1].InstallPage);
        Assert.Equal(["c"], apps[1].Accounts["labs"].DisallowRepos);
        Assert.Equal(["a", "b"], apps[1].Accounts["labs"].EffectiveRepos(["a", "b", "c"]));
        Assert.True(Policies.IsAppDisallowed("labs/c", "labs", "renovate"));
        Assert.Equal(3, Policies.AppsForAccount("LABS").Count);
        Assert.Empty(Policies.AppsForAccount("other"));
    }

    [Fact]
    public void A_bad_apps_section_is_rejected_with_where()
    {
        var bad = JsonNode.Parse("""{"github_apps": {"x": {"accounts": []}}}""");

        var error = Assert.Throws<InvalidDataException>(() => RepoPolicies.Parse(bad, "policy.json"));

        Assert.Equal("policy.json: github_apps.x: accounts is not an object", error.Message);
    }

    [Fact]
    public async Task Audit_reports_missing_partial_and_all_repo_installations()
    {
        _discovered.AddRange(["labs/d", "elsewhere/e"]);
        var apps = Installed("selected", "a");

        var audits = await apps.AuditAsync("labs", Token);

        var renovate = audits.Single(a => a.AppSlug == "renovate");
        Assert.True(renovate.Installed);
        Assert.Equal(["b"], renovate.MissingRepos);
        Assert.False(renovate.IsAligned);
        Assert.Equal(["Repositories missing connection: b"], renovate.Details);

        var codecov = audits.Single(a => a.AppSlug == "codecov");
        Assert.False(codecov.Installed);
        Assert.Equal(["a", "b", "d"], codecov.ExpectedRepos);
        Assert.True(codecov.IsAligned);

        Assert.True(audits.Single(a => a.AppSlug == "optional").IsAligned);
    }

    [Fact]
    public async Task An_all_repositories_installation_is_aligned_without_reading_its_repos()
    {
        var audits = await Installed("all").AuditAsync("labs", Token);

        Assert.Equal(["all"], audits.Single(a => a.AppSlug == "renovate").ActualRepos);
        Assert.DoesNotContain(_api.Calls, c => c.Route == RenovateRepos);
    }

    [Fact]
    public async Task A_failed_installation_lookup_throws_rather_than_reading_as_not_installed()
    {
        _ = _api.On("GET", "users/labs", """{"type": "Organization"}""").Fail("GET", Installations, "gh: HTTP 403");

        var error = await Assert.ThrowsAsync<GitHubReadException>(() => Subject.AuditAsync("labs", Token));

        Assert.StartsWith($"GET {Installations}: ", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_personal_account_reads_the_users_installations_filtered_to_it()
    {
        _ = _api.On("GET", "users/labs", """{"type": "User"}""")
            .On("GET", "user/installations?per_page=100&page=1", """{"installations": [{"id": 1, "app_slug": "renovate", "account": {"login": "someone"}}]}""");

        var audits = await Subject.AuditAsync("labs", Token);

        Assert.False(audits.Single(a => a.AppSlug == "renovate").Installed);
    }

    [Fact]
    public async Task Sync_connects_missing_and_disconnects_disallowed_repos()
    {
        var apps = Installed("selected", "a", "c");
        _ = _api.On("GET", "repos/labs/b", """{"id": 20}""").On("GET", "repos/labs/c", """{"id": 30}""")
            .On("PUT", "user/installations/7/repositories/20", "{}").On("DELETE", "user/installations/7/repositories/30", "{}");

        var sync = await apps.SyncAsync("labs", "renovate", dryRun: false, Token);

        Assert.True(sync.Ok);
        Assert.Equal(
            ["b: Successfully connected repo 20 to installation 7", "c (disallowed): Successfully removed repo 30 from installation 7"],
            sync.Messages);
    }

    [Fact]
    public async Task A_dry_run_sync_says_what_it_would_do_and_writes_nothing()
    {
        var apps = Installed("selected", "a");
        _ = _api.On("GET", "repos/labs/b", """{"id": 20}""");

        var sync = await apps.SyncAsync("labs", "renovate", dryRun: true, Token);

        Assert.Equal(["b: dry-run: would add repo 20 to installation 7"], sync.Messages);
        Assert.All(_api.Calls, c => Assert.Equal("GET", c.Method));
    }

    [Fact]
    public async Task Sync_reports_failed_writes_and_unresolvable_repos()
    {
        var apps = Installed("selected", "c");
        _ = _api.On("GET", "repos/labs/a", """{"id": 10}""").Fail("PUT", "user/installations/7/repositories/10", "gh: HTTP 422\n");

        var sync = await apps.SyncAsync("labs", "renovate", dryRun: false, Token);

        Assert.False(sync.Ok);
        Assert.Equal(
            ["a: Failed to connect repo 10: gh: HTTP 422", "Could not resolve repository ID for labs/b", "Could not resolve repository ID for labs/c"],
            sync.Messages);
    }

    [Theory]
    [InlineData("nope", "labs", "App 'nope' is not declared in policy configuration.")]
    [InlineData("renovate", "other", "Account 'other' has no declared policy for app 'renovate'.")]
    public async Task Sync_refuses_an_undeclared_app_or_account(string slug, string account, string message)
    {
        var sync = await Subject.SyncAsync(account, slug, dryRun: false, Token);

        Assert.False(sync.Ok);
        Assert.Equal([message], sync.Messages);
        Assert.Empty(_api.Calls);
    }

    [Fact]
    public async Task Sync_stops_when_the_app_is_not_installed_or_the_lookup_fails()
    {
        _ = _api.On("GET", "users/labs", """{"type": "Organization"}""").On("GET", Installations, """{"installations": []}""");
        var missing = await Subject.SyncAsync("labs", "renovate", dryRun: false, Token);

        _ = _api.Fail("GET", Installations, "gh: HTTP 500");
        var failed = await Subject.SyncAsync("labs", "renovate", dryRun: false, Token);

        Assert.Equal(
            ["Cannot sync repositories: App 'Renovate' (renovate) is NOT installed on 'labs'.", "Please install it first: https://github.com/apps/renovate/installations/new"],
            missing.Messages);
        Assert.StartsWith("Cannot sync repositories: installation lookup failed: GET ", failed.Messages.Single(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sync_stops_when_the_installations_repositories_cannot_be_read()
    {
        _ = _api.On("GET", "users/labs", """{"type": "Organization"}""")
            .On("GET", Installations, """{"installations": [{"id": 7, "app_slug": "renovate", "repository_selection": "selected"}]}""")
            .Fail("GET", RenovateRepos, "gh: HTTP 502");

        var sync = await Subject.SyncAsync("labs", "renovate", dryRun: false, Token);

        Assert.False(sync.Ok);
        Assert.StartsWith($"Cannot sync repositories: repository lookup failed: GET {RenovateRepos}: ", sync.Messages.Single(), StringComparison.Ordinal);
        Assert.All(_api.Calls, call => Assert.Equal("GET", call.Method));
    }

    [Fact]
    public async Task Sync_is_a_no_op_when_aligned_or_installed_on_all()
    {
        var aligned = await Installed("selected", "a", "b").SyncAsync("labs", "renovate", dryRun: false, Token);
        var all = await Installed("all").SyncAsync("labs", "renovate", dryRun: false, Token);

        Assert.Equal(["All declared repositories are correctly connected to 'renovate'."], aligned.Messages);
        Assert.Equal(["App 'renovate' has access to all repositories on 'labs' (selection='all')."], all.Messages);
    }
}
