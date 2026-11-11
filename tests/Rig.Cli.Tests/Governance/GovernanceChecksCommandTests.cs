using System.CommandLine;
using Rig.Cli.Governance;
using Rig.Core.Governance;
using Rig.Core.Processes;
using Rig.Core.Tests;
using Rig.Core.Tests.Fakes;

namespace Rig.Cli.Tests.Governance;

/// <summary>
/// <c>rig repo-governance check-names</c> and <c>prune-branches</c> end to end over a checkout in a temp folder, with
/// git and gh faked: nothing is fetched from or pushed to a real remote.
/// </summary>
public sealed class GovernanceChecksCommandTests : IDisposable
{
    private const string Slug = "acme/widget";

    private const string Origin = $"git@github.com:{Slug}.git";

    private readonly TempDir _root = new();
    private readonly FakeProc _proc = new();

    public GovernanceChecksCommandTests()
    {
        Policy("""{"defaults": {"required_checks": ["ci"]}}""");
        _ = _proc.Answer(new Ran(0, $"{Origin}\n", string.Empty), "git", "remote", "get-url", "origin")
            .Answer(new Ran(0, $"{Slug}\n", string.Empty), "gh", "repo", "view")
            .Answer(new Ran(0, $$"""{"nameWithOwner": "{{Slug}}", "viewerPermission": "ADMIN", "isFork": false}""", string.Empty), "gh", "repo", "view", Origin, "--json", "nameWithOwner,viewerPermission,isFork")
            .Answer(new Ran(0, string.Empty, string.Empty), "git", "fetch")
            .Answer(new Ran(0, "origin/HEAD -> origin/main\norigin/main\norigin/feat\norigin/stray\n", string.Empty), "git", "branch", "-r")
            .Answer(new Ran(0, """[{"number": 3, "headRefName": "feat"}]""", string.Empty), "gh", "pr", "list", "--state", "merged")
            .Answer(new Ran(0, "[]", string.Empty), "gh", "pr", "list", "--state", "open");
    }

    public void Dispose() => _root.Dispose();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string ChecksPanel => GovernanceCommand.Panel("Required Check Names: DRIFT & SHAPE");

    private static string Rule => "\n\n" + new string('=', 50) + "\n";

    private string Repo => _root.Folder("personal/widget");

    [Fact]
    public async Task The_lexicon_lists_every_preferred_name()
    {
        var (code, output) = await RunAsync("check-names", "--lexicon");

        Assert.Equal(0, code);
        Assert.Contains("Preferred check names (lowercase snake_case, max 2 words)", output, StringComparison.Ordinal);
        Assert.All(CheckNames.PreferredCheckNames, pair => Assert.Contains(pair.Key, output, StringComparison.Ordinal));
        Assert.Empty(_proc.Requests);
    }

    [Fact]
    public async Task Required_checks_every_job_declares_succeed()
    {
        Workflow("on: [pull_request]\njobs:\n  ci: {}\n");

        var (code, output) = await RunAsync("check-names", Repo);

        Assert.Equal((0, $"{ChecksPanel}✓ widget ({Slug}): ['ci']{Rule}SUCCESS: every required check is declared and well-named.\n"), (code, output));
    }

    [Fact]
    public async Task A_required_check_no_job_declares_fails_with_the_declared_names()
    {
        Policy("""{"defaults": {"required_checks": ["ci", "Build & Test"]}}""");
        Workflow("on: [pull_request]\njobs:\n  lint: {}\n  tests:\n    name: Build & Test\n");

        var (code, output) = await RunAsync("check-names", Repo);

        Assert.Equal(1, code);
        Assert.StartsWith(
            $"{ChecksPanel}✗ widget ({Slug})\n   required check 'ci' is declared by no pull_request job\n"
            + "   declared: ['Build & Test', 'lint']\n   suggested required_checks: ['Build & Test', 'lint']\n   'Build & Test' [ci.yml :: tests]\n      - ",
            output,
            StringComparison.Ordinal);
        Assert.EndsWith(
            $"{Rule}FAILED: 1 repo(s) require a check no job declares.\nRename the job and update required_checks in config/repo_policies.json in the same change.\n",
            output,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, 0, "OK with warnings: 1 repo(s) have names that will drift.")]
    [InlineData(true, 1, "FAILED (--strict): 1 repo(s) have badly-shaped names.")]
    public async Task Badly_shaped_names_warn_and_fail_only_under_strict(bool strict, int expected, string verdict)
    {
        Policy("""{"defaults": {"required_checks": ["my_check"]}}""");
        Workflow("on: [pull_request]\njobs:\n  my_check: {}\n");

        var (code, output) = await RunAsync(["check-names", Repo, .. strict ? ["--strict"] : Array.Empty<string>()]);

        Assert.Equal(expected, code);
        Assert.StartsWith($"{ChecksPanel}⚠ widget ({Slug})\n   'my_check' [ci.yml :: my_check]\n      - ", output, StringComparison.Ordinal);
        Assert.EndsWith($"{Rule}{verdict}\nSee 'repo-governance check-names --lexicon' for the preferred names.\n", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Repos_without_a_remote_or_pr_workflows_are_skipped()
    {
        var (_, noWorkflows) = await RunAsync("check-names", Repo);
        _ = _proc.Answer(new Ran(1, string.Empty, "no remote"), "gh", "repo", "view");
        var (code, noRemote) = await RunAsync("check-names", Repo);

        Assert.Equal($"{ChecksPanel}· widget ({Slug}): no pull_request workflows, skipped{Rule}SUCCESS: every required check is declared and well-named.\n", noWorkflows);
        Assert.Equal((0, $"{ChecksPanel}? widget: no GitHub remote, skipped{Rule}SUCCESS: every required check is declared and well-named.\n"), (code, noRemote));
    }

    [Fact]
    public async Task A_dry_run_reports_merged_and_prless_branches_and_deletes_nothing()
    {
        GitDir();

        var (code, output) = await RunAsync("prune-branches", Repo);

        var panel = GovernanceCommand.Panel("Branch Cleanup: DRY RUN");
        Assert.Equal(
            (0, $"{panel}\nwidget\n  would delete feat (PR #3 merged)\n  1 branch(es) with no PR — review manually, never auto-deleted:\n    · stray\n\n"
                + "1 merged branch(es) would be deleted. Re-run with --apply.\n"),
            (code, output));
        Assert.DoesNotContain(_proc.Requests, r => r.Argv.Contains("--delete"));
    }

    [Fact]
    public async Task Apply_deletes_merged_branches_and_reports_failures()
    {
        GitDir();
        _ = _proc.Answer(new Ran(0, "origin/main\norigin/feat\norigin/gone\n", string.Empty), "git", "branch", "-r")
            .Answer(new Ran(0, """[{"number": 3, "headRefName": "feat"}, {"number": 4, "headRefName": "gone"}]""", string.Empty), "gh", "pr", "list", "--state", "merged")
            .Answer(new Ran(0, string.Empty, string.Empty), "git", "push", "origin", "--delete", "feat")
            .Answer(new Ran(1, string.Empty, "error: gone already\n"), "git", "push", "origin", "--delete", "gone");

        var (code, output) = await RunAsync("prune-branches", Repo, "--apply", "--hide-no-pr");

        var panel = GovernanceCommand.Panel("Branch Cleanup: APPLY");
        Assert.Equal((0, $"{panel}\nwidget\n  ✗ deleted feat (PR #3)\n  ! failed gone: error: gone already\n\nDeleted 1/2 merged branch(es).\n"), (code, output));
    }

    [Fact]
    public async Task Unpushable_clean_and_repeat_checkouts_are_reported_not_pruned()
    {
        GitDir();
        var twin = _root.Folder("personal/widget-twin");
        _ = Directory.CreateDirectory(Path.Join(twin, ".git"));
        _ = _proc.Answer(new Ran(0, "origin/main\n", string.Empty), "git", "branch", "-r");

        var (code, output) = await RunAsync("prune-branches", "--all");
        var stranger = _root.Folder("personal/theirs");
        _ = Directory.CreateDirectory(Path.Join(stranger, ".git"));
        _ = _proc.Answer(new Ran(0, "git@github.com:them/theirs.git\n", string.Empty), "git", "remote", "get-url", "origin")
            .Answer(new Ran(0, """{"nameWithOwner": "them/theirs", "viewerPermission": "READ", "isFork": false}""", string.Empty), "gh", "repo", "view");
        var (_, skipped) = await RunAsync("prune-branches", stranger);

        var panel = GovernanceCommand.Panel("Branch Cleanup: DRY RUN");
        Assert.Equal(0, code);
        Assert.Equal($"{panel}- theirs: skipped (no push access to them/theirs)\n\nNo merged branches left behind.\n", skipped);
        Assert.Equal(
            $"{panel}✓ widget: nothing to prune\n- widget-twin: same remote as widget ({Slug})\n\nNo merged branches left behind.\n",
            output);
    }

    [Fact]
    public async Task No_targets_prune_nothing()
    {
        var (code, output) = await RunAsync("prune-branches", "--all");

        Assert.Equal((0, "No target repositories found.\n"), (code, output));
    }

    private void Policy(string json) => File.WriteAllText(_root.File($"checkout/config/{RepoPolicies.FileName}"), json);

    private void Workflow(string content) => File.WriteAllText(_root.File("personal/widget/.github/workflows/ci.yml"), content);

    private void GitDir() => Directory.CreateDirectory(Path.Join(Repo, ".git"));

    private Task<(int Code, string Out)> RunAsync(params string[] args) => RunAsync((IEnumerable<string>)args);

    private async Task<(int Code, string Out)> RunAsync(IEnumerable<string> args)
    {
        using var output = new StringWriter();
        var command = new GovernanceCommand(new GovernanceHost(_root.Path, Repo, Path.Join(_root.Path, "checkout")), _proc, TimeProvider.System, output, new FakeGitHub());
        var code = await new RootCommand { command.Build() }.Parse(["repo-governance", .. args])
            .InvokeAsync(new InvocationConfiguration { Output = output, Error = TextWriter.Null }, Token);
        return (code, output.ToString().ReplaceLineEndings("\n"));
    }
}
