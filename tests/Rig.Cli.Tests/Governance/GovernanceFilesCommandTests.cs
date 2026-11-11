using System.CommandLine;
using Rig.Cli.Governance;
using Rig.Core.Governance;
using Rig.Core.Processes;
using Rig.Core.Tests;
using Rig.Core.Tests.Fakes;

namespace Rig.Cli.Tests.Governance;

/// <summary>
/// <c>rig repo-governance license</c>, <c>sync-templates</c>, <c>gitignore</c> and <c>apps</c> end to end over a
/// checkout in a temp folder, with gh and its REST API faked and the year fixed.
/// </summary>
public sealed class GovernanceFilesCommandTests : IDisposable
{
    private const string Slug = "acme/widget";

    private const string Policy = """
        {
          "defaults": {
            "expected_owner": "acme",
            "copyright_holder": "Acme Ltd",
            "contact_email": "maintainers@example.com",
            "license": {"type": "MIT"},
            "gitignore_entries": [{"name": "temp", "comment": "Local temp", "write": ["temp/"]}]
          },
          "profiles": {"acme": {"expected_owner": "acme", "repos": ["widget"]}},
          "github_apps": {"renovate": {"name": "Renovate", "accounts": {"acme": {"required": true, "repos": ["widget"]}}}}
        }
        """;

    private const string Installations = "orgs/acme/installations?per_page=100&page=1";

    private readonly TempDir _root = new();
    private readonly FakeProc _proc = new();

    public GovernanceFilesCommandTests()
    {
        File.WriteAllText(_root.File($"checkout/config/{RepoPolicies.FileName}"), Policy);
        _ = _proc.Answer(new Ran(0, $"git@github.com:{Slug}.git\n", string.Empty), "git", "remote", "get-url", "origin");
        _ = _proc.Answer(new Ran(0, $"{Slug}\n", string.Empty), "gh", "repo", "view");
    }

    public void Dispose() => _root.Dispose();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private string Repo => _root.Folder("personal/widget");

    [Fact]
    public async Task A_missing_licence_fails_the_audit_and_enforce_writes_it()
    {
        var (missing, before) = await RunAsync(new FakeGitHub(), "license", Repo);
        var (enforced, wrote) = await RunAsync(new FakeGitHub(), "license", Repo, "--enforce");
        var (aligned, after) = await RunAsync(new FakeGitHub(), "license", Repo, "--spdx");

        var panel = GovernanceCommand.Panel("License Governance (policy config)");
        Assert.Equal((1, $"{panel}✗ widget ({Slug}): not aligned\n   - No LICENSE file found in repository root.\n\nFAILED: 1 repo(s) failed license compliance.\n"), (missing, before));
        Assert.Equal((0, $"{panel}✓ widget ({Slug}): Wrote canonical MIT LICENSE to LICENSE.\n"), (enforced, wrote));
        Assert.Equal((0, $"{panel}✓ widget ({Slug}): MIT (valid)\n   SPDX: all 0 files compliant\n\nSUCCESS: all inspected repos have valid licenses.\n"), (aligned, after));
        Assert.Contains("Copyright (c) 2026 Acme Ltd", await File.ReadAllTextAsync(Path.Join(Repo, "LICENSE"), Token), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sync_templates_audits_previews_and_writes_with_the_policy_contact()
    {
        var (audit, report) = await RunAsync(new FakeGitHub(), "sync-templates", Repo);
        var (dry, _) = await RunAsync(new FakeGitHub(), "sync-templates", Repo, "--apply", "--dry-run");
        var nothingWritten = Directory.EnumerateFileSystemEntries(Repo).Any();
        var (applied, _) = await RunAsync(new FakeGitHub(), "sync-templates", Repo, "--apply");

        Assert.Equal(0, audit);
        Assert.Contains($"! widget ({Slug}): missing 6/6 templates\n   - .github/ISSUE_TEMPLATE/bug_report.yml\n", report, StringComparison.Ordinal);
        Assert.Equal((0, false), (dry, nothingWritten));
        Assert.Equal(0, applied);
        Assert.True(OssTemplates.Inspect(Repo).IsFullyAligned);
        Assert.Contains("maintainers@example.com", await File.ReadAllTextAsync(Path.Join(Repo, "CODE_OF_CONDUCT.md"), Token), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Gitignore_reports_drift_as_json_and_apply_fixes_it()
    {
        var (drifted, json) = await RunAsync(new FakeGitHub(), "gitignore", Repo, "--json");
        var (applied, _) = await RunAsync(new FakeGitHub(), "gitignore", Repo, "--apply");
        var (clean, _) = await RunAsync(new FakeGitHub(), "gitignore", Repo);

        Assert.Equal(1, drifted);
        Assert.StartsWith("[\n  {\n    \"repo_name\": \"widget\",", json, StringComparison.Ordinal);
        Assert.Equal((0, 0), (applied, clean));
        Assert.Contains("temp/", await File.ReadAllTextAsync(Path.Join(Repo, ".gitignore"), Token), StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_targets_for_gitignore_exits_2() =>
        Assert.Equal((2, "❌ No target repositories found.\n"), await RunAsync(new FakeGitHub(), "gitignore", "--org", "nobody"));

    [Fact]
    public async Task Apps_with_none_declared_says_so()
    {
        var (code, output) = await RunAsync(new FakeGitHub(), "apps", "--org", "other");

        Assert.Equal(0, code);
        Assert.EndsWith("No GitHub Apps declared for account/organization 'other' in repo_policies.json.\n", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failed_apps_lookup_exits_1_rather_than_reading_as_uninstalled()
    {
        var api = new FakeGitHub().On("GET", "users/acme", """{"type": "Organization"}""").Fail("GET", Installations, "gh: HTTP 403");

        var (code, output) = await RunAsync(api, "apps", "--org", "acme");

        Assert.Equal(1, code);
        Assert.Contains($"✗ GitHub lookup failed for 'acme': GET {Installations}: ", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_required_app_that_is_not_installed_drifts_and_enforce_skips_it()
    {
        var api = new FakeGitHub().On("GET", "users/acme", """{"type": "Organization"}""").On("GET", Installations, """{"installations": []}""");

        var (code, output) = await RunAsync(api, "apps", "--org", "acme", "--enforce", "--dry-run");

        Assert.Equal(0, code);
        Assert.Contains("DRIFTED", output, StringComparison.Ordinal);
        Assert.Contains("⚠ Renovate (renovate) is not installed on @acme:\n   Install link: https://github.com/apps/renovate/installations/new\n", output, StringComparison.Ordinal);
        Assert.Contains("Skipping renovate: install manually before connecting repos.\n", output, StringComparison.Ordinal);
        Assert.All(api.Calls, call => Assert.Equal("GET", call.Method));
    }

    [Fact]
    public async Task Apps_review_tools_audits_a_repo_and_a_dry_run_enforce_writes_nothing()
    {
        var api = new FakeGitHub().On("GET", "users/acme", """{"type": "Organization"}""")
            .On("GET", Installations, """{"installations": [{"id": 7, "app_slug": "renovate", "repository_selection": "all"}]}""");

        var (audit, report) = await RunAsync(api, "apps", Repo, "--org", "acme", "--review-tools", "--coderabbit");
        var (_, preview) = await RunAsync(api, "apps", Repo, "--org", "acme", "--review-tools", "--enforce", "--dry-run");

        Assert.Equal(0, audit);
        Assert.Contains("   · widget: MISSING\n", report, StringComparison.Ordinal);
        Assert.Contains(" widget:\n   · coderabbit: MISSING\n   · codecov: MISSING\n   · deepsource: MISSING\n   · qodana: OPTIONAL\n", report, StringComparison.Ordinal);
        Assert.EndsWith("SUCCESS: all declared GitHub Apps conform to policy.\n", report, StringComparison.Ordinal);
        Assert.Contains("   · App 'renovate' has access to all repositories on 'acme' (selection='all').\n", preview, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Repo));
    }

    [Fact]
    public async Task Gitignore_drift_as_a_table_says_how_to_fix_it()
    {
        var (code, output) = await RunAsync(new FakeGitHub(), "gitignore", Repo, "-l", "node");

        Assert.Equal(1, code);
        Assert.Contains("Common .gitignore: AUDIT", output, StringComparison.Ordinal);
        Assert.EndsWith("\nRun 'repo-governance gitignore --apply' or 'workspace gitignore sync' to fix.\n", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_selected_installation_missing_a_repo_drifts_and_a_failed_sync_exits_1()
    {
        var api = new FakeGitHub().On("GET", "users/acme", """{"type": "Organization"}""")
            .On("GET", Installations, """{"installations": [{"id": 7, "app_slug": "renovate", "repository_selection": "selected"}]}""")
            .On("GET", "user/installations/7/repositories?per_page=100&page=1", """{"repositories": []}""");

        var (audit, report) = await RunAsync(api, "apps", "--org", "acme");
        var (enforce, synced) = await RunAsync(api, "apps", "--org", "acme", "--enforce");

        Assert.Equal(1, audit);
        Assert.Contains("\n⚠ Renovate is missing connections to: widget\n", report, StringComparison.Ordinal);
        Assert.EndsWith("\nFAILED: 1 required GitHub App(s) drifted on @acme.\n", report, StringComparison.Ordinal);
        Assert.Equal(1, enforce);
        Assert.EndsWith("\nFAILED: 1 GitHub App sync(s) failed on @acme.\n", synced, StringComparison.Ordinal);
        Assert.DoesNotContain(api.Calls, call => call.Method != "GET");
    }

    private async Task<(int Code, string Out)> RunAsync(FakeGitHub api, params string[] args)
    {
        using var output = new StringWriter();
        var command = new GovernanceCommand(new GovernanceHost(_root.Path, Repo, Path.Join(_root.Path, "checkout")), _proc, new FixedYear(), output, api);
        var code = await new RootCommand { command.Build() }.Parse(["repo-governance", .. args])
            .InvokeAsync(new InvocationConfiguration { Output = output, Error = TextWriter.Null }, Token);
        return (code, output.ToString().ReplaceLineEndings("\n"));
    }

    private sealed class FixedYear : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
    }
}
