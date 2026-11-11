using System.CommandLine;
using System.Text.Json.Nodes;
using Rig.Cli.Governance;
using Rig.Core.Governance;
using Rig.Core.Processes;
using Rig.Core.Tests;
using Rig.Core.Tests.Fakes;

namespace Rig.Cli.Tests.Governance;

/// <summary>
/// <c>rig repo-governance plan</c>, <c>sync-labels</c>, <c>environments</c> and <c>pages</c> end to end over a checkout in
/// a temp folder. gh is faked twice over: <see cref="FakeProc"/> names the checkout's repo, <see cref="FakeGitHub"/> serves
/// its REST API, so nothing real is read or written.
/// </summary>
public sealed class GovernanceSyncCommandTests : IDisposable
{
    private const string Slug = "acme/widget";

    private const string Policy = """
        {
          "defaults": {
            "expected_owner": "acme",
            "allowed_owners": ["acme"],
            "required_checks": [],
            "labels": [{"name": "type: bug", "color": "d73a4a", "description": "Something is broken"}],
            "environments": {"release": {}},
            "pages": {"enabled": true, "cname": "widget.example"}
          }
        }
        """;

    private readonly TempDir _root = new();
    private readonly FakeProc _proc = new();

    public GovernanceSyncCommandTests()
    {
        File.WriteAllText(_root.File($"checkout/config/{RepoPolicies.FileName}"), Policy);
        _ = _proc.Answer(new Ran(0, $"git@github.com:{Slug}.git\n", string.Empty), "git", "remote", "get-url", "origin");
        _ = _proc.Answer(new Ran(0, $"{Slug}\n", string.Empty), "gh", "repo", "view");
    }

    public void Dispose() => _root.Dispose();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private string Repo => _root.Folder("personal/widget");

    private const string Header = "╭──────────────────────────────────╮\n│ GitHub Label Taxonomy Governance │\n╰──────────────────────────────────╯\n";

    // GitHub where every resource matches the policy.
    private static FakeGitHub Aligned() => new FakeGitHub()
        .On("GET", $"repos/{Slug}", """{"default_branch": "main", "delete_branch_on_merge": true, "allow_squash_merge": true, "allow_merge_commit": false, "allow_rebase_merge": false, "allow_auto_merge": true}""")
        .Fail("GET", $"repos/{Slug}/rulesets", "Upgrade to GitHub Pro or make this repository public")
        .On("GET", $"repos/{Slug}/labels?per_page=100", """[{"name": "type: bug", "color": "d73a4a", "description": "Something is broken"}]""")
        .On("GET", $"repos/{Slug}/environments", """{"environments": [{"name": "release"}]}""")
        .On("GET", $"repos/{Slug}/pages", """{"cname": "widget.example", "build_type": "workflow", "https_enforced": true}""");

    private static FakeGitHub Drifted() => new FakeGitHub()
        .On("GET", $"repos/{Slug}/labels?per_page=100", "[]")
        .On("GET", $"repos/{Slug}/environments", """{"environments": []}""");

    [Fact]
    public void The_panel_fits_its_title() =>
        Assert.Equal(Header, GovernanceCommand.Panel("GitHub Label Taxonomy Governance"));

    [Fact]
    public async Task An_aligned_plan_says_so_and_exits_0()
    {
        var payload = RulesetResource.Payload(RepoPolicies.Parse(JsonNode.Parse(Policy), "policy.json"), Slug);
        var api = Aligned()
            .On("GET", $"repos/{Slug}/rulesets", $$"""[{"id": 7, "name": "{{payload["name"]}}"}]""")
            .On("GET", $"repos/{Slug}/rulesets/7", payload.ToJsonString());

        var (code, output) = await RunAsync(api, "plan");

        Assert.Equal((0, "Every repo matches its policy.\n"), (code, output));
    }

    [Fact]
    public async Task A_drifted_plan_is_a_table_and_exits_1()
    {
        var api = Aligned().On("GET", $"repos/{Slug}/labels?per_page=100", "[]");

        var (code, output) = await RunAsync(api, "plan");

        Assert.Equal(1, code);
        var lines = output.Split('\n');
        Assert.Equal("Governance plan", lines[0].Trim());
        Assert.Matches(@"^Repo\s+Resource\s+Action\s+Subject\s+Detail\s*$", lines[1]);
        Assert.Contains(lines, l => System.Text.RegularExpressions.Regex.IsMatch(l, @"^acme/widget\s+ruleset\s+unserved\s"));
        Assert.Contains(lines, l => System.Text.RegularExpressions.Regex.IsMatch(l, @"^acme/widget\s+labels\s+create\s+type: bug\s+Label missing from repository\s*$"));
        Assert.DoesNotContain(api.Calls, c => c.Method != "GET");
    }

    [Fact]
    public async Task An_applied_plan_writes_and_titles_its_table_so()
    {
        var api = Aligned().On("GET", $"repos/{Slug}/labels?per_page=100", "[]").On("POST", $"repos/{Slug}/labels", "{}");

        var (code, output) = await RunAsync(api, "plan", "--apply");

        Assert.Equal(0, code);
        Assert.Equal("Governance plan (applied)", output.Split('\n')[0].Trim());
        Assert.Single(api.Calls, c => c.Method == "POST");
    }

    [Fact]
    public async Task The_plan_json_is_one_compact_line_per_run()
    {
        var (code, output) = await RunAsync(Aligned().Fail("GET", $"repos/{Slug}/environments", 500), "plan", "--json");

        Assert.Equal(2, code);
        var plans = Assert.IsType<JsonArray>(JsonNode.Parse(output));
        Assert.Equal(["repo", "changes", "errors"], plans[0]!.AsObject().Select(p => p.Key));
        Assert.Equal($"environments: GET repos/{Slug}/environments: gh: HTTP 500", (string)plans[0]!["errors"]![0]!);
        Assert.EndsWith("]\n", output, StringComparison.Ordinal);
        Assert.Single(output.Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public async Task A_checkout_gh_cannot_name_is_an_error_row()
    {
        _ = _proc.Answer(new Ran(1, string.Empty, "no remote"), "gh", "repo", "view");

        var (code, output) = await RunAsync(new FakeGitHub(), "plan", "--json");

        Assert.Equal(2, code);
        Assert.Equal("""[{"repo":"widget","changes":[],"errors":["no GitHub repository found for this checkout"]}]""" + "\n", output);
    }

    [Fact]
    public async Task A_label_audit_lists_each_difference_and_still_exits_0()
    {
        var (code, output) = await RunAsync(Drifted(), "sync-labels");

        Assert.Equal(0, code);
        Assert.Equal(
            Header
            + "! widget (acme/widget): 1 missing, 0 mismatched\n"
            + "   - type: bug (create: Label missing from repository)\n"
            + "\nAudit complete: 1 repo(s) drifted from label taxonomy.\nRun with --apply to synchronize labels.\n",
            output);
    }

    [Fact]
    public async Task Aligned_labels_audit_to_success()
    {
        var (_, output) = await RunAsync(Aligned(), "sync-labels", "--audit");

        Assert.Equal(Header + "✓ widget (acme/widget): all 1 labels aligned\n\nSUCCESS: all inspected repos match standard label taxonomy.\n", output);
    }

    [Fact]
    public async Task A_label_dry_run_lists_what_it_would_do()
    {
        var api = Drifted();

        var (_, output) = await RunAsync(api, "sync-labels", "--apply", "--dry-run");

        Assert.Equal(Header + "✓ widget (acme/widget):\n   • [Dry Run] Would create label 'type: bug' (#d73a4a)\n", output);
        Assert.DoesNotContain(api.Calls, c => c.Method != "GET");
    }

    [Fact]
    public async Task Up_to_date_labels_apply_to_nothing()
    {
        var (_, output) = await RunAsync(Aligned(), "sync-labels", "--apply");

        Assert.EndsWith("· widget (acme/widget): all labels up to date\n", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_org_with_no_local_clone_syncs_the_org_s_repos_on_github()
    {
        _ = _proc.Answer(new Ran(0, """[{"nameWithOwner": "acme/remote"}]""", string.Empty), "gh", "repo", "list");
        var api = new FakeGitHub().On("GET", "repos/acme/remote/labels?per_page=100", "[]");

        var (_, output) = await RunInAsync(api, _root.Path, "sync-labels", "--org", "acme");

        Assert.Contains("! remote (acme/remote): 1 missing, 0 mismatched\n", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Environments_audit_names_the_missing_ones()
    {
        var (missing, drifted) = await RunAsync(Drifted(), "environments");
        var (_, aligned) = await RunAsync(Aligned(), "environments", "--audit");

        Assert.Equal(0, missing);
        Assert.Contains("! widget (acme/widget): missing release\n\nAudit complete: 1 repo(s) missing declared environments.\nRun with --apply to synchronize.\n", drifted, StringComparison.Ordinal);
        Assert.EndsWith("✓ widget (acme/widget): release present\n\nSUCCESS: all inspected repos have their declared environments.\n", aligned, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Environments_apply_logs_each_action()
    {
        var api = Drifted().On("PUT", $"repos/{Slug}/environments/release", "{}");

        var (_, output) = await RunAsync(api, "environments", "--apply");
        var (_, dry) = await RunAsync(Drifted(), "environments", "--apply", "--dry-run");
        var (_, none) = await RunAsync(Aligned(), "environments", "--apply");

        Assert.EndsWith("✓ widget (acme/widget):\n   • created release\n", output, StringComparison.Ordinal);
        Assert.EndsWith("   • dry-run create: release — wait_timer=0\n", dry, StringComparison.Ordinal);
        Assert.EndsWith("· widget (acme/widget): environments up to date\n", none, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_repo_declaring_no_environments_or_pages_is_skipped()
    {
        await File.WriteAllTextAsync(Path.Join(_root.Path, "checkout", "config", RepoPolicies.FileName), """{"defaults": {}}""", Token);

        var (_, environments) = await RunAsync(new FakeGitHub(), "environments");
        var (_, pages) = await RunAsync(new FakeGitHub(), "pages");

        Assert.Contains("· widget (acme/widget): declares no environments, skipped\n", environments, StringComparison.Ordinal);
        Assert.Contains("· widget (acme/widget): Pages not enabled in profile, skipped\n", pages, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pages_audit_passes_when_aligned_and_fails_on_drift()
    {
        var (ok, aligned) = await RunAsync(Aligned(), "pages", "--audit");
        var (bad, drifted) = await RunAsync(new FakeGitHub(), "pages");

        Assert.Equal((0, 1), (ok, bad));
        Assert.Contains("✓ widget (acme/widget): Pages aligned (widget.example)\n\nSUCCESS: all inspected repos have aligned Pages configurations.\n", aligned, StringComparison.Ordinal);
        Assert.Contains("! widget (acme/widget): Pages drift detected: Pages is disabled\n\nAudit complete: 1 repo(s) have Pages drift.\n", drifted, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pages_apply_reports_each_outcome()
    {
        var writes = new FakeGitHub().On("POST", $"repos/{Slug}/pages", "{}").On("PUT", $"repos/{Slug}/pages", "{}");

        var (_, applied) = await RunAsync(writes, "pages", "--apply");
        var (_, dry) = await RunAsync(new FakeGitHub(), "pages", "--apply", "--dry-run");
        var (_, current) = await RunAsync(Aligned(), "pages", "--apply");
        var (code, failed) = await RunAsync(new FakeGitHub().Fail("POST", $"repos/{Slug}/pages", "gh: Forbidden"), "pages", "--apply");

        Assert.Contains($"✓ widget (acme/widget): Pages configured: ['POST repos/{Slug}/pages {{\"build_type\":\"workflow\"}}', ", applied, StringComparison.Ordinal);
        Assert.Contains("~ widget (acme/widget): would configure Pages: [", dry, StringComparison.Ordinal);
        Assert.Contains("· widget (acme/widget): Pages up to date (widget.example)\n", current, StringComparison.Ordinal);
        Assert.Equal(0, code);
        Assert.Contains("✗ widget (acme/widget): failed to align Pages (gh: Forbidden)\n", failed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unreadable_repo_is_reported_and_exits_2_not_read_as_empty()
    {
        var api = new FakeGitHub()
            .Fail("GET", $"repos/{Slug}/labels?per_page=100", 500)
            .Fail("GET", $"repos/{Slug}/environments", 500)
            .Fail("GET", $"repos/{Slug}/pages", 500);

        var (labels, labelsOut) = await RunAsync(api, "sync-labels");
        var (labelsApply, labelsApplyOut) = await RunAsync(api, "sync-labels", "--apply");
        var (environments, environmentsOut) = await RunAsync(api, "environments");
        var (environmentsApply, _) = await RunAsync(api, "environments", "--apply");
        var (pages, pagesOut) = await RunAsync(api, "pages");

        Assert.Equal([2, 2, 2, 2, 2], new[] { labels, labelsApply, environments, environmentsApply, pages });
        Assert.EndsWith(
            $"✗ widget (acme/widget): GET repos/{Slug}/labels?per_page=100: gh: HTTP 500\n\nIncomplete: 1 repo(s) could not be read from GitHub.\n", labelsOut, StringComparison.Ordinal);
        Assert.Contains("✗ widget (acme/widget): GET repos/", labelsApplyOut, StringComparison.Ordinal);
        Assert.Contains($"✗ widget (acme/widget): GET repos/{Slug}/environments: gh: HTTP 500\n", environmentsOut, StringComparison.Ordinal);
        Assert.Contains($"✗ widget (acme/widget): cannot read Pages (GET repos/{Slug}/pages: gh: HTTP 500)\n", pagesOut, StringComparison.Ordinal);
        Assert.DoesNotContain("Run with --apply", pagesOut, StringComparison.Ordinal);
        Assert.DoesNotContain(api.Calls, c => c.Method != "GET");
    }

    [Fact]
    public async Task Pages_without_a_github_remote_are_skipped()
    {
        _ = _proc.Answer(new Ran(1, string.Empty, "no remote"), "gh", "repo", "view");

        var (_, output) = await RunAsync(new FakeGitHub(), "pages");

        Assert.Contains("? widget: no GitHub remote, skipped\n", output, StringComparison.Ordinal);
    }

    private Task<(int Code, string Out)> RunAsync(FakeGitHub api, params string[] args) => RunInAsync(api, Repo, args);

    private async Task<(int Code, string Out)> RunInAsync(FakeGitHub api, string cwd, params string[] args)
    {
        using var output = new StringWriter();
        var command = new GovernanceCommand(new GovernanceHost(_root.Path, cwd, Path.Join(_root.Path, "checkout")), _proc, TimeProvider.System, output, api);
        var code = await new RootCommand { command.Build() }.Parse(["repo-governance", .. args])
            .InvokeAsync(new InvocationConfiguration { Output = output, Error = TextWriter.Null }, Token);
        return (code, output.ToString().ReplaceLineEndings("\n"));
    }
}
