using System.CommandLine;
using System.Text.Json.Nodes;
using Rig.Cli.Governance;
using Rig.Core.Governance;
using Rig.Core.Policy;
using Rig.Core.Processes;
using Rig.Core.Tests;
using Rig.Core.Tests.Fakes;

namespace Rig.Cli.Tests.Governance;

/// <summary>
/// <c>rig repo-governance status</c>, <c>audit</c> and <c>enforce</c> end to end over a checkout in a temp folder, with
/// git, gh and GitHub's API faked: nothing is read from or written to a real repo.
/// </summary>
public sealed class GovernanceConventionsCommandTests : IDisposable
{
    private const string Slug = "acme/widget";

    private const string PolicyJson = """{"defaults": {"expected_owner": "acme", "allowed_owners": ["acme"], "required_checks": ["ci"]}}""";

    private readonly TempDir _root = new();
    private readonly FakeProc _proc = new();
    private FakeGitHub _api = new();

    public GovernanceConventionsCommandTests()
    {
        Policy(PolicyJson);
        Remote(Slug);
        _api = Aligned();
    }

    public void Dispose() => _root.Dispose();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string Rule => "\n" + new string('=', 50) + "\n";

    private static RepoPolicies Policies => RepoPolicies.Parse(JsonNode.Parse(PolicyJson)!.AsObject(), "policy.json");

    private string Repo => _root.Folder("personal/widget");

    private static FakeGitHub Aligned(string slug = Slug, string? ruleset = null) => new FakeGitHub()
        .On("GET", $"repos/{slug}", """{"default_branch": "main", "delete_branch_on_merge": true, "allow_squash_merge": true, "allow_merge_commit": false, "allow_rebase_merge": false, "allow_auto_merge": true, "private": false}""")
        .On("GET", $"repos/{slug}/rulesets", """[{"id": 7, "name": "Main Branch Governance Ruleset", "enforcement": "active"}]""")
        .On("GET", $"repos/{slug}/rulesets/7", ruleset ?? PyJson.DumpsLine(RulesetResource.Payload(Policies, slug)))
        .On("PATCH", $"repos/{slug}", "{}")
        .On("PUT", $"repos/{slug}/rulesets/7", "{}");

    [Fact]
    public async Task Enforce_then_audit_leaves_a_compliant_repo()
    {
        Checkout();

        var (enforced, enforceOut) = await RunAsync("enforce", Repo);
        var (audited, auditOut) = await RunAsync("audit", Repo);

        Assert.Equal(0, enforced);
        Assert.Contains("widget", enforceOut, StringComparison.Ordinal);
        Assert.EndsWith("\n✓ Conventions Enforcement Complete!\n", enforceOut, StringComparison.Ordinal);
        Assert.Contains(_api.Calls, c => c.Method == "PATCH");
        Assert.True(File.Exists(Path.Join(Repo, "TODO.md")));
        Assert.Equal(
            (0, $"{GovernanceCommand.Panel("Repository Conventions: AUDIT CHECK")}✓ widget ({Slug}): FULLY COMPLIANT\n{Rule}"
                + "SUCCESS: All 1 repositories are fully compliant with conventions!\n"),
            (audited, auditOut));
    }

    [Fact]
    public async Task Audit_fails_on_drift_and_on_a_missing_checkout()
    {
        Checkout();
        _api = Aligned(ruleset: Drifted());
        var missing = _root.Folder("personal/gone");

        var (code, output) = await RunAsync("audit", "--all");
        var (_, gone) = await RunAsync("audit", missing);

        Assert.Equal(1, code);
        Assert.Contains($"⚠ widget ({Slug}): DRIFT DETECTED\n", output, StringComparison.Ordinal);
        Assert.Contains("   - GitHub Ruleset: DRIFTED: missing rule ", output, StringComparison.Ordinal);
        Assert.EndsWith($"{Rule}AUDIT FAILED: 1/1 repositories have configuration drift.\nRun align-repo-conventions enforce to apply fixes.\n", output, StringComparison.Ordinal);
        Assert.Contains("✗ gone: Repo check failed (MISSING_LOCAL)\n", gone, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Audit_with_nothing_to_audit_fails()
    {
        var (code, output) = await RunAsync("audit", "--all");

        Assert.Equal((1, $"{GovernanceCommand.Panel("Repository Conventions: AUDIT CHECK")}{Rule}AUDIT FAILED: no repositories found to audit.\n"), (code, output));
    }

    [Fact]
    public async Task Status_tabulates_each_repo_and_lists_advisories_after()
    {
        Checkout();
        var gone = _root.Folder("personal/gone");
        _ = Directory.CreateDirectory(Path.Join(gone, ".git"));
        Policy(
            """{"defaults": {"expected_owner": "acme", "allowed_owners": ["acme"], "required_checks": ["ci"]}, "owner_profiles": {"acme": "acme_profile"}, "profiles": {"acme_profile": {}}}""");

        var (code, output) = await RunAsync("status", Repo);
        _ = _proc.Answer(new Ran(1, string.Empty, "no remote"), "gh", "repo", "view");
        var (_, unlinked) = await RunAsync("status", gone);

        Assert.Equal(0, code);
        Assert.StartsWith(GovernanceCommand.Panel("Repository Conventions: STATUS / PLAN") + "Convention Status Overview\nRepository ", output, StringComparison.Ordinal);
        Assert.Contains(" ✓ acme ", output, StringComparison.Ordinal);
        Assert.EndsWith($"· widget ({Slug}): not adopted: add it to profile acme_profile in config/repo_policies.json\n", output, StringComparison.Ordinal);
        Assert.Matches(@"gone\s+N/A\s+(-\s+){8}NOT_LINKED_TO_GITHUB", unlinked);
    }

    [Fact]
    public async Task Enforce_refuses_a_repo_under_the_wrong_owner()
    {
        Checkout();
        Remote("them/widget");

        var (code, output) = await RunAsync("enforce", Repo);

        Assert.Equal(0, code);
        Assert.Contains("✗ them/widget: refusing to enforce conventions — owner governance failure: Expected owner 'acme', but repo is owned by 'them'\n", output, StringComparison.Ordinal);
        Assert.Matches(@"widget\s+them/widget\s+blocked_owner_governance(\s+skipped){8}", output);
        Assert.Empty(_api.Calls);
    }

    [Theory]
    [InlineData(null, "blocked_no_pr_workflows", "✗ acme/widget: refusing to set required checks ['ci'] — no pull_request workflow exists to report them\n")]
    [InlineData("on: [pull_request]\njobs:\n  lint: {}\n", "blocked_stale_check_names", "✗ acme/widget: refusing to set required checks ['ci'] — no pull_request job declares them\n   declared: ['lint'] — run 'repo-governance check-names' and fix the map or the job name\n")]
    public async Task Enforce_never_requires_a_check_no_job_reports(string? workflow, string status, string refusal)
    {
        Checkout(workflow);

        var (_, output) = await RunAsync("enforce", Repo, "--dry-run");

        Assert.Contains(refusal, output, StringComparison.Ordinal);
        Assert.Contains($" {status} ", output, StringComparison.Ordinal);
        Assert.DoesNotContain(_api.Calls, c => c.Route.Contains("rulesets", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_dry_run_lists_ruleset_drift_and_writes_nothing()
    {
        Checkout();
        _api = Aligned(ruleset: Drifted());
        var missing = _root.Folder("personal/gone");

        var (code, output) = await RunAsync("enforce", Repo, "--dry-run");
        var (_, skipped) = await RunAsync("enforce", missing);

        Assert.Equal(0, code);
        Assert.Contains($"\nRuleset drift on {Slug} (dry run — would update):\n   - missing rule ", output, StringComparison.Ordinal);
        Assert.DoesNotContain(_api.Calls, c => c.Method != "GET");
        Assert.False(File.Exists(Path.Join(Repo, "TODO.md")));
        Assert.Matches(@"gone\s+N/A(\s+skipped){9}", skipped);
    }

    private static string Drifted()
    {
        var live = JsonNode.Parse(PyJson.DumpsLine(RulesetResource.Payload(Policies, Slug)))!.AsObject();
        live["rules"] = new JsonArray();
        return live.ToJsonString();
    }

    private void Policy(string json) => File.WriteAllText(_root.File($"checkout/config/{RepoPolicies.FileName}"), json);

    private void Remote(string slug) =>
        _ = _proc.Answer(new Ran(0, $"git@github.com:{slug}.git\n", string.Empty), "git", "remote", "get-url", "origin")
            .Answer(new Ran(0, $"{slug}\n", string.Empty), "gh", "repo", "view")
            .Answer(
                new Ran(0, $$"""{"nameWithOwner": "{{slug}}", "viewerPermission": "ADMIN", "isFork": false}""", string.Empty),
                "gh", "repo", "view", $"git@github.com:{slug}.git", "--json", "nameWithOwner,viewerPermission,isFork");

    private void Checkout(string? workflow = "on: [pull_request]\njobs:\n  ci: {}\n")
    {
        _ = Directory.CreateDirectory(Path.Join(Repo, ".git"));
        if (workflow is not null)
        {
            File.WriteAllText(_root.File("personal/widget/.github/workflows/ci.yml"), workflow);
        }
    }

    private async Task<(int Code, string Out)> RunAsync(params string[] args)
    {
        using var output = new StringWriter();
        var command = new GovernanceCommand(new GovernanceHost(_root.Path, Repo, Path.Join(_root.Path, "checkout")), _proc, TimeProvider.System, output, _api);
        var code = await new RootCommand { command.Build() }.Parse(["repo-governance", .. args])
            .InvokeAsync(new InvocationConfiguration { Output = output, Error = TextWriter.Null }, Token);
        return (code, output.ToString().ReplaceLineEndings("\n"));
    }
}
