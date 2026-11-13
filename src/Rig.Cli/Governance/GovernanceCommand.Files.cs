using System.CommandLine;
using System.Text.Json.Nodes;
using Rig.Cli.Vault;
using Rig.Cli.Workspace;
using Rig.Core.Governance;
using Rig.Core.Policy;
using Rig.Core.Processes;
using Rig.Core.Roots;

namespace Rig.Cli.Governance;

/// <summary>
/// The in-repo files governance writes: <c>license</c>, <c>sync-templates</c> and <c>gitignore</c>. Ported from
/// <c>rig.repo_governance.cli_sync</c> and <c>cli_audits</c>.
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

    private Command Gitignore()
    {
        var path = PathArgument();
        var all = AllOption();
        var org = OrgOption();
        var language = new Option<string?>("--language", "-l") { Description = "Language preset(s) to enforce (e.g. 'python', 'dotnet', 'node')" };
        var apply = new Option<bool>("--apply") { Description = "Apply missing gitignore entries. Without this, audits only." };
        var dryRun = new Option<bool>("--dry-run") { Description = "Preview mutations without applying changes" };
        var json = new Option<bool>("--json") { Description = "Output results as JSON" };
        var command = new Command("gitignore", "Audit or synchronize common .gitignore rules across repositories.") { path, all, org, language, apply, dryRun, json };
        command.SetAction((parsed, cancellationToken) => GitignoreAsync(
            new(parsed.GetValue(path), parsed.GetValue(all), parsed.GetValue(org), parsed.GetValue(language), parsed.GetValue(apply), parsed.GetValue(dryRun), parsed.GetValue(json)),
            cancellationToken));
        return command;
    }

    private async Task<int> GitignoreAsync(GitignoreOptions options, CancellationToken cancellationToken)
    {
        var (policies, repos) = Governed();
        var targets = await repos.ResolveTargetsAsync(options.Path, options.All, host.PersonalDir, host.Cwd, options.Org, cancellationToken).ConfigureAwait(false);
        if (targets.Count == 0)
        {
            await output.WriteLineAsync("❌ No target repositories found.").ConfigureAwait(false);
            return 2;
        }

        IReadOnlyList<string>? languages = options.Language is { Length: > 0 } given
            ? [.. given.Split(',').Select(part => part.Trim()).Where(part => part.Length > 0)]
            : null;
        var gitignores = new Gitignores(new Git(proc, host.CodeRoot), EstateScopes.Load(host.ScopesFile), policies);
        var rows = new JsonArray();
        var mode = options.Apply ? options.DryRun ? "ENFORCE (DRY-RUN)" : "ENFORCE (APPLY)" : "AUDIT";
        var table = new TextTable("Repository .gitignore Status", [("Repository", false), ("Languages", false), ("Status", false), ("Details", false)]);
        var drift = false;
        foreach (var target in targets)
        {
            var name = FolderName(target);
            if (options.Apply)
            {
                var aligned = await gitignores.AlignAsync(target, options.DryRun, languages, cancellationToken).ConfigureAwait(false);
                rows.Add(WorkspaceCommand.Row(name, target, aligned));
                _ = table.Add(name, WorkspaceCommand.Languages(aligned.Languages), aligned.Status, aligned.Added.Count > 0 ? $"Added: {string.Join(", ", aligned.Added)}" : "Already configured");
                continue;
            }

            var inspected = await gitignores.InspectAsync(target, languages, cancellationToken).ConfigureAwait(false);
            drift |= !inspected.IsAligned;
            rows.Add(WorkspaceCommand.Row(name, target, inspected));
            _ = table.Add(name, WorkspaceCommand.Languages(inspected.Languages), inspected.Status, inspected.MissingLines.Count > 0 ? $"Missing: {string.Join(", ", inspected.MissingLines)}" : "Fully compliant");
        }

        if (options.Json)
        {
            await output.WriteLineAsync(PyJson.Dumps(rows)).ConfigureAwait(false);
            return drift ? 1 : 0;
        }

        await output.WriteAsync(Panel($"Common .gitignore: {mode}")).ConfigureAwait(false);
        await output.WriteAsync(table.Render()).ConfigureAwait(false);
        if (!drift)
        {
            return 0;
        }

        await output.WriteLineAsync("\nRun 'repo-governance gitignore --apply' or 'workspace gitignore sync' to fix.").ConfigureAwait(false);
        return 1;
    }

    private Command Editorconfig()
    {
        var path = PathArgument();
        var audit = new Option<bool>("--audit") { Description = "Audit .editorconfig without mutating" };
        var enforce = new Option<bool>("--enforce") { Description = "Write/update canonical .editorconfig file" };
        var dryRun = new Option<bool>("--dry-run") { Description = "Preview .editorconfig writes without saving" };
        var org = OrgOption();
        var all = AllOption();
        var command = new Command("editorconfig", "Audits and enforces canonical .editorconfig formatting and style rules across repositories.")
        {
            path, audit, enforce, dryRun, org, all,
        };
        command.SetAction((parsed, cancellationToken) => EditorconfigAsync(
            parsed.GetValue(path), parsed.GetValue(audit), parsed.GetValue(enforce), parsed.GetValue(dryRun), parsed.GetValue(org), parsed.GetValue(all), cancellationToken));
        return command;
    }

    private async Task<int> EditorconfigAsync(string? path, bool audit, bool enforce, bool dryRun, string? org, bool all, CancellationToken cancellationToken)
    {
        audit = audit || !enforce;
        var (_, repos) = Governed();
        var targets = await repos.ResolveTargetsAsync(path, all, host.PersonalDir, host.Cwd, org, cancellationToken).ConfigureAwait(false);
        await output.WriteAsync(Panel("EditorConfig Governance")).ConfigureAwait(false);
        var table = new TextTable("Repository .editorconfig Status", [("Repository", false), ("Kind", false), ("Status", false), ("Details", false)]);
        var failures = 0;
        foreach (var target in targets)
        {
            var name = FolderName(target);
            var isDotnet = ReviewToolConfigs.IsDotnetRepo(target);
            var kind = isDotnet ? ".NET" : "General";
            if (enforce)
            {
                var aligned = RepoHygiene.AlignEditorconfig(target, dryRun);
                var detail = aligned.Status switch
                {
                    "already_configured" => "Already configured",
                    "aligned" => dryRun ? "Would create canonical .editorconfig" : "Created canonical .editorconfig",
                    _ => aligned.Reason ?? aligned.Status,
                };
                _ = table.Add(name, kind, aligned.Status, detail);
                continue;
            }

            var inspected = RepoHygiene.InspectEditorconfig(target);
            if (!inspected.IsAligned)
            {
                failures++;
            }

            var status = inspected.IsAligned ? "✓ COMPLIANT" : "✗ DRIFT";
            var message = inspected.IsAligned ? "Valid .editorconfig" : inspected.Reason ?? inspected.Status;
            _ = table.Add(name, kind, status, message);
        }

        await output.WriteAsync(table.Render()).ConfigureAwait(false);
        if (audit && failures > 0)
        {
            await output.WriteLineAsync($"\nFAILED: {failures} repo(s) failed .editorconfig compliance. Run with --enforce to fix.").ConfigureAwait(false);
            return 1;
        }

        await output.WriteLineAsync("\nSUCCESS: All inspected repositories are compliant with .editorconfig rules.").ConfigureAwait(false);
        return 0;
    }

    private sealed record GitignoreOptions(string? Path, bool All, string? Org, string? Language, bool Apply, bool DryRun, bool Json);

    private sealed record LicenseOptions(string? Path, bool Audit, bool Enforce, bool Spdx, string? Type, string? Holder, string? Org, bool All);
}
