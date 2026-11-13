using System.CommandLine;
using System.Text.Json.Nodes;
using Rig.Cli.Vault;
using Rig.Core.Governance;
using Rig.Core.Processes;
using Rig.Core.Roots;

namespace Rig.Cli.Governance;

/// <summary>
/// The GitHub-side resources: <c>plan</c> across all of them, and <c>sync-labels</c>, <c>environments</c> and
/// <c>pages</c> one at a time. Ported from the Python <c>cli_plan</c> and <c>cli_sync</c> modules.
/// </summary>
internal sealed partial class GovernanceCommand
{
    private IGitHubApi Api => gitHub ?? new GhApi(proc);

    private Command Plan()
    {
        var path = PathArgument();
        var all = new Option<bool>("--all", "-a") { Description = "Plan every managed repo under <code root>/personal" };
        var org = OrgOption();
        var apply = new Option<bool>("--apply") { Description = "Make the planned writes on GitHub" };
        var json = new Option<bool>("--json") { Description = "Emit the plan as JSON" };
        var command = new Command(
            "plan",
            "Plan repo settings, the ruleset, environments, labels and Pages against `config/repo_policies.json`. Read-only unless `--apply`. "
            + "A required check that no workflow reports is refused, never written: it would block every PR (see `check-names`).")
        {
            path, all, org, apply, json,
        };
        command.SetAction((parsed, cancellationToken) => PlanAsync(
            parsed.GetValue(path), parsed.GetValue(all), parsed.GetValue(org), parsed.GetValue(apply), parsed.GetValue(json), cancellationToken));
        return command;
    }

    private async Task<int> PlanAsync(string? path, bool all, string? org, bool apply, bool json, CancellationToken cancellationToken)
    {
        var (policies, repos) = Governed();
        var directories = await repos.ResolveTargetsAsync(path, all, host.PersonalDir, host.Cwd, org, cancellationToken).ConfigureAwait(false);
        var api = Api;
        var plans = new List<RepoPlan>();
        foreach (var directory in directories)
        {
            var slug = await repos.GhRepoNameAsync(directory, cancellationToken).ConfigureAwait(false);
            plans.Add(slug is null
                ? new(FolderName(directory), [], ["no GitHub repository found for this checkout"])
                : await GovernancePlan.PlanRepoAsync(policies, api, new(directory, slug), apply, cancellationToken: cancellationToken).ConfigureAwait(false));
        }

        if (json)
        {
            await output.WriteLineAsync(PyRepr.Json(new JsonArray([.. plans.Select(JsonNode (p) => p.ToJson())]))).ConfigureAwait(false);
        }
        else
        {
            var table = new TextTable(apply ? "Governance plan (applied)" : "Governance plan", [("Repo", false), ("Resource", false), ("Action", false), ("Subject", false), ("Detail", false)]);
            var rows = 0;
            foreach (var plan in plans)
            {
                foreach (var change in plan.Changes)
                {
                    table.Add(plan.Repo, change.Resource, change.Action, change.Subject, change.Detail);
                    rows++;
                }

                foreach (var error in plan.Errors)
                {
                    table.Add(plan.Repo, string.Empty, "error", string.Empty, error);
                    rows++;
                }
            }

            await output.WriteAsync(rows > 0 ? table.Render() : "Every repo matches its policy.\n").ConfigureAwait(false);
        }

        return GovernancePlan.ExitCode(plans, apply);
    }

    private Command SyncLabels()
    {
        var (path, org, audit, apply, dryRun, all) = SyncOptions("Audit labels without mutating", "Create and update labels on GitHub", "Preview label changes without applying");
        var command = new Command("sync-labels", "Audits and synchronizes the standard GitHub label taxonomy.") { path, org, audit, apply, dryRun, all };
        command.SetAction((parsed, cancellationToken) => SyncLabelsAsync(
            parsed.GetValue(path), parsed.GetValue(org), parsed.GetValue(audit), parsed.GetValue(apply), parsed.GetValue(dryRun), parsed.GetValue(all), cancellationToken));
        return command;
    }

    private async Task<int> SyncLabelsAsync(string? path, string? org, bool audit, bool apply, bool dryRun, bool all, CancellationToken cancellationToken)
    {
        audit = audit || !apply;
        var (policies, repos) = Governed();
        var targets = await ReposToSyncAsync(repos, path, all, org, cancellationToken).ConfigureAwait(false);
        await output.WriteAsync(Panel("GitHub Label Taxonomy Governance")).ConfigureAwait(false);
        var api = Api;
        var (failures, unreadable) = (0, 0);
        foreach (var (repo, display) in targets)
        {
            var taxonomy = policies.Labels(repo);
            if (apply)
            {
                if (await ReadOrReportAsync(display, repo, () => LabelTaxonomy.SyncAsync(api, repo, taxonomy, dryRun, cancellationToken)).ConfigureAwait(false) is not { } synced)
                {
                    unreadable++;
                    continue;
                }

                await WriteGroupAsync(synced.Changed, $"{display} ({repo})", synced.Messages, "all labels up to date").ConfigureAwait(false);
                continue;
            }

            if (await ReadOrReportAsync(display, repo, () => LabelTaxonomy.ReadAsync(api, repo, cancellationToken)).ConfigureAwait(false) is not { } labels)
            {
                unreadable++;
                continue;
            }

            var result = LabelTaxonomy.Inspect(taxonomy, labels);
            if (result.IsFullyAligned)
            {
                await output.WriteLineAsync($"✓ {display} ({repo}): all {result.TotalStandard} labels aligned").ConfigureAwait(false);
                continue;
            }

            failures++;
            await output.WriteLineAsync($"! {display} ({repo}): {result.Missing.Count} missing, {result.Mismatched.Count} mismatched").ConfigureAwait(false);
            foreach (var difference in result.Differences)
            {
                await output.WriteLineAsync($"   - {difference.LabelName} ({difference.Action}: {difference.Details})").ConfigureAwait(false);
            }
        }

        if (unreadable > 0)
        {
            return await UnreadableAsync(unreadable).ConfigureAwait(false);
        }

        await SummaryAsync(audit, failures, $"{failures} repo(s) drifted from label taxonomy.", "Run with --apply to synchronize labels.", "all inspected repos match standard label taxonomy.").ConfigureAwait(false);
        return 0;
    }

    private Command Environments()
    {
        var (path, org, audit, apply, dryRun, all) = SyncOptions("Audit environments without mutating", "Create/update declared environments on GitHub", "Preview environment changes without applying");
        var command = new Command(
            "environments",
            "Audits and synchronizes GitHub deployment environments declared in the profile. Environments are the approval gate release "
            + "workflows point at. A repo whose profile declares none is skipped — most repos in the estate do not publish.")
        {
            path, org, audit, apply, dryRun, all,
        };
        command.SetAction((parsed, cancellationToken) => EnvironmentsAsync(
            parsed.GetValue(path), parsed.GetValue(org), parsed.GetValue(audit), parsed.GetValue(apply), parsed.GetValue(dryRun), parsed.GetValue(all), cancellationToken));
        return command;
    }

    private async Task<int> EnvironmentsAsync(string? path, string? org, bool audit, bool apply, bool dryRun, bool all, CancellationToken cancellationToken)
    {
        audit = audit || !apply;
        var (policies, repos) = Governed();
        var targets = await ReposToSyncAsync(repos, path, all, org, cancellationToken).ConfigureAwait(false);
        await output.WriteAsync(Panel("GitHub Deployment Environment Governance")).ConfigureAwait(false);
        var api = Api;
        var (failures, unreadable) = (0, 0);
        foreach (var (repo, display) in targets)
        {
            var desired = policies.Environments(repo);
            if (desired.Count == 0)
            {
                await output.WriteLineAsync($"· {display} ({repo}): declares no environments, skipped").ConfigureAwait(false);
                continue;
            }

            if (apply)
            {
                if (await ReadOrReportAsync(display, repo, () => ReleaseEnvironments.SyncAsync(api, repo, desired, dryRun, cancellationToken)).ConfigureAwait(false) is not { } synced)
                {
                    unreadable++;
                    continue;
                }

                await WriteGroupAsync(synced.Actions.Count > 0, $"{display} ({repo})", synced.Log, "environments up to date").ConfigureAwait(false);
                continue;
            }

            if (await ReadOrReportAsync(display, repo, () => ReleaseEnvironments.ReadAsync(api, repo, cancellationToken)).ConfigureAwait(false) is not { } current)
            {
                unreadable++;
                continue;
            }

            var missing = desired.Select(e => e.Name).Where(name => !current.ContainsKey(name)).Distinct().Order(StringComparer.Ordinal).ToList();
            if (missing.Count > 0)
            {
                failures++;
                await output.WriteLineAsync($"! {display} ({repo}): missing {string.Join(", ", missing)}").ConfigureAwait(false);
            }
            else
            {
                var present = desired.Select(e => e.Name).Distinct().Order(StringComparer.Ordinal);
                await output.WriteLineAsync($"✓ {display} ({repo}): {string.Join(", ", present)} present").ConfigureAwait(false);
            }
        }

        if (unreadable > 0)
        {
            return await UnreadableAsync(unreadable).ConfigureAwait(false);
        }

        await SummaryAsync(audit, failures, $"{failures} repo(s) missing declared environments.", "Run with --apply to synchronize.", "all inspected repos have their declared environments.").ConfigureAwait(false);
        return 0;
    }

    private Command Pages()
    {
        var path = PathArgument();
        var audit = new Option<bool>("--audit") { Description = "Audit GitHub Pages configuration without mutating" };
        var apply = new Option<bool>("--apply") { Description = "Enforce GitHub Pages configuration on GitHub" };
        var dryRun = new Option<bool>("--dry-run") { Description = "Preview Pages changes without applying" };
        var all = AllOption();
        var command = new Command("pages", "Audits and synchronizes GitHub Pages and custom domain configuration declared in the profile.") { path, audit, apply, dryRun, all };
        command.SetAction((parsed, cancellationToken) => PagesAsync(
            parsed.GetValue(path), parsed.GetValue(audit), parsed.GetValue(apply), parsed.GetValue(dryRun), parsed.GetValue(all), cancellationToken));
        return command;
    }

    private async Task<int> PagesAsync(string? path, bool audit, bool apply, bool dryRun, bool all, CancellationToken cancellationToken)
    {
        audit = audit || !apply;
        var (policies, repos) = Governed();
        var targets = await repos.ResolveTargetsAsync(path, all, host.PersonalDir, host.Cwd, cancellationToken: cancellationToken).ConfigureAwait(false);
        await output.WriteAsync(Panel("GitHub Pages Governance")).ConfigureAwait(false);
        var api = Api;
        var (failures, unreadable) = (0, 0);
        foreach (var target in targets)
        {
            var name = FolderName(target);
            var repo = await repos.GhRepoNameAsync(target, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(repo))
            {
                await output.WriteLineAsync($"? {name}: no GitHub remote, skipped").ConfigureAwait(false);
                continue;
            }

            var policy = policies.Pages(repo);
            if (!policy.Enabled)
            {
                await output.WriteLineAsync($"· {name} ({repo}): Pages not enabled in profile, skipped").ConfigureAwait(false);
                continue;
            }

            if (apply)
            {
                var result = await PagesGovernance.AlignAsync(api, repo, policy, dryRun, cancellationToken).ConfigureAwait(false);
                var line = result.Status switch
                {
                    "aligned" => $"· {name} ({repo}): Pages up to date ({OrDefaultDomain(policy.Cname)})",
                    "dry_run" => $"~ {name} ({repo}): would configure Pages: {PyRepr.List(result.Actions)}",
                    "success" => $"✓ {name} ({repo}): Pages configured: {PyRepr.List(result.Actions)}",
                    _ => $"✗ {name} ({repo}): failed to align Pages ({result.Error ?? "None"})",
                };
                failures += result.Status is "aligned" or "dry_run" or "success" ? 0 : 1;
                await output.WriteLineAsync(line).ConfigureAwait(false);
                continue;
            }

            var current = await PagesGovernance.InspectAsync(api, repo, policy, cancellationToken).ConfigureAwait(false);
            if (current.Error is { } error)
            {
                unreadable++;
                await output.WriteLineAsync($"✗ {name} ({repo}): cannot read Pages ({error})").ConfigureAwait(false);
            }
            else if (current.IsAligned)
            {
                await output.WriteLineAsync($"✓ {name} ({repo}): Pages aligned ({OrDefaultDomain(current.Cname)})").ConfigureAwait(false);
            }
            else
            {
                failures++;
                var reasons = current.DriftReasons.Count > 0 ? string.Join(", ", current.DriftReasons) : "not aligned";
                await output.WriteLineAsync($"! {name} ({repo}): Pages drift detected: {reasons}").ConfigureAwait(false);
            }
        }

        if (unreadable > 0)
        {
            return await UnreadableAsync(unreadable).ConfigureAwait(false);
        }

        if (audit && failures > 0)
        {
            await SummaryAsync(audit, failures, $"{failures} repo(s) have Pages drift.", "Run with --apply to synchronize.", string.Empty).ConfigureAwait(false);
            return 1;
        }

        await SummaryAsync(audit, 0, string.Empty, string.Empty, "all inspected repos have aligned Pages configurations.").ConfigureAwait(false);
        return 0;
    }

    /// <summary>Rich's <c>Panel.fit</c> around <paramref name="title"/>, colour dropped.</summary>
    internal static string Panel(string title)
    {
        var border = new string('─', title.Length + 2);
        return $"╭{border}╮\n│ {title} │\n╰{border}╯\n";
    }

    private static Argument<string?> PathArgument() =>
        new("path") { Description = "Target repo directory (defaults to current directory)", Arity = ArgumentArity.ZeroOrOne };

    private static Option<string?> OrgOption() => new("--org", "-o") { Description = "Target GitHub organization (e.g. rtkelly-labs)" };

    private static Option<bool> AllOption() => new("--all", "-a") { Description = "Run across all personal repositories under <code root>/personal" };

    private static (Argument<string?> Path, Option<string?> Org, Option<bool> Audit, Option<bool> Apply, Option<bool> DryRun, Option<bool> All) SyncOptions(
        string audit, string apply, string dryRun) =>
        (PathArgument(), OrgOption(), new("--audit") { Description = audit }, new("--apply") { Description = apply }, new("--dry-run") { Description = dryRun }, AllOption());

    private static string FolderName(string directory) => Path.GetFileName(Path.TrimEndingDirectorySeparator(directory));

    private static string OrDefaultDomain(string? cname) => string.IsNullOrEmpty(cname) ? "default domain" : cname;

    private (RepoPolicies Policies, GovernedRepos Repos) Governed()
    {
        var policies = RepoPolicies.Load(host.PolicyFile);
        var scopes = EstateScopes.Load(host.ScopesFile);
        return (policies, new GovernedRepos(proc, new Git(proc, host.CodeRoot), scopes, policies));
    }

    // gh slug → display name, in target order; with --org and no local clone, the org's repos on GitHub.
    private async Task<IReadOnlyList<(string Repo, string Display)>> ReposToSyncAsync(
        GovernedRepos repos, string? path, bool all, string? org, CancellationToken cancellationToken)
    {
        var found = new List<(string Repo, string Display)>();
        void Put(string repo, string display)
        {
            var at = found.FindIndex(r => r.Repo == repo);
            if (at < 0)
            {
                found.Add((repo, display));
            }
            else
            {
                found[at] = (repo, display);
            }
        }

        foreach (var target in await repos.ResolveTargetsAsync(path, all, host.PersonalDir, host.Cwd, org, cancellationToken).ConfigureAwait(false))
        {
            if (await repos.GhRepoNameAsync(target, cancellationToken).ConfigureAwait(false) is { Length: > 0 } repo)
            {
                Put(repo, FolderName(target));
            }
        }

        if (!string.IsNullOrEmpty(org) && found.Count == 0)
        {
            foreach (var repo in await repos.DiscoverOrgReposAsync(org, host.PersonalDir, cancellationToken).ConfigureAwait(false))
            {
                Put(repo, repo.Split('/')[^1]);
            }
        }

        return found;
    }

    // Runs a GitHub read for one repo; a failed read is reported against the repo and comes back null, never as an empty state.
    private async Task<T?> ReadOrReportAsync<T>(string display, string repo, Func<Task<T>> read)
        where T : class
    {
        try
        {
            return await read().ConfigureAwait(false);
        }
        catch (GitHubReadException ex)
        {
            await output.WriteLineAsync($"✗ {display} ({repo}): {ex.Message}").ConfigureAwait(false);
            return null;
        }
    }

    private async Task<int> UnreadableAsync(int unreadable)
    {
        await output.WriteLineAsync($"\nIncomplete: {unreadable} repo(s) could not be read from GitHub.").ConfigureAwait(false);
        return 2;
    }

    private async Task WriteGroupAsync(bool changed, string label, IReadOnlyList<string> lines, string upToDate)
    {
        if (!changed)
        {
            await output.WriteLineAsync($"· {label}: {upToDate}").ConfigureAwait(false);
            return;
        }

        await output.WriteLineAsync($"✓ {label}:").ConfigureAwait(false);
        foreach (var line in lines)
        {
            await output.WriteLineAsync($"   • {line}").ConfigureAwait(false);
        }
    }

    private async Task SummaryAsync(bool audit, int failures, string drifted, string hint, string success)
    {
        if (!audit)
        {
            return;
        }

        if (failures > 0)
        {
            await output.WriteLineAsync($"\nAudit complete: {drifted}").ConfigureAwait(false);
            await output.WriteLineAsync(hint).ConfigureAwait(false);
        }
        else
        {
            await output.WriteLineAsync($"\nSUCCESS: {success}").ConfigureAwait(false);
        }
    }
}
