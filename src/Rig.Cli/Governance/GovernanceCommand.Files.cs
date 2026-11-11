using System.CommandLine;
using Rig.Core.Governance;
using Rig.Core.Processes;
using Rig.Core.Roots;

namespace Rig.Cli.Governance;

/// <summary>
/// The in-repo files governance writes: <c>license</c> and <c>sync-templates</c>. Ported from
/// <c>rig.repo_governance.cli_sync</c>.
/// </summary>
internal sealed partial class GovernanceCommand
{
    private Command License()
    {
        var path = PathArgument();
        var audit = new Option<bool>("--audit") { Description = "Audit license without mutating" };
        var enforce = new Option<bool>("--enforce") { Description = "Write/update canonical LICENSE file" };
        var spdx = new Option<bool>("--spdx") { Description = "Scan source files for SPDX headers" };
        var type = new Option<string?>("--type", "-t") { Description = "Expected license type (MIT, Apache-2.0, BSD-3-Clause, Unlicense)" };
        var holder = new Option<string?>("--holder") { Description = "Expected copyright holder" };
        var org = OrgOption();
        var all = AllOption();
        var command = new Command("license", "Audits and enforces canonical open source licenses and SPDX headers.") { path, audit, enforce, spdx, type, holder, org, all };
        command.SetAction((parsed, cancellationToken) => LicenseAsync(
            new(parsed.GetValue(path), parsed.GetValue(audit), parsed.GetValue(enforce), parsed.GetValue(spdx), parsed.GetValue(type), parsed.GetValue(holder), parsed.GetValue(org), parsed.GetValue(all)),
            cancellationToken));
        return command;
    }

    private async Task<int> LicenseAsync(LicenseOptions options, CancellationToken cancellationToken)
    {
        var audit = options.Audit || !options.Enforce;
        var (policies, repos) = Governed();
        var git = new Git(proc, host.CodeRoot);
        var scopes = EstateScopes.Load(host.ScopesFile);
        var targets = await repos.ResolveTargetsAsync(options.Path, options.All, host.PersonalDir, host.Cwd, options.Org, cancellationToken).ConfigureAwait(false);
        await output.WriteAsync(Panel($"License Governance ({options.Type ?? "policy config"})")).ConfigureAwait(false);
        var year = time.GetUtcNow().Year;
        var failures = 0;
        foreach (var target in targets)
        {
            var name = FolderName(target);
            var repo = await repos.GhRepoNameAsync(target, cancellationToken).ConfigureAwait(false) is { Length: > 0 } slug ? slug : name;
            var key = await PolicyKeys.OfAsync(git, scopes, target, cancellationToken).ConfigureAwait(false);
            var type = options.Type is { Length: > 0 } given ? given : policies.LicenseType(key);
            var holder = options.Holder is { Length: > 0 } named ? named : policies.CopyrightHolder(key);
            if (options.Enforce)
            {
                var aligned = Licenses.Align(target, type, holder, year, dryRun: false);
                await output.WriteLineAsync($"{(aligned.Changed ? "✓" : "·")} {name} ({repo}): {aligned.Message}").ConfigureAwait(false);
            }
            else
            {
                var result = Licenses.Inspect(target, type, holder, year);
                if (result.IsAligned)
                {
                    await output.WriteLineAsync($"✓ {name} ({repo}): {result.DetectedType} (valid)").ConfigureAwait(false);
                }
                else
                {
                    failures++;
                    await output.WriteLineAsync($"✗ {name} ({repo}): not aligned").ConfigureAwait(false);
                    foreach (var detail in result.Details)
                    {
                        await output.WriteLineAsync($"   - {detail}").ConfigureAwait(false);
                    }
                }
            }

            if (options.Spdx)
            {
                var scan = Licenses.AuditSpdx(target, type);
                await output.WriteLineAsync(scan.MissingSpdx.Count > 0
                    ? $"   SPDX: {scan.MissingSpdx.Count}/{scan.TotalFiles} source files missing headers"
                    : $"   SPDX: all {scan.TotalFiles} files compliant").ConfigureAwait(false);
            }
        }

        if (!audit)
        {
            return 0;
        }

        if (failures > 0)
        {
            await output.WriteLineAsync($"\nFAILED: {failures} repo(s) failed license compliance.").ConfigureAwait(false);
            return 1;
        }

        await output.WriteLineAsync("\nSUCCESS: all inspected repos have valid licenses.").ConfigureAwait(false);
        return 0;
    }

    private Command SyncTemplates()
    {
        var path = PathArgument();
        var audit = new Option<bool>("--audit") { Description = "Audit templates without mutating" };
        var apply = new Option<bool>("--apply") { Description = "Write/update community and issue templates" };
        var dryRun = new Option<bool>("--dry-run") { Description = "Preview template writes without saving" };
        var email = new Option<string?>("--email") { Description = "Contact email for Code of Conduct" };
        var org = OrgOption();
        var all = AllOption();
        var command = new Command("sync-templates", "Audits and synchronizes GitHub Issue Forms, PR templates, and community health files.")
        {
            path, audit, apply, dryRun, email, org, all,
        };
        command.SetAction((parsed, cancellationToken) => SyncTemplatesAsync(
            parsed.GetValue(path), parsed.GetValue(audit), parsed.GetValue(apply), parsed.GetValue(dryRun), parsed.GetValue(email), parsed.GetValue(org), parsed.GetValue(all), cancellationToken));
        return command;
    }

    private async Task<int> SyncTemplatesAsync(string? path, bool audit, bool apply, bool dryRun, string? email, string? org, bool all, CancellationToken cancellationToken)
    {
        audit = audit || !apply;
        var (policies, repos) = Governed();
        var git = new Git(proc, host.CodeRoot);
        var scopes = EstateScopes.Load(host.ScopesFile);
        var targets = await repos.ResolveTargetsAsync(path, all, host.PersonalDir, host.Cwd, org, cancellationToken).ConfigureAwait(false);
        await output.WriteAsync(Panel("Community & Issue Templates")).ConfigureAwait(false);
        var failures = 0;
        foreach (var target in targets)
        {
            var name = FolderName(target);
            var repo = await repos.GhRepoNameAsync(target, cancellationToken).ConfigureAwait(false) is { Length: > 0 } slug ? slug : name;
            if (apply)
            {
                var contact = email is { Length: > 0 }
                    ? email
                    : policies.ContactEmail(await PolicyKeys.OfAsync(git, scopes, target, cancellationToken).ConfigureAwait(false));
                var synced = OssTemplates.Sync(target, new(name, repo, contact), dryRun);
                await WriteGroupAsync(synced.Changed, $"{name} ({repo})", synced.Messages, "all templates up to date").ConfigureAwait(false);
                continue;
            }

            var result = OssTemplates.Inspect(target);
            if (result.IsFullyAligned)
            {
                await output.WriteLineAsync($"✓ {name} ({repo}): all {result.TotalTemplates} templates present").ConfigureAwait(false);
                continue;
            }

            failures++;
            await output.WriteLineAsync($"! {name} ({repo}): missing {result.Missing.Count}/{result.TotalTemplates} templates").ConfigureAwait(false);
            foreach (var missing in result.Missing)
            {
                await output.WriteLineAsync($"   - {missing}").ConfigureAwait(false);
            }
        }

        await SummaryAsync(audit, failures, $"{failures} repo(s) missing community templates.", "Run with --apply to write missing templates.", "all inspected repos have complete community templates.").ConfigureAwait(false);
        return 0;
    }

    private sealed record LicenseOptions(string? Path, bool Audit, bool Enforce, bool Spdx, string? Type, string? Holder, string? Org, bool All);
}
