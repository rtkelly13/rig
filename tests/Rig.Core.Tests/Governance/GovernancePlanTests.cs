using System.Text.Json.Nodes;
using Rig.Core.Governance;
using Rig.Core.Policy;
using Rig.Core.Tests.Fakes;

namespace Rig.Core.Tests.Governance;

/// <summary><c>repo-governance plan</c> through its seams: the policy, a checkout on disk, and a fake GitHub.</summary>
public sealed class GovernancePlanTests : IDisposable
{
    private const string Slug = "acme/widget";

    private const string Workflow = """
        on: pull_request
        jobs:
          lint:
            runs-on: ubuntu-latest
            steps:
              - run: "true"
        """;

    private static readonly (string Name, string Color, string Description)[] Taxonomy =
    [
        ("type: bug", "d73a4a", "Something is broken"),
        ("good first issue", "7057ff", "Good for newcomers"),
        ("priority: high", "b60205", "Needs attention now"),
    ];

    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static JsonObject Settings(bool mergeCommit = false, bool deleteBranch = true, string branch = "main") => new()
    {
        ["default_branch"] = branch,
        ["delete_branch_on_merge"] = deleteBranch,
        ["allow_squash_merge"] = true,
        ["allow_merge_commit"] = mergeCommit,
        ["allow_rebase_merge"] = false,
        ["allow_auto_merge"] = true,
        ["private"] = false,
    };

    private static JsonArray Labels(IEnumerable<(string Name, string Color, string Description)> labels) =>
        [.. labels.Select(l => (JsonNode)new JsonObject { ["name"] = l.Name, ["color"] = l.Color, ["description"] = l.Description })];

    private static RepoPolicies Policy(string[] required, JsonObject? layer = null)
    {
        var defaults = new JsonObject
        {
            ["expected_owner"] = "acme",
            ["allowed_owners"] = new JsonArray("acme"),
            ["required_checks"] = new JsonArray([.. required.Select(r => (JsonNode)r)]),
            ["labels"] = Labels(Taxonomy),
        };
        foreach (var (key, value) in layer ?? [])
        {
            defaults[key] = value?.DeepClone();
        }

        return RepoPolicies.Parse(new JsonObject { ["defaults"] = defaults }, "policy.json");
    }

    // GitHub as the policy wants it: nothing to plan.
    private static FakeGitHub Aligned(RepoPolicies policies) => new FakeGitHub()
        .On("GET", $"repos/{Slug}", Settings().ToJsonString())
        .On("GET", $"repos/{Slug}/rulesets", """[{"id": 7, "name": "Main Branch Governance Ruleset", "enforcement": "active"}]""")
        .On("GET", $"repos/{Slug}/rulesets/7", PyJson.DumpsLine(RulesetResource.Payload(policies, Slug)))
        .On("GET", $"repos/{Slug}/labels?per_page=100", Labels(Taxonomy).ToJsonString());

    private string Checkout()
    {
        var file = _dir.File("widget/.github/workflows/ci.yml");
        File.WriteAllText(file, Workflow);
        return Path.Join(_dir.Path, "widget");
    }

    private static List<(string Method, string Route)> Writes(FakeGitHub api) => [.. api.Calls.Where(c => c.Method != "GET").Select(c => (c.Method, c.Route))];

    private Task<RepoPlan> PlanAsync(RepoPolicies policies, FakeGitHub api, bool apply = false, string slug = Slug) =>
        GovernancePlan.PlanRepoAsync(policies, api, new(Checkout(), slug), apply, cancellationToken: Token);

    [Fact]
    public async Task An_aligned_repo_plans_nothing_and_exits_0()
    {
        var policies = Policy(["lint"]);
        var api = Aligned(policies);

        var plan = await PlanAsync(policies, api);

        Assert.Equal("""{"repo":"acme/widget","changes":[],"errors":[]}""", PyRepr.Json(plan.ToJson()));
        Assert.Equal(0, GovernancePlan.ExitCode([plan], applied: false));
        Assert.Empty(Writes(api));
    }

    [Fact]
    public async Task Settings_drift_is_one_change_and_apply_sends_one_patch()
    {
        var policies = Policy(["lint"]);
        var api = Aligned(policies)
            .On("GET", $"repos/{Slug}", Settings(mergeCommit: true, deleteBranch: false).ToJsonString())
            .On("PATCH", $"repos/{Slug}", "{}");

        var plan = await PlanAsync(policies, api);

        Assert.Equal([("repo-settings", "update", "delete_branch_on_merge False -> True; allow_merge_commit True -> False")], plan.Changes.Select(c => (c.Resource, c.Action, c.Detail)));
        Assert.Equal(1, GovernancePlan.ExitCode([plan], applied: false));
        Assert.Empty(Writes(api));

        var applied = await PlanAsync(policies, api, apply: true);

        Assert.Equal(0, GovernancePlan.ExitCode([applied], applied: true));
        var write = Assert.Single(api.Calls, c => c.Method != "GET");
        Assert.Equal(("PATCH", $"repos/{Slug}", new RepoSettings("main", MergeSettings.Default).Body()), (write.Method, write.Route, write.Body));
    }

    [Fact]
    public async Task Repo_settings_come_from_the_policy()
    {
        var policies = Policy(["lint"], new() { ["repo_settings"] = new JsonObject { ["allow_rebase_merge"] = true, ["allow_auto_merge"] = false } });
        var api = Aligned(policies);

        var change = Assert.Single((await PlanAsync(policies, api, apply: true)).Changes);

        Assert.Equal("allow_rebase_merge False -> True; allow_auto_merge True -> False", change.Detail);
        Assert.Equal(
            """{"default_branch":"main","delete_branch_on_merge":true,"allow_squash_merge":true,"allow_merge_commit":false,"allow_rebase_merge":true,"allow_auto_merge":false}""",
            Assert.Single(api.Calls, c => c.Method == "PATCH").Body);
    }

    [Fact]
    public async Task A_private_repo_s_auto_merge_and_a_missing_branch_are_not_drift()
    {
        var live = Settings();
        live["allow_auto_merge"] = false;
        live["private"] = true;
        live.Remove("default_branch");
        var policies = Policy(["lint"]);

        var plan = await PlanAsync(policies, Aligned(policies).On("GET", $"repos/{Slug}", live.ToJsonString()));

        Assert.Empty(plan.Changes);
    }

    [Fact]
    public async Task A_policy_without_labels_leaves_labels_alone()
    {
        var policies = Policy(["lint"], new() { ["labels"] = new JsonArray() });
        var api = Aligned(policies);

        var plan = await PlanAsync(policies, api);

        Assert.Empty(plan.Changes);
        Assert.DoesNotContain(api.Calls, c => c.Route.Contains("labels", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_stale_required_check_is_refused_and_never_written()
    {
        var policies = Policy(["lint", "build"]);
        var api = Aligned(policies).On("GET", $"repos/{Slug}/rulesets", "[]");

        var plan = await PlanAsync(policies, api, apply: true);

        var change = Assert.Single(plan.Changes);
        Assert.Equal(("ruleset", Change.Refused), (change.Resource, change.Kind));
        Assert.Equal("required checks ['build']: no pull_request job declares them (declared: ['lint'])", change.Detail);
        Assert.Equal(1, GovernancePlan.ExitCode([plan], applied: true));
        Assert.Empty(Writes(api));
    }

    [Fact]
    public async Task Required_checks_without_any_pull_request_workflow_are_refused()
    {
        var policies = Policy(["lint"]);
        var api = Aligned(policies).On("GET", $"repos/{Slug}/rulesets", "[]");

        var plan = await GovernancePlan.PlanRepoAsync(policies, api, new(_dir.Folder("bare"), Slug), apply: false, cancellationToken: Token);

        Assert.Equal("required checks ['lint']: no pull_request workflow exists to report them", Assert.Single(plan.Changes).Detail);
    }

    [Fact]
    public async Task A_missing_ruleset_is_created_on_apply_and_a_drifted_one_replaced_in_place()
    {
        var policies = Policy(["lint"]);
        var api = Aligned(policies).On("GET", $"repos/{Slug}/rulesets", "[]");

        var created = await PlanAsync(policies, api, apply: true);

        Assert.Equal(("create", "Main Branch Governance Ruleset is missing"), (Assert.Single(created.Changes).Action, created.Changes[0].Detail));
        Assert.Equal([("POST", $"repos/{Slug}/rulesets")], Writes(api));

        var drifted = Policy(["lint"]);
        var live = RulesetResource.Payload(drifted, Slug);
        live["enforcement"] = "disabled";
        var replaced = Aligned(drifted).On("GET", $"repos/{Slug}/rulesets/7", live.ToJsonString());

        var plan = await PlanAsync(drifted, replaced, apply: true);

        Assert.Equal(("update", "7", "enforcement disabled"), (plan.Changes[0].Action, plan.Changes[0].Subject, plan.Changes[0].Detail));
        Assert.Equal([("PUT", $"repos/{Slug}/rulesets/7")], Writes(replaced));
    }

    [Fact]
    public async Task Unserved_rulesets_on_a_private_repo_are_a_note_not_drift()
    {
        var policies = Policy(["lint"]);
        var api = Aligned(policies).Fail("GET", $"repos/{Slug}/rulesets", "Upgrade to GitHub Pro or make this repository public");

        var plan = await PlanAsync(policies, api);

        Assert.Equal([("ruleset", Change.Note)], plan.Changes.Select(c => (c.Resource, c.Kind)));
        Assert.Equal(0, GovernancePlan.ExitCode([plan], applied: false));
    }

    [Fact]
    public async Task Unreadable_github_exits_2()
    {
        var policies = Policy(["lint"]);
        var api = Aligned(policies).Fail("GET", $"repos/{Slug}", "gh: Bad credentials (HTTP 401)");

        var plan = await PlanAsync(policies, api);

        Assert.Equal($"repo-settings: GET repos/{Slug}: gh: Bad credentials (HTTP 401)", Assert.Single(plan.Errors));
        Assert.Equal(2, GovernancePlan.ExitCode([plan], applied: false));
    }

    [Theory]
    [InlineData("""{"x": 1}""", "unreadable response")]
    [InlineData("not json", "unreadable response")]
    [InlineData("""[{"id": 7, "name": "Main Branch Governance Ruleset"}, 3]""", "unreadable response")]
    public async Task An_unreadable_ruleset_list_is_an_error(string listed, string reason)
    {
        var policies = Policy(["lint"]);

        var plan = await PlanAsync(policies, Aligned(policies).On("GET", $"repos/{Slug}/rulesets", listed));

        Assert.Equal($"ruleset: GET repos/{Slug}/rulesets: {reason}", Assert.Single(plan.Errors));
    }

    [Fact]
    public async Task The_wrong_owner_is_refused_before_github_is_read()
    {
        var policies = Policy(["lint"]);
        var api = Aligned(policies);

        var plan = await PlanAsync(policies, api, apply: true, slug: "stranger/widget");

        Assert.Equal([("owner", Change.Refused)], plan.Changes.Select(c => (c.Resource, c.Kind)));
        Assert.Equal("owner governance failure: Expected owner 'acme', but repo is owned by 'stranger'", plan.Changes[0].Detail);
        Assert.Empty(api.Calls);
        Assert.Equal(1, GovernancePlan.ExitCode([plan], applied: true));
    }

    [Fact]
    public async Task Label_and_pages_writes_go_through_the_port()
    {
        var policies = Policy(["lint"], new() { ["pages"] = new JsonObject { ["enabled"] = true, ["cname"] = "widget.example" } });
        var api = Aligned(policies)
            .On("GET", $"repos/{Slug}/labels?per_page=100", Labels(Taxonomy[1..]).ToJsonString())
            .On("POST", $"repos/{Slug}/labels", "{}")
            .On("POST", $"repos/{Slug}/pages", "{}")
            .On("PUT", $"repos/{Slug}/pages", "{}");

        var plan = await PlanAsync(policies, api, apply: true);

        Assert.Equal(0, GovernancePlan.ExitCode([plan], applied: true));
        Assert.Equal(
            [
                ("POST", $"repos/{Slug}/labels", """{"name":"type: bug","color":"d73a4a","description":"Something is broken"}"""),
                ("POST", $"repos/{Slug}/pages", """{"build_type":"workflow"}"""),
                ("PUT", $"repos/{Slug}/pages", """{"cname":"widget.example","https_enforced":true}"""),
            ],
            api.Calls.Where(c => c.Method != "GET").Select(c => (c.Method, c.Route, c.Body)));
    }

    [Fact]
    public async Task A_label_update_url_encodes_the_name()
    {
        var policies = Policy(["lint"]);
        var stale = Taxonomy.ToArray();
        stale[1] = ("good first issue", "000000", string.Empty);
        var api = Aligned(policies).On("GET", $"repos/{Slug}/labels?per_page=100", Labels(stale).ToJsonString());

        var plan = await PlanAsync(policies, api, apply: true);

        Assert.Equal(("update", "good first issue", "color mismatch (#000000 vs #7057ff), description mismatch"), (plan.Changes[0].Action, plan.Changes[0].Subject, plan.Changes[0].Detail));
        Assert.Equal([("PATCH", $"repos/{Slug}/labels/good%20first%20issue")], Writes(api));
    }

    [Fact]
    public async Task Environments_apply_what_is_declared_and_note_what_is_not()
    {
        var policies = Policy(["lint"], new() { ["environments"] = new JsonObject { ["release"] = new JsonObject { ["wait_timer"] = 5, ["reviewers"] = new JsonArray("octo") } } });
        var api = Aligned(policies)
            .On("GET", $"repos/{Slug}/environments", """{"environments": [{"name": "release"}, {"name": "github-pages"}]}""")
            .On("GET", $"repos/{Slug}/environments/release", """{"name": "release"}""")
            .On("GET", "users/octo", """{"id": 42}""")
            .On("PUT", $"repos/{Slug}/environments/release", "{}");

        var plan = await PlanAsync(policies, api);

        Assert.Equal(
            [("wait-timer", "release", Change.Apply), ("add-reviewers", "release", Change.Apply), ("undeclared", "github-pages", Change.Note)],
            plan.Changes.Select(c => (c.Action, c.Subject, c.Kind)));

        var applied = await PlanAsync(policies, api, apply: true);

        Assert.Equal(0, GovernancePlan.ExitCode([applied], applied: true));
        Assert.Equal(
            [("PUT", $"repos/{Slug}/environments/release", """{"wait_timer":5,"reviewers":[{"type":"User","id":42}]}""")],
            api.Calls.Where(c => c.Method != "GET").Select(c => (c.Method, c.Route, c.Body)));
    }

    [Fact]
    public async Task A_failed_write_exits_2_with_its_reason()
    {
        var policies = Policy(["lint"], new() { ["environments"] = new JsonObject { ["release"] = new JsonObject() } });
        var api = Aligned(policies)
            .On("GET", $"repos/{Slug}", Settings(mergeCommit: true).ToJsonString())
            .On("GET", $"repos/{Slug}/rulesets", "[]")
            .On("GET", $"repos/{Slug}/environments", """{"environments": []}""")
            .Fail("PATCH", $"repos/{Slug}", "gh: Forbidden (HTTP 403)\n")
            .Fail("POST", $"repos/{Slug}/rulesets", "gh: Validation Failed (HTTP 422)")
            .Fail("PUT", $"repos/{Slug}/environments/release", "gh: nope");

        var plan = await PlanAsync(policies, api, apply: true);

        Assert.Equal(
            [
                $"PATCH repos/{Slug}: gh: Forbidden (HTTP 403)",
                $"create Main Branch Governance Ruleset on {Slug}: gh: Validation Failed (HTTP 422)",
                "FAILED create release: gh: nope",
            ],
            plan.Errors);
        Assert.Equal(2, GovernancePlan.ExitCode([plan], applied: true));
    }

    [Fact]
    public async Task A_failed_pages_write_stops_at_the_first_failure()
    {
        var policies = Policy(["lint"], new() { ["pages"] = new JsonObject { ["enabled"] = true } });
        var api = Aligned(policies).Fail("POST", $"repos/{Slug}/pages", "gh: no pages\n");

        var plan = await PlanAsync(policies, api, apply: true);

        Assert.Equal([$"POST repos/{Slug}/pages {{\"build_type\":\"workflow\"}}: gh: no pages"], plan.Errors);
        Assert.DoesNotContain(api.Calls, c => c.Method == "PUT");
    }

    [Fact]
    public async Task Labels_are_read_past_the_first_page()
    {
        var first = Labels(Enumerable.Range(0, 100).Select(n => ($"extra {n}", "ffffff", string.Empty)));
        var api = new FakeGitHub()
            .On("GET", $"repos/{Slug}/labels?per_page=100", first.ToJsonString())
            .On("GET", $"repos/{Slug}/labels?per_page=100&page=2", Labels(Taxonomy[..1]).ToJsonString());

        var labels = await LabelTaxonomy.ReadAsync(api, Slug, Token);

        Assert.Equal(101, labels.Count);
        Assert.Equal(new RepoLabel("d73a4a", "Something is broken"), labels["type: bug"]);
    }

    [Theory]
    [InlineData("error", 2, 2)]
    [InlineData("refused", 1, 1)]
    [InlineData("note", 0, 0)]
    [InlineData("drift", 1, 0)]
    public void The_exit_code_ranks_errors_over_refusals_over_drift(string state, int planned, int applied)
    {
        RepoPlan plan = state switch
        {
            "error" => new("a", [], ["boom"]),
            "refused" => new("a", [new("ruleset", "refused", "x", Change.Refused)], []),
            "note" => new("a", [new("ruleset", "unserved", "x", Change.Note)], []),
            _ => new("a", [new("ruleset", "update", "x", Change.Apply)], []),
        };

        Assert.Equal((planned, applied), (GovernancePlan.ExitCode([plan], applied: false), GovernancePlan.ExitCode([plan], applied: true)));
    }
}
