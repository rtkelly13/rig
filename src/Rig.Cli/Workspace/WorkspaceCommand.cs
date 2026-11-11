using System.CommandLine;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Rig.Cli.Vault;
using Rig.Core.GitHub;
using Rig.Core.Processes;
using Rig.Core.Roots;
using Rig.Core.Workspace;

namespace Rig.Cli.Workspace;

/// <summary>Where <c>rig workspace</c> looks.</summary>
/// <param name="PersonalDir">The personal estate folder, the default <c>--root</c>.</param>
/// <param name="ScopesFile">The rig checkout's <c>config/estate-scopes.json</c>, naming the org folders beside it.</param>
/// <param name="Home">The home folder, which holds the repository name cache.</param>
/// <param name="ManagedProjectsFile">The rig checkout's <c>config/managed-projects.json</c>, the default <c>--config</c>.</param>
/// <param name="MachineConfigFile">This machine's <c>~/.workspace/config.json</c>, naming the projects it skips.</param>
internal sealed record WorkspaceHost(string PersonalDir, string ScopesFile, string Home, string ManagedProjectsFile, string MachineConfigFile);

/// <summary>
/// <c>rig workspace</c>: the subcommands ported from <c>src/rig/workspace/cli.py</c>. The group's other subcommands are
/// still Python's, so <see cref="Forwarding.Forwarded"/> hands any line naming one of them over.
/// </summary>
/// <param name="host">Where it looks.</param>
/// <param name="proc">Runs git and gh.</param>
/// <param name="time">The clock the repository name cache is aged by.</param>
/// <param name="output">Where reports go.</param>
internal sealed class WorkspaceCommand(WorkspaceHost host, IProc proc, TimeProvider time, TextWriter output)
{
    /// <summary>What <c>rig workspace</c> is.</summary>
    public const string Description = "Virtual monorepo estate management for the personal folder.";

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>The <c>workspace</c> group with its ported subcommands.</summary>
    public Command Build()
    {
        var workspace = new Command("workspace", Description);
        workspace.Subcommands.Add(Audit());
        workspace.Subcommands.Add(Status());
        workspace.Subcommands.Add(Clean());
        workspace.Subcommands.Add(Doctor());
        return workspace;
    }

    private static Option<string?> RootOption() => new("--root", "-r") { Description = "Path to personal estate root directory" };

    private static Option<string?> ConfigOption() => new("--config", "-c") { Description = "Path to managed-projects.json registry file" };

    private Command Audit()
    {
        var root = RootOption();
        var config = ConfigOption();
        var json = new Option<bool>("--json") { Description = "Output audit report as JSON" };
        var strict = new Option<bool>("--strict") { Description = "Fail with non-zero exit code if unmanaged items exist" };
        var audit = new Command("audit", "Audit the personal folder against registered managed projects.") { root, config, json, strict };
        audit.SetAction((parsed, cancellationToken) =>
            AuditAsync(parsed.GetValue(root), parsed.GetValue(config), parsed.GetValue(json), parsed.GetValue(strict), cancellationToken));
        return audit;
    }

    private Command Status()
    {
        var root = RootOption();
        var config = ConfigOption();
        var status = new Command("status", "Show an inventory overview of all managed projects in the estate.") { root, config };
        status.SetAction((parsed, cancellationToken) => StatusAsync(parsed.GetValue(root), parsed.GetValue(config), cancellationToken));
        return status;
    }

    private Command Clean()
    {
        var root = RootOption();
        var config = ConfigOption();
        var dryRun = new Option<bool>("--dry-run") { Description = "Preview cleanup actions without deleting (the default)" };
        var apply = new Option<bool>("--apply") { Description = "Delete the unmanaged items" };
        var force = new Option<bool>("--force") { Description = "Allow deletion of unmanaged git repos (only if clean)" };
        var clean = new Command("clean", "Enforce the invariant: remove unmanaged files and directories from the personal folder.") { root, config, dryRun, apply, force };
        clean.SetAction((parsed, cancellationToken) =>
            CleanAsync(parsed.GetValue(root), parsed.GetValue(config), !parsed.GetValue(apply) || parsed.GetValue(dryRun), parsed.GetValue(force), cancellationToken));
        return clean;
    }

    // Resolved like the Python's Path.resolve(): a linked personal folder's org folders sit beside its target.
    private string EstateDir(string? root) => PathResolution.Resolve(string.IsNullOrEmpty(root) ? host.PersonalDir : root);

    // The code root git hardens untrusted folders under: the estate's parent.
    private Git GitFor(string estate) => new(proc, Path.GetDirectoryName(estate) ?? estate);

    private async Task<(string Estate, IReadOnlyList<ManagedRepoSpec> Specs, EstateAuditResult Result, EstateAudit Audit)> RunAuditAsync(
        string? root, string? config, CancellationToken cancellationToken)
    {
        var estate = EstateDir(root);
        var specs = ManagedProjects.Load(string.IsNullOrEmpty(config) ? host.ManagedProjectsFile : PathResolution.Resolve(config));
        var audit = new EstateAudit(GitFor(estate), EstateScopes.Load(host.ScopesFile));
        var result = await audit.RunAsync(estate, specs, MachineConfig.SkipRepos(host.MachineConfigFile), cancellationToken).ConfigureAwait(false);
        return (estate, specs, result, audit);
    }

    private async Task<int> AuditAsync(string? root, string? config, bool json, bool strict, CancellationToken cancellationToken)
    {
        var (estate, _, audit, _) = await RunAuditAsync(root, config, cancellationToken).ConfigureAwait(false);
        var exit = strict && !audit.IsClean ? 1 : 0;
        if (json)
        {
            await output.WriteLineAsync(AuditReport(audit).ToJsonString(Indented)).ConfigureAwait(false);
            return exit;
        }

        var text = new StringBuilder();
        _ = text.Append("Personal Virtual Monorepo Audit: ").AppendLine(estate);
        Section(text, "✓ Managed Repositories", audit.ManagedRepos, item => item.Name + Parens(item.Remote), always: true);
        Section(text, "📦 Retained Archives", audit.RetainedArchives, item => $"{item.Name}{Parens(item.Remote)} ({item.Detail})");
        Section(text, "⌥ Linked Git Worktrees", audit.LinkedWorktrees, item => $"{item.Name} ({item.Detail})");
        Section(text, "⚙ System Orchestration Items", audit.KnownSystemItems, item => item.Name);
        Section(text, "✗ Missing Declared Clones", audit.MissingManagedRepos, spec => $"{spec.Name}: {spec.Repo} ({spec.Description})");
        Section(text, "✗ Blocked Declared Locations", audit.BlockedManagedRepos, item => $"{item.Name} at {item.Path} ({EstateAudit.Value(item.Kind)}: {item.Detail})");
        Section(text, "! Unmanaged Items In Estate", audit.UnmanagedItems, item => $"{item.Name} ({EstateAudit.Value(item.Kind)}: {item.Detail})");
        _ = text.AppendLine().AppendLine(audit.UnmanagedItems.Count > 0
            ? "Run 'workspace clean --dry-run' to inspect or register them in config/managed-projects.json"
            : "SUCCESS: No unmanaged files or directories in estate root.");
        await output.WriteAsync(text.ToString()).ConfigureAwait(false);
        return exit;
    }

    private async Task<int> StatusAsync(string? root, string? config, CancellationToken cancellationToken)
    {
        var (estate, specs, audit, _) = await RunAuditAsync(root, config, cancellationToken).ConfigureAwait(false);
        var present = audit.ManagedRepos.Select(item => item.Name).ToHashSet(StringComparer.Ordinal);
        var retained = audit.RetainedArchives.Select(item => item.Name).ToHashSet(StringComparer.Ordinal);
        var blocked = audit.BlockedManagedRepos.Select(item => item.Name).ToHashSet(StringComparer.Ordinal);
        var table = new TextTable($"Estate Inventory — {estate}", [("Repository", false), ("Status", false), ("Description", false)]);
        foreach (var spec in specs)
        {
            var status = !spec.Enabled ? (retained.Contains(spec.Name) ? "Retained (Archive)" : "Archived (Absent)")
                : present.Contains(spec.Name) ? "Present"
                : blocked.Contains(spec.Name) ? "Blocked"
                : "Missing";
            _ = table.Add(spec.Name, status, spec.Description.Length > 0 ? spec.Description : "--");
        }

        await output.WriteAsync(table.Render()).ConfigureAwait(false);
        await output.WriteLineAsync(
            $"\n{present.Count}/{specs.Count(spec => spec.Enabled)} active managed repos present · {audit.RetainedArchives.Count} retained archives · {audit.UnmanagedItems.Count} unmanaged items detected\n").ConfigureAwait(false);
        return 0;
    }

    private async Task<int> CleanAsync(string? root, string? config, bool dryRun, bool force, CancellationToken cancellationToken)
    {
        var (estate, _, result, audit) = await RunAuditAsync(root, config, cancellationToken).ConfigureAwait(false);
        if (result.UnmanagedItems.Count == 0)
        {
            await output.WriteLineAsync("✓ Estate is clean. Nothing to remove.").ConfigureAwait(false);
            return 0;
        }

        await output.WriteLineAsync($"{(dryRun ? "[DRY-RUN]" : "[APPLY]")} Found {result.UnmanagedItems.Count} unmanaged item(s) in {estate}:\n").ConfigureAwait(false);
        foreach (var item in result.UnmanagedItems)
        {
            var line = item.Kind == EstateItemKind.UnmanagedRepo && !force
                ? $"Skipped repo {item.Name} (requires --force to remove git clones)"
                : (await audit.CleanAsync(item, dryRun, cancellationToken).ConfigureAwait(false)).Message;
            await output.WriteLineAsync("  " + line).ConfigureAwait(false);
        }

        return 0;
    }

    private static string Parens(string? text) => string.IsNullOrEmpty(text) ? string.Empty : $" ({text})";

    // A titled list; empty ones are left out unless <paramref name="always"/>.
    private static void Section<T>(StringBuilder text, string title, IReadOnlyList<T> items, Func<T, string> line, bool always = false)
    {
        if (items.Count == 0 && !always)
        {
            return;
        }

        _ = text.AppendLine().AppendLine(CultureInfo.InvariantCulture, $"{title} ({items.Count})");
        foreach (var item in items)
        {
            _ = text.Append("  • ").AppendLine(line(item));
        }
    }

    // The JSON the Python printed, field for field.
    private static JsonObject AuditReport(EstateAuditResult audit) =>
        new()
        {
            ["estate_root"] = audit.EstateRoot,
            ["is_clean"] = audit.IsClean,
            ["total_items"] = audit.TotalItems,
            ["managed_repos"] = Items(audit.ManagedRepos, item => new() { ["name"] = item.Name, ["path"] = item.Path, ["remote"] = item.Remote }),
            ["linked_worktrees"] = Items(audit.LinkedWorktrees, item => new() { ["name"] = item.Name, ["path"] = item.Path }),
            ["unmanaged_items"] = Items(audit.UnmanagedItems, Located),
            ["missing_managed_repos"] = Items(audit.MissingManagedRepos, spec => new() { ["name"] = spec.Name, ["repo"] = spec.Repo, ["description"] = spec.Description }),
            ["blocked_managed_repos"] = Items(audit.BlockedManagedRepos, Located),
            ["known_system_items"] = Items(audit.KnownSystemItems, item => new() { ["name"] = item.Name, ["path"] = item.Path }),
            ["retained_archives"] = Items(audit.RetainedArchives, item => new() { ["name"] = item.Name, ["path"] = item.Path, ["remote"] = item.Remote, ["detail"] = item.Detail }),
        };

    private static JsonObject Located(EstateItem item) =>
        new() { ["name"] = item.Name, ["path"] = item.Path, ["kind"] = EstateAudit.Value(item.Kind), ["detail"] = item.Detail };

    private static JsonArray Items<T>(IEnumerable<T> items, Func<T, JsonObject> shape) => new([.. items.Select(item => (JsonNode)shape(item))]);

    private Command Doctor()
    {
        var root = new Option<string?>("--root", "-r") { Description = "Path to personal estate root directory" };
        var refresh = new Option<bool>("--refresh-cache") { Description = "Bypass cache and re-query GitHub GraphQL" };
        var json = new Option<bool>("--json") { Description = "Output doctor report as JSON" };
        var strict = new Option<bool>("--strict") { Description = "Fail with non-zero exit code if any repo mismatches exist" };
        var doctor = new Command("doctor", "Diagnose repository folder names and remotes against canonical GitHub names.") { root, refresh, json, strict };
        doctor.SetAction((parsed, cancellationToken) =>
            DoctorAsync(parsed.GetValue(root), parsed.GetValue(refresh), parsed.GetValue(json), parsed.GetValue(strict), cancellationToken));
        return doctor;
    }

    private async Task<int> DoctorAsync(string? root, bool refresh, bool json, bool strict, CancellationToken cancellationToken)
    {
        var estate = EstateDir(root);
        // The Python skipped the scan when the estate folder is missing; the org folders are only looked at beside it.
        var parents = Directory.Exists(estate) ? EstateScopes.Load(host.ScopesFile).RepoParentDirs(estate) : [];
        var resolver = new RepoResolver(new GhApi(proc), RepoResolver.DefaultCacheFile(host.Home), time);
        var checks = await RepoFolderCheck.EstateAsync(GitFor(estate), resolver, parents, refresh, cancellationToken).ConfigureAwait(false);
        var clean = !checks.Any(check => check.IsIssue);

        if (json)
        {
            await output.WriteLineAsync(Report(estate, clean, checks).ToJsonString(Indented)).ConfigureAwait(false);
        }
        else
        {
            var table = new TextTable(
                $"Estate Repository Doctor: {estate}",
                [("Folder", false), ("Canonical Upstream", false), ("Remote Slug", false), ("Status", false), ("Detail", false)]);
            foreach (var check in checks)
            {
                _ = table.Add(check.FolderName, check.Canonical?.CanonicalName ?? "-", check.Remote?.ToString() ?? "-", Label(check.Status), check.Detail);
            }

            await output.WriteLineAsync(table.Render()).ConfigureAwait(false);
            await output.WriteLineAsync(clean
                ? "✓ All estate repository folders match their canonical upstream names.\n"
                : "! Repository folder or remote mismatches detected. Review the report above.\n").ConfigureAwait(false);
        }

        return strict && !clean ? 1 : 0;
    }

    // The JSON the Python printed, field for field.
    private static JsonObject Report(string estate, bool clean, IReadOnlyList<RepoFolderCheck> checks) =>
        new()
        {
            ["estate_root"] = estate,
            ["is_clean"] = clean,
            ["total_repos"] = checks.Count,
            ["checks"] = new JsonArray([.. checks.Select(check => (JsonNode)new JsonObject
            {
                ["folder_name"] = check.FolderName,
                ["canonical_name"] = check.Canonical?.CanonicalName,
                ["owner"] = check.Remote?.Owner,
                ["remote_repo_name"] = check.Remote?.Name,
                ["status"] = Snake(check.Status),
                ["is_match"] = check.IsMatch,
                ["remote_matches_canonical"] = check.RemoteMatchesCanonical,
                ["is_renamed"] = check.Canonical?.IsRenamed ?? false,
                ["detail"] = check.Detail,
            })]),
        };

    private static string Snake(RepoFolderStatus status) => status switch
    {
        RepoFolderStatus.Ok => "ok",
        RepoFolderStatus.FolderRenamed => "folder_renamed",
        RepoFolderStatus.FolderMismatch => "folder_mismatch",
        RepoFolderStatus.RemoteOutdated => "remote_outdated",
        RepoFolderStatus.LocalOnly => "local_only",
        _ => "unresolved",
    };

    private static string Label(RepoFolderStatus status) => status switch
    {
        RepoFolderStatus.Ok => "✓ OK",
        RepoFolderStatus.FolderRenamed => "! Renamed (Outdated)",
        RepoFolderStatus.FolderMismatch => "✗ Mismatch",
        RepoFolderStatus.RemoteOutdated => "! Remote Outdated",
        RepoFolderStatus.LocalOnly => "- Local/Non-GH",
        _ => "? Unresolved",
    };
}
