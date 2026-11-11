using System.CommandLine;
using Rig.Cli.Vault;
using Rig.Core.Governance;

namespace Rig.Cli.Governance;

/// <summary>
/// <c>apps</c>: the GitHub Apps declared under <c>github_apps</c> and the review tools' in-repo configs. Ported from
/// <c>rig.repo_governance.cli_audits</c>.
/// </summary>
internal sealed partial class GovernanceCommand
{
    private Command Apps()
    {
        var path = new Argument<string?>("target_dir") { Description = "Target repository directory path (defaults to org scan)", Arity = ArgumentArity.ZeroOrOne };
        var org = new Option<string>("--org", "-o") { Description = "Target GitHub organization or account", DefaultValueFactory = _ => "rtkelly-labs" };
        var audit = new Option<bool>("--audit") { Description = "Audit GitHub Apps installations and repo scoping" };
        var enforce = new Option<bool>("--enforce") { Description = "Sync missing repo connections and apply in-repo config" };
        var dryRun = new Option<bool>("--dry-run") { Description = "Preview API mutations without applying" };
        var coderabbit = new Option<bool>("--coderabbit") { Description = "Also audit/enforce .coderabbit.yaml across target repos" };
        var reviewTools = new Option<bool>("--review-tools") { Description = "Also audit/enforce code review tool configs (.coderabbit.yaml, codecov.yml)" };
        var command = new Command("apps", "Audits and governs GitHub Apps and PR reviewer integrations across organizations.")
        {
            path, org, audit, enforce, dryRun, coderabbit, reviewTools,
        };
        command.SetAction((parsed, cancellationToken) => AppsAsync(
            new(parsed.GetValue(path), parsed.GetValue(org)!, parsed.GetValue(audit), parsed.GetValue(enforce), parsed.GetValue(dryRun), parsed.GetValue(coderabbit), parsed.GetValue(reviewTools)),
            cancellationToken));
        return command;
    }

    private async Task<int> AppsAsync(AppsOptions options, CancellationToken cancellationToken)
    {
        var audit = options.Audit || !options.Enforce;
        var org = options.Org;
        var mode = options.Enforce ? options.DryRun ? "ENFORCE (DRY-RUN)" : "ENFORCE (SYNC)" : "AUDIT";
        await output.WriteAsync(Panel($"GitHub Apps Governance ({org}): {mode}")).ConfigureAwait(false);
        var (policies, repos) = Governed();
        var apps = new GitHubApps(Api, policies, (account, ct) => repos.DiscoverOrgReposAsync(account, host.PersonalDir, ct));
        IReadOnlyList<AppAudit> results;
        try
        {
            results = await apps.AuditAsync(org, cancellationToken).ConfigureAwait(false);
        }
        catch (GitHubReadException e)
        {
            await output.WriteLineAsync($"✗ GitHub lookup failed for '{org}': {e.Message}").ConfigureAwait(false);
            return 1;
        }

        if (results.Count == 0)
        {
            await output.WriteLineAsync($"No GitHub Apps declared for account/organization '{org}' in repo_policies.json.").ConfigureAwait(false);
            return 0;
        }

        var table = new TextTable($"Declared GitHub Apps for @{org}", [("App Name", false), ("Slug", false), ("Installed", false), ("Selection", false), ("Connected Repos", false), ("Status", false)]);
        var failures = 0;
        foreach (var result in results)
        {
            var drifted = !result.IsAligned && result.Required;
            failures += drifted ? 1 : 0;
            var connected = result.Selection == "all" ? "All repos" : $"{result.ActualRepos.Count} repos";
            if (result.MissingRepos.Count > 0)
            {
                connected += $" (missing: {result.MissingRepos.Count})";
            }

            _ = table.Add(result.AppName, result.AppSlug, result.Installed ? "✓ Yes" : "✗ No", result.Selection, connected, result.IsAligned ? "ALIGNED" : drifted ? "DRIFTED" : "UNINSTALLED");
        }

        await output.WriteAsync(table.Render()).ConfigureAwait(false);
        foreach (var result in results)
        {
            if (!result.Installed)
            {
                await output.WriteLineAsync($"\n⚠ {result.AppName} ({result.AppSlug}) is not installed on @{org}:").ConfigureAwait(false);
                await output.WriteLineAsync($"   Install link: {result.InstallPage}").ConfigureAwait(false);
                await output.WriteLineAsync("   GitHub security requires an organization admin to authorize third-party apps.").ConfigureAwait(false);
            }
            else if (result.MissingRepos.Count > 0)
            {
                await output.WriteLineAsync($"\n⚠ {result.AppName} is missing connections to: {string.Join(", ", result.MissingRepos)}").ConfigureAwait(false);
            }
        }

        var syncFailures = 0;
        if (options.Enforce)
        {
            await output.WriteLineAsync("\nSyncing GitHub App repository connections...").ConfigureAwait(false);
            foreach (var result in results)
            {
                if (!result.Installed)
                {
                    await output.WriteLineAsync($"Skipping {result.AppSlug}: install manually before connecting repos.").ConfigureAwait(false);
                    continue;
                }

                var sync = await apps.SyncAsync(org, result.AppSlug, options.DryRun, cancellationToken).ConfigureAwait(false);
                syncFailures += sync.Ok ? 0 : 1;
                foreach (var message in sync.Messages)
                {
                    await output.WriteLineAsync($"   · {message}").ConfigureAwait(false);
                }
            }
        }

        if (options.CodeRabbit || options.ReviewTools)
        {
            await ReviewConfigsAsync(options, policies, repos, cancellationToken).ConfigureAwait(false);
        }

        if (options.Enforce && syncFailures > 0)
        {
            await output.WriteLineAsync($"\nFAILED: {syncFailures} GitHub App sync(s) failed on @{org}.").ConfigureAwait(false);
            return 1;
        }

        if (!audit)
        {
            return 0;
        }

        if (failures > 0)
        {
            await output.WriteLineAsync($"\nFAILED: {failures} required GitHub App(s) drifted on @{org}.").ConfigureAwait(false);
            return 1;
        }

        await output.WriteLineAsync("\nSUCCESS: all declared GitHub Apps conform to policy.").ConfigureAwait(false);
        return 0;
    }

    // The --coderabbit and --review-tools sections, over the target repo or every repo of the org.
    private async Task ReviewConfigsAsync(AppsOptions options, RepoPolicies policies, GovernedRepos repos, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> targets = options.Path is { Length: > 0 } given
            ? [Path.GetFullPath(given, host.Cwd)]
            : await repos.ResolveTargetsAsync(null, true, host.PersonalDir, host.Cwd, options.Org, cancellationToken).ConfigureAwait(false);
        if (options.CodeRabbit)
        {
            await output.WriteLineAsync("\nAuditing/Enforcing .coderabbit.yaml configurations...").ConfigureAwait(false);
            foreach (var target in targets)
            {
                var line = options.Enforce ? ReviewToolConfigs.AlignCodeRabbit(target, options.DryRun).Message : ReviewToolConfigs.InspectCodeRabbit(target).Status;
                await output.WriteLineAsync($"   · {FolderName(target)}: {line}").ConfigureAwait(false);
            }
        }

        if (!options.ReviewTools)
        {
            return;
        }

        await output.WriteLineAsync("\nAuditing/Enforcing code review tool configurations (.coderabbit.yaml, codecov.yml)...").ConfigureAwait(false);
        foreach (var target in targets)
        {
            await output.WriteLineAsync($" {FolderName(target)}:").ConfigureAwait(false);
            IEnumerable<string> lines = options.Enforce
                ? ReviewToolConfigs.Align(target, options.Org, policies, options.DryRun).Select(tool => $"{tool.Tool}: {tool.Message}")
                : ReviewToolConfigs.Inspect(target).Select(row => $"{row.Tool}: {row.Status.Status}");
            foreach (var line in lines)
            {
                await output.WriteLineAsync($"   · {line}").ConfigureAwait(false);
            }
        }
    }

    private sealed record AppsOptions(string? Path, string Org, bool Audit, bool Enforce, bool DryRun, bool CodeRabbit, bool ReviewTools);
}
