using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Rig.Core.Processes;

namespace Rig.Core.Estate;

/// <summary>
/// One thing on this machine a human has to decide about: an entry of the decisions feed. The JSON keys, in this order,
/// are what <c>rig.estate.decisions.Decision</c> reads back for the web console.
/// </summary>
/// <param name="Kind">One of <see cref="Decisions.Kinds"/>.</param>
/// <param name="Repo">The clone's directory name.</param>
/// <param name="Path">The checkout, worktree or directory in question.</param>
/// <param name="Reason">Why it needs a person.</param>
/// <param name="SizeBytes">Its apparent size.</param>
/// <param name="SuggestedAction">What to do about it.</param>
/// <param name="Command">One command that carries out the suggested action.</param>
public sealed record Decision(string Kind, string Repo, string Path, string Reason, long SizeBytes, string SuggestedAction, string Command)
{
    /// <summary>As the feed writes it.</summary>
    public JsonObject ToJson() => new()
    {
        ["kind"] = Kind,
        ["repo"] = Repo,
        ["path"] = Path,
        ["reason"] = Reason,
        ["size_bytes"] = SizeBytes,
        ["suggested_action"] = SuggestedAction,
        ["command"] = Command,
    };
}

/// <summary>What an unfinished delete left: its files' bytes, how many, and a few of their names.</summary>
/// <param name="Bytes">The regular files' apparent size.</param>
/// <param name="Files">How many regular files remain.</param>
/// <param name="Sample">The first <see cref="Decisions.SampleFiles"/> of them, relative and with forward slashes.</param>
public sealed record Leftover(long Bytes, int Files, IReadOnlyList<string> Sample);

/// <summary>The newest PR per head ref for a clone, or null when GitHub could not be asked.</summary>
public delegate Task<IReadOnlyDictionary<string, PullRequest>?> PullRequestLookup(string clone, CancellationToken cancellationToken);

/// <summary>
/// The worktree questions only a human should answer, as one feed. Ported from the Python <c>rig.estate.decisions</c>, which keeps only the feed reader.
/// </summary>
/// <remarks>
/// <para>
/// Everything else the cleanup jobs touch regenerates, so they act on it alone. What lands here does not, or cannot be
/// judged from the disk: a checkout git refuses to read (<c>dubious-ownership</c>), an idle worktree holding uncommitted
/// files or commits on no remote (<c>unpushed-worktree</c>), an idle worktree whose branch's newest PR was closed
/// unmerged (<c>closed-pr-worktree</c>) or that never had one (<c>no-pr-worktree</c>), and a worktree directory git no
/// longer tracks (<c>locked-path</c>).
/// </para>
/// <para>
/// Idle means git's bookkeeping for the worktree has not moved for the idle period (<see cref="Trim.LastActivity"/>), so
/// live work never shows up. Each path gets one decision, the first kind that applies in that order. A merged or open PR
/// is not a question. PR state is asked for only for idle worktrees that are clean and pushed; when GitHub cannot be
/// asked, those worktrees are left out rather than reported as having no PR.
/// </para>
/// </remarks>
/// <param name="git">Reads each clone and worktree.</param>
/// <param name="time">The clock idleness is measured by.</param>
/// <param name="pullRequests">The newest PR per branch; <see cref="GitHub(PullRequests)"/> outside tests.</param>
/// <param name="windows">Paths compare without case, as <c>os.path.normcase</c> does there.</param>
public sealed class Decisions(Git git, TimeProvider time, PullRequestLookup pullRequests, bool windows)
{
    /// <summary>The feed's file name.</summary>
    public const string FeedName = "decisions.json";

    /// <summary>How many worktrees are judged at once.</summary>
    public const int Workers = 8;

    /// <summary>How many leftover files a <c>locked-path</c> reason names.</summary>
    public const int SampleFiles = 5;

    /// <summary>Each kind, most urgent first.</summary>
    public static IReadOnlyList<string> Kinds => KindOrder;

    private static readonly string[] KindOrder = ["dubious-ownership", "unpushed-worktree", "closed-pr-worktree", "no-pr-worktree", "locked-path"];

    private const string Shelve =
        "Preview a shelve, then re-run it with --apply: the work goes to a shelved/ branch on origin, verified, before the worktree is removed";

    // As Python's json writes it: a `+` or a non-ASCII path stays as it is, not escaped.
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly Trim trim = new(git, time);

    /// <summary>The newest PR per branch from GitHub; null for a clone with no GitHub origin.</summary>
    public static PullRequestLookup GitHub(PullRequests pulls)
    {
        ArgumentNullException.ThrowIfNull(pulls);
        return async (clone, cancellationToken) => await pulls.OriginAsync(clone, cancellationToken).ConfigureAwait(false) is null
            ? null
            : await pulls.HeadsAsync(clone, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary><c>%LOCALAPPDATA%\shared-utilities\jobs\decisions.json</c>, beside the jobs coordinator's state.</summary>
    /// <param name="localAppData">The <c>LOCALAPPDATA</c> variable; <c>~/AppData/Local</c> when unset.</param>
    /// <param name="home">The user's home folder.</param>
    public static string DefaultFeedPath(string? localAppData, string home) =>
        Path.Join(string.IsNullOrEmpty(localAppData) ? Path.Join(home, "AppData", "Local") : localAppData, "shared-utilities", "jobs", FeedName);

    /// <summary>The feed as a JSON array of decisions, indented as pydantic writes it.</summary>
    public static string DumpFeed(IEnumerable<Decision> decisions) =>
        new JsonArray([.. decisions.Select(item => (JsonNode)item.ToJson())]).ToJsonString(Indented);

    /// <summary>Replaces the feed at <paramref name="path"/> in one step, so a reader never sees half of it.</summary>
    public static void WriteFeed(string path, IEnumerable<Decision> decisions)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(folder))
        {
            _ = Directory.CreateDirectory(folder);
        }

        var temporary = Path.ChangeExtension(path, ".tmp");
        File.WriteAllText(temporary, DumpFeed(decisions) + "\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>Most urgent kind first, then biggest first, then by path.</summary>
    public static IReadOnlyList<Decision> Ordered(IEnumerable<Decision> decisions) =>
        [.. decisions.OrderBy(Rank).ThenByDescending(item => item.SizeBytes).ThenBy(item => item.Path, StringComparer.Ordinal)];

    /// <summary>Every decision under the given roots; a root that does not exist contributes nothing, and a path appears once.</summary>
    public async Task<IReadOnlyList<Decision>> CollectRootsAsync(IEnumerable<string> roots, int idleDays = Trim.DefaultIdleDays, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roots);
        var found = new List<Decision>();
        foreach (var root in roots.Where(Directory.Exists))
        {
            found.AddRange(await CollectAsync(root, idleDays, cancellationToken).ConfigureAwait(false));
        }

        return Ordered(Unique(found, windows));
    }

    /// <summary>One decision per path, the last one found standing; on Windows paths differing only in case or slash direction are one.</summary>
    public static IEnumerable<Decision> Unique(IEnumerable<Decision> decisions, bool windows)
    {
        ArgumentNullException.ThrowIfNull(decisions);
        var unique = new Dictionary<string, Decision>(StringComparer.Ordinal);
        foreach (var item in decisions)
        {
            unique[windows ? item.Path.ToLowerInvariant().Replace('/', '\\') : item.Path] = item;
        }

        return unique.Values;
    }

    /// <summary>Every decision under one code root, ordered.</summary>
    public async Task<IReadOnlyList<Decision>> CollectAsync(string root, int idleDays = Trim.DefaultIdleDays, CancellationToken cancellationToken = default)
    {
        var found = new List<Decision>();
        foreach (var clone in CloneDiscovery.Scan(root))
        {
            found.AddRange(await CloneDecisionsAsync(clone, idleDays, cancellationToken).ConfigureAwait(false));
        }

        return Ordered(found);
    }

    /// <summary>
    /// The decisions one clone and its worktrees need. Worktrees are judged <see cref="Workers"/> at a time: each costs a
    /// few git calls and, when it becomes a decision, a walk of its files, and a work clone can hold a hundred.
    /// </summary>
    public async Task<IReadOnlyList<Decision>> CloneDecisionsAsync(string clone, int idleDays = Trim.DefaultIdleDays, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(clone);
        var repo = Path.GetFileName(Path.TrimEndingDirectorySeparator(clone));
        if (await IsDubiousAsync(clone).ConfigureAwait(false))
        {
            return [Dubious(repo, clone)];
        }

        var now = time.GetUtcNow();
        var trees = (await Worktree.ListAsync(git, clone).ConfigureAwait(false))
            .Where(tree => !(tree.IsMain || tree.Bare || tree.Prunable) && Directory.Exists(tree.Path))
            .ToList();
        var verdicts = await MapAsync(trees, tree => JudgeAsync(repo, tree, idleDays, now), cancellationToken).ConfigureAwait(false);
        var found = verdicts.Where(verdict => verdict.Decision is not null).Select(verdict => verdict.Decision!).ToList();
        var quiet = trees.Where((_, index) => verdicts[index].NeedsPullRequest).ToList();
        var heads = quiet.Count > 0 ? await pullRequests(clone, cancellationToken).ConfigureAwait(false) : null;
        if (heads is not null)
        {
            var pulls = await MapAsync(quiet, tree => Task.FromResult(PullRequestDecision(repo, clone, tree, heads, idleDays)), cancellationToken).ConfigureAwait(false);
            found.AddRange(pulls.Where(item => item is not null).Select(item => item!));
        }

        found.AddRange(await MapAsync(LeftBehind(clone), path => Task.FromResult(Locked(repo, path)), cancellationToken).ConfigureAwait(false));
        return found;
    }

    /// <summary>True when git refuses to read <paramref name="path"/> because someone else owns it.</summary>
    public async Task<bool> IsDubiousAsync(string path)
    {
        var ran = await git.RunAsync(path, ["rev-parse", "--git-dir"]).ConfigureAwait(false);
        return !ran.Ok && ran.Err.Contains("dubious ownership", StringComparison.Ordinal);
    }

    /// <summary>Worktree directories under <paramref name="clone"/> that a previous delete did not finish.</summary>
    public static IReadOnlyList<string> LeftBehind(string clone)
    {
        // Inside a worktree container a directory with no `.git` at all is what an interrupted delete leaves; in plain
        // `temp/` it is ordinary scratch, so a marker is required.
        var containers = new[] { Path.Join(clone, ".worktrees"), Path.Join(clone, "temp", "worktrees") };
        var found = containers.SelectMany(Subdirs).Where(child => IsLeftBehind(child, needsMarker: false));
        var scratch = Subdirs(Path.Join(clone, "temp")).Where(child => Path.GetFileName(child) != "worktrees" && IsLeftBehind(child, needsMarker: true));
        return [.. found, .. scratch];
    }

    /// <summary>Walks <paramref name="path"/> without entering links, counting the regular files still in it.</summary>
    public static Leftover LeftoverIn(string path)
    {
        long total = 0;
        var count = 0;
        var sample = new List<string>();
        var stack = new Stack<string>([path]);
        while (stack.TryPop(out var current))
        {
            foreach (var entry in TreeMeasure.Entries(current) ?? [])
            {
                try
                {
                    if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        continue;
                    }

                    if (entry is DirectoryInfo)
                    {
                        stack.Push(entry.FullName);
                        continue;
                    }

                    total += ((FileInfo)entry).Length;
                    count++;
                    if (sample.Count < SampleFiles)
                    {
                        sample.Add(Path.GetRelativePath(path, entry.FullName).Replace('\\', '/'));
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // Gone or unreadable since the listing: passed over, as Python's OSError is.
                }
            }
        }

        return new(total, count, sample);
    }

    private static int Rank(Decision item)
    {
        var index = Array.IndexOf(KindOrder, item.Kind);
        return index < 0 ? Kinds.Count : index;
    }

    // As Python's str(Path) prints git's forward-slashed path on Windows.
    private static string Shown(string path) => Path.GetFullPath(path);

    private static long Size(string path) => Footprint.Of(path).Bytes;

    private static Decision Dubious(string repo, string path) => new(
        "dubious-ownership",
        repo,
        path,
        "git refuses to read it: the directory is owned by another account",
        Size(path),
        $"Trust it with safe.directory, or take ownership if it is yours: takeown /f \"{path}\" /r /d y",
        $"git config --global --add safe.directory {path.Replace('\\', '/')}");

    private static Decision Unpushed(string repo, string path, int orphaned, bool dirty, int idleDays)
    {
        var parts = new List<string>();
        if (orphaned > 0)
        {
            parts.Add($"{orphaned} commit(s) on no remote");
        }

        if (dirty)
        {
            parts.Add("uncommitted changes");
        }

        // Not `push -u origin <branch>`: that lands half-finished work on a PR branch. The shelve preview pushes
        // nothing and lists anything a person must settle first.
        return new("unpushed-worktree", repo, path, $"{string.Join(" and ", parts)}; untouched for {idleDays}+ days", Size(path), Shelve, $"estate shelve \"{path}\"");
    }

    private static Decision? PullRequestDecision(string repo, string clone, Worktree tree, IReadOnlyDictionary<string, PullRequest> heads, int idleDays)
    {
        var path = Shown(tree.Path);
        var remove = $"cd {clone} && git worktree remove {path}";
        var pull = tree.Branch.Length > 0 ? heads.GetValueOrDefault(tree.Branch) : null;
        if (pull is null)
        {
            var reason = tree.Branch.Length > 0
                ? $"`{tree.Branch}` has never had a PR; untouched for {idleDays}+ days"
                : $"detached HEAD, so no PR can match it; untouched for {idleDays}+ days";
            return new("no-pr-worktree", repo, path, reason, Size(path), "Open a PR if the work is wanted; otherwise remove the worktree", remove);
        }

        return pull.State == "CLOSED"
            ? new(
                "closed-pr-worktree",
                repo,
                path,
                $"PR #{pull.Number} for `{tree.Branch}` was closed without merging",
                Size(path),
                "Remove the worktree if the work is abandoned, or reopen the PR",
                remove)
            : null;
    }

    private static Decision Locked(string repo, string path)
    {
        var found = LeftoverIn(path);
        if (found.Files == 0)
        {
            // `rmdir /s` removes a junction without entering it, so with no files left (what pnpm's junctions leave
            // after `git worktree remove`) it can only delete links and empty directories.
            return new(
                "locked-path",
                repo,
                path,
                "git no longer tracks it, and only links and empty directories remain",
                0,
                "Delete the skeleton; it holds no files",
                $"cmd /c rmdir /s /q \"{path}\"");
        }

        var more = found.Files > found.Sample.Count ? $" and {found.Files - found.Sample.Count} more" : string.Empty;
        return new(
            "locked-path",
            repo,
            path,
            $"git no longer tracks it, but {found.Files} file(s) remain: {string.Join(", ", found.Sample)}{more}. Some may be hand-written; git cannot restore them",
            found.Bytes,
            "List the files, keep anything hand-written, then delete the rest",
            $"cmd /c dir /s /b /a-d \"{path}\"");
    }

    // A `.git` directory is a clone of its own, and a `.git` file whose admin dir exists is a live worktree of some repo
    // (not necessarily this clone).
    private static bool IsLeftBehind(string path, bool needsMarker)
    {
        var marker = Path.Join(path, ".git");
        if (Directory.Exists(marker))
        {
            return false;
        }

        if (File.Exists(marker))
        {
            return GitDir(marker) is { } admin && !Path.Exists(admin);
        }

        return !needsMarker;
    }

    // Where a worktree's `.git` file points, or null when it does not say.
    private static string? GitDir(string marker)
    {
        string text;
        try
        {
            text = File.ReadAllText(marker, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            return null;
        }

        var at = text.IndexOf("gitdir:", StringComparison.Ordinal);
        if (at < 0 || !string.IsNullOrWhiteSpace(text[..at]))
        {
            return null;
        }

        var admin = text[(at + "gitdir:".Length)..].Trim();
        return Path.IsPathRooted(admin) ? admin : Path.Join(Path.GetDirectoryName(marker), admin);
    }

    private static IEnumerable<string> Subdirs(string path)
    {
        List<string> children;
        try
        {
            children = [.. new DirectoryInfo(path).EnumerateDirectories("*", new EnumerationOptions { AttributesToSkip = 0 })
                .Where(child => child.LinkTarget is null)
                .Select(child => child.FullName)];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        children.Sort(PathOrder.Instance);
        return children;
    }

    private async Task<Verdict> JudgeAsync(string repo, Worktree tree, int idleDays, DateTimeOffset now)
    {
        var path = Shown(tree.Path);
        if (await IsDubiousAsync(tree.Path).ConfigureAwait(false))
        {
            return new(Dubious(repo, path));
        }

        if (now - trim.LastActivity(tree.Path) < TimeSpan.FromDays(idleDays))
        {
            return new();
        }

        var orphaned = await Exposure.CountAsync(git, tree.Path, "rev-list", "--count", "HEAD", "--not", "--remotes").ConfigureAwait(false);
        var dirty = (await git.OutAsync(tree.Path, "status", "--porcelain").ConfigureAwait(false)).Length > 0;
        return orphaned > 0 || dirty ? new(Unpushed(repo, path, orphaned, dirty, idleDays)) : new(NeedsPullRequest: !tree.Locked);
    }

    // Each item mapped Workers at a time, the results in the items' order.
    private static async Task<IReadOnlyList<TResult>> MapAsync<TItem, TResult>(IReadOnlyList<TItem> items, Func<TItem, Task<TResult>> map, CancellationToken cancellationToken)
    {
        using var gate = new SemaphoreSlim(Workers);
        return await Task.WhenAll(items.Select(async item =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await Task.Run(() => map(item), cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _ = gate.Release();
            }
        })).ConfigureAwait(false);
    }

    // What the disk alone says about one worktree; idle, clean and pushed, only its PR state can make it a decision.
    private sealed record Verdict(Decision? Decision = null, bool NeedsPullRequest = false);
}
