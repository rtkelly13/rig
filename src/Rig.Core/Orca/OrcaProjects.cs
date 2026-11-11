using System.Text.Json;
using System.Text.Json.Nodes;

namespace Rig.Core.Orca;

/// <summary>A registered project, as <c>repo.list</c> reports it.</summary>
/// <param name="Id">Orca's id for the project.</param>
/// <param name="Path">The project's main checkout.</param>
/// <param name="DisplayName">The name Orca shows; empty when none was set.</param>
/// <param name="GroupId">The project group it sits in; null at the top level.</param>
/// <param name="CanonicalKey">The origin remote as Orca keys it, e.g. <c>github.com/owner/name</c>.</param>
public sealed record OrcaProject(string Id, string Path, string DisplayName = "", string? GroupId = null, string? CanonicalKey = null);

/// <summary>A project group, as <c>projectGroup.list</c> reports it.</summary>
/// <param name="Id">Orca's id for the group.</param>
/// <param name="Name">The group's name.</param>
/// <param name="ParentId">The group it sits in; null at the top level.</param>
/// <param name="ParentPath">The folder a folder-scan group was made from; null for one made by hand.</param>
public sealed record OrcaGroup(string Id, string Name, string? ParentId = null, string? ParentPath = null);

/// <summary>A folder workspace, as <c>folderWorkspace.list</c> reports it. It lives in a group, and goes with it.</summary>
/// <param name="Id">Orca's id for the folder workspace.</param>
/// <param name="GroupId">The group it lives in.</param>
public sealed record OrcaFolderWorkspace(string Id, string? GroupId = null);

/// <summary>A workspace, as <c>worktree.list</c> reports it: a project's main checkout or a linked worktree.</summary>
/// <param name="Id">Orca's id for the worktree.</param>
/// <param name="Path">The worktree's folder.</param>
/// <param name="RepoId">The project it belongs to.</param>
/// <param name="Branch"><c>refs/heads/&lt;name&gt;</c>, or null when HEAD is detached.</param>
/// <param name="IsMain">It is the project's main checkout.</param>
/// <param name="DisplayName">The name Orca shows.</param>
/// <param name="LastActivityMs">Milliseconds since the epoch.</param>
public sealed record OrcaWorktree(
    string Id,
    string Path,
    string RepoId = "",
    string? Branch = null,
    bool IsMain = false,
    string? DisplayName = null,
    double? LastActivityMs = null);

/// <summary>What <c>worktree.ps</c> reports running in one worktree.</summary>
/// <param name="WorktreeId">The worktree.</param>
/// <param name="AgentStates">Each agent pane's state: <c>working</c>, <c>blocked</c>, <c>waiting</c> or <c>done</c>.</param>
public sealed record OrcaActivity(string WorktreeId, IReadOnlyList<string?> AgentStates);

/// <summary>
/// The project, group and worktree methods of Orca's runtime. Ported from <c>src/rig/estate/orca_runtime.py</c>. A
/// field of the wrong type in a required place shows up as <see cref="OrcaUnavailableException"/>, never as a guess.
/// </summary>
public static class OrcaProjects
{
    /// <summary>Every registered project.</summary>
    public static async Task<IReadOnlyList<OrcaProject>> ProjectsAsync(this OrcaRuntime runtime, CancellationToken cancellationToken = default) =>
        Read(await Call(runtime, "repo.list", null, cancellationToken).ConfigureAwait(false), "repo.list", result => List(result, "repos", Project));

    /// <summary>Every project group.</summary>
    public static async Task<IReadOnlyList<OrcaGroup>> GroupsAsync(this OrcaRuntime runtime, CancellationToken cancellationToken = default) =>
        Read(await Call(runtime, "projectGroup.list", null, cancellationToken).ConfigureAwait(false), "projectGroup.list", result => List(result, "groups", Group));

    /// <summary>Every folder workspace.</summary>
    public static async Task<IReadOnlyList<OrcaFolderWorkspace>> FolderWorkspacesAsync(this OrcaRuntime runtime, CancellationToken cancellationToken = default) =>
        Read(
            await Call(runtime, "folderWorkspace.list", null, cancellationToken).ConfigureAwait(false),
            "folderWorkspace.list",
            result => List(result, "folderWorkspaces", raw => new OrcaFolderWorkspace(Required(raw, "id"), LenientString(raw, "projectGroupId"))));

    /// <summary>Registers the checkout at <paramref name="path"/>.</summary>
    public static async Task<OrcaProject> AddProjectAsync(this OrcaRuntime runtime, string path, CancellationToken cancellationToken = default) =>
        Read(
            await Call(runtime, "repo.add", new JsonObject { ["path"] = path }, cancellationToken).ConfigureAwait(false),
            "repo.add",
            result => Project(OrcaJson.Object(result, "repo") ?? throw new InvalidDataException("repo: missing")));

    /// <summary>Creates a group named <paramref name="name"/> inside <paramref name="parentId"/>, or at the top level for null.</summary>
    public static async Task<OrcaGroup> CreateGroupAsync(this OrcaRuntime runtime, string name, string? parentId, CancellationToken cancellationToken = default) =>
        Read(
            await Call(runtime, "projectGroup.create", new JsonObject { ["name"] = name, ["parentGroupId"] = parentId }, cancellationToken).ConfigureAwait(false),
            "projectGroup.create",
            result => Group(OrcaJson.Object(result, "group") ?? throw new InvalidDataException("group: missing")));

    /// <summary>Unregisters a project. Orca stops its terminals and deletes no files.</summary>
    public static async Task RemoveProjectAsync(this OrcaRuntime runtime, string projectId, CancellationToken cancellationToken = default) =>
        _ = Read(
            await Call(runtime, "repo.rm", new JsonObject { ["repo"] = $"id:{projectId}" }, cancellationToken).ConfigureAwait(false),
            "repo.rm",
            result => OrcaJson.Bool(result, "removed") ?? false);

    /// <summary>Deletes a group and its subgroups; Orca moves their projects to the top level and drops their folder workspaces.</summary>
    public static async Task<bool> DeleteGroupAsync(this OrcaRuntime runtime, string groupId, CancellationToken cancellationToken = default) =>
        Read(
            await Call(runtime, "projectGroup.delete", new JsonObject { ["groupId"] = groupId }, cancellationToken).ConfigureAwait(false),
            "projectGroup.delete",
            result => OrcaJson.Bool(result, "deleted") ?? false);

    /// <summary>Moves a project into a group, or to the top level for null. It lands last in its new place.</summary>
    public static async Task<OrcaProject> MoveProjectAsync(this OrcaRuntime runtime, string projectId, string? groupId, CancellationToken cancellationToken = default) =>
        Read(
            await Call(runtime, "projectGroup.moveProject", new JsonObject { ["repo"] = $"id:{projectId}", ["groupId"] = groupId }, cancellationToken).ConfigureAwait(false),
            "projectGroup.moveProject",
            result => Project(OrcaJson.Object(result, "repo") ?? throw new InvalidDataException("repo: missing")));

    /// <summary>Every worktree of every project, main checkouts included.</summary>
    public static async Task<IReadOnlyList<OrcaWorktree>> WorktreesAsync(this OrcaRuntime runtime, CancellationToken cancellationToken = default) =>
        Read(await Call(runtime, "worktree.list", new JsonObject(), cancellationToken).ConfigureAwait(false), "worktree.list", result => List(result, "worktrees", Worktree));

    /// <summary>Terminals and agents per worktree. Throws rather than answer from a truncated list.</summary>
    public static async Task<IReadOnlyList<OrcaActivity>> ActivityAsync(this OrcaRuntime runtime, CancellationToken cancellationToken = default)
    {
        var result = await Call(runtime, "worktree.ps", new JsonObject(), cancellationToken).ConfigureAwait(false);
        var (truncated, activity) = Read(result, "worktree.ps", reply => (LenientBool(reply, "truncated"), List(reply, "worktrees", Activity)));
        return truncated == true
            ? throw new OrcaUnavailableException("Orca truncated worktree.ps; refusing to judge worktrees it did not report")
            : activity;
    }

    /// <summary>
    /// Removes a worktree the way Orca's own delete does; true when Orca says it is gone. Orca stops its terminals and
    /// runs <c>git worktree remove</c> without force, so a dirty tree is refused, and deletes the branch unless git calls
    /// it unmerged. False means Orca accepted the request but has not finished it, so check <see cref="WorktreesAsync"/>.
    /// </summary>
    public static async Task<bool> RemoveWorktreeAsync(this OrcaRuntime runtime, string worktreeId, CancellationToken cancellationToken = default) =>
        Read(
            await Call(runtime, "worktree.rm", new JsonObject { ["worktree"] = $"id:{worktreeId}" }, cancellationToken).ConfigureAwait(false),
            "worktree.rm",
            result => OrcaJson.Bool(result, "removed") ?? false);

    private static Task<JsonNode?> Call(OrcaRuntime runtime, string method, JsonObject? parameters, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        return runtime.CallAsync(method, parameters, cancellationToken);
    }

    private static T Read<T>(JsonNode? result, string method, Func<JsonObject, T> decode)
    {
        try
        {
            return decode(result as JsonObject ?? throw new InvalidDataException("expected an object"));
        }
        catch (Exception e) when (e is InvalidDataException or InvalidOperationException or FormatException)
        {
            throw new OrcaUnavailableException($"Orca answered {method} in a shape rig does not know: {e.Message}", e);
        }
    }

    private static List<T> List<T>(JsonObject parent, string key, Func<JsonObject, T> item) =>
        [.. (OrcaJson.Array(parent, key) ?? []).Select(raw => item(raw as JsonObject ?? throw new InvalidDataException($"{key}: expected objects")))];

    private static OrcaProject Project(JsonObject raw) => new(
        Required(raw, "id"),
        Required(raw, "path"),
        OrcaJson.String(raw, "displayName") ?? string.Empty,
        LenientString(raw, "projectGroupId"),
        OrcaJson.Lenient(() => OrcaJson.Object(raw, "gitRemoteIdentity")) is { } remote ? LenientString(remote, "canonicalKey") : null);

    private static OrcaGroup Group(JsonObject raw) => new(Required(raw, "id"), Required(raw, "name"), LenientString(raw, "parentGroupId"), LenientString(raw, "parentPath"));

    private static OrcaWorktree Worktree(JsonObject raw) => new(
        Required(raw, "id"),
        Required(raw, "path"),
        OrcaJson.String(raw, "repoId") ?? string.Empty,
        LenientString(raw, "branch"),
        LenientBool(raw, "isMainWorktree") ?? false,
        LenientString(raw, "displayName"),
        raw["lastActivityAt"] is JsonValue value && value.GetValueKind() == JsonValueKind.Number && value.TryGetValue<double>(out var ms) ? ms : null);

    private static OrcaActivity Activity(JsonObject raw)
    {
        var agents = OrcaJson.Lenient(() => OrcaJson.Array(raw, "agents"));
        IReadOnlyList<string?> states = agents is null || agents.Any(agent => agent is not JsonObject)
            ? []
            : [.. agents.Cast<JsonObject>().Select(agent => LenientString(agent, "state"))];
        return new OrcaActivity(Required(raw, "worktreeId"), states);
    }

    private static string Required(JsonObject raw, string key) => OrcaJson.String(raw, key) ?? throw new InvalidDataException($"{key}: missing");

    private static string? LenientString(JsonObject raw, string key) => OrcaJson.Lenient(() => OrcaJson.String(raw, key));

    private static bool? LenientBool(JsonObject raw, string key)
    {
        try
        {
            return OrcaJson.Bool(raw, key);
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }
}
