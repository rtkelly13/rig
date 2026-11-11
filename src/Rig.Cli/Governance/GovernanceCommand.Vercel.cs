using System.CommandLine;
using System.Globalization;
using Rig.Cli.Vault;
using Rig.Core.Governance;
using Rig.Core.Processes;
using Rig.Core.Workspace;

namespace Rig.Cli.Governance;

/// <summary>
/// The Vercel audits: <c>vercel-gating</c> and <c>vercel-projects</c>. Both only read Vercel and GitHub. Ported from
/// <c>rig.repo_governance.cli_audits</c>.
/// </summary>
internal sealed partial class GovernanceCommand
{
    private const string NoTeam = "❌ No Vercel team id: set defaults.vercel.team_id or VERCEL_TEAM_ID";

    private IVercelApi VercelApi => vercel ?? throw new InvalidOperationException("No Vercel API was given to repo-governance.");

    private Command VercelProjectsCommand()
    {
        var command = new Command(
            "vercel-projects",
            "Audits which repository each Vercel project belongs to, against `vercel` in managed-projects.json. Each project's repository "
            + "comes from Vercel: its git link, or for a project deployed from the CLI, the managed repo on GitHub holding its newest "
            + "deployment's commit. Errors: a project nobody declares, one declared under the wrong repo, or a declared name Vercel does "
            + "not have. Warnings: a git link still naming a repo's old owner, or a project with no link or commit, which rests on its "
            + "declaration. Reports only; edit the config to fix.");
        command.SetAction((_, cancellationToken) => VercelProjectsAsync(cancellationToken));
        return command;
    }

    private Command VercelGatingCommand()
    {
        var path = new Argument<string?>("path") { Description = "Target repository directory path (defaults to CWD)", Arity = ArgumentArity.ZeroOrOne };
        var all = new Option<bool>("--all", "-a") { Description = "Check every Vercel project in the team, not just this repo's" };
        var days = new Option<int?>("--days") { Description = "Window for observed deployments (default: config)" };
        var command = new Command(
            "vercel-gating",
            "Audits that Vercel-linked repos do not create deployments for branch pushes. The Hobby quota is spent when a deployment is "
            + "created, not when it builds, so ignoreCommand skipping is not gating (docs/repo-governance/vercel-gating.md). Rules: "
            + "creation-gate (error), the project creates deployments for every branch push with no deny-all git.deploymentEnabled map; "
            + "allowlist-has-no-deny-all (warn), a map without '**': false; budget-leak (error), deployments were created off the allowed "
            + "refs in the window; production-stale (warn), the latest Ready production deployment does not match origin/main. Team id "
            + "and window come from defaults.vercel in config/repo_policies.json. The token is the Vercel CLI's own; VERCEL_TOKEN "
            + "overrides it.")
        {
            path, all, days,
        };
        command.SetAction((parsed, cancellationToken) => VercelGatingAsync(parsed.GetValue(path), parsed.GetValue(all), parsed.GetValue(days), cancellationToken));
        return command;
    }

    private async Task<int> VercelProjectsAsync(CancellationToken cancellationToken)
    {
        var teamId = TeamId(RepoPolicies.Load(host.PolicyFile).VercelGating(string.Empty));
        if (teamId.Length == 0)
        {
            await output.WriteLineAsync(NoTeam).ConfigureAwait(false);
            return 2;
        }

        IReadOnlyList<OwnershipFinding> findings;
        try
        {
            var registry = ManagedProjects.RegistryFile(host.Checkout, host.Cwd);
            var (claims, repos) = (VercelProjects.LoadClaims(registry), VercelProjects.LoadRepoSlugs(registry));
            var gating = new VercelGating(VercelApi, new Git(proc, host.CodeRoot));
            var owned = await VercelProjects.GatherAsync(gating, gitHub ?? new GhApi(proc), teamId, claims, repos, cancellationToken).ConfigureAwait(false);
            findings = VercelProjects.PlanFindings(owned, claims, repos);
        }
        catch (Exception e) when (e is VercelApiException or VercelClaimException)
        {
            await output.WriteLineAsync($"❌ {e.Message}").ConfigureAwait(false);
            return 2;
        }

        var table = new TextTable("Vercel project ownership", [("Vercel project", false), ("Managed project", false), ("Verdict", false), ("Detail", false)]);
        foreach (var finding in findings)
        {
            table.Add(finding.VercelProject, finding.Managed ?? "—", finding.Rule, finding.Detail);
        }

        await output.WriteAsync(table.Render()).ConfigureAwait(false);
        return findings.Any(f => f.Severity == "error") ? 1 : 0;
    }

    private async Task<int> VercelGatingAsync(string? path, bool all, int? days, CancellationToken cancellationToken)
    {
        string? onlyRepo = null;
        if (!all)
        {
            if (!string.IsNullOrEmpty(path) && !Path.Exists(Path.Combine(host.Cwd, path))
                && Path.Join(host.PersonalDir, Path.GetFileName(Path.TrimEndingDirectorySeparator(path))) is var named && Path.Exists(Path.Join(named, ".git")))
            {
                path = named;
            }

            var (_, repos) = Governed();
            var targets = await repos.ResolveTargetsAsync(path, false, host.PersonalDir, host.Cwd, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(path) && targets.Count == 0)
            {
                await output.WriteLineAsync($"❌ No repository found at {path}").ConfigureAwait(false);
                return 2;
            }

            onlyRepo = targets.Count > 0 ? Path.GetFileName(Path.TrimEndingDirectorySeparator(targets[0])) : null;
        }

        var rules = RepoPolicies.Load(host.PolicyFile).VercelGating(onlyRepo ?? string.Empty);
        var teamId = TeamId(rules);
        if (teamId.Length == 0)
        {
            await output.WriteLineAsync(NoTeam).ConfigureAwait(false);
            return 2;
        }

        var window = days is { } asked && asked != 0 ? asked : rules.WindowDays;
        IReadOnlyList<VercelProjectAudit> results;
        try
        {
            results = await new VercelGating(VercelApi, new Git(proc, host.CodeRoot))
                .AuditProjectsAsync(teamId, window, time.GetUtcNow().ToUnixTimeMilliseconds(), host.PersonalDir, onlyRepo, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (VercelApiException e)
        {
            await output.WriteLineAsync($"❌ {e.Message}").ConfigureAwait(false);
            return 2;
        }

        if (results.Count == 0)
        {
            await output.WriteLineAsync($"No linked Vercel projects for {onlyRepo ?? "the team"}").ConfigureAwait(false);
            return 0;
        }

        var table = new TextTable(
            string.Create(CultureInfo.InvariantCulture, $"Vercel deployment-creation gating (window: {window}d)"),
            [("Project", false), ("Repo", false), ("Creates on push", false), ("vercel.json gate", false), ("Verdict", false)]);
        var errors = 0;
        foreach (var (facts, findings) in results)
        {
            errors += findings.Count(f => f.Severity == "error");
            var denyAll = (facts.Deny ?? []).Any(p => p is "*" or "**");
            var gate = facts.Allow is null
                ? "—"
                : string.Create(CultureInfo.InvariantCulture, $"{facts.Allow.Count} allowed / {(denyAll ? "deny-all" : "NO FALLBACK")}");
            var create = facts.CreateDeployments switch
            {
                "enabled" => "all branches",
                "disabled" => "off",
                _ => "legacy",
            };
            var failing = findings.Where(f => f.Severity == "error").Select(f => f.Rule).ToList();
            var warning = findings.Where(f => f.Severity == "warn").Select(f => f.Rule).ToList();
            var verdict = failing.Count > 0 ? string.Join("; ", failing) : warning.Count > 0 ? string.Join("; ", warning) : "✓ gated";
            table.Add(facts.Name, facts.Repo ?? "—", create, gate, verdict);
        }

        await output.WriteAsync(table.Render()).ConfigureAwait(false);
        foreach (var (facts, findings) in results)
        {
            foreach (var finding in findings)
            {
                await output.WriteLineAsync($"  {finding.Rule} {facts.Name}: {finding.Detail}").ConfigureAwait(false);
            }
        }

        return errors > 0 ? 1 : 0;
    }

    private string TeamId(VercelGatingRules rules) => rules.TeamId is { Length: > 0 } team ? team : (environment ?? Environment.GetEnvironmentVariable)("VERCEL_TEAM_ID") ?? string.Empty;
}
