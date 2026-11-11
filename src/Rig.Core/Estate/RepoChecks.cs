using Rig.Core.Processes;
using Rig.Core.Roots;

namespace Rig.Core.Estate;

/// <summary>How a finding should be treated.</summary>
public enum Severity
{
    /// <summary>Live work: cleanup refuses the clone until it clears.</summary>
    Error,

    /// <summary>Worth acting on.</summary>
    Warn,

    /// <summary>Worth knowing.</summary>
    Info,
}

/// <summary>One piece of local git hygiene worth a human's attention.</summary>
/// <param name="Check">The check's identifier, such as <c>worktree-dirty</c>.</param>
/// <param name="Severity">How it should be treated.</param>
/// <param name="Subject">The path or branch it is about.</param>
/// <param name="Message">What is wrong.</param>
/// <param name="Fix">The command that resolves it, when there is a single safe one.</param>
/// <param name="Actionable">The cleanup command is willing to act on it.</param>
public sealed record Finding(string Check, Severity Severity, string Subject, string Message, string Fix = "", bool Actionable = false)
{
    /// <summary>The severity as reports spell it: <c>error</c>, <c>warn</c> or <c>info</c>.</summary>
    public string SeverityName => Severity switch
    {
        Severity.Error => "error",
        Severity.Warn => "warn",
        _ => "info",
    };
}

/// <summary>One local branch and what git knows about it.</summary>
/// <param name="Name">The branch.</param>
/// <param name="Upstream">What it tracks, without <c>refs/remotes/</c>.</param>
/// <param name="UpstreamGone">The upstream was deleted on the remote.</param>
/// <param name="IsCurrent">The main checkout has it out.</param>
/// <param name="CheckedOutElsewhere">A linked worktree has it out.</param>
public sealed record LocalBranch(string Name, string Upstream = "", bool UpstreamGone = false, bool IsCurrent = false, bool CheckedOutElsewhere = false)
{
    /// <summary>Parses <c>for-each-ref</c> lines of refname, upstream and upstream track state, tab-separated.</summary>
    public static IReadOnlyList<LocalBranch> Parse(string forEachRef, string current, IReadOnlySet<string> worktreeBranches)
    {
        ArgumentNullException.ThrowIfNull(forEachRef);
        ArgumentNullException.ThrowIfNull(worktreeBranches);
        var found = new List<LocalBranch>();
        foreach (var line in forEachRef.ReplaceLineEndings("\n").Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var parts = line.Split('\t');
            var name = Strip(parts[0], "refs/heads/");
            var upstream = parts.Length > 1 ? parts[1] : string.Empty;
            var track = parts.Length > 2 ? parts[2] : string.Empty;
            found.Add(new LocalBranch(
                name,
                Strip(upstream, "refs/remotes/"),
                track.Contains("gone", StringComparison.Ordinal),
                name == current,
                worktreeBranches.Contains(name) && name != current));
        }

        return found;
    }

    private static string Strip(string text, string prefix) => text.StartsWith(prefix, StringComparison.Ordinal) ? text[prefix.Length..] : text;
}

/// <summary>One thing inside a clone's scratch folder.</summary>
/// <param name="Path">The entry.</param>
/// <param name="Bytes">Its apparent size, links counted as themselves.</param>
/// <param name="Modified">When it last changed, the entry itself rather than anything a link points at.</param>
/// <param name="IsWorktree">A registered worktree of the clone.</param>
public sealed record TempEntry(string Path, long Bytes, DateTimeOffset Modified, bool IsWorktree = false);

/// <summary>Everything the read-only checks found in one clone.</summary>
/// <param name="Repo">The clone.</param>
/// <param name="Findings">In check order: worktrees, branches, temp, the checkout itself.</param>
/// <param name="TempBytes">What its scratch folder holds.</param>
/// <param name="WorktreeCount">Linked worktrees, the main checkout not counted.</param>
/// <param name="BranchCount">Local branches.</param>
public sealed record RepoReport(string Repo, IReadOnlyList<Finding> Findings, long TempBytes, int WorktreeCount, int BranchCount)
{
    /// <summary>Something dirty or unpushed makes cleanup unsafe.</summary>
    public bool Blocked => Findings.Any(finding => finding.Severity == Severity.Error);
}

/// <summary>
/// Read-only inspection of one checkout's local git hygiene. Ported from <c>src/rig/estate/checks.py</c>.
/// </summary>
/// <remarks>
/// <para>
/// Merged-ness cannot be answered locally: under squash merges a branch's commits never become ancestors of
/// <c>main</c>. So the free checks only report what git can prove, an upstream marked gone or true ancestry, and asking
/// GitHub is opt-in through <c>mergedHeads</c>.
/// </para>
/// <para>
/// A worktree is not a directory. The convention puts them at <c>&lt;repo&gt;/temp/&lt;name&gt;</c>, beside scratch
/// files, so every temp entry is classified as a registered worktree or not.
/// </para>
/// </remarks>
/// <param name="git">Runs git.</param>
/// <param name="time">The clock staleness is read against.</param>
public sealed class RepoChecks(Git git, TimeProvider time)
{
    /// <summary>How long scratch may sit untouched before it is reported.</summary>
    public static readonly TimeSpan DefaultStaleAfter = TimeSpan.FromDays(14);

    /// <summary>Branches the branch checks never report.</summary>
    public static readonly IReadOnlySet<string> Protected = new HashSet<string>(StringComparer.Ordinal) { "main", "master" };

    private const string Base = "origin/main";

    /// <summary>Runs every read-only check over <paramref name="repo"/>.</summary>
    /// <param name="repo">The clone.</param>
    /// <param name="mergedHeads">From <see cref="PullRequests.MergedHeadsAsync"/>; null when GitHub was not asked.</param>
    /// <param name="staleAfter">How old scratch must be to report; <see cref="DefaultStaleAfter"/> when null.</param>
    public async Task<RepoReport> InspectAsync(string repo, IReadOnlyDictionary<string, PullRequest>? mergedHeads = null, TimeSpan? staleAfter = null)
    {
        var findings = new List<Finding>();
        findings.AddRange(await CheckWorktreesAsync(repo, mergedHeads).ConfigureAwait(false));
        findings.AddRange(await CheckBranchesAsync(repo).ConfigureAwait(false));
        var temp = await ScanTempAsync(repo).ConfigureAwait(false);
        findings.AddRange(await CheckTempAsync(repo, temp, staleAfter ?? DefaultStaleAfter).ConfigureAwait(false));
        findings.AddRange(await CheckRepoStateAsync(repo).ConfigureAwait(false));
        var trees = await Worktree.ListAsync(git, repo).ConfigureAwait(false);
        var branches = await ListBranchesAsync(repo).ConfigureAwait(false);
        return new RepoReport(repo, findings, temp.Sum(entry => entry.Bytes), Math.Max(trees.Count - 1, 0), branches.Count);
    }

    /// <summary>
    /// Worktree problems: prunable, owned by another clone, locked, unborn, dirty, unpushed, detached, finished,
    /// off-convention.
    /// </summary>
    public async Task<IReadOnlyList<Finding>> CheckWorktreesAsync(string repo, IReadOnlyDictionary<string, PullRequest>? mergedHeads = null)
    {
        var temp = Path.Join(repo, "temp");
        var findings = new List<Finding>();
        var common = await git.OutAsync(repo, "rev-parse", "--path-format=absolute", "--git-common-dir").ConfigureAwait(false);
        var ownWorktrees = common.Length > 0 ? Path.Join(common, "worktrees") : null;
        foreach (var tree in await Worktree.ListAsync(git, repo).ConfigureAwait(false))
        {
            var subject = tree.Path;
            if (tree.Prunable)
            {
                var reason = tree.PruneReason.Length > 0 ? tree.PruneReason : "its directory is gone";
                findings.Add(new Finding("worktree-prunable", Severity.Warn, subject, $"Registered but {reason}.", "git worktree prune", Actionable: true));
                continue; // nothing else can be read from a path that is not there
            }

            if (!tree.IsMain && ownWorktrees is not null && ForeignAdminDir(tree.Path, ownWorktrees) is { } foreign)
            {
                findings.Add(new Finding(
                    "worktree-foreign-owner",
                    Severity.Error,
                    subject,
                    $"Registered here, but its `.git` points at {foreign}, so the worktree belongs to another clone. Never remove or prune it from this one: the stale record is this clone's entry under {ownWorktrees}, and only that should go.",
                    $"cd {tree.Path} && git rev-parse --git-common-dir"));
                continue; // every other check would read the other clone's state and report it as this one's
            }

            if (tree.Locked)
            {
                var reason = tree.LockReason.Length > 0 ? $": {tree.LockReason}" : string.Empty;
                findings.Add(new Finding("worktree-locked", Severity.Info, subject, $"Locked{reason}.", $"git worktree unlock {tree.Path}"));
            }

            if (tree.Unborn && !tree.IsMain)
            {
                findings.Add(await UnbornFindingAsync(repo, tree).ConfigureAwait(false));
            }
            else if ((await git.OutAsync(tree.Path, "status", "--porcelain").ConfigureAwait(false)).Length > 0)
            {
                findings.Add(new Finding(
                    "worktree-dirty",
                    Severity.Error,
                    subject,
                    "Uncommitted changes. Nothing will remove this until they are dealt with.",
                    $"cd {tree.Path} && git status"));
            }

            var orphaned = await Exposure.CountAsync(git, tree.Path, "rev-list", "--count", "HEAD", "--not", "--remotes").ConfigureAwait(false);
            if (orphaned > 0)
            {
                findings.Add(new Finding(
                    "worktree-unpushed",
                    Severity.Error,
                    subject,
                    $"{orphaned} commit(s) on no remote — they exist only here.",
                    $"cd {tree.Path} && git push"));
            }

            if (tree.Detached && !tree.Bare)
            {
                findings.Add(new Finding("worktree-detached", Severity.Warn, subject, $"Detached at {Prefix(tree.Head, 8)} — commits here are reachable from nothing."));
            }

            var carriesBranch = tree.Branch.Length > 0 && !tree.IsMain && !tree.Unborn;
            if (carriesBranch && await PullRequests.MergedPrAsync(git, repo, tree.Branch, mergedHeads).ConfigureAwait(false) is { } pr)
            {
                findings.Add(new Finding(
                    "worktree-merged",
                    Severity.Info,
                    subject,
                    $"Holds `{tree.Branch}`, merged in #{pr}. The worktree is finished.",
                    $"cd {repo} && git worktree remove {tree.Path}",
                    Actionable: true));
            }

            if (!tree.IsMain && !IsBelow(tree.Path, temp))
            {
                findings.Add(new Finding("worktree-off-convention", Severity.Info, subject, $"Outside `{Path.GetFileName(temp)}/`, so it is not gitignored and not swept."));
            }
        }

        return findings;
    }

    /// <summary>The local branches of <paramref name="repo"/>.</summary>
    public async Task<IReadOnlyList<LocalBranch>> ListBranchesAsync(string repo)
    {
        var raw = (await git.RunAsync(repo, ["for-each-ref", "--format=%(refname)\t%(upstream)\t%(upstream:track)", "refs/heads"]).ConfigureAwait(false)).OutOrEmpty;
        var current = await git.OutAsync(repo, "rev-parse", "--abbrev-ref", "HEAD").ConfigureAwait(false);
        var elsewhere = (await Worktree.ListAsync(git, repo).ConfigureAwait(false))
            .Where(tree => tree.Branch.Length > 0 && !tree.IsMain)
            .Select(tree => tree.Branch)
            .ToHashSet(StringComparer.Ordinal);
        return LocalBranch.Parse(raw, current, elsewhere);
    }

    /// <summary>Local branches that look finished, from free git signals only.</summary>
    public async Task<IReadOnlyList<Finding>> CheckBranchesAsync(string repo)
    {
        var findings = new List<Finding>();
        foreach (var branch in await ListBranchesAsync(repo).ConfigureAwait(false))
        {
            if (Protected.Contains(branch.Name) || branch.IsCurrent)
            {
                continue;
            }

            if (branch.UpstreamGone)
            {
                findings.Add(new Finding(
                    "branch-upstream-gone",
                    Severity.Warn,
                    branch.Name,
                    $"Tracked `{branch.Upstream}`, which no longer exists — the remote branch was deleted.",
                    $"cd {repo} && git branch -D {branch.Name}",
                    !branch.CheckedOutElsewhere));
                continue;
            }

            // Ancestry proves a merge-commit or fast-forward landing; a squash leaves none, so false means unknown.
            if ((await git.RunAsync(repo, ["merge-base", "--is-ancestor", branch.Name, Base]).ConfigureAwait(false)).Ok)
            {
                findings.Add(new Finding(
                    "branch-merged",
                    Severity.Warn,
                    branch.Name,
                    $"An ancestor of `{Base}`, so its work has landed.",
                    $"cd {repo} && git branch -d {branch.Name}",
                    !branch.CheckedOutElsewhere));
            }
            else if (branch.Upstream.Length == 0)
            {
                findings.Add(new Finding("branch-local-only", Severity.Info, branch.Name, "No upstream, and no ancestry in the base. Never removed automatically."));
            }
        }

        return findings;
    }

    /// <summary>The immediate contents of <c>temp/</c>, sized and dated.</summary>
    public async Task<IReadOnlyList<TempEntry>> ScanTempAsync(string repo)
    {
        var temp = Path.Join(repo, "temp");
        if (!Directory.Exists(temp))
        {
            return [];
        }

        var registered = (await Worktree.ListAsync(git, repo).ConfigureAwait(false))
            .Where(tree => !tree.IsMain)
            .Select(tree => PathResolution.Resolve(tree.Path))
            .ToHashSet(StringComparer.Ordinal);
        var entries = new List<TempEntry>();
        foreach (var child in Directory.EnumerateFileSystemEntries(temp).Order(PathOrder.Instance))
        {
            var info = new FileInfo(child);
            if (!info.Exists && !Directory.Exists(child) && info.LinkTarget is null)
            {
                continue;
            }

            entries.Add(new TempEntry(child, Size(child), new DateTimeOffset(info.LastWriteTimeUtc), registered.Contains(PathResolution.Resolve(child))));
        }

        return entries;
    }

    /// <summary>Whether <c>temp/</c> is ignored, and which of <paramref name="entries"/> have gone stale.</summary>
    public async Task<IReadOnlyList<Finding>> CheckTempAsync(string repo, IReadOnlyList<TempEntry> entries, TimeSpan staleAfter)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var temp = Path.Join(repo, "temp");
        var findings = new List<Finding>();
        if (!Directory.Exists(temp))
        {
            return findings;
        }

        if (!await IsIgnoredAsync(repo, temp).ConfigureAwait(false))
        {
            findings.Add(new Finding(
                "temp-not-ignored",
                Severity.Error,
                temp,
                "Exists but is not gitignored, so scratch files can be committed by accident.",
                $"echo 'temp/' >> {Path.Join(repo, ".gitignore")}"));
        }

        var now = time.GetUtcNow();
        foreach (var entry in entries)
        {
            var age = now - entry.Modified;
            if (age < staleAfter)
            {
                continue;
            }

            var size = Bytes.HumanToGb(entry.Bytes);
            findings.Add(entry.IsWorktree
                ? new Finding(
                    "temp-stale-worktree",
                    Severity.Info,
                    entry.Path,
                    $"A registered worktree, untouched for {age.Days} days ({size}).",
                    $"cd {repo} && git worktree remove {entry.Path}",
                    Actionable: true)
                : new Finding("temp-stale", Severity.Info, entry.Path, $"Scratch, untouched for {age.Days} days ({size}).", $"rm -rf {entry.Path}", Actionable: true));
        }

        return findings;
    }

    /// <summary>Stashes, a detached HEAD, and an unignored local task file on the checkout itself.</summary>
    public async Task<IReadOnlyList<Finding>> CheckRepoStateAsync(string repo)
    {
        var findings = new List<Finding>();
        var stashes = (await git.LinesAsync(repo, "stash", "list").ConfigureAwait(false))?.Count ?? 0;
        if (stashes > 0)
        {
            findings.Add(new Finding(
                "stash-entries",
                Severity.Warn,
                repo,
                $"{stashes} stash entr{(stashes == 1 ? "y" : "ies")}. Every worktree shares one stash stack, so a bare `git stash pop` can restore another session's work into this tree.",
                $"cd {repo} && git stash list"));
        }

        if (await git.OutAsync(repo, "rev-parse", "--abbrev-ref", "HEAD").ConfigureAwait(false) == "HEAD")
        {
            findings.Add(new Finding("detached-head", Severity.Warn, repo, "Detached HEAD in the main checkout."));
        }

        foreach (var name in TodoFiles(repo))
        {
            if (!await IsIgnoredAsync(repo, Path.Join(repo, name)).ConfigureAwait(false))
            {
                findings.Add(new Finding(
                    "todo-not-ignored",
                    Severity.Warn,
                    name,
                    $"Local task file `{name}` exists but is not gitignored, so it can be committed by accident.",
                    $"echo '{name}' >> {Path.Join(repo, ".gitignore")}"));
            }
        }

        return findings;
    }

    /// <summary>
    /// The admin folder a linked worktree's <c>.git</c> file points at; null when <paramref name="path"/> has no such file
    /// (a main checkout's <c>.git</c> is a folder).
    /// </summary>
    public static string? WorktreeAdminDir(string path)
    {
        string text;
        try
        {
            text = File.ReadAllText(Path.Join(path, ".git"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var first = text.Trim().Split('\n', 2)[0].TrimEnd('\r');
        if (!first.StartsWith("gitdir:", StringComparison.Ordinal))
        {
            return null;
        }

        var target = first["gitdir:".Length..].Trim();
        return Path.IsPathRooted(target) ? target : Path.Join(path, target);
    }

    /// <summary>
    /// The admin folder of a worktree that belongs to another clone; null when it is this clone's. A registry entry
    /// survives a clone being copied or moved while the worktree is re-pointed at the new clone, and removing it from the
    /// old one deletes the new clone's work.
    /// </summary>
    /// <param name="treePath">The worktree.</param>
    /// <param name="ownWorktrees">This clone's <c>&lt;common git dir&gt;/worktrees</c>.</param>
    public static string? ForeignAdminDir(string treePath, string ownWorktrees)
    {
        var admin = WorktreeAdminDir(treePath);
        if (admin is null)
        {
            return null;
        }

        var parent = Path.GetDirectoryName(PathResolution.Resolve(admin));
        return parent is not null && PathResolution.Same(parent, PathResolution.Resolve(ownWorktrees)) ? null : admin;
    }

    /// <summary>The commit a worktree's HEAD last pointed at, from its own reflog; empty when none survives.</summary>
    public static string ReflogTip(string admin)
    {
        string[] lines;
        try
        {
            lines = File.ReadAllLines(Path.Join(admin, "logs", "HEAD"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }

        foreach (var line in lines.Reverse())
        {
            var fields = line.Split((char[]?)null, 3, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length >= 2 && !fields[1].All(c => c == '0'))
            {
                return fields[1];
            }
        }

        return string.Empty;
    }

    // The apparent bytes under a path, links counted as themselves and never followed.
    private static long Size(string path)
    {
        var top = new FileInfo(path);
        if (top.LinkTarget is { } link)
        {
            return System.Text.Encoding.UTF8.GetByteCount(link);
        }

        if (top.Exists)
        {
            return top.Length;
        }

        long total = 0;
        var stack = new Stack<string>([path]);
        while (stack.Count > 0)
        {
            IEnumerable<FileSystemInfo> entries;
            try
            {
                entries = [.. new DirectoryInfo(stack.Pop()).EnumerateFileSystemInfos()];
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var entry in entries)
            {
                if (entry.LinkTarget is { } target)
                {
                    total += System.Text.Encoding.UTF8.GetByteCount(target);
                }
                else if (entry is DirectoryInfo)
                {
                    stack.Push(entry.FullName);
                }
                else if (entry is FileInfo file)
                {
                    total += file.Length;
                }
            }
        }

        return total;
    }

    // Each local task file once, by its own name. On a case-insensitive disk TODO.md and todo.md can be one file: it
    // counts once, under the first name, unless the folder really lists both.
    private static List<string> TodoFiles(string repo)
    {
        string[] names = ["TODO.md", "todo.md"];
        var present = names.Where(name => File.Exists(Path.Join(repo, name))).ToList();
        if (present.Count < 2)
        {
            return present;
        }

        var listed = Directory.EnumerateFiles(repo).Select(Path.GetFileName).ToHashSet(StringComparer.Ordinal);
        return listed.Contains(names[0]) && listed.Contains(names[1]) ? present : present[..1];
    }

    // Strictly inside the folder, both resolved: git reports a worktree by its real path, so /var and /private/var match.
    private static bool IsBelow(string path, string folder)
    {
        var (inner, outer) = (PathResolution.Resolve(path), PathResolution.Resolve(folder));
        return !PathResolution.Same(inner, outer) && PathResolution.IsUnder(inner, outer);
    }

    private static string Prefix(string text, int length) => text.Length > length ? text[..length] : text;

    // Reported instead of worktree-dirty: with no commit behind HEAD, git shows every file as newly staged.
    private async Task<Finding> UnbornFindingAsync(string repo, Worktree tree)
    {
        var admin = WorktreeAdminDir(tree.Path);
        var tip = admin is null ? string.Empty : ReflogTip(admin);
        var lead = $"`{tree.Branch}` has no ref in this clone, so git shows every file as newly staged.";
        if (tip.Length > 0 && (await git.RunAsync(repo, ["cat-file", "-e", $"{tip}^{{commit}}"]).ConfigureAwait(false)).Ok)
        {
            return new Finding(
                "worktree-unborn-head",
                Severity.Error,
                tree.Path,
                $"{lead} Its last commit, {Prefix(tip, 10)}, is still here: recreating the ref restores it exactly.",
                $"cd {repo} && git branch {tree.Branch} {tip}");
        }

        var message = tip.Length > 0
            ? $"{lead} Its last commit, {Prefix(tip, 10)}, is not in this clone. If it was a PR head, `git fetch origin pull/<n>/head` brings it back; otherwise the files on disk are the only copy."
            : $"{lead} No reflog survives to say what it held, so the files on disk are the only copy.";
        return new Finding("worktree-unborn-head", Severity.Error, tree.Path, message, $"cd {tree.Path} && git status");
    }

    // Ignored, or git could not say: a missing or hung git does not cry wolf.
    private async Task<bool> IsIgnoredAsync(string repo, string path)
    {
        var ran = await git.RunAsync(repo, ["check-ignore", "-q", path], TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        return ran.Ok || ran.Code is Ran.NotFound or Ran.TimedOut;
    }
}
