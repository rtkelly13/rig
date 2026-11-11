using System.CommandLine;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Rig.Cli.Vault;
using Rig.Core.GitHub;
using Rig.Core.Governance;
using Rig.Core.Policy;
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

/// <summary>The options <c>workspace gitignore audit</c> and <c>sync</c> share.</summary>
/// <param name="Root">The estate root, when not the personal folder.</param>
/// <param name="Repo">The one repository to look at, when not every registered one.</param>
/// <param name="Language">The language presets to enforce, comma-separated, when not each repo's own.</param>
/// <param name="Config">The registry, when not the checkout's.</param>
internal sealed record GitignoreArgs(string? Root, string? Repo, string? Language, string? Config);

/// <summary>
/// <c>rig workspace</c>: the subcommands ported from <c>src/rig/workspace/cli.py</c>. The group's other subcommands are
/// still Python's, so <see cref="Forwarding.Forwarded"/> hands any line naming one of them over.
/// </summary>
/// <param name="host">Where it looks.</param>
/// <param name="proc">Runs git and gh.</param>
/// <param name="time">The clock the repository name cache is aged by.</param>
/// <param name="output">Where reports go.</param>
/// <param name="http">Reads the package registries for <c>workspace packages</c>.</param>
/// <param name="orca">How <c>workspace orca</c> reaches Orca; this machine's when null.</param>
internal sealed class WorkspaceCommand(WorkspaceHost host, IProc proc, TimeProvider time, TextWriter output, HttpClient http, OrcaHost? orca = null)
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
        workspace.Subcommands.Add(Gitignore());
        workspace.Subcommands.Add(Ci());
        workspace.Subcommands.Add(new PackagesCommand(host, proc, http, time, output).Build());
        workspace.Subcommands.Add(new OrcaCommand(host, orca ?? OrcaHost.Current, proc, time, output).Build());
        workspace.Subcommands.Add(Sync("Synchronize root AGENTS.md, personal.code-workspace, and common gitignores."));
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

    // Git hardened under the configured code root (the personal folder's parent), never one derived from --root:
    // a --root inside other/ would otherwise move the untrusted folders out from under it.
    private Git CodeGit => new(proc, Path.GetDirectoryName(PathResolution.Resolve(host.PersonalDir)) ?? host.PersonalDir);

    private async Task<(string Estate, IReadOnlyList<ManagedRepoSpec> Specs, EstateAuditResult Result, EstateAudit Audit)> RunAuditAsync(
        string? root, string? config, CancellationToken cancellationToken)
    {
        var estate = EstateDir(root);
        var specs = ManagedProjects.Load(string.IsNullOrEmpty(config) ? host.ManagedProjectsFile : PathResolution.Resolve(config));
        var audit = new EstateAudit(CodeGit, EstateScopes.Load(host.ScopesFile));
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

    /// <summary><c>rig sync</c>: the same as <c>rig workspace sync</c>, at the top level.</summary>
    public Command BuildSync() => Sync("Synchronize estate index (AGENTS.md) and personal.code-workspace.");

    private Command Sync(string description)
    {
        var root = RootOption();
        var config = ConfigOption();
        var gitignore = new Option<bool>("--gitignore") { Description = "Synchronize common gitignores across managed projects (the default)" };
        var noGitignore = new Option<bool>("--no-gitignore") { Description = "Leave the managed projects' gitignores alone" };
        var sync = new Command("sync", description) { root, config, gitignore, noGitignore };
        sync.SetAction((parsed, cancellationToken) =>
            SyncAsync(parsed.GetValue(root), parsed.GetValue(config), parsed.GetValue(gitignore) || !parsed.GetValue(noGitignore), cancellationToken));
        return sync;
    }

    private Command Gitignore()
    {
        var group = new Command("gitignore", "Audit and synchronize common .gitignore rules across estate repositories.");

        var root = RootOption();
        var repo = new Option<string?>("--repo") { Description = "Check only this repository name" };
        var language = LanguageOption();
        var config = ConfigOption();
        var json = new Option<bool>("--json") { Description = "Output audit report as JSON" };
        var strict = new Option<bool>("--strict") { Description = "Fail with non-zero exit code if drift is detected" };
        var audit = new Command("audit", "Audit common .gitignore rules across estate repositories.") { root, repo, language, config, json, strict };
        audit.SetAction((parsed, cancellationToken) => GitignoreAuditAsync(
            new(parsed.GetValue(root), parsed.GetValue(repo), parsed.GetValue(language), parsed.GetValue(config)), parsed.GetValue(json), parsed.GetValue(strict), cancellationToken));
        group.Subcommands.Add(audit);

        var syncRoot = RootOption();
        var syncRepo = new Option<string?>("--repo") { Description = "Synchronize only this repository name" };
        var syncLanguage = LanguageOption();
        var syncConfig = ConfigOption();
        var dryRun = new Option<bool>("--dry-run") { Description = "Preview mutations without applying changes" };
        var syncJson = new Option<bool>("--json") { Description = "Output sync results as JSON" };
        var sync = new Command("sync", "Synchronize common .gitignore rules across estate repositories.") { syncRoot, syncRepo, syncLanguage, syncConfig, dryRun, syncJson };
        sync.SetAction((parsed, cancellationToken) => GitignoreSyncAsync(
            new(parsed.GetValue(syncRoot), parsed.GetValue(syncRepo), parsed.GetValue(syncLanguage), parsed.GetValue(syncConfig)), parsed.GetValue(dryRun), parsed.GetValue(syncJson), cancellationToken));
        group.Subcommands.Add(sync);
        return group;
    }

    private static Option<string?> LanguageOption() =>
        new("--language", "-l") { Description = "Language preset(s) to enforce (e.g. 'python', 'dotnet', 'node')" };

    // The registry's projects; --config names another registry.
    private IReadOnlyList<ManagedRepoSpec> Specs(string? config) =>
        ManagedProjects.Load(string.IsNullOrEmpty(config) ? host.ManagedProjectsFile : PathResolution.Resolve(config));

    // The repo policy beside the checkout's registry, not beside a --config one.
    private Gitignores GitignoresFor(EstateScopes scopes) =>
        new(CodeGit, scopes, RepoPolicies.Load(Path.Join(Path.GetDirectoryName(host.ManagedProjectsFile), RepoPolicies.FileName)));

    private (string Estate, Gitignores Gitignores, IReadOnlyList<string> Repos, IReadOnlyList<string>? Languages) GitignoreTargets(GitignoreArgs args)
    {
        var estate = EstateDir(args.Root);
        // Every registered name, retired ones too; an empty registry checks every clone.
        IReadOnlyList<string> repos = string.IsNullOrEmpty(args.Repo) ? [.. Specs(args.Config).Select(spec => spec.Name)] : [args.Repo];
        IReadOnlyList<string>? languages = string.IsNullOrEmpty(args.Language)
            ? null
            : [.. args.Language.Split(',').Select(part => part.Trim()).Where(part => part.Length > 0)];
        return (estate, GitignoresFor(EstateScopes.Load(host.ScopesFile)), repos, languages);
    }

    private async Task<int> GitignoreAuditAsync(GitignoreArgs args, bool json, bool strict, CancellationToken cancellationToken)
    {
        var (estate, gitignores, repos, languages) = GitignoreTargets(args);
        var results = await gitignores.AuditEstateAsync(estate, repos, languages, cancellationToken).ConfigureAwait(false);
        var drift = results.Any(r => !r.Result.IsAligned);
        var exit = strict && drift ? 1 : 0;
        if (json)
        {
            await output.WriteLineAsync(PyJson.Dumps(new JsonArray([.. results.Select(r => (JsonNode)Row(r.RepoName, r.RepoPath, r.Result))]))).ConfigureAwait(false);
            return exit;
        }

        var table = new TextTable("Managed Repositories .gitignore Alignment", [("Repository", false), ("Languages", false), ("Status", false), ("Missing Patterns", false)]);
        foreach (var (name, _, result) in results)
        {
            _ = table.Add(name, Languages(result.Languages), result.Status, result.MissingLines.Count > 0 ? string.Join(", ", result.MissingLines) : "None (Compliant)");
        }

        await output.WriteLineAsync($"Estate .gitignore Audit: {estate}").ConfigureAwait(false);
        await output.WriteAsync(table.Render()).ConfigureAwait(false);
        await output.WriteLineAsync(drift
            ? "\nRun 'workspace gitignore sync' or 'workspace sync' to align missing rules."
            : "\n✓ All repositories have compliant common .gitignore entries.").ConfigureAwait(false);
        return exit;
    }

    private async Task<int> GitignoreSyncAsync(GitignoreArgs args, bool dryRun, bool json, CancellationToken cancellationToken)
    {
        var (estate, gitignores, repos, languages) = GitignoreTargets(args);
        var results = await gitignores.SyncEstateAsync(estate, repos, dryRun, languages, cancellationToken).ConfigureAwait(false);
        if (json)
        {
            await output.WriteLineAsync(PyJson.Dumps(new JsonArray([.. results.Select(r => (JsonNode)Row(r.RepoName, r.RepoPath, r.Result))]))).ConfigureAwait(false);
            return 0;
        }

        var table = new TextTable("Synchronization Results", [("Repository", false), ("Languages", false), ("Action", false), ("Patterns Added", false)]);
        foreach (var (name, _, result) in results)
        {
            _ = table.Add(name, Languages(result.Languages), result.Status, result.Added.Count > 0 ? string.Join(", ", result.Added) : "None (already compliant)");
        }

        var updated = results.Count(r => r.Result.Status == "aligned");
        await output.WriteLineAsync($"Estate .gitignore Sync ({(dryRun ? "DRY-RUN" : "APPLY")}): {estate}").ConfigureAwait(false);
        await output.WriteAsync(table.Render()).ConfigureAwait(false);
        await output.WriteLineAsync(updated > 0
            ? $"\n✓ {(dryRun ? "Would update" : "Updated")} .gitignore in {updated} repository/repositories."
            : "\n✓ All repositories already have compliant .gitignore files.").ConfigureAwait(false);
        return 0;
    }

    private async Task<int> SyncAsync(string? root, string? config, bool gitignore, CancellationToken cancellationToken)
    {
        var estate = EstateDir(root);
        var specs = Specs(config);
        var scopes = EstateScopes.Load(host.ScopesFile);
        var sync = new EstateSync(scopes);
        await output.WriteLineAsync($"Synchronizing estate orchestration in {estate}...\n").ConfigureAwait(false);

        var agents = await sync.SyncRouterAsync(estate, specs, cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync($"  ✓ Generated {Path.GetFileName(agents)} ({specs.Count} managed projects mapped)").ConfigureAwait(false);
        var workspace = await sync.SyncCodeWorkspaceAsync(estate, specs, cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync($"  ✓ Synchronized {Path.GetFileName(workspace)}").ConfigureAwait(false);

        if (gitignore)
        {
            var results = await GitignoresFor(scopes).SyncEstateAsync(estate, [.. specs.Select(spec => spec.Name)], cancellationToken: cancellationToken).ConfigureAwait(false);
            var updated = results.Where(r => r.Result.Status == "aligned").Select(r => r.RepoName).ToList();
            await output.WriteLineAsync(updated.Count > 0
                ? $"  ✓ Synchronized .gitignore across {results.Count} repos ({updated.Count} updated: {string.Join(", ", updated)})"
                : $"  ✓ Verified .gitignore across {results.Count} repos (all compliant)").ConfigureAwait(false);
        }

        await output.WriteLineAsync("\nEstate orchestration successfully synchronized.").ConfigureAwait(false);
        return 0;
    }

    internal static string Languages(IReadOnlyList<string> languages) => languages.Count > 0 ? string.Join(", ", languages) : "common";

    // The row the Python's --json printed: the repo, then the result's fields in their declared order.
    internal static JsonObject Row(string name, string path, GitignoreInspection result) =>
        new()
        {
            ["repo_name"] = name,
            ["repo_path"] = path,
            ["status"] = result.Status,
            ["is_aligned"] = result.IsAligned,
            ["languages"] = Strings(result.Languages),
            ["missing"] = Strings(result.Missing),
            ["missing_lines"] = Strings(result.MissingLines),
            ["file"] = result.File,
        };

    internal static JsonObject Row(string name, string path, GitignoreAlignment result) =>
        new()
        {
            ["repo_name"] = name,
            ["repo_path"] = path,
            ["status"] = result.Status,
            ["file"] = result.File,
            ["languages"] = Strings(result.Languages),
            ["added"] = Strings(result.Added),
        };

    private static JsonArray Strings(IEnumerable<string> items) => new([.. items.Select(item => (JsonNode)item)]);

    private Command Ci()
    {
        var group = new Command("ci", "Audit and analyze CI utilisation and runner costs across the estate.");
        var repo = new Option<string?>("--repo", "-r") { Description = "Specific repository name (e.g. rig)" };
        var limit = new Option<int>("--limit", "-l") { Description = "Number of recent workflow runs to evaluate per repo", DefaultValueFactory = _ => 10 };
        var config = ConfigOption();
        var audit = new Command("audit", "Audit recent CI runs and billable runner minutes.") { repo, limit, config };
        audit.SetAction((parsed, cancellationToken) => CiAuditAsync(parsed.GetValue(repo), parsed.GetValue(limit), parsed.GetValue(config), cancellationToken));
        group.Subcommands.Add(audit);
        return group;
    }

    // A --repo matches a project's name exactly or any part of its remote, ignoring case.
    private async Task<int> CiAuditAsync(string? repo, int limit, string? config, CancellationToken cancellationToken)
    {
        var specs = Specs(config);
        if (!string.IsNullOrEmpty(repo))
        {
            specs = [.. specs.Where(spec => string.Equals(spec.Name, repo, StringComparison.OrdinalIgnoreCase)
                || spec.Repo.Contains(repo, StringComparison.OrdinalIgnoreCase))];
            if (specs.Count == 0)
            {
                await output.WriteLineAsync($"Error: repository '{repo}' not found in managed projects.").ConfigureAwait(false);
                return 1;
            }
        }

        var telemetry = new CiTelemetry(new GhApi(proc));
        var table = new TextTable(
            "Estate CI Utilisation Audit",
            [("Repository", false), ("Workflow", false), ("Event / Branch", false), ("Conclusion", false), ("Wall Time", true), ("Billable Equiv.", true)]);
        var billable = 0.0;
        var checkedRuns = 0;
        foreach (var spec in specs)
        {
            if (RepoSlug.FromRemote(spec.Repo) is not { } slug)
            {
                continue;
            }

            foreach (var run in await telemetry.RecentAsync(spec.Name, slug.FullName, limit, cancellationToken).ConfigureAwait(false))
            {
                billable += run.BillableSeconds;
                checkedRuns++;
                var branch = run.HeadBranch.Length > 18 ? run.HeadBranch[..18] : run.HeadBranch;
                _ = table.Add(spec.Name, run.WorkflowName, $"{run.Event} ({branch})", run.Conclusion, Minutes(run.DurationSeconds), Minutes(run.BillableSeconds));
            }
        }

        await output.WriteAsync(table.Render()).ConfigureAwait(false);
        await output.WriteLineAsync(string.Create(
            CultureInfo.InvariantCulture,
            $"\nTotal Evaluated: {checkedRuns} runs | Total Billable Minutes Equivalent: {billable / 60.0:F1}m\n")).ConfigureAwait(false);
        return 0;
    }

    private static string Minutes(double seconds) =>
        string.Create(CultureInfo.InvariantCulture, $"{(int)(seconds / 60)}m {(int)(seconds % 60)}s");

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
        var checks = await RepoFolderCheck.EstateAsync(CodeGit, resolver, parents, refresh, cancellationToken).ConfigureAwait(false);
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
