using System.Text.Json.Nodes;
using Rig.Core.Governance;
using Rig.Core.Policy;
using Rig.Core.Processes;
using Rig.Core.Roots;
using Rig.Core.Tests.Fakes;

namespace Rig.Core.Tests.Governance;

/// <summary>The per-repo conventions behind <c>status</c>, <c>audit</c> and <c>enforce</c>, over a temp checkout and a fake GitHub.</summary>
public sealed class ConventionsTests : IDisposable
{
    private const string Slug = "acme/widget";

    private const string Origin = $"git@github.com:{Slug}.git";

    private static readonly RepoPolicies Policies = RepoPolicies.Parse(
        new JsonObject { ["defaults"] = new JsonObject { ["expected_owner"] = "acme", ["allowed_owners"] = new JsonArray("acme") } }, "policy.json");

    private static readonly HygieneResult Ok = new("CONFIGURED", true);

    private static readonly GitignoreInspection Ignored = new("CONFIGURED", true, [], [], [], ".gitignore");

    private static readonly CheckNamesResult Named = new("ok", ["ci"], new Dictionary<string, string> { ["ci"] = "ci.yml :: ci" }, [], [], true, true);

    private readonly TempDir _root = new();
    private readonly FakeProc _proc = new();

    public ConventionsTests() =>
        _ = _proc.Answer(new Ran(0, $"{Origin}\n", string.Empty), "git", "remote", "get-url", "origin")
            .Answer(new Ran(0, $"{Slug}\n", string.Empty), "gh", "repo", "view");

    public void Dispose() => _root.Dispose();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static JsonObject Settings(bool deleteBranch = true, string branch = "main", bool mergeCommit = false) => new()
    {
        ["default_branch"] = branch,
        ["delete_branch_on_merge"] = deleteBranch,
        ["allow_squash_merge"] = true,
        ["allow_merge_commit"] = mergeCommit,
        ["allow_rebase_merge"] = false,
        ["allow_auto_merge"] = true,
        ["private"] = false,
    };

    private static string Declared => PyJson.DumpsLine(RulesetResource.Payload(Policies, Slug));

    private static FakeGitHub WithRuleset(string detail) => new FakeGitHub()
        .On("GET", $"repos/{Slug}", Settings().ToJsonString())
        .On("GET", $"repos/{Slug}/rulesets", """[{"id": 7, "name": "Main Branch Governance Ruleset", "enforcement": "active"}]""")
        .On("GET", $"repos/{Slug}/rulesets/7", detail);

    private static string Drifted()
    {
        var live = JsonNode.Parse(Declared)!.AsObject();
        live["rules"] = new JsonArray();
        return live.ToJsonString();
    }

    private static ConventionsReport Report(
        OwnerInspection? owner = null,
        SettingsInspection? settings = null,
        RulesetInspection? ruleset = null,
        HygieneResult? npmrc = null,
        CheckNamesResult? names = null,
        HygieneResult? dependabot = null) =>
        new(
            "widget",
            Slug,
            owner ?? RepoOwners.Inspect(Policies, Slug),
            settings ?? new SettingsInspection(true, true, true),
            ruleset ?? new RulesetInspection("ACTIVE", true, 7, []),
            npmrc ?? Ok,
            Ok,
            Ignored,
            Ok,
            Ok,
            Ok,
            names ?? Named,
            dependabot ?? new HygieneResult("OK", true));

    private Conventions Subject(IGitHubApi api)
    {
        var git = new Git(_proc, _root.Path);
        var scopes = new EstateScopes([], []);
        return new Conventions(_proc, api, new GovernedRepos(_proc, git, scopes, Policies), new Gitignores(git, scopes, Policies), Policies);
    }

    private string Checkout(bool git = true)
    {
        var repo = _root.Folder("widget");
        if (git)
        {
            _ = Directory.CreateDirectory(Path.Join(repo, ".git"));
        }

        return repo;
    }

    [Fact]
    public async Task Existence_needs_a_checkout_a_remote_and_a_live_repo()
    {
        var subject = Subject(new FakeGitHub());
        var missing = await subject.ExistsAsync(Checkout(git: false), Token);
        var repo = Checkout();
        var exists = await subject.ExistsAsync(repo, Token);
        _ = _proc.Answer(new Ran(1, string.Empty, "Not Found"), "gh", "repo", "view", Slug, "--json", "nameWithOwner", "-q");
        var gone = await subject.ExistsAsync(repo, Token);
        _ = _proc.Answer(new Ran(1, string.Empty, "no remote"), "gh", "repo", "view");
        var unlinked = await subject.ExistsAsync(repo, Token);

        Assert.Equal(new RepoExistence("MISSING_LOCAL", null), missing);
        Assert.Equal((new RepoExistence("EXISTS", Slug), true), (exists, exists.Exists));
        Assert.Equal(new RepoExistence("NOT_FOUND_ON_GITHUB", Slug), gone);
        Assert.Equal(new RepoExistence("NOT_LINKED_TO_GITHUB", null), unlinked);
    }

    [Fact]
    public async Task Settings_read_merge_methods_and_the_default_branch()
    {
        var aligned = await Subject(WithRuleset(Declared)).InspectSettingsAsync(Slug, Token);
        var drifted = await Subject(new FakeGitHub().On("GET", $"repos/{Slug}", Settings(false, "master", true).ToJsonString())).InspectSettingsAsync(Slug, Token);
        var unreadable = await Subject(new FakeGitHub().Fail("GET", $"repos/{Slug}", 500)).InspectSettingsAsync(Slug, Token);

        Assert.Equal(new SettingsInspection(true, true, true), aligned);
        Assert.Equal((false, false, false, "master", false), (drifted.IsAligned, drifted.DeleteOnMerge, drifted.SquashOnly, drifted.DefaultBranch, drifted.BaseBranchAligned));
        Assert.Equal((false, "main"), (unreadable.IsAligned, unreadable.DefaultBranch));
        Assert.NotNull(unreadable.Error);
    }

    [Fact]
    public async Task The_ruleset_reads_as_active_drifted_missing_unserved_or_error()
    {
        var active = await Subject(WithRuleset(Declared)).InspectRulesetAsync(Slug, Token);
        var drifted = await Subject(WithRuleset(Drifted())).InspectRulesetAsync(Slug, Token);
        var missing = await Subject(new FakeGitHub().On("GET", $"repos/{Slug}/rulesets", "[]")).InspectRulesetAsync(Slug, Token);
        var unserved = await Subject(new FakeGitHub().Fail("GET", $"repos/{Slug}/rulesets", "Upgrade to GitHub Pro or make this repository public")).InspectRulesetAsync(Slug, Token);
        var error = await Subject(new FakeGitHub()).InspectRulesetAsync(Slug, Token);

        Assert.Equal(("ACTIVE", true, (long?)7), (active.Status, active.IsAligned, active.Id));
        Assert.Equal((false, 7L), (drifted.IsAligned, drifted.Id));
        Assert.StartsWith("DRIFTED: missing rule ", drifted.Status, StringComparison.Ordinal);
        Assert.NotEmpty(drifted.Drift);
        Assert.Equal(new RulesetInspection("MISSING", false, null, []), missing with { Drift = [] });
        Assert.Equal(("N/A (Private Repo)", true), (unserved.Status, unserved.IsAligned));
        Assert.Equal(("error", false), (error.Status, error.IsAligned));
    }

    [Fact]
    public async Task Settings_are_patched_unless_dry_run()
    {
        var api = new FakeGitHub().On("PATCH", $"repos/{Slug}", "{}");

        var dry = await Subject(api).AlignSettingsAsync(Slug, dryRun: true, Token);
        var done = await Subject(api).AlignSettingsAsync(Slug, dryRun: false, Token);
        var failed = await Subject(new FakeGitHub()).AlignSettingsAsync(Slug, dryRun: false, Token);

        Assert.Equal(("dry_run", "success", "error"), (dry.Status, done.Status, failed.Status));
        Assert.Single(api.Calls);
    }

    [Fact]
    public async Task A_drifted_ruleset_is_replaced_in_place_and_a_missing_one_created()
    {
        var replaced = WithRuleset(Drifted()).On("PUT", $"repos/{Slug}/rulesets/7", "{}");
        var created = new FakeGitHub().On("GET", $"repos/{Slug}/rulesets", "[]").On("POST", $"repos/{Slug}/rulesets", "{}");
        var refused = new FakeGitHub().On("GET", $"repos/{Slug}/rulesets", "[]")
            .Fail("POST", $"repos/{Slug}/rulesets", "Upgrade to GitHub Pro or make this repository public");
        var failed = new FakeGitHub().On("GET", $"repos/{Slug}/rulesets", "[]");

        Assert.Equal("success", (await Subject(replaced).AlignRulesetAsync(Slug, false, Token)).Status);
        Assert.Equal("success", (await Subject(created).AlignRulesetAsync(Slug, false, Token)).Status);
        Assert.Equal("skipped (private repo restriction)", (await Subject(refused).AlignRulesetAsync(Slug, false, Token)).Status);
        Assert.Equal("error", (await Subject(failed).AlignRulesetAsync(Slug, false, Token)).Status);
        Assert.Contains(replaced.Calls, c => c.Method == "PUT");
        Assert.Contains(created.Calls, c => c.Method == "POST");
    }

    [Fact]
    public async Task Ruleset_alignment_skips_what_needs_no_write()
    {
        var unserved = new FakeGitHub().Fail("GET", $"repos/{Slug}/rulesets", "Upgrade to GitHub Pro or make this repository public");
        var dry = await Subject(WithRuleset(Drifted())).AlignRulesetAsync(Slug, true, Token);

        Assert.Equal("already_active", (await Subject(WithRuleset(Declared)).AlignRulesetAsync(Slug, false, Token)).Status);
        Assert.Equal("skipped (private repo restriction)", (await Subject(unserved).AlignRulesetAsync(Slug, false, Token)).Status);
        Assert.Equal("error", (await Subject(new FakeGitHub()).AlignRulesetAsync(Slug, false, Token)).Status);
        Assert.Equal("dry_run", dry.Status);
        Assert.NotEmpty(dry.Drift);
    }

    [Fact]
    public async Task Inspection_gathers_every_convention()
    {
        var report = await Subject(WithRuleset(Declared)).InspectAsync(Checkout(), Slug, Token);

        Assert.Equal(("widget", Slug, true, true), (report.Name, report.Slug, report.Owner.IsAligned, report.Ruleset.IsAligned));
        Assert.Equal("widget", report.StatusRow()[0]);
    }

    [Fact]
    public void A_compliant_repo_audits_as_one_line_and_a_status_row_of_ticks()
    {
        var report = Report();

        Assert.Equal(["✓ widget (acme/widget): FULLY COMPLIANT"], report.AuditLines());
        Assert.Equal(
            ["widget", Slug, "✓ acme", "✓ main", "SQUASH+DELETE+AUTOMERGE", "ACTIVE (+THREADS RESOLVED)", "CONFIGURED", "CONFIGURED", "CONFIGURED", "CONFIGURED", "✓ ALIGNED"],
            report.StatusRow());
    }

    [Fact]
    public void Drift_lists_every_failing_convention_in_order()
    {
        var report = Report(
            owner: new OwnerInspection("WRONG_OWNER", false, "Expected owner 'acme', but repo is owned by 'them'", "them"),
            settings: new SettingsInspection(false, DefaultBranch: "master"),
            ruleset: new RulesetInspection("MISSING", false, null, []),
            npmrc: new HygieneResult("MISSING / DRIFTED"),
            names: Named with { Missing = ["build"], IsAligned = false },
            dependabot: new HygieneResult("CONFLICT", Reason: "both configured"));

        Assert.Equal(
            [
                "⚠ widget (acme/widget): DRIFT DETECTED",
                "   - Repository owner/org governance failure: Expected owner 'acme', but repo is owned by 'them'",
                "   - Base branch deviation: default branch is 'master', expected 'main'",
                "   - Repo settings (squash merge / delete branch) not enforced",
                "   - GitHub Ruleset: MISSING",
                "   - .npmrc status: MISSING / DRIFTED",
                "   - Required check(s) no pull_request job declares: ['build']",
                "     declared: ['ci'] — run 'repo-governance check-names' for detail",
                "   - Dependabot / Renovate conflict: both configured",
            ],
            report.AuditLines());
        Assert.Equal(
            ["widget", Slug, "✗ them", "✗ master (need main)", "DRIFTED", "MISSING", "MISSING / DRIFTED", "CONFIGURED", "CONFIGURED", "CONFIGURED", "⚠ DRIFTED"],
            report.StatusRow());
    }

    [Fact]
    public void Merge_methods_set_but_settings_drifted_reads_as_not_enforced()
    {
        var report = Report(settings: new SettingsInspection(false, true, true));

        Assert.Equal(["⚠ widget (acme/widget): DRIFT DETECTED", "   - Repo settings not enforced"], report.AuditLines());
    }

    [Fact]
    public void Advisories_follow_the_verdict_and_never_count_as_drift()
    {
        var report = Report(
            owner: new OwnerInspection("ALIGNED", true, "authorized", "acme", "acme_profile"),
            ruleset: new RulesetInspection("N/A (Private Repo)", true, null, []),
            names: Named with { Warnings = [new("ci", (IReadOnlyList<string>)["too short", "vague"])] });

        Assert.Equal(
            [
                "✓ widget (acme/widget): FULLY COMPLIANT",
                "   · not adopted: add it to profile acme_profile in config/repo_policies.json",
                "   · check name 'ci': too short; vague",
            ],
            report.AuditLines());
        Assert.Equal("N/A (Private Repo)", report.StatusRow()[5]);
        Assert.Equal("✗ N/A", Report(owner: new OwnerInspection("UNKNOWN_OWNER", false, "?")).StatusRow()[2]);
    }
}
