using Rig.Core.GitHub;
using Rig.Core.Processes;
using Rig.Core.Roots;

namespace Rig.Core.Governance;

/// <summary>Whether a repo's root <c>.gitignore</c> holds every entry its policy requires.</summary>
/// <param name="Status">The verdict as printed: <c>CONFIGURED</c>, <c>NO POLICY</c>, <c>MISSING</c>, <c>MISSING &lt;lines&gt;</c> or <c>NOT IGNORED …</c>.</param>
/// <param name="IsAligned">True when nothing is missing.</param>
/// <param name="Languages">The languages whose entries were required.</param>
/// <param name="Missing">The names of the entries not satisfied.</param>
/// <param name="MissingLines">Their unsatisfied lines.</param>
/// <param name="File">The <c>.gitignore</c> looked at.</param>
public sealed record GitignoreInspection(
    string Status,
    bool IsAligned,
    IReadOnlyList<string> Languages,
    IReadOnlyList<string> Missing,
    IReadOnlyList<string> MissingLines,
    string File);

/// <summary>What appending a repo's missing <c>.gitignore</c> entries did.</summary>
/// <param name="Status"><c>aligned</c> (lines were, or would be, appended), <c>already_configured</c> or <c>no_policy</c>.</param>
/// <param name="File">The <c>.gitignore</c> written.</param>
/// <param name="Languages">The languages whose entries were required.</param>
/// <param name="Added">The lines appended.</param>
public sealed record GitignoreAlignment(string Status, string File, IReadOnlyList<string> Languages, IReadOnlyList<string> Added);

/// <summary>A result tagged with the clone it describes.</summary>
/// <typeparam name="T">An inspection or an alignment.</typeparam>
/// <param name="RepoName">The clone's folder name.</param>
/// <param name="RepoPath">The clone's folder.</param>
/// <param name="Result">What was found or done.</param>
public sealed record RepoGitignore<T>(string RepoName, string RepoPath, T Result);

/// <summary>
/// Checks and fills in the <c>.gitignore</c> entries <c>config/repo_policies.json</c> requires of each clone. Only
/// missing lines are appended, so running it again changes nothing. Ported from
/// <c>rig.repo_governance.gitignore_manager</c>.
/// </summary>
/// <param name="git">Runs git, for each clone's remote and whether git ignores rig's state folder.</param>
/// <param name="scopes">The estate's owners and org folders.</param>
/// <param name="policies">The policy.</param>
public sealed class Gitignores(Git git, EstateScopes scopes, RepoPolicies policies)
{
    /// <summary>Reported missing when git does not ignore rig's per-worktree state folder.</summary>
    public const string WorktreeStateEntry = "worktree-state";

    /// <summary>The per-worktree state folder, relative to the worktree, which every repo must ignore.</summary>
    public const string StateDir = "temp/.rig";

    /// <summary>
    /// The key a checkout's policy is looked up by: <c>owner/name</c> from its origin remote, else
    /// <c>&lt;org&gt;/&lt;folder&gt;</c> for a checkout in an org folder, else the folder name. Only a folder that is
    /// itself a checkout is asked for a remote, since git would otherwise answer for an enclosing repo.
    /// </summary>
    public async Task<string> PolicyKeyAsync(string repoPath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repoPath);
        string? origin = null;
        if (Path.Exists(Path.Join(repoPath, ".git")))
        {
            var ran = await git.RunAsync(repoPath, ["config", "--get", "remote.origin.url"], TimeSpan.FromSeconds(5), cancellationToken: cancellationToken).ConfigureAwait(false);
            origin = ran.OutOrEmpty;
        }

        if (RepoSlug.FromRemote(origin) is { } slug)
        {
            return slug.FullName;
        }

        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(repoPath));
        var org = scopes.OrgFolder(Path.GetFileName(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(repoPath))) ?? string.Empty);
        return org is null ? name : $"{org}/{name}";
    }

    /// <summary>
    /// Every entry the repo keyed <paramref name="key"/> must hold, ending with the bespoke agent files block unless
    /// the policy names one itself: the paths the AGENTS.md audit fails are the paths git ignores.
    /// </summary>
    public IReadOnlyList<GitignoreEntry> RequiredEntries(string key, string repoPath, IReadOnlyList<string>? languages = null)
    {
        var entries = policies.GitignoreEntries(key, repoPath, languages);
        var bespoke = policies.BespokeAgentFilesFor(key);
        return bespoke.Count == 0 || entries.Any(entry => entry.Name == BespokeAgentFiles.EntryName)
            ? entries
            : [.. entries, new GitignoreEntry(BespokeAgentFiles.EntryName, BespokeAgentFiles.Comment, bespoke, bespoke, MatchAll: true)];
    }

    /// <summary>Whether <paramref name="repoPath"/>'s root <c>.gitignore</c> holds every required entry.</summary>
    public async Task<GitignoreInspection> InspectAsync(string repoPath, IReadOnlyList<string>? languages = null, CancellationToken cancellationToken = default)
    {
        var (resolved, entries, file) = await RequiredAsync(repoPath, languages, cancellationToken).ConfigureAwait(false);
        if (entries.Count == 0)
        {
            return new("NO POLICY", true, resolved, [], [], file);
        }

        if (!File.Exists(file))
        {
            return new("MISSING", false, resolved, [.. entries.Select(e => e.Name)], [.. entries.SelectMany(e => e.Write)], file);
        }

        var content = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
        var unmet = entries.Select(entry => (entry, Lines: entry.MissingLines(content))).Where(m => m.Lines.Count > 0).ToList();
        if (unmet.Count > 0)
        {
            var lines = unmet.SelectMany(m => m.Lines).ToList();
            return new("MISSING " + string.Join(", ", lines.Order(StringComparer.Ordinal)), false, resolved, [.. unmet.Select(m => m.entry.Name)], lines, file);
        }

        // The lines are there, yet git may still see the state folder: a later `!` rule can re-include it.
        return await StateDirIgnoredAsync(repoPath, cancellationToken).ConfigureAwait(false) == false
            ? new($"NOT IGNORED {StateDir}/ (fix the .gitignore rule that re-includes it)", false, resolved, [WorktreeStateEntry], [], file)
            : new("CONFIGURED", true, resolved, [], [], file);
    }

    /// <summary>Appends the required entries <paramref name="repoPath"/>'s <c>.gitignore</c> is missing, unless <paramref name="dryRun"/>.</summary>
    public async Task<GitignoreAlignment> AlignAsync(string repoPath, bool dryRun = false, IReadOnlyList<string>? languages = null, CancellationToken cancellationToken = default)
    {
        var (resolved, entries, file) = await RequiredAsync(repoPath, languages, cancellationToken).ConfigureAwait(false);
        if (entries.Count == 0)
        {
            return new("no_policy", file, resolved, []);
        }

        var content = File.Exists(file) ? await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false) : string.Empty;
        var unmet = entries.Select(entry => (entry, Lines: entry.MissingLines(content))).Where(m => m.Lines.Count > 0).ToList();
        if (unmet.Count == 0)
        {
            return new("already_configured", file, resolved, []);
        }

        var appended = string.Concat(unmet.Select(m => m.entry.RenderMissing(m.Lines)));
        if (!dryRun)
        {
            await File.WriteAllTextAsync(file, content.Length > 0 ? content + appended : appended.TrimStart('\n'), cancellationToken).ConfigureAwait(false);
        }

        return new("aligned", file, resolved, [.. unmet.SelectMany(m => m.Lines)]);
    }

    /// <summary>Inspects each clone of <paramref name="repos"/> in the estate (every clone when none are named).</summary>
    public async Task<IReadOnlyList<RepoGitignore<GitignoreInspection>>> AuditEstateAsync(
        string estateRoot, IReadOnlyList<string>? repos = null, IReadOnlyList<string>? languages = null, CancellationToken cancellationToken = default)
    {
        var results = new List<RepoGitignore<GitignoreInspection>>();
        foreach (var target in Clones(estateRoot, repos))
        {
            results.Add(new(Path.GetFileName(target), target, await InspectAsync(target, languages, cancellationToken).ConfigureAwait(false)));
        }

        return results;
    }

    /// <summary>Aligns each clone of <paramref name="repos"/> in the estate (every clone when none are named).</summary>
    public async Task<IReadOnlyList<RepoGitignore<GitignoreAlignment>>> SyncEstateAsync(
        string estateRoot, IReadOnlyList<string>? repos = null, bool dryRun = false, IReadOnlyList<string>? languages = null, CancellationToken cancellationToken = default)
    {
        var results = new List<RepoGitignore<GitignoreAlignment>>();
        foreach (var target in Clones(estateRoot, repos))
        {
            results.Add(new(Path.GetFileName(target), target, await AlignAsync(target, dryRun, languages, cancellationToken).ConfigureAwait(false)));
        }

        return results;
    }

    /// <summary>
    /// The clones (folders holding <c>.git</c>) in the estate root and its org folders: those named
    /// <paramref name="repos"/>, or all of them when it is empty. Sorted by folder, then name.
    /// </summary>
    public IReadOnlyList<string> Clones(string estateRoot, IReadOnlyList<string>? repos = null)
    {
        var parents = scopes.RepoParentDirs(estateRoot).Where(Directory.Exists).ToList();
        var candidates = repos is { Count: > 0 }
            ? repos.SelectMany(name => parents.Select(parent => Path.Join(parent, name)))
            : parents.SelectMany(Directory.EnumerateDirectories);
        return [.. candidates
            .Where(child => Directory.Exists(child) && Path.Exists(Path.Join(child, ".git")))
            .OrderBy(child => Path.GetDirectoryName(child), StringComparer.Ordinal)
            .ThenBy(Path.GetFileName, StringComparer.Ordinal)];
    }

    // Whether git ignores a file in the state folder: true, false, or null when the folder is not a git worktree.
    // `git check-ignore` exits 1 for "not ignored"; nothing needs to exist yet.
    private async Task<bool?> StateDirIgnoredAsync(string worktree, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(worktree))
        {
            return null;
        }

        var ran = await git.RunAsync(worktree, ["check-ignore", "--quiet", "--no-index", $"{StateDir}/probe.json"], cancellationToken: cancellationToken).ConfigureAwait(false);
        return ran.Ok ? true : ran.Code == 1 ? false : null;
    }

    private async Task<(IReadOnlyList<string> Languages, IReadOnlyList<GitignoreEntry> Entries, string File)> RequiredAsync(
        string repoPath, IReadOnlyList<string>? languages, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(repoPath);
        var key = await PolicyKeyAsync(repoPath, cancellationToken).ConfigureAwait(false);
        var resolved = policies.ResolveLanguages(key, repoPath, languages);
        return (resolved, RequiredEntries(key, repoPath, resolved), Path.Join(repoPath, ".gitignore"));
    }
}
