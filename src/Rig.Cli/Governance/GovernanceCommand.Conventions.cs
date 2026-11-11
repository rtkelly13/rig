using System.CommandLine;
using Rig.Cli.Vault;
using Rig.Core.Governance;
using Rig.Core.Processes;
using Rig.Core.Roots;

namespace Rig.Cli.Governance;

/// <summary>
/// The per-repo conventions: <c>status</c> and <c>audit</c> read them, <c>enforce</c> writes them. Ported from
/// <c>rig.repo_governance.cli_repo_checks</c>.
/// </summary>
internal sealed partial class GovernanceCommand
{
    private static readonly string[] SkippedCells = [.. Enumerable.Repeat("skipped", 9)];

    private Command Status()
    {
        var path = PathArgument();
        var all = new Option<bool>("--all", "-a") { Description = "Scan all personal repositories under <code root>/personal" };
        var org = OrgOption();
        var command = new Command("status", "Inspects convention status across target repositories without mutating settings.") { path, all, org };
        command.SetAction((parsed, cancellationToken) => StatusAsync(parsed.GetValue(path), parsed.GetValue(all), parsed.GetValue(org), cancellationToken));
        return command;
    }

    private Command Audit()
    {
        var path = PathArgument();
        var all = new Option<bool>("--all", "-a") { Description = "Audit all personal repositories under <code root>/personal" };
        var org = OrgOption();
        var command = new Command("audit", "Audits repository conventions for CI compliance. Exits with 0 if compliant, 1 if drift detected.") { path, all, org };
        command.SetAction((parsed, cancellationToken) => AuditAsync(parsed.GetValue(path), parsed.GetValue(all), parsed.GetValue(org), cancellationToken));
        return command;
    }

    private Command Enforce()
    {
        var path = PathArgument();
        var all = new Option<bool>("--all", "-a") { Description = "Enforce conventions across all personal repositories under <code root>/personal" };
        var org = OrgOption();
        var dryRun = new Option<bool>("--dry-run") { Description = "Preview mutations without applying changes" };
        var command = new Command(
            "enforce",
            "Enforces conventions on target repositories (mutates repo settings, .npmrc, AGENTS.md, TODO.md). The GitHub ruleset is "
            + "compared rule by rule with the declared payload and replaced on any drift. Run --dry-run first: it lists each drifted "
            + "rule before anything is overwritten. A required check that no workflow reports is refused, because it would block "
            + "every PR (see `check-names`).")
        {
            path, all, org, dryRun,
        };
        command.SetAction((parsed, cancellationToken) => EnforceAsync(
            parsed.GetValue(path), parsed.GetValue(all), parsed.GetValue(org), parsed.GetValue(dryRun), cancellationToken));
        return command;
    }

    private async Task<int> StatusAsync(string? path, bool all, string? org, CancellationToken cancellationToken)
    {
        await output.WriteAsync(Panel("Repository Conventions: STATUS / PLAN")).ConfigureAwait(false);
        var (targets, conventions, _, _) = await ConventionTargetsAsync(path, all, org, cancellationToken).ConfigureAwait(false);
        var table = new TextTable(
            "Convention Status Overview",
            [("Repository", false), ("GitHub Name", false), ("Owner", false), ("Base Branch", false), ("Repo Settings", false), ("GitHub Ruleset", false),
                ("NPMRC Status", false), ("AGENTS.md", false), ("Pinned SHAs", false), ("ls-lint", false), ("Overall Alignment", false)]);
        var advisories = new List<string>();
        foreach (var target in targets)
        {
            var existence = await conventions.ExistsAsync(target, cancellationToken).ConfigureAwait(false);
            if (!existence.Exists)
            {
                _ = table.Add([FolderName(target), existence.Slug ?? "N/A", .. Enumerable.Repeat("-", 8), existence.Status]);
                continue;
            }

            var report = await conventions.InspectAsync(target, existence.Slug!, cancellationToken).ConfigureAwait(false);
            if (report.NotAdopted is { } advisory)
            {
                advisories.Add($"{report.Name} ({report.Slug}): {advisory}");
            }

            _ = table.Add([.. report.StatusRow()]);
        }

        await output.WriteAsync(table.Render()).ConfigureAwait(false);
        foreach (var advisory in advisories)
        {
            await output.WriteLineAsync($"· {advisory}").ConfigureAwait(false);
        }

        return 0;
    }

    private async Task<int> AuditAsync(string? path, bool all, string? org, CancellationToken cancellationToken)
    {
        await output.WriteAsync(Panel("Repository Conventions: AUDIT CHECK")).ConfigureAwait(false);
        var (targets, conventions, _, _) = await ConventionTargetsAsync(path, all, org, cancellationToken).ConfigureAwait(false);
        var drifted = 0;
        foreach (var target in targets)
        {
            var existence = await conventions.ExistsAsync(target, cancellationToken).ConfigureAwait(false);
            if (!existence.Exists)
            {
                await output.WriteLineAsync($"✗ {FolderName(target)}: Repo check failed ({existence.Status})").ConfigureAwait(false);
                drifted++;
                continue;
            }

            var report = await conventions.InspectAsync(target, existence.Slug!, cancellationToken).ConfigureAwait(false);
            drifted += report.AuditAligned ? 0 : 1;
            foreach (var line in report.AuditLines())
            {
                await output.WriteLineAsync(line).ConfigureAwait(false);
            }
        }

        await output.WriteLineAsync("\n" + new string('=', 50)).ConfigureAwait(false);
        if (targets.Count == 0)
        {
            // `--org rtkelly-labs` once reported "All 0 repositories are fully compliant" with no clones present.
            await output.WriteLineAsync("AUDIT FAILED: no repositories found to audit.").ConfigureAwait(false);
            return 1;
        }

        if (drifted == 0)
        {
            await output.WriteLineAsync($"SUCCESS: All {targets.Count} repositories are fully compliant with conventions!").ConfigureAwait(false);
            return 0;
        }

        await output.WriteLineAsync($"AUDIT FAILED: {drifted}/{targets.Count} repositories have configuration drift.").ConfigureAwait(false);
        await output.WriteLineAsync("Run align-repo-conventions enforce to apply fixes.").ConfigureAwait(false);
        return 1;
    }

    private async Task<int> EnforceAsync(string? path, bool all, string? org, bool dryRun, CancellationToken cancellationToken)
    {
        await output.WriteAsync(Panel("Repository Conventions: ENFORCE")).ConfigureAwait(false);
        var (targets, conventions, gitignores, policies) = await ConventionTargetsAsync(path, all, org, cancellationToken).ConfigureAwait(false);
        var table = new TextTable(
            "Enforcement Execution",
            [("Repository Path", false), ("GitHub Repo", false), ("Repo Settings", false), ("GitHub Ruleset", false), ("NPMRC Config", false),
                ("AGENTS.md Config", false), (".gitignore /temp/", false), ("TODO.md File", false), ("Pre-Push Hook", false), ("Pinned SHAs", false),
                ("ls-lint Config", false)]);
        var rulesetDrift = new List<(string Slug, IReadOnlyList<string> Drift)>();
        foreach (var target in targets)
        {
            var name = FolderName(target);
            var existence = await conventions.ExistsAsync(target, cancellationToken).ConfigureAwait(false);
            if (!existence.Exists)
            {
                _ = table.Add([name, existence.Slug ?? "N/A", .. SkippedCells]);
                continue;
            }

            var slug = existence.Slug!;
            var owner = RepoOwners.Inspect(policies, slug);
            if (!owner.IsAligned)
            {
                await output.WriteLineAsync($"✗ {slug}: refusing to enforce conventions — owner governance failure: {owner.Message}").ConfigureAwait(false);
                _ = table.Add([name, slug, "blocked_owner_governance", .. SkippedCells[1..]]);
                continue;
            }

            var settings = await conventions.AlignSettingsAsync(slug, dryRun, cancellationToken).ConfigureAwait(false);
            var ruleset = await EnforceRulesetAsync(conventions, policies, target, slug, dryRun, cancellationToken).ConfigureAwait(false);
            var key = await gitignores.PolicyKeyAsync(target, cancellationToken).ConfigureAwait(false);
            var gitignore = await gitignores.AlignAsync(target, dryRun, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (ruleset.Drift.Count > 0)
            {
                rulesetDrift.Add((slug, ruleset.Drift));
            }

            _ = table.Add(
                name,
                slug,
                settings.Status,
                ruleset.Status,
                RepoHygiene.AlignNpmrc(target, dryRun).Status,
                RepoHygiene.AlignAgentsMd(target, dryRun).Status,
                gitignore.Status,
                RepoHygiene.AlignTodoMd(target, dryRun).Status,
                RepoHygiene.AlignGitHooks(target, dryRun).Status,

                // Reported, not rewritten: pinning is pinact + Dependabot + zizmor in each repo.
                RepoHygiene.InspectPinnedShas(target).Status,
                RepoHygiene.AlignLsLint(target, policies, key, dryRun).Status);
        }

        await output.WriteAsync(table.Render()).ConfigureAwait(false);
        foreach (var (slug, drift) in rulesetDrift)
        {
            await output.WriteLineAsync($"\nRuleset drift on {slug} (dry run — would update):").ConfigureAwait(false);
            foreach (var line in drift)
            {
                await output.WriteLineAsync($"   - {line}").ConfigureAwait(false);
            }
        }

        await output.WriteLineAsync("\n✓ Conventions Enforcement Complete!").ConfigureAwait(false);
        return 0;
    }

    // Refuses to push a required check no pull_request job declares: the one mutation here with no local undo, since
    // recovery means editing the ruleset in the GitHub UI. See docs/repo-governance/required-checks.md.
    private async Task<AlignOutcome> EnforceRulesetAsync(
        Conventions conventions, RepoPolicies policies, string target, string slug, bool dryRun, CancellationToken cancellationToken)
    {
        var names = CheckNames.Inspect(target, slug, policies);
        if (names.Registered && names.Status == "no_pr_workflows")
        {
            await output.WriteLineAsync($"✗ {slug}: refusing to set required checks {PyRepr.List(names.Required)} — no pull_request workflow exists to report them")
                .ConfigureAwait(false);
            return new("blocked_no_pr_workflows", []);
        }

        if (names.Registered && names.Missing.Count > 0)
        {
            await output.WriteLineAsync($"✗ {slug}: refusing to set required checks {PyRepr.List(names.Missing)} — no pull_request job declares them").ConfigureAwait(false);
            await output.WriteLineAsync(
                $"   declared: {PyRepr.List(names.Declared.Keys.Order(StringComparer.Ordinal))} — run 'repo-governance check-names' and fix the map or the job name")
                .ConfigureAwait(false);
            return new("blocked_stale_check_names", []);
        }

        return await conventions.AlignRulesetAsync(slug, dryRun, cancellationToken).ConfigureAwait(false);
    }

    private async Task<(IReadOnlyList<string> Targets, Conventions Conventions, Gitignores Gitignores, RepoPolicies Policies)> ConventionTargetsAsync(
        string? path, bool all, string? org, CancellationToken cancellationToken)
    {
        var (policies, repos) = Governed();
        var targets = await repos.ResolveTargetsAsync(path, all, host.PersonalDir, host.Cwd, org, cancellationToken).ConfigureAwait(false);
        var gitignores = new Gitignores(new Git(proc, host.CodeRoot), EstateScopes.Load(host.ScopesFile), policies);
        return (targets, new Conventions(proc, Api, repos, gitignores, policies), gitignores, policies);
    }
}
