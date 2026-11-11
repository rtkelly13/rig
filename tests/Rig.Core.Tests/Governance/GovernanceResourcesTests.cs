using System.Text.Json.Nodes;
using Rig.Core.Governance;
using Rig.Core.Tests.Fakes;

namespace Rig.Core.Tests.Governance;

/// <summary>The labels, environments and Pages resources, owner policy, check names and the policy keys they read.</summary>
public sealed class GovernanceResourcesTests : IDisposable
{
    private const string Repo = "acme/docs";

    private static readonly LabelSpec[] Taxonomy =
    [
        new("type: bug", "d73a4a", "Something is broken"),
        new("good first issue", "7057ff", "Good for newcomers"),
    ];

    private static readonly PagesPolicy Site = new(Enabled: true, Cname: "docs.example.dev");

    private static readonly EnvironmentPolicy Release = new("release", 0, ["octo"]);

    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string LabelsJson(params (string Name, string Color, string Description)[] labels) =>
        new JsonArray([.. labels.Select(l => (JsonNode)new JsonObject { ["name"] = l.Name, ["color"] = l.Color, ["description"] = l.Description })]).ToJsonString();

    private static Dictionary<string, RepoLabel> Live(params (string Name, string Color, string Description)[] labels) =>
        labels.ToDictionary(l => l.Name, l => new RepoLabel(l.Color, l.Description));

    private static List<(string Method, string Route, string? Body)> Writes(FakeGitHub api) =>
        [.. api.Calls.Where(c => c.Method != "GET").Select(c => (c.Method, c.Route, c.Body))];

    // ---- labels ----

    [Fact]
    public void Matching_labels_differ_in_nothing_and_colour_is_compared_case_insensitively()
    {
        var audit = LabelTaxonomy.Inspect(Taxonomy, Live(("type: bug", "D73A4A", "Something is broken"), ("good first issue", "7057ff", "Good for newcomers")));

        Assert.True(audit.IsFullyAligned);
        Assert.Equal(2, audit.TotalStandard);
    }

    [Fact]
    public void A_missing_and_a_stale_label_are_a_create_and_an_update()
    {
        var audit = LabelTaxonomy.Inspect(Taxonomy, Live(("good first issue", "000000", "Good for newcomers")));

        Assert.Equal(["type: bug"], audit.Missing);
        Assert.Equal(["good first issue"], audit.Mismatched);
        Assert.Equal("color mismatch (#000000 vs #7057ff)", audit.Differences[1].Details);
    }

    [Fact]
    public async Task A_dry_run_label_sync_says_what_it_would_do_and_writes_nothing()
    {
        var api = new FakeGitHub().On("GET", $"repos/{Repo}/labels?per_page=100", LabelsJson(("good first issue", "7057ff", "old")));

        var outcome = await LabelTaxonomy.SyncAsync(api, Repo, Taxonomy, dryRun: true, Token);

        Assert.True(outcome.Changed);
        Assert.Equal(["[Dry Run] Would create label 'type: bug' (#d73a4a)", "[Dry Run] Would update label 'good first issue' (description mismatch)"], outcome.Messages);
        Assert.Empty(Writes(api));
    }

    [Fact]
    public async Task A_label_sync_reports_each_write_and_each_failure()
    {
        var api = new FakeGitHub()
            .On("GET", $"repos/{Repo}/labels?per_page=100", LabelsJson(("good first issue", "7057ff", "old")))
            .Fail("POST", $"repos/{Repo}/labels", "gh: Validation Failed (HTTP 422)\n")
            .On("PATCH", $"repos/{Repo}/labels/good%20first%20issue", "{}");

        var outcome = await LabelTaxonomy.SyncAsync(api, Repo, Taxonomy, dryRun: false, Token);

        Assert.Equal(["Failed to create 'type: bug': gh: Validation Failed (HTTP 422)", "Updated label 'good first issue'"], outcome.Messages);
        Assert.Equal(("PATCH", $"repos/{Repo}/labels/good%20first%20issue", """{"color":"7057ff","description":"Good for newcomers"}"""), Writes(api)[1]);
    }

    [Fact]
    public async Task Aligned_labels_sync_to_nothing_and_unreadable_labels_write_nothing()
    {
        var aligned = new FakeGitHub().On("GET", $"repos/{Repo}/labels?per_page=100", LabelsJson(("type: bug", "d73a4a", "Something is broken")));
        var created = new FakeGitHub().On("GET", $"repos/{Repo}/labels?per_page=100", "[]").On("POST", $"repos/{Repo}/labels", "{}");
        var unreadable = new FakeGitHub().On("GET", $"repos/{Repo}/labels?per_page=100", """{"message": "nope"}""").On("POST", $"repos/{Repo}/labels", "{}");

        var nothing = await LabelTaxonomy.SyncAsync(aligned, Repo, Taxonomy[..1], dryRun: false, Token);
        var everything = await LabelTaxonomy.SyncAsync(created, Repo, Taxonomy[..1], dryRun: false, Token);

        Assert.Equal((false, "All standard labels already aligned."), (nothing.Changed, Assert.Single(nothing.Messages)));
        Assert.Equal("Created label 'type: bug' (#d73a4a)", Assert.Single(everything.Messages));
        await Assert.ThrowsAsync<GitHubReadException>(() => LabelTaxonomy.SyncAsync(unreadable, Repo, Taxonomy[..1], dryRun: false, Token));
        Assert.Empty(Writes(unreadable));
    }

    // ---- pages ----

    [Fact]
    public async Task Pages_enabled_as_declared_are_aligned()
    {
        var api = new FakeGitHub().On("GET", $"repos/{Repo}/pages", """{"status": "built", "cname": "docs.example.dev", "build_type": "workflow", "https_enforced": true}""");

        var inspection = await PagesGovernance.InspectAsync(api, Repo, Site, Token);

        Assert.Equal(new PagesInspection(true, "docs.example.dev", true, []), inspection with { DriftReasons = [] });
        Assert.Empty(inspection.DriftReasons);
        Assert.Equal("aligned", (await PagesGovernance.AlignAsync(api, Repo, Site, dryRun: false, Token)).Status);
    }

    [Fact]
    public void Pages_drift_names_every_difference()
    {
        Assert.Equal(
            ["build_type is 'legacy', expected 'workflow'", "cname is 'None', expected 'docs.example.dev'", "https_enforced is False, expected True"],
            PagesGovernance.Drift(Site, new LivePages(null, "legacy", false)));
        Assert.Equal(["Pages is disabled"], PagesGovernance.Drift(Site, null));
        Assert.Equal(["Pages is enabled, expected disabled"], PagesGovernance.Drift(new PagesPolicy(), new LivePages()));
        Assert.Empty(PagesGovernance.Drift(null, new LivePages()));
        Assert.Empty(PagesGovernance.Drift(new PagesPolicy(), null));
    }

    [Fact]
    public async Task Pages_off_are_a_404_and_an_unreadable_site_is_an_error()
    {
        var off = await PagesGovernance.InspectAsync(new FakeGitHub(), Repo, Site, Token);
        var broken = await PagesGovernance.InspectAsync(new FakeGitHub().Fail("GET", $"repos/{Repo}/pages", 500), Repo, Site, Token);
        var garbled = await PagesGovernance.InspectAsync(new FakeGitHub().On("GET", $"repos/{Repo}/pages", "[]"), Repo, Site, Token);

        Assert.Equal((false, false, "Pages is disabled"), (off.Enabled, off.IsAligned, Assert.Single(off.DriftReasons)));
        Assert.Equal($"GET repos/{Repo}/pages: gh: HTTP 500", broken.Error);
        Assert.Equal($"GET repos/{Repo}/pages: unreadable response", garbled.Error);
    }

    [Fact]
    public async Task An_unreadable_site_is_never_aligned()
    {
        var api = new FakeGitHub().Fail("GET", $"repos/{Repo}/pages", 500);

        var result = await PagesGovernance.AlignAsync(api, Repo, Site, dryRun: false, Token);

        Assert.Equal(new PagesAlignResult("error", [], $"GET repos/{Repo}/pages: gh: HTTP 500"), result with { Actions = [] });
        Assert.Empty(result.Actions);
        Assert.Empty(Writes(api));
    }

    [Fact]
    public async Task An_existing_site_takes_its_build_type_on_the_update()
    {
        var api = new FakeGitHub().On("GET", $"repos/{Repo}/pages", """{"build_type": "legacy", "https_enforced": false}""");

        var result = await PagesGovernance.AlignAsync(api, Repo, Site with { Cname = null, HttpsEnforced = false }, dryRun: true, Token);

        Assert.Equal([$"PUT repos/{Repo}/pages {{\"build_type\":\"workflow\"}}"], result.Actions);
    }

    [Fact]
    public async Task A_dry_run_pages_align_enables_then_configures_and_writes_nothing()
    {
        var api = new FakeGitHub();

        var result = await PagesGovernance.AlignAsync(api, Repo, Site, dryRun: true, Token);

        Assert.Equal(
            new[] { $"POST repos/{Repo}/pages {{\"build_type\":\"workflow\"}}", $"PUT repos/{Repo}/pages {{\"cname\":\"docs.example.dev\",\"https_enforced\":true}}" },
            result.Actions);
        Assert.Equal("dry_run", result.Status);
        Assert.Empty(Writes(api));
    }

    [Fact]
    public async Task Without_a_cname_pages_align_leaves_the_custom_domain_alone()
    {
        var result = await PagesGovernance.AlignAsync(new FakeGitHub(), Repo, Site with { Cname = null }, dryRun: true, Token);

        Assert.Equal($"PUT repos/{Repo}/pages {{\"https_enforced\":true}}", result.Actions[1]);
    }

    [Fact]
    public async Task Pages_align_writes_in_order_and_stops_at_a_failure()
    {
        var api = new FakeGitHub().On("POST", $"repos/{Repo}/pages", "{}").On("PUT", $"repos/{Repo}/pages", "{}");
        var failing = new FakeGitHub().Fail("POST", $"repos/{Repo}/pages", "gh: Forbidden\n");

        var result = await PagesGovernance.AlignAsync(api, Repo, Site, dryRun: false, Token);
        var failed = await PagesGovernance.AlignAsync(failing, Repo, Site, dryRun: false, Token);

        Assert.Equal("success", result.Status);
        Assert.Equal([("POST", $"repos/{Repo}/pages"), ("PUT", $"repos/{Repo}/pages")], Writes(api).Select(w => (w.Method, w.Route)));
        Assert.Equal(new PagesAlignResult("error", [], "gh: Forbidden"), failed with { Actions = [] });
        Assert.Single(Writes(failing));
    }

    [Fact]
    public async Task A_disabled_policy_never_enables_pages()
    {
        var api = new FakeGitHub().On("GET", $"repos/{Repo}/pages", """{"build_type": "workflow"}""");

        var result = await PagesGovernance.AlignAsync(api, Repo, new PagesPolicy(), dryRun: false, Token);

        Assert.Equal(("success", 0), (result.Status, result.Actions.Count));
        Assert.Empty(Writes(api));
    }

    // ---- environments ----

    [Fact]
    public void A_missing_environment_is_created_and_an_existing_one_left_alone()
    {
        Assert.Equal([new EnvironmentAction("create", "release", "wait_timer=0")], ReleaseEnvironments.PlanActions([Release], new Dictionary<string, CurrentEnvironment>()));
        Assert.Empty(ReleaseEnvironments.PlanActions([Release], Current(new CurrentEnvironment("release", 0, ["octo"]))));
    }

    [Fact]
    public void Wait_timer_drift_and_a_missing_reviewer_are_corrected()
    {
        var actions = ReleaseEnvironments.PlanActions([Release with { WaitTimer = 30, Reviewers = ["octo", "cat"] }], Current(new CurrentEnvironment("release", 0, ["octo"])));

        Assert.Equal([new("wait-timer", "release", "0 -> 30"), new("add-reviewers", "release", "cat")], actions);
        Assert.All(actions, a => Assert.True(a.Mutates));
    }

    [Fact]
    public void Undeclared_environments_are_reported_not_written_and_unknown_reviewers_count_as_none()
    {
        var actions = ReleaseEnvironments.PlanActions([Release], Current(new CurrentEnvironment("release", 0, null), new CurrentEnvironment("staging")));

        Assert.Equal([new("add-reviewers", "release", "octo"), new("undeclared", "staging", "exists on GitHub but no profile declares it")], actions);
        Assert.Equal([true, false], actions.Select(a => a.Mutates));
    }

    [Fact]
    public async Task Environments_are_read_with_their_protection_rules()
    {
        var api = new FakeGitHub().On(
            "GET",
            $"repos/{Repo}/environments",
            """
            {"environments": [
              {"name": "release", "protection_rules": [
                {"type": "wait_timer", "wait_timer": 5},
                {"type": "required_reviewers", "reviewers": [
                  {"type": "User", "reviewer": {"login": "octo", "id": 1}},
                  {"type": "Team", "reviewer": {"slug": "core", "id": 2}},
                  {"type": "User", "reviewer": {"id": 3}}]}]},
              {"name": "pages", "wait_timer": 9},
              {"name": ""}]}
            """);

        var found = await ReleaseEnvironments.ReadAsync(api, Repo, Token);

        Assert.Equal(["pages", "release"], found.Keys.Order(StringComparer.Ordinal));
        Assert.Equal((5L, 0L), (found["release"].WaitTimer, found["pages"].WaitTimer));
        Assert.Equal(["octo"], found["release"].Reviewers!);
        Assert.Empty(found["pages"].Reviewers!);
        Assert.Single(api.Calls);
    }

    [Fact]
    public async Task An_unreadable_environment_list_throws_from_read_and_sync()
    {
        var api = new FakeGitHub().On("GET", $"repos/{Repo}/environments", """{"environments": [{"id": 1}]}""");

        await Assert.ThrowsAsync<GitHubReadException>(() => ReleaseEnvironments.ReadAsync(api, Repo, Token));
        await Assert.ThrowsAsync<GitHubReadException>(() => ReleaseEnvironments.SyncAsync(api, Repo, [Release], dryRun: true, Token));
    }

    [Fact]
    public async Task An_environment_sync_logs_dry_runs_writes_and_failures()
    {
        var api = new FakeGitHub()
            .On("GET", $"repos/{Repo}/environments", """{"environments": [{"name": "release"}, {"name": "old"}]}""")
            .On("GET", $"repos/{Repo}/environments/release", """{"name": "release"}""")
            .On("PUT", $"repos/{Repo}/environments/build", "{}");
        EnvironmentPolicy[] desired = [Release, new("build", 0, [])];

        var dry = await ReleaseEnvironments.SyncAsync(api, Repo, desired, dryRun: true, Token);
        var wet = await ReleaseEnvironments.SyncAsync(api, Repo, desired, dryRun: false, Token);

        Assert.Equal(
            ["dry-run create: build — wait_timer=0", "dry-run add-reviewers: release — octo", "undeclared: old — exists on GitHub but no profile declares it"],
            dry.Log);
        Assert.Equal(["undeclared: old — exists on GitHub but no profile declares it", "created build", "FAILED add-reviewers release: cannot resolve user octo"], wet.Log);
        Assert.Equal([("PUT", $"repos/{Repo}/environments/build", """{"wait_timer":0}""")], Writes(api));
    }

    [Fact]
    public async Task One_put_per_environment_keeps_what_is_there_and_adds_what_is_declared()
    {
        var api = new FakeGitHub()
            .On("GET", $"repos/{Repo}/environments", """{"environments": [{"name": "release", "protection_rules": [{"type": "wait_timer", "wait_timer": 1}]}]}""")
            .On(
                "GET",
                $"repos/{Repo}/environments/release",
                """
                {"name": "release", "protection_rules": [
                  {"type": "wait_timer", "wait_timer": 1},
                  {"type": "required_reviewers", "prevent_self_review": true, "reviewers": [{"type": "Team", "reviewer": {"slug": "core", "id": 7}}]}]}
                """)
            .On("GET", "users/octo", """{"id": 42}""")
            .On("PUT", $"repos/{Repo}/environments/release", "{}");

        var synced = await ReleaseEnvironments.SyncAsync(api, Repo, [Release with { WaitTimer = 9 }], dryRun: false, Token);

        Assert.Equal(["updated release: wait-timer, add-reviewers"], synced.Log);
        Assert.Equal(
            [("PUT", $"repos/{Repo}/environments/release", """{"wait_timer":9,"reviewers":[{"type":"Team","id":7},{"type":"User","id":42}],"prevent_self_review":true}""")],
            Writes(api));
    }

    [Fact]
    public async Task An_undeclared_wait_timer_is_carried_over()
    {
        var api = new FakeGitHub()
            .On("GET", $"repos/{Repo}/environments", """{"environments": [{"name": "release", "protection_rules": [{"type": "wait_timer", "wait_timer": 4}]}]}""")
            .On("GET", $"repos/{Repo}/environments/release", """{"name": "release", "protection_rules": [{"type": "wait_timer", "wait_timer": 4}]}""")
            .On("GET", "users/octo", """{"id": 42}""")
            .On("PUT", $"repos/{Repo}/environments/release", "{}");

        _ = await ReleaseEnvironments.SyncAsync(api, Repo, [Release], dryRun: false, Token);

        Assert.Equal([("PUT", $"repos/{Repo}/environments/release", """{"wait_timer":4,"reviewers":[{"type":"User","id":42}]}""")], Writes(api));
    }

    [Fact]
    public async Task Environment_write_failures_name_the_actions()
    {
        var listed = """{"environments": [{"name": "release", "protection_rules": [{"type": "wait_timer", "wait_timer": 1}]}]}""";
        var denied = new FakeGitHub()
            .On("GET", $"repos/{Repo}/environments", listed)
            .On("GET", $"repos/{Repo}/environments/release", """{"name": "release"}""")
            .On("GET", "users/octo", """{"id": 42}""")
            .Fail("PUT", $"repos/{Repo}/environments/release", "nope");
        var unreadable = new FakeGitHub()
            .On("GET", $"repos/{Repo}/environments", listed)
            .Fail("GET", $"repos/{Repo}/environments/release", "gone");

        var failed = await ReleaseEnvironments.SyncAsync(denied, Repo, [Release with { WaitTimer = 9 }], dryRun: false, Token);
        var unread = await ReleaseEnvironments.SyncAsync(unreadable, Repo, [Release with { WaitTimer = 9, Reviewers = [] }], dryRun: false, Token);

        Assert.Equal(["FAILED wait-timer, add-reviewers release: nope"], failed.Log);
        Assert.Single(unread.Log);
        Assert.StartsWith("FAILED wait-timer release: GET repos/", unread.Log[0], StringComparison.Ordinal);
        Assert.Empty(Writes(unreadable));
    }

    private static Dictionary<string, CurrentEnvironment> Current(params CurrentEnvironment[] environments) =>
        environments.ToDictionary(e => e.Name, StringComparer.Ordinal);

    // ---- owners ----

    [Theory]
    [InlineData("acme/widget", "ALIGNED", "Owner 'acme' is authorized")]
    [InlineData("other/widget", "WRONG_OWNER", "Expected owner 'acme', but repo is owned by 'other'")]
    [InlineData(null, "UNKNOWN_OWNER", "Could not determine repository owner")]
    [InlineData("/widget", "UNKNOWN_OWNER", "Could not determine owner from repository slug '/widget'")]
    [InlineData("bystander/loose", "UNAUTHORIZED_OWNER", "Owner 'bystander' is not in allowed owners (acme, labs)")]
    [InlineData("labs/fresh", "ALIGNED", "Owner 'labs' is authorized; not adopted: add it to profile labs_profile")]
    public void The_owner_must_be_expected_or_allowed(string? slug, string status, string message)
    {
        var inspection = RepoOwners.Inspect(Owners, slug);

        Assert.Equal((status, status == "ALIGNED", message), (inspection.Status, inspection.IsAligned, inspection.Message));
    }

    private static RepoPolicies Owners => RepoPolicies.Parse(
        JsonNode.Parse("""
            {
              "defaults": {"allowed_owners": ["acme", "labs"]},
              "profiles": {
                "acme_profile": {"expected_owner": "acme", "repos": ["widget"]},
                "labs_profile": {"expected_owner": "labs", "repos": ["lake"]}
              },
              "owner_profiles": {"labs": "labs_profile"}
            }
            """),
        "policy.json");

    [Fact]
    public void Repos_belong_to_an_org_by_slug_owner_or_registration()
    {
        Assert.Equal(["lake"], Owners.ReposForOrg("LABS"));
        Assert.True(Owners.IsRepoInOrg("labs/anything", "labs"));
        Assert.True(Owners.IsRepoInOrg("widget", "acme"));
        Assert.True(Owners.IsRepoInOrg("lake", "labs"));
        Assert.False(Owners.IsRepoInOrg("stray", "labs"));
        Assert.Null(Owners.OwnerProfileFor("labs/lake"));
        Assert.Null(Owners.OwnerProfileFor("fresh"));
    }

    // ---- policy keys ----

    [Fact]
    public void The_github_policy_keys_resolve_through_layers()
    {
        var policies = RepoPolicies.Parse(
            JsonNode.Parse("""
                {
                  "defaults": {
                    "required_checks": ["lint"],
                    "labels": [{"name": "bug", "color": "fff", "description": "b"}],
                    "repo_settings": {"allow_rebase_merge": true},
                    "expected_base_branch": "trunk"
                  },
                  "profiles": {
                    "site": {"repos": ["docs"], "pages": {"enabled": true, "cname": "d.dev"},
                             "environments": {"release": {"wait_timer": null, "reviewers": ["octo"]}},
                             "fork": {"upstream": "up/docs"}}
                  }
                }
                """),
            "policy.json");

        Assert.Equal(["lint"], policies.RequiredChecks("docs"));
        Assert.Equal([new LabelSpec("bug", "fff", "b")], policies.Labels("docs"));
        Assert.Equal(MergeSettings.Default with { AllowRebaseMerge = true }, policies.RepoSettings("docs"));
        Assert.Equal(("trunk", "main"), (policies.ExpectedBaseBranch("docs"), RepoPolicies.Empty.ExpectedBaseBranch("docs")));
        Assert.Equal(new PagesPolicy(true, "workflow", "d.dev", true), policies.Pages("docs"));
        Assert.Equal(PagesPolicy.Default, policies.Pages("other"));
        Assert.Equal(("release", 0, "octo"), (policies.Environments("docs")[0].Name, policies.Environments("docs")[0].WaitTimer, policies.Environments("docs")[0].Reviewers[0]));
        Assert.Empty(policies.Environments("other"));
        Assert.Equal(new ForkSpec("up/docs", "main"), policies.Fork("docs"));
    }

    [Theory]
    [InlineData("""{"labels": {}}""", "labels is not a list")]
    [InlineData("""{"labels": [1]}""", "a label is not an object")]
    [InlineData("""{"labels": [{"color": "f", "description": ""}]}""", "a label has no name")]
    [InlineData("""{"labels": [{"name": "x", "description": ""}]}""", "a label has no color")]
    [InlineData("""{"labels": [{"name": "x", "color": "f"}]}""", "a label has no description")]
    [InlineData("""{"environments": {"release": 1}}""", "environments.release is not an object")]
    [InlineData("""{"environments": {"release": {"reviewers": null}}}""", "environments.release.reviewers is null")]
    [InlineData("""{"pages": {"build_type": "jekyll"}}""", "pages.build_type 'jekyll' is neither 'workflow' nor 'legacy'")]
    [InlineData("""{"fork": {}}""", "fork has no upstream")]
    public void A_malformed_github_key_is_refused(string defaults, string reason)
    {
        var error = Assert.ThrowsAny<Exception>(() => RepoPolicies.Parse(JsonNode.Parse($$"""{"defaults": {{defaults}}}"""), "policy.json"));

        Assert.Contains(reason, error.Message, StringComparison.Ordinal);
    }

    // ---- check names ----

    [Fact]
    public void Check_names_come_from_pull_request_jobs_and_status_contexts()
    {
        Workflow("ci.yml", "on: [pull_request, push]\njobs:\n  lint:\n    name: Lint code\n  test: {}\n");
        Workflow("status.yaml", "on:\n  pull_request:\nenv:\n  STATUS_CONTEXT: scope\njobs:\n  post: {}\n");
        Workflow("release.yml", "on: push\njobs:\n  ship: {}\n");
        Workflow("broken.yml", "on: [pull_request\n");
        Workflow("notes.txt", "on: pull_request\njobs:\n  x: {}\n");

        var names = CheckNames.DiscoverPrCheckNames(Path.Join(_dir.Path, "repo"));

        Assert.Equal("ci.yml :: lint", names["Lint code"]);
        Assert.Equal("ci.yml :: test", names["test"]);
        Assert.Equal("status.yaml :: status", names["scope"]);
        Assert.Equal("status.yaml :: post", names["post"]);
        Assert.False(names.ContainsKey("ship") || names.ContainsKey("x"));
        Assert.Empty(CheckNames.DiscoverPrCheckNames(_dir.Folder("empty")));
    }

    [Fact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void An_unreadable_workflows_folder_names_no_checks()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "POSIX file modes");
        Workflow("ci.yml", "on: pull_request\njobs:\n  lint: {}\n");
        var workflows = Path.Join(_dir.Path, "repo", ".github", "workflows");
        File.SetUnixFileMode(workflows, UnixFileMode.None);
        try
        {
            Assert.Empty(CheckNames.DiscoverPrCheckNames(Path.Join(_dir.Path, "repo")));
        }
        finally
        {
            File.SetUnixFileMode(workflows, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private void Workflow(string name, string text) => File.WriteAllText(_dir.File($"repo/.github/workflows/{name}"), text);
}
