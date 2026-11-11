using Rig.Core.Processes;
using Rig.Core.Roots;

namespace Rig.Core.Workspace;

/// <summary>What an entry in the estate is, in the audit's terms.</summary>
public enum EstateItemKind
{
    /// <summary>The clone of a declared project, where it is declared, from the remote it declares.</summary>
    ManagedRepo,

    /// <summary>A folder whose <c>.git</c> is a file: a linked worktree.</summary>
    LinkedWorktree,

    /// <summary>A clone nothing declares here, or declared with another remote.</summary>
    UnmanagedRepo,

    /// <summary>A folder that is not a clone and is not declared.</summary>
    UnmanagedDir,

    /// <summary>A stray file.</summary>
    UnmanagedFile,

    /// <summary>An orchestration file or folder the estate keeps at its root, such as <c>AGENTS.md</c>.</summary>
    KnownSystem,

    /// <summary>The clone or folder of a retired project, kept as an archive.</summary>
    RetainedArchive,
}

/// <summary>An entry found in the estate.</summary>
/// <param name="Name">Its name.</param>
/// <param name="Path">Where it is.</param>
/// <param name="Kind">What it is.</param>
/// <param name="IsDir">Whether it is a folder (or a link to one).</param>
/// <param name="Remote">A clone's origin, when it has one.</param>
/// <param name="Detail">Why it is that kind.</param>
public sealed record EstateItem(string Name, string Path, EstateItemKind Kind, bool IsDir, string? Remote, string Detail);

/// <summary>Whether an estate item was (or would be) removed, and the line to show for it.</summary>
/// <param name="Ok">False when it was refused or failed.</param>
/// <param name="Message">What happened.</param>
public sealed record CleanOutcome(bool Ok, string Message);

/// <summary>The estate measured against the managed projects.</summary>
/// <param name="EstateRoot">The personal estate folder.</param>
/// <param name="TotalItems">How many entries the estate folder holds.</param>
/// <param name="ManagedRepos">Declared clones, in the estate folder or their org folder.</param>
/// <param name="LinkedWorktrees">Linked worktrees in the estate folder.</param>
/// <param name="UnmanagedItems">What <c>clean</c> removes: nothing declares it.</param>
/// <param name="MissingManagedRepos">Enabled projects with nothing at their location.</param>
/// <param name="BlockedManagedRepos">
/// Org-folder locations of enabled projects holding something other than their clone. Reported only: they lie outside
/// the estate folder, so <c>clean</c> never touches them.
/// </param>
/// <param name="KnownSystemItems">Orchestration entries.</param>
/// <param name="RetainedArchives">Retired projects' clones and folders.</param>
public sealed record EstateAuditResult(
    string EstateRoot,
    int TotalItems,
    IReadOnlyList<EstateItem> ManagedRepos,
    IReadOnlyList<EstateItem> LinkedWorktrees,
    IReadOnlyList<EstateItem> UnmanagedItems,
    IReadOnlyList<ManagedRepoSpec> MissingManagedRepos,
    IReadOnlyList<EstateItem> BlockedManagedRepos,
    IReadOnlyList<EstateItem> KnownSystemItems,
    IReadOnlyList<EstateItem> RetainedArchives)
{
    /// <summary>Clean when nothing is unmanaged, missing, or blocking a declared location.</summary>
    public bool IsClean => UnmanagedItems.Count == 0 && MissingManagedRepos.Count == 0 && BlockedManagedRepos.Count == 0;
}

/// <summary>
/// Audits and cleans the personal estate folder against the managed projects (ADR 0017: nothing exists in the
/// personal folder without the CLI managing it). Ported from <c>rig.workspace.engine</c>.
/// </summary>
/// <param name="git">Reads clones' remotes and statuses.</param>
/// <param name="scopes">The estate's orgs, which place org-owned projects in sibling folders.</param>
public sealed class EstateAudit(Git git, EstateScopes scopes)
{
    /// <summary>File and folder names the estate keeps at its root for orchestration, matched exactly.</summary>
    public static readonly IReadOnlySet<string> KnownSystemEntries = new HashSet<string>(
        [".claude", ".DS_Store", ".pnpm-store", "personal.code-workspace", "AGENTS.md", "CLAUDE.md", "TODO.md", "temp"],
        StringComparer.Ordinal);

    /// <summary>The snake-case name the Python gave <paramref name="kind"/>, used in reports.</summary>
    public static string Value(EstateItemKind kind) => kind switch
    {
        EstateItemKind.ManagedRepo => "managed_repo",
        EstateItemKind.LinkedWorktree => "linked_worktree",
        EstateItemKind.UnmanagedRepo => "unmanaged_repo",
        EstateItemKind.UnmanagedDir => "unmanaged_dir",
        EstateItemKind.UnmanagedFile => "unmanaged_file",
        EstateItemKind.KnownSystem => "known_system",
        _ => "retained_archive",
    };

    /// <summary>
    /// A git remote reduced for comparison: no scheme, user or trailing <c>.git</c>, the scp-style colon a slash,
    /// lowercased. Null for an empty one.
    /// </summary>
    public static string? NormalizeRemote(string? remote)
    {
        if (string.IsNullOrEmpty(remote))
        {
            return null;
        }

        var cleaned = remote.Trim();
        if (cleaned.EndsWith(".git", StringComparison.Ordinal))
        {
            cleaned = cleaned[..^4];
        }

        if (cleaned.StartsWith("git@", StringComparison.Ordinal))
        {
            cleaned = ReplaceFirstColon(cleaned[4..]);
        }
        else if (cleaned.StartsWith("https://", StringComparison.Ordinal))
        {
            cleaned = cleaned[8..];
        }
        else if (cleaned.StartsWith("http://", StringComparison.Ordinal))
        {
            cleaned = cleaned[7..];
        }
        else if (cleaned.StartsWith("ssh://git@", StringComparison.Ordinal))
        {
            cleaned = ReplaceFirstColon(cleaned[10..]);
        }

        return cleaned.ToLowerInvariant().Trim('/');
    }

    /// <summary>
    /// Audits <paramref name="estateDir"/> against <paramref name="specs"/>. A spec named in <paramref name="skip"/>
    /// is never reported missing: this machine is not meant to have it.
    /// </summary>
    public async Task<EstateAuditResult> RunAsync(
        string estateDir,
        IReadOnlyList<ManagedRepoSpec> specs,
        IReadOnlySet<string> skip,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(specs);
        ArgumentNullException.ThrowIfNull(skip);
        var managed = new List<EstateItem>();
        var worktrees = new List<EstateItem>();
        var unmanaged = new List<EstateItem>();
        var missing = new List<ManagedRepoSpec>();
        var blocked = new List<EstateItem>();
        var system = new List<EstateItem>();
        var retained = new List<EstateItem>();
        if (!Directory.Exists(estateDir))
        {
            return new(estateDir, 0, managed, worktrees, unmanaged, missing, blocked, system, retained);
        }

        var retainedNames = specs.Where(s => !s.Enabled).Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
        var discovered = new HashSet<string>(StringComparer.Ordinal);
        var entries = Directory.EnumerateFileSystemEntries(estateDir)
            .OrderBy(path => Path.GetFileName(path).ToLowerInvariant(), StringComparer.Ordinal)
            .ToList();

        foreach (var path in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(path);
            if (KnownSystemEntries.Contains(name))
            {
                system.Add(new(name, path, EstateItemKind.KnownSystem, Directory.Exists(path), null, "Workspace orchestration file/directory"));
                continue;
            }

            if (!Directory.Exists(path))
            {
                unmanaged.Add(new(name, path, EstateItemKind.UnmanagedFile, false, null, "Orphaned unmanaged file in personal directory root"));
                continue;
            }

            var dotGit = Path.Join(path, ".git");
            if (Directory.Exists(dotGit))
            {
                var remote = await RemoteAsync(path).ConfigureAwait(false);
                var spec = specs.FirstOrDefault(s => s.Name == name && s.Enabled);
                if (spec is not null)
                {
                    if (scopes.OrgFolderOf(spec.Repo) is not null)
                    {
                        // Declared in an org folder: this root copy is not the managed clone.
                        unmanaged.Add(new(name, path, EstateItemKind.UnmanagedRepo, true, remote, $"Misplaced: declared at {scopes.ProjectRelPath(name, spec.Repo)}"));
                    }
                    else if (RemoteMismatch(spec, remote))
                    {
                        unmanaged.Add(new(name, path, EstateItemKind.UnmanagedRepo, true, remote, MismatchDetail(remote, spec)));
                    }
                    else
                    {
                        _ = discovered.Add(name);
                        managed.Add(new(name, path, EstateItemKind.ManagedRepo, true, remote, "Registered managed repository"));
                    }
                }
                else if (retainedNames.Contains(name))
                {
                    _ = discovered.Add(name);
                    retained.Add(new(name, path, EstateItemKind.RetainedArchive, true, remote, RetainedDetail(specs, name)));
                }
                else
                {
                    unmanaged.Add(new(name, path, EstateItemKind.UnmanagedRepo, true, remote, "Git clone not registered in managed-projects.json"));
                }
            }
            else if (Path.Exists(dotGit))
            {
                worktrees.Add(new(name, path, EstateItemKind.LinkedWorktree, true, null, "Linked git worktree sibling"));
            }
            else if (retainedNames.Contains(name))
            {
                _ = discovered.Add(name);
                retained.Add(new(name, path, EstateItemKind.RetainedArchive, true, null, RetainedDetail(specs, name)));
            }
            else
            {
                unmanaged.Add(new(name, path, EstateItemKind.UnmanagedDir, true, null, "Directory not a git repository and unmanaged"));
            }
        }

        // Org-owned repos live in sibling org folders; anything not found is missing. Whatever else occupies an
        // org-folder location is reported as blocking it, never as unmanaged: `clean` removes unmanaged items, and
        // its scope is the estate root.
        foreach (var spec in specs.Where(s => s.Enabled && !discovered.Contains(s.Name)))
        {
            var expected = scopes.ProjectDir(estateDir, spec.Name, spec.Repo);
            var inOrgFolder = scopes.OrgFolderOf(spec.Repo) is not null;
            if (inOrgFolder && Path.Exists(expected))
            {
                var kind = OccupantKind(expected);
                var remote = kind == EstateItemKind.UnmanagedRepo ? await RemoteAsync(expected).ConfigureAwait(false) : null;
                if (kind == EstateItemKind.UnmanagedRepo && !RemoteMismatch(spec, remote))
                {
                    var org = Path.GetFileName(Path.GetDirectoryName(expected));
                    managed.Add(new(spec.Name, expected, EstateItemKind.ManagedRepo, true, remote, $"Registered managed repository ({org} org folder)"));
                }
                else
                {
                    var detail = kind == EstateItemKind.UnmanagedRepo ? MismatchDetail(remote, spec) : $"Not a git clone; declared as {spec.Repo}";
                    blocked.Add(new(spec.Name, expected, kind, kind != EstateItemKind.UnmanagedFile, remote, detail));
                }
            }
            else if (!Path.Exists(expected) && !skip.Contains(spec.Name))
            {
                missing.Add(spec);
            }
        }

        return new(estateDir, entries.Count, managed, worktrees, unmanaged, missing, blocked, system, retained);
    }

    /// <summary>
    /// Removes an unmanaged <paramref name="item"/>, or says it would. Managed, system and retained items are always
    /// refused, as is a clone with uncommitted work or one git cannot read, and a linked folder is never followed.
    /// </summary>
    public async Task<CleanOutcome> CleanAsync(EstateItem item, bool dryRun, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.Kind is EstateItemKind.ManagedRepo or EstateItemKind.KnownSystem or EstateItemKind.RetainedArchive)
        {
            return new(false, $"Refused: {item.Name} is a managed, system, or retained archive item");
        }

        if (dryRun)
        {
            return new(true, $"[dry-run] Would remove {Value(item.Kind)}: {item.Path}");
        }

        try
        {
            if (item.IsDir)
            {
                if (item.Kind == EstateItemKind.UnmanagedRepo)
                {
                    var status = await git.RunAsync(item.Path, ["status", "--porcelain"], cancellationToken: cancellationToken).ConfigureAwait(false);
                    if (!status.Ok || status.Out.Trim().Length > 0)
                    {
                        return new(false, $"Refused to delete dirty or unreadable unmanaged repo: {item.Name}");
                    }
                }

                // Python's rmtree refused a link rather than deleting through it; so does this.
                if (new DirectoryInfo(item.Path).LinkTarget is not null)
                {
                    return new(false, $"Failed to remove {item.Name}: it is a symbolic link");
                }

                Directory.Delete(item.Path, recursive: true);
            }
            else
            {
                File.Delete(item.Path);
            }

            return new(true, $"Removed {item.Name}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new(false, $"Failed to remove {item.Name}: {e.Message}");
        }
    }

    private async Task<string?> RemoteAsync(string clone)
    {
        var remote = await git.OutAsync(clone, "config", "--get", "remote.origin.url").ConfigureAwait(false);
        return remote.Length == 0 ? null : remote;
    }

    // True when a clone's origin is known and differs from the spec's declared remote.
    private static bool RemoteMismatch(ManagedRepoSpec spec, string? remote)
    {
        if (spec.Repo.Length == 0 || string.IsNullOrEmpty(remote) || spec.Repo.StartsWith("local/", StringComparison.Ordinal))
        {
            return false;
        }

        var local = NormalizeRemote(remote);
        var declared = NormalizeRemote(spec.Repo);
        return !string.IsNullOrEmpty(local) && !string.IsNullOrEmpty(declared) && local != declared;
    }

    private static string MismatchDetail(string? remote, ManagedRepoSpec spec) => $"Remote URL mismatch: local '{remote}' != declared '{spec.Repo}'";

    private static string RetainedDetail(IReadOnlyList<ManagedRepoSpec> specs, string name)
    {
        var description = specs.FirstOrDefault(s => s.Name == name)?.Description ?? string.Empty;
        return description.Length > 0 ? $"Retained archive ({description})" : "Retained archive";
    }

    private static EstateItemKind OccupantKind(string path)
    {
        var dotGit = Path.Join(path, ".git");
        return !Directory.Exists(path) ? EstateItemKind.UnmanagedFile
            : Directory.Exists(dotGit) ? EstateItemKind.UnmanagedRepo
            : Path.Exists(dotGit) ? EstateItemKind.LinkedWorktree
            : EstateItemKind.UnmanagedDir;
    }

    private static string ReplaceFirstColon(string text)
    {
        var colon = text.IndexOf(':', StringComparison.Ordinal);
        return colon < 0 ? text : string.Concat(text.AsSpan(0, colon), "/", text.AsSpan(colon + 1));
    }
}
