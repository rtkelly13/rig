using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Nodes;
using Rig.Cli.Vault;
using Rig.Core.GitHub;
using Rig.Core.Processes;
using Rig.Core.Roots;

namespace Rig.Cli.Workspace;

/// <summary>Where <c>rig workspace</c> looks.</summary>
/// <param name="PersonalDir">The personal estate folder, the default <c>--root</c>.</param>
/// <param name="ScopesFile">The rig checkout's <c>config/estate-scopes.json</c>, naming the org folders beside it.</param>
/// <param name="Home">The home folder, which holds the repository name cache.</param>
internal sealed record WorkspaceHost(string PersonalDir, string ScopesFile, string Home);

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
        workspace.Subcommands.Add(Doctor());
        return workspace;
    }

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
        var estate = Path.GetFullPath(string.IsNullOrEmpty(root) ? host.PersonalDir : root);
        // The Python skipped the scan when the estate folder is missing; the org folders are only looked at beside it.
        var parents = Directory.Exists(estate) ? EstateScopes.Load(host.ScopesFile).RepoParentDirs(estate) : [];
        var resolver = new RepoResolver(new GhApi(proc), RepoResolver.DefaultCacheFile(host.Home), time);
        var checks = await RepoFolderCheck.EstateAsync(new Git(proc, estate), resolver, parents, refresh, cancellationToken).ConfigureAwait(false);
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
