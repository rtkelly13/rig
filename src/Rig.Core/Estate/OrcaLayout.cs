using System.Text.Json;
using System.Text.Json.Nodes;
using Rig.Core.GitHub;
using Rig.Core.Orca;
using Rig.Core.Workspace;

namespace Rig.Core.Estate;

/// <summary>One way Orca differs from the declared layout, and how to close the gap.</summary>
/// <param name="Kind">One of the <see cref="OrcaLayout"/> kinds, e.g. <c>wrong-group</c>.</param>
/// <param name="Repo">The managed project, Orca's name for it, or the group the finding is about.</param>
/// <param name="Path">The checkout, with forward slashes, or the group's path.</param>
/// <param name="Reason">Why it is reported.</param>
/// <param name="SuggestedAction">What closes the gap.</param>
/// <param name="Command">A command that carries out the action, or empty when it is a config fix.</param>
public sealed record LayoutFinding(string Kind, string Repo, string Path, string Reason, string SuggestedAction, string Command);

/// <summary>A managed project's place in Orca.</summary>
/// <param name="Name">The managed project's name.</param>
/// <param name="Repo">Its repo URL, as written.</param>
/// <param name="Slug"><c>owner/name</c> as written in the repo URL.</param>
/// <param name="Group">A <c>/</c>-joined group path, empty for the top level, null for not in Orca.</param>
/// <param name="Archived">Listed in <c>archived-projects.json</c>; GitHub's own archive flag is checked separately.</param>
public sealed record DeclaredProject(string Name, string Repo, string Slug, string? Group, bool Archived = false);

/// <summary>A repo registered in Orca.</summary>
/// <param name="Id">Orca's id for it.</param>
/// <param name="Name">Orca's display name, else the folder's name.</param>
/// <param name="Path">Its checkout, as Orca spells it.</param>
/// <param name="Slug"><c>owner/name</c> from the remote, lowercase; null for a folder without a GitHub remote.</param>
/// <param name="GroupId">The group it sits in; null at the top level.</param>
public sealed record Registration(string Id, string Name, string Path, string? Slug, string? GroupId)
{
    /// <summary>The registration Orca's <paramref name="project"/> describes.</summary>
    public static Registration Of(OrcaProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return new(
            project.Id,
            project.DisplayName is { Length: > 0 } shown ? shown : OrcaLayout.FileName(project.Path),
            project.Path,
            RepoSlug.FromRemote(project.CanonicalKey)?.Key,
            project.GroupId);
    }
}

/// <summary>Orca's project groups, addressed by their <c>/</c>-joined path.</summary>
public sealed class GroupTree
{
    private readonly Dictionary<string, OrcaGroup> _byId = new(StringComparer.Ordinal);

    /// <summary>The groups, in Orca's order.</summary>
    public GroupTree(IReadOnlyList<OrcaGroup> rows)
    {
        Rows = rows ?? throw new ArgumentNullException(nameof(rows));
        foreach (var row in rows)
        {
            _byId[row.Id] = row;
        }
    }

    /// <summary>The groups, in Orca's order.</summary>
    public IReadOnlyList<OrcaGroup> Rows { get; }

    /// <summary>The group's path, or null for no group or one Orca no longer has.</summary>
    public string? PathOf(string? groupId)
    {
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var current = groupId;
        while (current is not null && seen.Add(current))
        {
            if (!_byId.TryGetValue(current, out var row))
            {
                return null;
            }

            names.Add(row.Name);
            current = row.ParentId;
        }

        names.Reverse();
        return names.Count > 0 && string.Join('/', names) is { Length: > 0 } path ? path : null;
    }

    /// <summary>The group's id and each ancestor's, nearest first; empty for no group or one Orca no longer has.</summary>
    public IReadOnlyList<string> Lineage(string? groupId)
    {
        var ids = new List<string>();
        var current = groupId;
        while (current is not null && !ids.Contains(current) && _byId.TryGetValue(current, out var row))
        {
            ids.Add(current);
            current = row.ParentId;
        }

        return ids;
    }

    /// <summary>The id of the first group at <paramref name="path"/>, or null.</summary>
    public string? IdOf(string path) => Rows.FirstOrDefault(row => PathOf(row.Id) == path)?.Id;

    /// <summary>Whether Orca has a group at <paramref name="path"/>.</summary>
    public bool Has(string path) => IdOf(path) is not null;

    /// <summary>The code-root folder the top-level group <paramref name="top"/> was scanned from, or null.</summary>
    public string? Folder(string top) =>
        Rows.FirstOrDefault(row => row.ParentId is null && row.Name == top && !string.IsNullOrEmpty(row.ParentPath))?.ParentPath;

    /// <summary>These groups and <paramref name="added"/>.</summary>
    public GroupTree With(OrcaGroup added) => new([.. Rows, added]);
}

/// <summary>What Orca holds right now.</summary>
/// <param name="Registrations">The registered repos.</param>
/// <param name="Groups">The project groups.</param>
/// <param name="WorkspaceGroups">The groups holding a folder workspace: deleting one would delete the workspace too.</param>
public sealed record LayoutSnapshot(IReadOnlyList<Registration> Registrations, GroupTree Groups, IReadOnlySet<string> WorkspaceGroups)
{
    /// <summary>Reads the running Orca's projects, groups and folder workspaces.</summary>
    /// <exception cref="OrcaUnavailableException">Orca could not be asked.</exception>
    public static async Task<LayoutSnapshot> ReadAsync(OrcaRuntime runtime, CancellationToken cancellationToken = default)
    {
        var projects = await runtime.ProjectsAsync(cancellationToken).ConfigureAwait(false);
        var groups = await runtime.GroupsAsync(cancellationToken).ConfigureAwait(false);
        var workspaces = await runtime.FolderWorkspacesAsync(cancellationToken).ConfigureAwait(false);
        return new(
            [.. projects.Select(Registration.Of)],
            new GroupTree(groups),
            workspaces.Select(w => w.GroupId).OfType<string>().Where(id => id.Length > 0).ToHashSet(StringComparer.Ordinal));
    }
}

/// <summary>
/// Orca's project sidebar, held to the layout <c>managed-projects.json</c> declares. Ported from
/// <c>src/rig/estate/orca.py</c>.
/// </summary>
/// <remarks>
/// <para>
/// Each managed project may carry <c>"orca": {"group": "personal/knowledge"}</c>: the group, and any subgroups, it
/// belongs under in Orca. Groups are <c>/</c>-joined names from the top. A top-level group is normally Orca's folder-scan
/// group for a code-root folder, and its subgroups are made by hand. <c>"group": ""</c> puts a project at the top level.
/// A project without the field is not meant to be in Orca.
/// </para>
/// <para>
/// A project in <c>tools.estate.skip_repos</c> is never registered on this machine. An archived repo never belongs in
/// Orca: GitHub is asked which repos are archived, and when it cannot be, only <c>archived-projects.json</c> counts. Only
/// the personal estate counts; work repos keep their hand-made layout. On Windows the estate has its own Orca profile,
/// and estate repos are taken out of the default (work) one.
/// </para>
/// </remarks>
public static class OrcaLayout
{
    /// <summary>Declared and cloned, but not in Orca: <c>--apply</c> registers it, then moves it.</summary>
    public const string Unregistered = "unregistered";

    /// <summary>In Orca, but under another group or none: <c>--apply</c> moves it.</summary>
    public const string WrongGroup = "wrong-group";

    /// <summary>A declared group Orca does not have: <c>--apply</c> creates it.</summary>
    public const string MissingGroup = "missing-group";

    /// <summary>In Orca, but its entry has no <c>orca.group</c>: a config fix.</summary>
    public const string Undeclared = "undeclared";

    /// <summary>In Orca, owned by an estate account, not in managed-projects: a config fix.</summary>
    public const string Unmanaged = "unmanaged";

    /// <summary>In Orca, but its folder is gone.</summary>
    public const string Stale = "stale";

    /// <summary>Declared, but neither in Orca nor cloned where its group expects.</summary>
    public const string NoClone = "no-clone";

    /// <summary>In Orca, but its repo is archived: <c>--apply</c> removes it, unless a process works in it.</summary>
    public const string Archived = "archived";

    /// <summary>An archived repo still cloned locally.</summary>
    public const string ArchivedClone = "archived-clone";

    /// <summary>A group holding no project or folder workspace, not declared: <c>--apply</c> deletes it.</summary>
    public const string EmptyGroup = "empty-group";

    /// <summary>An estate repo in the work Orca profile: <c>--apply</c> removes it from that profile.</summary>
    public const string InWorkProfile = "in-work-profile";

    /// <summary>In Orca, but listed in this machine's <c>tools.estate.skip_repos</c>.</summary>
    public const string Skipped = "skipped";

    /// <summary>A work-profile group that only held estate repos: <c>--apply</c> deletes it once they are gone.</summary>
    public const string WorkEmptyGroup = "work-empty-group";

    /// <summary>The archive of retired projects, beside <c>managed-projects.json</c>.</summary>
    public const string ArchivedProjectsFile = "archived-projects.json";

    private const string ApplyCommand = "estate orca --apply";

    /// <summary>The kinds <c>--apply</c> can close by itself.</summary>
    public static readonly IReadOnlySet<string> Appliable = new HashSet<string>(StringComparer.Ordinal)
    {
        Unregistered, WrongGroup, MissingGroup, Archived, EmptyGroup, InWorkProfile, WorkEmptyGroup,
    };

    /// <summary>
    /// The enabled GitHub projects in <paramref name="config"/>, then those in the <c>archived-projects.json</c> beside it,
    /// so a clone or registration left behind by a retired project is still reported.
    /// </summary>
    /// <exception cref="IOException">The file cannot be read.</exception>
    /// <exception cref="InvalidDataException">A file is malformed.</exception>
    public static IReadOnlyList<DeclaredProject> LoadDeclared(string config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var declared = new List<DeclaredProject>();
        foreach (var entry in Projects(config))
        {
            var name = Text(entry, "name", config);
            var repo = Text(entry, "repo", config);
            var enabled = entry["enabled"] is null || (entry["enabled"]?.GetValueKind() switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => throw new InvalidDataException($"{config}: {name}.enabled: expected a boolean"),
            });
            string? group = entry.TryGetPropertyValue("orca", out var orca) && orca is not null
                ? (orca as JsonObject ?? throw new InvalidDataException($"{config}: {name}.orca: expected an object")) is var placement
                    && placement.TryGetPropertyValue("group", out var raw)
                        ? Text(placement, "group", config, raw).Trim('/')
                        : string.Empty
                : null;
            if (enabled && RepoSlug.FromRemote(repo) is { } slug)
            {
                declared.Add(new(name, repo, slug.FullName, group));
            }
        }

        var archive = Path.Join(Path.GetDirectoryName(config), ArchivedProjectsFile);
        if (File.Exists(archive))
        {
            foreach (var entry in Projects(archive))
            {
                var repo = Text(entry, "repo", archive);
                var name = Text(entry, "name", archive);
                if (RepoSlug.FromRemote(repo) is { } slug)
                {
                    declared.Add(new(name, repo, slug.FullName, null, Archived: true));
                }
            }
        }

        return declared;
    }

    /// <summary>Where a declared project's clone belongs: its top group's folder, then its repo name.</summary>
    /// <remarks>A top-level project has no group folder, so the first code-root folder holding a clone of that name wins, or the code root itself.</remarks>
    public static string ExpectedClone(DeclaredProject project, GroupTree groups, string codeRoot)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(groups);
        var name = project.Slug.Split('/', 2)[^1];
        var top = (project.Group ?? string.Empty).Split('/', 2)[0];
        if (top.Length > 0)
        {
            return Path.Join(groups.Folder(top) ?? Path.Join(codeRoot, top), name);
        }

        var candidates = Directory.Exists(codeRoot)
            ? Directory.EnumerateDirectories(codeRoot).Select(child => Path.Join(child, name)).Order(PathOrder.Instance).ToList()
            : [];
        return candidates.FirstOrDefault(Directory.Exists) ?? Path.Join(codeRoot, name);
    }

    /// <summary>The outermost groups with no project or folder workspace anywhere below them.</summary>
    /// <param name="state">What Orca holds.</param>
    /// <param name="keep">Group paths never reported.</param>
    /// <param name="gone">Registration ids to count as already removed.</param>
    /// <param name="within">When given, the only group ids that may be reported.</param>
    public static IReadOnlyList<OrcaGroup> EmptyGroups(
        LayoutSnapshot state,
        IEnumerable<string>? keep = null,
        IEnumerable<string>? gone = null,
        IEnumerable<string>? within = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        var groups = state.Groups;
        var removed = (gone ?? []).ToHashSet(StringComparer.Ordinal);
        var occupied = new HashSet<string>(StringComparer.Ordinal);
        foreach (var found in state.Registrations.Where(r => !removed.Contains(r.Id)))
        {
            occupied.UnionWith(groups.Lineage(found.GroupId));
        }

        foreach (var groupId in state.WorkspaceGroups)
        {
            occupied.UnionWith(groups.Lineage(groupId));
        }

        var kept = (keep ?? []).ToHashSet(StringComparer.Ordinal);
        var allowed = within?.ToHashSet(StringComparer.Ordinal);
        var candidates = groups.Rows
            .Where(row => !occupied.Contains(row.Id) && !(groups.PathOf(row.Id) is { } path && kept.Contains(path)) && (allowed is null || allowed.Contains(row.Id)))
            .Select(row => row.Id)
            .ToHashSet(StringComparer.Ordinal);
        return [.. groups.Rows.Where(row => candidates.Contains(row.Id) && !(row.ParentId is not null && candidates.Contains(row.ParentId)))];
    }

    /// <summary>Every difference between the declared layout and Orca, in managed-projects order.</summary>
    /// <param name="declared">The declared projects.</param>
    /// <param name="state">What Orca holds.</param>
    /// <param name="codeRoot">The folder holding the group folders.</param>
    /// <param name="isEstateOwner">Whether a GitHub owner is part of the personal estate.</param>
    /// <param name="exists">Whether a folder exists; <see cref="Directory.Exists(string)"/> by default.</param>
    /// <param name="archived">The lowercase <c>owner/name</c> of each repo GitHub reports archived.</param>
    /// <param name="skipped">The projects this machine never registers; reported only while registered.</param>
    public static IReadOnlyList<LayoutFinding> Audit(
        IReadOnlyList<DeclaredProject> declared,
        LayoutSnapshot state,
        string codeRoot,
        Func<string, bool> isEstateOwner,
        Func<string, bool>? exists = null,
        IReadOnlySet<string>? archived = null,
        IReadOnlySet<string>? skipped = null)
    {
        ArgumentNullException.ThrowIfNull(declared);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(isEstateOwner);
        archived ??= new HashSet<string>();
        skipped ??= new HashSet<string>();
        var bySlug = BySlug(state);
        var run = new AuditRun(state.Groups, codeRoot, exists ?? Directory.Exists);
        var known = new HashSet<string>(StringComparer.Ordinal);
        foreach (var project in declared)
        {
            var slug = project.Slug.ToLowerInvariant();
            _ = known.Add(slug);
            var found = bySlug.GetValueOrDefault(slug);
            if (skipped.Contains(project.Name))
            {
                run.Skipped(project, found);
            }
            else if (project.Archived || archived.Contains(slug))
            {
                run.Retired(project, found);
            }
            else
            {
                run.Declared(project, found);
            }
        }

        foreach (var (slug, found) in bySlug.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (!known.Contains(slug) && archived.Contains(slug))
            {
                run.Add(Archived, found.Name, Posix(found.Path), $"in Orca, but {slug} is archived", "remove it from Orca", ApplyCommand);
                if (run.Exists(found.Path))
                {
                    run.Findings.Add(ArchivedCloneOf(found.Name, found.Path, slug));
                }
            }
            else if (!known.Contains(slug) && isEstateOwner(slug.Split('/', 2)[0]))
            {
                run.Add(
                    Unmanaged,
                    found.Name,
                    Posix(found.Path),
                    $"in Orca, but {slug} is not in managed-projects.json",
                    "add it to managed-projects.json with an orca.group, or remove it from Orca");
            }
        }

        foreach (var group in EmptyGroups(state, keep: KeptPaths(Unskipped(declared, skipped), archived)))
        {
            var path = state.Groups.PathOf(group.Id) ?? group.Name;
            run.Add(
                EmptyGroup,
                path,
                path,
                "holds no project or folder workspace, and managed-projects.json declares no project in it",
                "delete it",
                ApplyCommand);
        }

        return run.Findings;
    }

    /// <summary>The findings about the projects named in <paramref name="only"/>, and any group they are declared in that Orca lacks.</summary>
    public static IReadOnlyList<LayoutFinding> Scoped(IEnumerable<LayoutFinding> findings, IEnumerable<DeclaredProject> declared, IReadOnlySet<string> only)
    {
        ArgumentNullException.ThrowIfNull(only);
        var groups = declared.Where(p => only.Contains(p.Name) && !string.IsNullOrEmpty(p.Group)).Select(p => p.Group!).ToHashSet(StringComparer.Ordinal);
        return [.. findings.Where(f => only.Contains(f.Repo) || (f.Kind == MissingGroup && groups.Contains(f.Repo)))];
    }

    /// <summary>
    /// Registers, creates groups, moves projects and drops archived ones until Orca matches what can be matched. Top-level
    /// projects move last, so each lands at the bottom of the top level. An archived project is removed from Orca (which
    /// only unregisters it) except while a process works in it. Config fixes, stale registrations and missing clones are
    /// left for a human. With <paramref name="only"/>, only the named projects are touched and no empty group is deleted.
    /// </summary>
    /// <returns>The <c>archived-clone</c> findings for repos it unregistered that no entry lists, which nothing else would remember.</returns>
    /// <exception cref="OrcaUnavailableException">Orca could not be reached or refused a change.</exception>
    public static async Task<IReadOnlyList<LayoutFinding>> ApplyAsync(
        OrcaRuntime runtime,
        IReadOnlyList<DeclaredProject> declared,
        string codeRoot,
        Func<string, bool> isEstateOwner,
        Func<string, Task<bool?>> inUse,
        Action<string> log,
        Func<string, bool>? exists = null,
        IReadOnlySet<string>? archived = null,
        IReadOnlySet<string>? skipped = null,
        IReadOnlySet<string>? only = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(declared);
        ArgumentNullException.ThrowIfNull(inUse);
        ArgumentNullException.ThrowIfNull(log);
        exists ??= Directory.Exists;
        archived ??= new HashSet<string>();
        skipped ??= new HashSet<string>();
        var carried = new List<LayoutFinding>();
        var active = Unskipped(declared, skipped);
        var state = await LayoutSnapshot.ReadAsync(runtime, cancellationToken).ConfigureAwait(false);
        var byPath = new Dictionary<string, Registration>(StringComparer.Ordinal);
        foreach (var registration in state.Registrations)
        {
            byPath[Posix(registration.Path)] = registration;
        }

        var declaredSlugs = declared.Select(p => p.Slug.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
        foreach (var finding in Audit(declared, state, codeRoot, isEstateOwner, exists, archived, skipped))
        {
            if (only is not null && !only.Contains(finding.Repo))
            {
                continue;
            }

            if (finding.Kind == Unregistered)
            {
                _ = await runtime.AddProjectAsync(Native(finding.Path), cancellationToken).ConfigureAwait(false);
                log($"registered {finding.Repo}");
            }
            else if (finding.Kind == Archived && byPath.GetValueOrDefault(finding.Path) is { } found)
            {
                switch (await inUse(found.Path).ConfigureAwait(false))
                {
                    case null:
                        log($"left {finding.Repo} in Orca: could not check whether a process is working in {finding.Path}");
                        continue;
                    case true:
                        log($"left {finding.Repo} in Orca: a process is working in {finding.Path}");
                        continue;
                }

                await runtime.RemoveProjectAsync(found.Id, cancellationToken).ConfigureAwait(false);
                log($"removed archived {finding.Repo} from Orca");
                if (!(found.Slug is not null && declaredSlugs.Contains(found.Slug)) && exists(found.Path))
                {
                    carried.Add(ArchivedCloneOf(finding.Repo, found.Path, found.Slug ?? finding.Repo));
                }
            }
        }

        var scope = only is null ? active : [.. active.Where(p => only.Contains(p.Name))];
        state = await LayoutSnapshot.ReadAsync(runtime, cancellationToken).ConfigureAwait(false);
        var groups = state.Groups;
        var wanted = scope.Where(p => !string.IsNullOrEmpty(p.Group) && Live(p, archived)).Select(p => p.Group!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(g => g.Count(c => c == '/'))
            .ThenBy(g => g, StringComparer.Ordinal);
        foreach (var group in wanted)
        {
            groups = await EnsureGroupAsync(runtime, groups, group, log, cancellationToken).ConfigureAwait(false);
        }

        var bySlug = BySlug(state);
        var placed = scope.Where(p => Live(p, archived) && p.Group is not null && bySlug.ContainsKey(p.Slug.ToLowerInvariant()));
        foreach (var project in placed.OrderBy(p => p.Group is { Length: 0 }))
        {
            var found = bySlug[project.Slug.ToLowerInvariant()];
            var target = project.Group ?? string.Empty;
            if (!exists(found.Path) || (groups.PathOf(found.GroupId) ?? string.Empty) == target)
            {
                continue;
            }

            _ = await runtime.MoveProjectAsync(found.Id, target.Length > 0 ? groups.IdOf(target) : null, cancellationToken).ConfigureAwait(false);
            log($"moved {project.Name} to {(target.Length > 0 ? target : "the top level")}");
        }

        if (only is not null)
        {
            return carried;
        }

        state = await LayoutSnapshot.ReadAsync(runtime, cancellationToken).ConfigureAwait(false);
        foreach (var group in EmptyGroups(state, keep: KeptPaths(active, archived)))
        {
            if (await runtime.DeleteGroupAsync(group.Id, cancellationToken).ConfigureAwait(false))
            {
                log($"deleted empty group {state.Groups.PathOf(group.Id) ?? group.Name}");
            }
        }

        return carried;
    }

    /// <summary>Estate repos in the work Orca profile, and the groups that would be left empty without them.</summary>
    public static IReadOnlyList<LayoutFinding> AuditWork(LayoutSnapshot state, Func<string, bool> isEstateOwner)
    {
        ArgumentNullException.ThrowIfNull(state);
        var (evicted, emptied) = WorkPlan(state, isEstateOwner);
        var findings = evicted.Select(r => new LayoutFinding(
            InWorkProfile,
            r.Name,
            Posix(r.Path),
            $"{r.Slug} belongs to the personal estate, but it is in the work Orca profile",
            "remove it from the work profile",
            ApplyCommand)).ToList();
        foreach (var group in emptied)
        {
            var path = state.Groups.PathOf(group.Id) ?? group.Name;
            findings.Add(new(WorkEmptyGroup, path, path, "a work-profile group that holds only estate repos", "delete it once they are removed", ApplyCommand));
        }

        return findings;
    }

    /// <summary>
    /// Takes the estate repos out of the work profile, then deletes the groups they leave empty. Removal only unregisters
    /// a repo and stops its terminals in that profile; no file is touched.
    /// </summary>
    /// <exception cref="OrcaUnavailableException">Orca could not be reached or refused a change.</exception>
    public static async Task ApplyWorkAsync(OrcaRuntime runtime, Func<string, bool> isEstateOwner, Action<string> log, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(log);
        var state = await LayoutSnapshot.ReadAsync(runtime, cancellationToken).ConfigureAwait(false);
        var (evicted, emptied) = WorkPlan(state, isEstateOwner);
        foreach (var found in evicted)
        {
            await runtime.RemoveProjectAsync(found.Id, cancellationToken).ConfigureAwait(false);
            log($"removed {found.Name} from the work Orca profile");
        }

        foreach (var group in emptied)
        {
            if (await runtime.DeleteGroupAsync(group.Id, cancellationToken).ConfigureAwait(false))
            {
                log($"deleted work-profile group {state.Groups.PathOf(group.Id) ?? group.Name}");
            }
        }
    }

    /// <summary>The last segment of <paramref name="path"/>, as <c>pathlib</c>'s <c>name</c> reads it.</summary>
    internal static string FileName(string path) => Path.GetFileName(Path.TrimEndingDirectorySeparator(path));

    /// <summary><paramref name="path"/> with forward slashes and no trailing one, as <c>pathlib</c>'s <c>as_posix()</c> prints it.</summary>
    internal static string Posix(string path)
    {
        var slashed = OperatingSystem.IsWindows() ? path.Replace('\\', '/') : path;
        return slashed.Length > 1 && slashed.EndsWith('/') && !slashed.EndsWith(":/", StringComparison.Ordinal) ? slashed.TrimEnd('/') : slashed;
    }

    private static string Native(string path) => OperatingSystem.IsWindows() ? path.Replace('/', '\\') : path;

    private static bool Live(DeclaredProject project, IReadOnlySet<string> archived) =>
        !project.Archived && !archived.Contains(project.Slug.ToLowerInvariant());

    private static Dictionary<string, Registration> BySlug(LayoutSnapshot state)
    {
        var bySlug = new Dictionary<string, Registration>(StringComparer.Ordinal);
        foreach (var registration in state.Registrations.Where(r => r.Slug is not null))
        {
            bySlug[registration.Slug!] = registration;
        }

        return bySlug;
    }

    private static List<DeclaredProject> Unskipped(IEnumerable<DeclaredProject> projects, IReadOnlySet<string> skipped) =>
        [.. projects.Where(p => !skipped.Contains(p.Name))];

    // Each live declared group and every group above it.
    private static HashSet<string> KeptPaths(IEnumerable<DeclaredProject> projects, IReadOnlySet<string> archived)
    {
        var kept = new HashSet<string>(StringComparer.Ordinal);
        foreach (var project in projects.Where(p => !string.IsNullOrEmpty(p.Group) && Live(p, archived)))
        {
            var names = project.Group!.Split('/');
            for (var depth = 1; depth <= names.Length; depth++)
            {
                _ = kept.Add(string.Join('/', names[..depth]));
            }
        }

        return kept;
    }

    private static (List<Registration> Evicted, IReadOnlyList<OrcaGroup> Emptied) WorkPlan(LayoutSnapshot state, Func<string, bool> isEstateOwner)
    {
        ArgumentNullException.ThrowIfNull(isEstateOwner);
        var evicted = state.Registrations.Where(r => !string.IsNullOrEmpty(r.Slug) && isEstateOwner(r.Slug.Split('/', 2)[0])).ToList();
        var touched = evicted.SelectMany(r => state.Groups.Lineage(r.GroupId)).ToHashSet(StringComparer.Ordinal);
        return (evicted, EmptyGroups(state, gone: evicted.Select(r => r.Id), within: touched));
    }

    // Creates each missing segment of path, parents first; returns the groups afterwards.
    private static async Task<GroupTree> EnsureGroupAsync(OrcaRuntime runtime, GroupTree groups, string path, Action<string> log, CancellationToken cancellationToken)
    {
        string? parentId = null;
        var names = path.Split('/');
        for (var depth = 1; depth <= names.Length; depth++)
        {
            var prefix = string.Join('/', names[..depth]);
            var existing = groups.IdOf(prefix);
            if (existing is null)
            {
                var created = await runtime.CreateGroupAsync(names[depth - 1], parentId, cancellationToken).ConfigureAwait(false);
                groups = groups.With(created);
                log($"created group {prefix}");
                existing = created.Id;
            }

            parentId = existing;
        }

        return groups;
    }

    private static LayoutFinding ArchivedCloneOf(string repo, string path, string slug) => new(
        ArchivedClone,
        repo,
        Posix(path),
        $"{slug} is archived, but still cloned here",
        "delete the clone once nothing exists only there (estate risk shows what does)",
        string.Empty);

    private static IEnumerable<JsonObject> Projects(string path)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(File.ReadAllText(path));
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"{path}: {e.Message}", e);
        }

        if (root is not JsonObject top)
        {
            throw new InvalidDataException($"{path}: expected a JSON object");
        }

        if (!top.TryGetPropertyValue("projects", out var projects))
        {
            return [];
        }

        return projects is JsonArray list && list.All(item => item is JsonObject)
            ? list.Cast<JsonObject>()
            : throw new InvalidDataException($"{path}: projects: expected a list of objects");
    }

    private static string Text(JsonObject entry, string key, string file) =>
        Text(entry, key, file, entry.TryGetPropertyValue(key, out var raw) ? raw : throw new InvalidDataException($"{file}: {key}: missing"));

    private static string Text(JsonObject entry, string key, string file, JsonNode? raw) =>
        raw is JsonValue value && value.GetValueKind() == JsonValueKind.String
            ? value.GetValue<string>()
            : throw new InvalidDataException($"{file}: {key}: expected a string{(entry["name"] is JsonValue name && name.GetValueKind() == JsonValueKind.String ? $" in {name}" : string.Empty)}");

    // One audit's findings, and the groups already reported missing.
    private sealed class AuditRun(GroupTree groups, string codeRoot, Func<string, bool> exists)
    {
        private readonly HashSet<string> _missing = new(StringComparer.Ordinal);

        public List<LayoutFinding> Findings { get; } = [];

        public Func<string, bool> Exists => exists;

        public void Add(string kind, string repo, string path, string reason, string action, string command = "") =>
            Findings.Add(new(kind, repo, path, reason, action, command));

        // An archived project: out of Orca, and its clone flagged for cleanup.
        public void Retired(DeclaredProject project, Registration? found)
        {
            if (found is not null)
            {
                Add(Archived, project.Name, Posix(found.Path), $"in Orca, but {project.Slug} is archived", "remove it from Orca", ApplyCommand);
            }

            var clone = found?.Path ?? ExpectedClone(project, groups, codeRoot);
            if (exists(clone))
            {
                Findings.Add(ArchivedCloneOf(project.Name, clone, project.Slug));
            }
        }

        // A project this machine skips: never registered, and a registration already there is only reported.
        public void Skipped(DeclaredProject project, Registration? found)
        {
            if (found is not null)
            {
                Add(
                    OrcaLayout.Skipped,
                    project.Name,
                    Posix(found.Path),
                    "in Orca, but listed in tools.estate.skip_repos on this machine",
                    "remove it from Orca by hand, or drop it from the skip list");
            }
        }

        public void Declared(DeclaredProject project, Registration? found)
        {
            if (project.Group is not { } group)
            {
                if (found is not null)
                {
                    Add(
                        Undeclared,
                        project.Name,
                        Posix(found.Path),
                        "in Orca, but managed-projects.json gives it no orca.group",
                        "add \"orca\": {\"group\": ...} to its managed-projects.json entry");
                }

                return;
            }

            var where = group.Length > 0 ? group : "the top level";
            if (group.Length > 0 && !groups.Has(group) && _missing.Add(group))
            {
                Add(MissingGroup, group, group, $"Orca has no group {group}", "create it", ApplyCommand);
            }

            if (found is null)
            {
                var clone = ExpectedClone(project, groups, codeRoot);
                if (exists(clone))
                {
                    Add(Unregistered, project.Name, Posix(clone), "cloned, but not in Orca", $"register it and move it to {where}", ApplyCommand);
                }
                else
                {
                    Add(
                        NoClone,
                        project.Name,
                        Posix(clone),
                        "neither in Orca nor cloned where its group expects",
                        "clone it, then run estate orca --apply",
                        $"git clone {project.Repo} {Posix(clone)}");
                }

                return;
            }

            if (!exists(found.Path))
            {
                Add(Stale, project.Name, Posix(found.Path), "registered in Orca, but the folder is gone", "remove it from Orca, or clone it back");
                return;
            }

            var actual = groups.PathOf(found.GroupId) ?? string.Empty;
            if (actual != group)
            {
                Add(WrongGroup, project.Name, Posix(found.Path), $"under {(actual.Length > 0 ? actual : "no group")}, declared {where}", $"move it to {where}", ApplyCommand);
            }
        }
    }
}
