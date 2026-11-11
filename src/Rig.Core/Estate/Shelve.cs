using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Rig.Core.Policy;
using Rig.Core.Processes;
using Rig.Core.Roots;

namespace Rig.Core.Estate;

/// <summary><c>git status --porcelain -z</c> split into tracked changes and untracked paths.</summary>
/// <param name="Changed">Tracked paths with changes, staged or not.</param>
/// <param name="Untracked">Untracked paths.</param>
public sealed record Porcelain(IReadOnlyList<string> Changed, IReadOnlyList<string> Untracked);

/// <summary>What <c>git status</c> and <c>ls-files</c> say about one worktree.</summary>
/// <param name="Changed">Tracked paths with changes.</param>
/// <param name="Untracked">Untracked paths.</param>
/// <param name="Ignored">Ignored paths; a wholly ignored folder ends in <c>/</c>.</param>
public sealed record ShelveStatus(IReadOnlyList<string> Changed, IReadOnlyList<string> Untracked, IReadOnlyList<string> Ignored)
{
    /// <summary>The worktree holds anything a snapshot would carry.</summary>
    public bool Dirty => Changed.Count > 0 || Untracked.Count > 0;
}

/// <summary>How <c>estate shelve</c> treats what it finds.</summary>
/// <param name="Idle">How long a worktree must be untouched before it is taken; zero turns the guard off.</param>
/// <param name="IncludeIgnored">Carry ignored, non-regenerable paths in the snapshot.</param>
/// <param name="DiscardIgnored">Let ignored, non-regenerable paths go with the worktree.</param>
/// <param name="ToUpstream">Also push a branch's own commits to its upstream, never forced.</param>
public sealed record ShelveOptions(TimeSpan Idle, bool IncludeIgnored = false, bool DiscardIgnored = false, bool ToUpstream = false)
{
    /// <summary>Two hours: long enough that an agent's session between commands still looks live.</summary>
    public static readonly TimeSpan DefaultIdle = TimeSpan.FromHours(2);

    /// <summary>The defaults: a two-hour idle guard, ignored paths left to a person.</summary>
    public static ShelveOptions Default { get; } = new(DefaultIdle);
}

/// <summary>What shelving one worktree would do, and why — or why it needs a person.</summary>
public sealed class ShelvePlan
{
    /// <summary>The clone the worktree belongs to.</summary>
    public required string Clone { get; init; }

    /// <summary>The worktree.</summary>
    public required string Path { get; init; }

    /// <summary>The shelf name.</summary>
    public required string Name { get; init; }

    /// <summary>The shelf ref on origin.</summary>
    public required string Ref { get; init; }

    /// <summary>The checked-out branch; empty when detached.</summary>
    public required string Branch { get; init; }

    /// <summary>HEAD when planned.</summary>
    public required string Head { get; init; }

    /// <summary><see cref="Shelver.Remove"/>, <see cref="Shelver.Push"/> or <see cref="Shelver.Snapshot"/>.</summary>
    public required string Action { get; set; }

    /// <summary>Commits on HEAD that no remote has.</summary>
    public int Orphaned { get; init; }

    /// <summary>Tracked paths with changes.</summary>
    public int Changed { get; init; }

    /// <summary>Untracked paths.</summary>
    public int Untracked { get; init; }

    /// <summary>The PR the branch heads, if any.</summary>
    public int? Pr { get; set; }

    /// <summary>That PR's GitHub state.</summary>
    public string PrState { get; set; } = string.Empty;

    /// <summary>Why the action is what it is.</summary>
    public Collection<string> Reasons { get; } = [];

    /// <summary>What a person must settle first; any entry means nothing is done.</summary>
    public Collection<string> DecisionNeeded { get; } = [];

    /// <summary>Ignored paths the snapshot will carry (<c>--include-ignored</c>).</summary>
    public Collection<string> CarryIgnored { get; } = [];

    /// <summary>Ignored paths that do not regenerate.</summary>
    public Collection<string> KeepIgnored { get; } = [];

    /// <summary>Their total size.</summary>
    public long KeepIgnoredBytes { get; set; }

    /// <summary>The shelf already on the remote, if any.</summary>
    public string Existing { get; init; } = string.Empty;

    /// <summary>The live upstream <c>--to-upstream</c> pushes to.</summary>
    public string Upstream { get; set; } = string.Empty;

    /// <summary>The plan as <c>estate shelve --json</c> prints it, in the Python field order.</summary>
    public JsonObject ToJson() => new()
    {
        ["clone"] = Clone,
        ["path"] = Path,
        ["name"] = Name,
        ["ref"] = Ref,
        ["branch"] = Branch,
        ["head"] = Head,
        ["action"] = Action,
        ["orphaned"] = Orphaned,
        ["changed"] = Changed,
        ["untracked"] = Untracked,
        ["pr"] = Pr,
        ["pr_state"] = PrState,
        ["reasons"] = Strings(Reasons),
        ["decision_needed"] = Strings(DecisionNeeded),
        ["carry_ignored"] = Strings(CarryIgnored),
        ["keep_ignored"] = Strings(KeepIgnored),
        ["keep_ignored_bytes"] = KeepIgnoredBytes,
        ["existing"] = Existing,
        ["upstream"] = Upstream,
    };

    private static JsonArray Strings(IEnumerable<string> items) => [.. items.Select(item => (JsonNode)item)];
}

/// <summary>What applying one plan did.</summary>
/// <param name="Plan">The plan.</param>
/// <param name="Outcome"><c>shelved</c>, <c>removed</c>, <c>failed</c> or <c>needs-decision</c>.</param>
/// <param name="Detail">What happened.</param>
/// <param name="Sha">The shelf commit, once there is one.</param>
public sealed record ShelveResult(ShelvePlan Plan, string Outcome, string Detail = "", string Sha = "")
{
    /// <summary>The plan's JSON with the outcome after it.</summary>
    public JsonObject ToJson()
    {
        var data = Plan.ToJson();
        data["outcome"] = Outcome;
        data["detail"] = Detail;
        data["sha"] = Sha;
        return data;
    }
}

/// <summary>
/// Resolves a worktree <c>tidy</c> refuses: pushes what only exists there to a <c>TEMP-SHELVED:</c> marker commit on a
/// side ref, proves origin holds it, then removes the worktree. The working tree is staged into a throwaway index and
/// written with <c>commit-tree</c>, so the real index, the branch and any open PR are never touched. A worktree that
/// needs a person's judgement (ignored files that do not regenerate, anything too big to push, a conflicting shelf, a
/// live session) is not shelved; its plan carries <see cref="ShelvePlan.DecisionNeeded"/> instead. Ported from
/// <c>src/rig/estate/shelve.py</c>.
/// </summary>
/// <param name="git">Runs git.</param>
/// <param name="removal">Finishes a removal git stopped part-way through.</param>
/// <param name="clock">Stamps the marker and the ledger.</param>
/// <param name="ledger">The JSONL file each removal is appended to; null keeps none.</param>
public sealed partial class Shelver(Git git, TreeRemoval removal, TimeProvider clock, string? ledger)
{
    /// <summary>Remove only: nothing here is missing from a remote.</summary>
    public const string Remove = "remove";

    /// <summary>Push local-only commits, then remove.</summary>
    public const string Push = "push+remove";

    /// <summary>Snapshot uncommitted work, push it, then remove.</summary>
    public const string Snapshot = "snapshot+remove";

    /// <summary>GitHub rejects files over 100 MB; well before that, a snapshot is the wrong home.</summary>
    public const long BigFile = 50L * 1024 * 1024;

    /// <summary>
    /// Ignored names that come back from a command (or were never worth keeping), so losing them costs nothing: the
    /// reclaim catalogue bar <c>temp</c>, which inside a worktree mixes run output with hand-written scripts, plus what a
    /// first estate run found blocking plans.
    /// </summary>
    public static readonly IReadOnlySet<string> Regenerable = new HashSet<string>(
        Reclaim.Categories.Where(category => category.Name != "temp").SelectMany(category => category.Names)
            .Concat([".DS_Store", "Thumbs.db", ".wireit", "packages", "paket-files", "TestResults", "logs"]),
        StringComparer.Ordinal);

    /// <summary>Folders that only ever hold generated content, so anything below them regenerates.</summary>
    public static readonly IReadOnlySet<string> CacheDirs = new HashSet<string>(
        ["node_modules", ".venv", "venv", "__pycache__", ".pytest_cache", ".ruff_cache", ".mypy_cache", ".next", ".turbo", ".wireit", "paket-files", "TestResults"],
        StringComparer.Ordinal);

    private static readonly string[] RegenerableFiles = [".DS_Store", "Thumbs.db"];
    private static readonly string[] RegenerableSuffixes = [".pyc", ".egg-info", ".tsbuildinfo", ".log", ".feature.cs"];
    private static readonly string[] RegenerablePaths = [".husky/_"];
    private static readonly string[] Containers = [".worktrees", "worktrees"];
    private static readonly TimeSpan CommitTimeout = TimeSpan.FromSeconds(60);
    private static readonly string[] ReadHead = ["read-tree", "HEAD"];
    private static readonly string[] AddAll = ["add", "-A", "--", "."];

    private readonly ShelfStore _store = new(git);

    // ------------------------------------------------------------------ naming and the commit message

    /// <summary>Makes one path segment a legal ref component.</summary>
    public static string RefComponent(string part)
    {
        ArgumentNullException.ThrowIfNull(part);
        var cleaned = IllegalRefChars().Replace(part, "-").Replace("..", "-", StringComparison.Ordinal).Replace("@{", "-", StringComparison.Ordinal).Trim('.');
        if (cleaned.EndsWith(".lock", StringComparison.Ordinal))
        {
            cleaned = cleaned[..^".lock".Length];
        }

        return cleaned.Length > 0 ? cleaned : "-";
    }

    /// <summary>
    /// Names a shelf after the worktree's path inside the clone. <c>.worktrees/</c> is the convention, so it is dropped;
    /// anything else (<c>temp/x</c>) stays, so two worktrees with the same leaf do not collide. A worktree outside the
    /// clone is named by its leaf.
    /// </summary>
    public static string ShelfName(string clone, string path)
    {
        ArgumentNullException.ThrowIfNull(clone);
        ArgumentNullException.ThrowIfNull(path);
        var resolved = PathResolution.Resolve(path);
        var root = PathResolution.Resolve(clone);
        IEnumerable<string> parts = PathResolution.IsUnder(resolved, root)
            ? System.IO.Path.GetRelativePath(root, resolved).Split(System.IO.Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries).Where(part => part != ".")
            : [System.IO.Path.GetFileName(path.TrimEnd(System.IO.Path.DirectorySeparatorChar))];
        var list = parts.ToList();
        if (list.Count > 1 && Containers.Contains(list[0], StringComparer.Ordinal))
        {
            list.RemoveAt(0);
        }

        return string.Join('/', list.Select(RefComponent));
    }

    /// <summary>The shelf ref for <paramref name="user"/>'s shelf <paramref name="name"/>.</summary>
    public static string ShelfRef(string user, string name) => $"{ShelfStore.RefRoot}/{user}/{name}";

    /// <summary>The marker commit's message. The subject prefix is the contract.</summary>
    public static string Message(string repo, string name, string branch, string baseSha, string path, int? pr, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(branch);
        ArgumentNullException.ThrowIfNull(baseSha);
        var where = branch.Length > 0 ? branch : $"detached@{Head(baseSha, 7)}";
        var lines = new List<string>
        {
            $"{ShelfStore.Prefix} {repo}/{name} ({where})",
            string.Empty,
            "Set aside by `estate shelve` so the worktree could be removed. Never merge this.",
            $"Restore with: estate unshelve {name} --repo <clone of {repo}>",
            string.Empty,
        };
        if (branch.Length > 0)
        {
            lines.Add($"Shelved-Branch: {branch}");
        }

        lines.Add($"Shelved-Base: {baseSha}");
        lines.Add($"Shelved-Path: {path}");
        if (pr is { } number)
        {
            lines.Add($"Shelved-PR: #{number.ToString(CultureInfo.InvariantCulture)}");
        }

        lines.Add($"Shelved-At: {IsoSeconds(at)}");
        lines.Add("Shelved-By: estate shelve");
        return string.Join('\n', lines) + "\n";
    }

    /// <summary>Python's <c>isoformat(timespec="seconds")</c>.</summary>
    public static string IsoSeconds(DateTimeOffset at) => at.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);

    /// <summary>Python's <c>isoformat()</c>: microseconds shown only when there are any.</summary>
    public static string Iso(DateTimeOffset at) =>
        at.Ticks % TimeSpan.TicksPerSecond / 10 == 0
            ? IsoSeconds(at)
            : at.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffffzzz", CultureInfo.InvariantCulture);

    // ------------------------------------------------------------------ inspection

    /// <summary>
    /// Splits <c>status --porcelain -z</c> into changed and untracked paths. A rename carries its source as an extra
    /// NUL-separated field, which a line-based parse would read as a separate entry. Pure.
    /// </summary>
    public static Porcelain ParseStatus(string porcelainZ)
    {
        ArgumentNullException.ThrowIfNull(porcelainZ);
        var changed = new List<string>();
        var untracked = new List<string>();
        var fields = porcelainZ.Split('\0');
        for (var i = 0; i < fields.Length; i++)
        {
            var entry = fields[i];
            if (entry.Length < 4)
            {
                continue;
            }

            var code = entry[..2];
            var path = entry[3..];
            if (code == "??")
            {
                untracked.Add(path);
                continue;
            }

            changed.Add(path);
            if (code[0] is 'R' or 'C')
            {
                i++;
            }
        }

        return new Porcelain(changed, untracked);
    }

    /// <summary>
    /// True when an ignored path is a build output or cache by name. A wholly ignored folder (listed with a trailing
    /// <c>/</c>) counts by its own name; a file by its name or suffix, or when it sits under a <see cref="CacheDirs"/>
    /// folder: <c>packages/api/.env</c> is not output.
    /// </summary>
    public static bool IsRegenerable(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var normal = path.Replace('\\', '/');
        var parts = normal.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return false;
        }

        var joined = string.Join('/', parts);
        var last = parts[^1];
        return (normal.EndsWith('/') && Regenerable.Contains(last))
            || RegenerableFiles.Contains(last, StringComparer.Ordinal)
            || parts.Any(part => RegenerableSuffixes.Any(suffix => part.EndsWith(suffix, StringComparison.Ordinal)))
            || parts[..^1].Any(CacheDirs.Contains)
            || RegenerablePaths.Any(known => joined == known
                || joined.StartsWith(known + "/", StringComparison.Ordinal)
                || $"/{joined}/".Contains($"/{known}/", StringComparison.Ordinal));
    }

    /// <summary>What <c>git status</c> and <c>ls-files</c> report for <paramref name="path"/>, read raw: a path may start with a space.</summary>
    public async Task<ShelveStatus> ReadStatusAsync(string path)
    {
        var raw = await git.RunAsync(path, ["status", "--porcelain", "-z", "--untracked-files=all"]).ConfigureAwait(false);
        var porcelain = ParseStatus(raw.Ok ? raw.Out : string.Empty);
        var listed = await git.RunAsync(path, ["ls-files", "-z", "--others", "--ignored", "--exclude-standard", "--directory"]).ConfigureAwait(false);
        var ignored = (listed.Ok ? listed.Out : string.Empty).Split('\0').Where(entry => entry.Length > 0).ToList();
        return new ShelveStatus(porcelain.Changed, porcelain.Untracked, ignored);
    }

    /// <summary>The namespace segment: the local part of the committer email.</summary>
    public async Task<string> ShelfUserAsync(string repo)
    {
        var email = await _store.OutAsync(repo, "config", "user.email").ConfigureAwait(false);
        var local = email.Split('@', 2)[0];
        if (local.Length == 0)
        {
            local = Environment.GetEnvironmentVariable("USERNAME") ?? string.Empty;
        }

        return RefComponent(local.Length > 0 ? local : "unknown");
    }

    /// <summary>
    /// Other repositories inside this worktree, which removing it would destroy. Registered worktrees of the same clone
    /// are checked directly; a walk of <paramref name="tops"/> finds any other <c>.git</c>. <c>git worktree remove</c>
    /// checks for neither.
    /// </summary>
    public async Task<IReadOnlyList<string>> NestedReposAsync(string clone, string path, IEnumerable<string> tops)
    {
        ArgumentNullException.ThrowIfNull(tops);
        var own = PathResolution.Resolve(path);
        var found = new List<string>();
        foreach (var tree in await Worktree.ListAsync(git, clone).ConfigureAwait(false))
        {
            var resolved = PathResolution.Resolve(tree.Path);
            if (!PathResolution.Same(resolved, own) && PathResolution.IsUnder(resolved, own))
            {
                found.Add(tree.Path);
            }
        }

        foreach (var top in tops)
        {
            var stack = new Stack<string>([System.IO.Path.Join(path, top.TrimEnd('/'))]);
            while (stack.Count > 0)
            {
                var current = stack.Pop();
                FileSystemInfo[] entries;
                try
                {
                    entries = new DirectoryInfo(current).GetFileSystemInfos();
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                foreach (var entry in entries)
                {
                    if (entry.Name == ".git")
                    {
                        found.Add(current);
                        break;
                    }

                    if (entry is DirectoryInfo && !IsLink(entry))
                    {
                        stack.Push(entry.FullName);
                    }
                }
            }
        }

        return [.. found.Select(PathResolution.Resolve).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
    }

    /// <summary>
    /// The newest mtime among what a live session would touch: the worktree folder, its index, HEAD and reflog, and every
    /// changed or untracked file. Not a full walk, so a <c>node_modules</c> is never stat-ed.
    /// </summary>
    public async Task<DateTimeOffset?> LastActivityAsync(string worktree, ShelveStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        var gitDir = await _store.OutAsync(worktree, "rev-parse", "--absolute-git-dir").ConfigureAwait(false);
        var candidates = new List<string> { worktree };
        if (gitDir.Length > 0)
        {
            candidates.AddRange([System.IO.Path.Join(gitDir, "index"), System.IO.Path.Join(gitDir, "HEAD"), System.IO.Path.Join(gitDir, "logs", "HEAD")]);
        }

        candidates.AddRange(status.Changed.Concat(status.Untracked).Take(2000).Select(entry => System.IO.Path.Join(worktree, entry)));
        DateTime? newest = null;
        foreach (var candidate in candidates)
        {
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
            {
                continue;
            }

            try
            {
                var written = File.GetLastWriteTimeUtc(candidate);
                newest = newest is null || written > newest ? written : newest;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
            }
        }

        return newest is { } at ? new DateTimeOffset(at, TimeSpan.Zero) : null;
    }

    /// <summary>Decides how one worktree would be resolved. Reads only; never writes.</summary>
    /// <param name="clone">The clone.</param>
    /// <param name="tree">The worktree, as <c>git worktree list</c> reported it.</param>
    /// <param name="user">The shelf namespace.</param>
    /// <param name="shelves">The shelves already on origin, <c>{ref: sha}</c>.</param>
    /// <param name="prs">Each branch's PR.</param>
    /// <param name="options">The flags.</param>
    public async Task<ShelvePlan> PlanAsync(
        string clone,
        Worktree tree,
        string user,
        IReadOnlyDictionary<string, string> shelves,
        IReadOnlyDictionary<string, PullRequest> prs,
        ShelveOptions options)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(shelves);
        ArgumentNullException.ThrowIfNull(prs);
        ArgumentNullException.ThrowIfNull(options);
        var now = clock.GetUtcNow();
        var path = tree.Path;
        var name = ShelfName(clone, path);
        var reference = ShelfRef(user, name);
        var head = tree.Head.Length > 0 ? tree.Head : await _store.OutAsync(path, "rev-parse", "HEAD").ConfigureAwait(false);
        var status = await ReadStatusAsync(path).ConfigureAwait(false);
        var orphanedOut = await _store.OutAsync(path, "rev-list", "--count", "HEAD", "--not", "--remotes").ConfigureAwait(false);
        var orphaned = orphanedOut.Length > 0 && orphanedOut.All(char.IsAsciiDigit) ? int.Parse(orphanedOut, CultureInfo.InvariantCulture) : 0;

        var plan = new ShelvePlan
        {
            Clone = clone,
            Path = path,
            Name = name,
            Ref = reference,
            Branch = tree.Branch,
            Head = head,
            Action = Remove,
            Orphaned = orphaned,
            Changed = status.Changed.Count,
            Untracked = status.Untracked.Count,
            Existing = shelves.GetValueOrDefault(reference, string.Empty),
        };
        if (prs.TryGetValue(tree.Branch, out var pr))
        {
            plan.Pr = pr.Number;
            plan.PrState = pr.State;
        }

        if (status.Dirty)
        {
            plan.Action = Snapshot;
            plan.Reasons.Add($"{status.Changed.Count} changed, {status.Untracked.Count} untracked");
        }
        else if (orphaned > 0)
        {
            plan.Action = Push;
            plan.Reasons.Add($"{orphaned} commit(s) on no remote");
        }
        else
        {
            plan.Reasons.Add("clean, every commit on a remote");
        }

        if (plan.Action == Push && plan.PrState == "MERGED")
        {
            // Squash-stranded: the work landed, so there is nothing to keep. Same authority `tidy --merged` uses.
            plan.Action = Remove;
            plan.Reasons.Add($"squash-merged in #{plan.Pr}");
        }

        if (options.ToUpstream && plan.Action != Remove && tree.Branch.Length > 0)
        {
            var upstream = await _store.OutAsync(clone, "rev-parse", "--abbrev-ref", $"{tree.Branch}@{{upstream}}").ConfigureAwait(false);
            if (upstream.StartsWith($"{ShelfStore.Remote}/", StringComparison.Ordinal) && plan.PrState != "MERGED")
            {
                plan.Upstream = upstream;
            }
            else
            {
                plan.Reasons.Add("--to-upstream: no live upstream, shelf ref only");
            }
        }

        var keep = status.Ignored.Where(entry => !IsRegenerable(entry)).ToList();
        var nested = await NestedReposAsync(clone, path, [.. keep, .. status.Untracked.Where(entry => entry.EndsWith('/'))]).ConfigureAwait(false);
        if (nested.Count > 0)
        {
            // No flag clears this: removing the worktree would delete the nested one.
            plan.DecisionNeeded.Add($"holds another repository, move it out first: {string.Join(", ", nested.Take(3))}");
        }

        var keptFiles = keep.SelectMany(entry => Files(System.IO.Path.Join(path, entry))).ToList();
        foreach (var entry in keep) { plan.KeepIgnored.Add(entry); }
        plan.KeepIgnoredBytes = keptFiles.Sum(file => file.Size);
        if (keep.Count > 0)
        {
            var megabytes = (plan.KeepIgnoredBytes / 1048576.0).ToString("N1", CultureInfo.InvariantCulture);
            var weight = $"{keep.Count} ignored path(s), {megabytes} MB";
            if (options.IncludeIgnored)
            {
                foreach (var entry in keep) { plan.CarryIgnored.Add(entry); }
                plan.Action = Snapshot;
                plan.Reasons.Add($"{weight} carried");
            }
            else if (options.DiscardIgnored)
            {
                plan.Reasons.Add($"{weight} discarded");
            }
            else
            {
                var shown = string.Join(", ", keep.Take(5)) + (keep.Count > 5 ? " …" : string.Empty);
                plan.DecisionNeeded.Add($"ignored, not regenerable ({weight}): {shown} — --include-ignored or --discard-ignored");
            }
        }

        var big = status.Untracked.Where(entry =>
        {
            var file = new FileInfo(System.IO.Path.Join(path, entry));
            return file.Exists && file.Length > BigFile;
        }).ToList();
        if (options.IncludeIgnored)
        {
            big.AddRange(keptFiles.Where(file => file.Size > BigFile).Select(file => System.IO.Path.GetRelativePath(path, file.Path)));
        }

        if (big.Count > 0)
        {
            plan.DecisionNeeded.Add($"over {BigFile / 1048576} MB: {string.Join(", ", big.Take(5))}");
        }

        if (tree.Locked)
        {
            plan.DecisionNeeded.Add(tree.LockReason.Length > 0 ? $"locked: {tree.LockReason}" : "locked");
        }

        // `--idle 0` disables the guard outright, including for mtimes ahead of now.
        var active = await LastActivityAsync(path, status).ConfigureAwait(false);
        if (options.Idle > TimeSpan.Zero && active is { } touched && now - touched < options.Idle)
        {
            var minutes = (long)Math.Floor((now - touched).TotalSeconds / 60);
            plan.DecisionNeeded.Add($"touched {minutes.ToString(CultureInfo.InvariantCulture)}m ago — may be a live session (--idle)");
        }

        if (plan.Existing.Length > 0 && plan.Action != Remove && !await HoldsAsync(plan, plan.Existing, status).ConfigureAwait(false))
        {
            plan.DecisionNeeded.Add($"{reference} already on {ShelfStore.Remote} at {Head(plan.Existing, 9)} with other content");
        }

        // Last, so every action change above (`--include-ignored` can turn a remove into a snapshot) is guarded. No flag
        // clears it: a clone made before its remote was restarted would push the history the restart dropped.
        if (plan.Action != Remove && await HistoryProblemAsync(path).ConfigureAwait(false) is { Length: > 0 } problem)
        {
            plan.DecisionNeeded.Add(problem);
        }

        return plan;
    }

    /// <summary>
    /// Why HEAD is not shown to share history with what the remote serves now; empty when it is. Asks the remote itself
    /// rather than trusting <c>origin/*</c> tracking refs, which in a clone from before a restart still name the old
    /// history. Only a remote with no default branch at all passes unjudged.
    /// </summary>
    public async Task<string> HistoryProblemAsync(string path)
    {
        var remote = ShelfStore.Remote;
        var listed = await _store.RunAsync(path, ["ls-remote", remote, "HEAD"]).ConfigureAwait(false);
        if (!listed.Ok)
        {
            return $"could not read {remote}'s default branch, so its history is unproven: {Head(listed.Detail, 120)}";
        }

        if (listed.Detail.Length == 0)
        {
            return string.Empty;
        }

        var sha = listed.Detail.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries)[0];
        if (!(await _store.RunAsync(path, ["cat-file", "-e", $"{sha}^{{commit}}"]).ConfigureAwait(false)).Ok)
        {
            return $"{remote}'s default branch is at {Head(sha, 9)}, which this clone has not fetched: fetch {remote} first";
        }

        if ((await _store.OutAsync(path, "merge-base", "HEAD", sha).ConfigureAwait(false)).Length == 0)
        {
            return $"shares no history with {remote}'s default branch; a shelf would push history {remote} does not have. Rebase onto {remote} first";
        }

        return string.Empty;
    }

    /// <summary>
    /// True when shelf commit <paramref name="sha"/> sits on the plan's HEAD and holds the tree as it is now. A snapshot
    /// that cannot be written counts as not holding it.
    /// </summary>
    public async Task<bool> HoldsAsync(ShelvePlan plan, string sha, ShelveStatus status)
    {
        var held = await HeldAsync(plan, sha, status).ConfigureAwait(false);
        return held.Ok && held.Detail.Length > 0;
    }

    /// <summary>
    /// Why the worktree no longer matches the shelf, or empty when it still does. <c>--all</c> plans everything before
    /// applying anything, so a worktree can change in between. Checked just before the forced remove.
    /// </summary>
    public async Task<string> DriftedAsync(ShelvePlan plan, string sha)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var status = await ReadStatusAsync(plan.Path).ConfigureAwait(false);
        var head = await _store.OutAsync(plan.Path, "rev-parse", "HEAD").ConfigureAwait(false);
        if (head != plan.Head)
        {
            return $"HEAD moved to {(head.Length > 0 ? Head(head, 9) : "?")} since planning";
        }

        var held = await HeldAsync(plan, sha, status).ConfigureAwait(false);
        if (!held.Ok)
        {
            return $"could not re-read the worktree: {held.Detail}";
        }

        if (held.Detail.Length == 0)
        {
            return "changed since planning";
        }

        var fresh = status.Ignored.Where(entry => !IsRegenerable(entry) && !plan.KeepIgnored.Contains(entry, StringComparer.Ordinal)).ToList();
        return fresh.Count > 0 ? $"new ignored path(s) since planning: {string.Join(", ", fresh.Take(5))}" : string.Empty;
    }

    // ------------------------------------------------------------------ acting

    /// <summary>Writes the working tree as a tree object via a throwaway index; the worktree's own index is never touched.</summary>
    public async Task<ShelfStep> SnapshotTreeAsync(string path, IReadOnlyList<string> carryIgnored)
    {
        ArgumentNullException.ThrowIfNull(carryIgnored);
        var scratch = Directory.CreateTempSubdirectory("estate-shelve-");
        try
        {
            var environment = new Dictionary<string, string>(StringComparer.Ordinal) { ["GIT_INDEX_FILE"] = System.IO.Path.Join(scratch.FullName, "index") };
            var steps = new List<string[]> { ReadHead, AddAll };
            if (carryIgnored.Count > 0)
            {
                steps.Add(["add", "-f", "--", .. carryIgnored]);
            }

            foreach (var step in steps)
            {
                var ran = await _store.RunAsync(path, step, environment).ConfigureAwait(false);
                if (!ran.Ok)
                {
                    return new ShelfStep(false, $"git {step[0]} {step[1]}: {ran.Detail}");
                }
            }

            var tree = await _store.RunAsync(path, ["write-tree"], environment).ConfigureAwait(false);
            return tree.Ok ? tree : new ShelfStep(false, $"git write-tree: {tree.Detail}");
        }
        finally
        {
            try
            {
                scratch.Delete(recursive: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>Creates the <c>TEMP-SHELVED:</c> commit on top of HEAD; the detail is its SHA, or why it failed.</summary>
    public async Task<ShelfStep> MarkerAsync(ShelvePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        string tree;
        if (plan.Action == Snapshot)
        {
            var written = await SnapshotTreeAsync(plan.Path, plan.CarryIgnored).ConfigureAwait(false);
            if (!written.Ok)
            {
                return written;
            }

            tree = written.Detail;
        }
        else
        {
            tree = $"{plan.Head}^{{tree}}";
        }

        var resolvedPath = PathResolution.Resolve(plan.Path);
        var resolvedClone = PathResolution.Resolve(plan.Clone);
        var path = Trim.IsStrictlyBelow(resolvedPath, resolvedClone)
            ? System.IO.Path.GetRelativePath(resolvedClone, resolvedPath).Replace(System.IO.Path.DirectorySeparatorChar, '/')
            : plan.Path;
        var text = Message(System.IO.Path.GetFileName(plan.Clone.TrimEnd(System.IO.Path.DirectorySeparatorChar)), plan.Name, plan.Branch, plan.Head, path, plan.Pr, clock.GetUtcNow());
        var done = await git.RunAsync(plan.Path, ["commit-tree", tree, "-p", plan.Head, "-F", "-"], CommitTimeout, stdinText: text).ConfigureAwait(false);
        return done.Ok ? new ShelfStep(true, done.Out.Trim()) : new ShelfStep(false, $"git commit-tree: {done.Err.Trim()}");
    }

    /// <summary>The proof required before deleting anything: origin holds <paramref name="sha"/> at <paramref name="reference"/>.</summary>
    public async Task<bool> OnRemoteAsync(string clone, string reference, string sha)
    {
        ArgumentNullException.ThrowIfNull(reference);
        var listed = await _store.RunAsync(clone, ["ls-remote", ShelfStore.Remote, reference]).ConfigureAwait(false);
        if (!listed.Ok || listed.Detail.Split('\t', 2)[0] != sha)
        {
            return false;
        }

        var local = reference.StartsWith("refs/heads/", StringComparison.Ordinal) ? reference["refs/heads/".Length..] : reference;
        _ = await _store.RunAsync(clone, ["fetch", "-q", ShelfStore.Remote, $"+{reference}:refs/remotes/{ShelfStore.Remote}/{local}"]).ConfigureAwait(false);
        return await _store.OutAsync(clone, "rev-list", "--count", sha, "--not", "--remotes").ConfigureAwait(false) == "0";
    }

    /// <summary>
    /// Deletes what <c>git worktree remove</c> leaves on Windows: pnpm's junctions in otherwise empty folders. Links are
    /// unlinked, never followed, and the sweep refuses outright if a regular file remains. Returns a detail suffix.
    /// </summary>
    public static string SweepLinkSkeleton(string root)
    {
        var folders = new List<string>();
        var links = new List<FileSystemInfo>();
        var stack = new Stack<string>([root]);
        try
        {
            while (stack.Count > 0)
            {
                var current = stack.Pop();
                folders.Add(current);
                foreach (var entry in new DirectoryInfo(current).EnumerateFileSystemInfos())
                {
                    if (IsLink(entry))
                    {
                        links.Add(entry);
                    }
                    else if (entry is DirectoryInfo)
                    {
                        stack.Push(entry.FullName);
                    }
                    else
                    {
                        return $", {root} kept: it still holds files";
                    }
                }
            }

            foreach (var link in links)
            {
                if (link is DirectoryInfo folder)
                {
                    folder.Delete(); // A junction or directory symlink: removes the link, never the target.
                }
                else
                {
                    File.Delete(link.FullName);
                }
            }

            for (var i = folders.Count - 1; i >= 0; i--)
            {
                Directory.Delete(folders[i]);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return $", leftover links not cleared: {error.Message}";
        }

        return $", {links.Count.ToString(CultureInfo.InvariantCulture)} leftover link(s) cleared";
    }

    /// <summary>
    /// Whether git unregistered the worktree and then stopped deleting it. The plan already settled that nothing in the
    /// tree is lost by removing it, so finishing the delete is the removal git was asked for.
    /// </summary>
    public async Task<bool> AbandonedPartWayAsync(ShelvePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var trees = await Worktree.ListAsync(git, plan.Clone).ConfigureAwait(false);
        if (!trees.Any(tree => tree.IsMain))
        {
            return false; // git could not list the worktrees, so nothing is known.
        }

        var target = PathResolution.Resolve(plan.Path);
        return Exists(plan.Path) && !trees.Any(tree => PathResolution.Same(PathResolution.Resolve(tree.Path), target));
    }

    /// <summary>Carries out one plan. Refuses anything with <see cref="ShelvePlan.DecisionNeeded"/>.</summary>
    public async Task<ShelveResult> ApplyAsync(ShelvePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.DecisionNeeded.Count > 0)
        {
            return new ShelveResult(plan, "needs-decision", string.Join("; ", plan.DecisionNeeded));
        }

        if (plan.Action == Remove)
        {
            var removed = await RemoveAsync(plan, force: false).ConfigureAwait(false);
            return Ledger(new ShelveResult(plan, removed.Ok ? "removed" : "failed", removed.Detail));
        }

        var remote = ShelfStore.Remote;
        if (plan.Upstream.Length > 0)
        {
            var branch = plan.Upstream[(remote.Length + 1)..];
            var pushed = await _store.RunAsync(plan.Clone, ["push", "-q", remote, $"{plan.Head}:refs/heads/{branch}"]).ConfigureAwait(false);
            if (!pushed.Ok)
            {
                return new ShelveResult(plan, "failed", $"push to {plan.Upstream} (no force): {pushed.Detail}");
            }
        }

        var sha = plan.Existing;
        if (sha.Length == 0)
        {
            var marker = await MarkerAsync(plan).ConfigureAwait(false);
            if (!marker.Ok)
            {
                return new ShelveResult(plan, "failed", marker.Detail);
            }

            sha = marker.Detail;
            var pushed = await _store.RunAsync(plan.Clone, ["push", "-q", remote, $"{sha}:{plan.Ref}"]).ConfigureAwait(false);
            if (!pushed.Ok)
            {
                return new ShelveResult(plan, "failed", $"push {plan.Ref}: {pushed.Detail}", sha);
            }
        }

        if (!await OnRemoteAsync(plan.Clone, plan.Ref, sha).ConfigureAwait(false))
        {
            return new ShelveResult(plan, "failed", $"{plan.Ref} not verified on {remote}; worktree kept", sha);
        }

        if (await DriftedAsync(plan, sha).ConfigureAwait(false) is { Length: > 0 } drift)
        {
            return new ShelveResult(plan, "failed", $"{drift}; worktree kept, shelf {plan.Ref} pushed", sha);
        }

        // Forced only now: the snapshot holds every change and the remote has it.
        var gone = await RemoveAsync(plan, force: true).ConfigureAwait(false);
        var shelf = plan.Ref.StartsWith("refs/heads/", StringComparison.Ordinal) ? plan.Ref["refs/heads/".Length..] : plan.Ref;
        return Ledger(new ShelveResult(plan, gone.Ok ? "shelved" : "failed", $"{shelf} · {gone.Detail}", sha));
    }

    [GeneratedRegex(@"[\s~^:?*\[\\\x00-\x1f\x7f]+")]
    private static partial Regex IllegalRefChars();

    private static string Head(string text, int length) => text.Length > length ? text[..length] : text;

    private static bool Exists(string path) => Directory.Exists(path) || File.Exists(path);

    private static bool IsLink(FileSystemInfo entry) => entry.LinkTarget is not null || entry.Attributes.HasFlag(FileAttributes.ReparsePoint);

    // Every file under root (or root itself) with its size, links never followed.
    private static List<(string Path, long Size)> Files(string root)
    {
        if (File.Exists(root))
        {
            return [(root, new FileInfo(root).Length)];
        }

        var found = new List<(string Path, long Size)>();
        var stack = new Stack<string>([root]);
        while (stack.Count > 0)
        {
            FileSystemInfo[] entries;
            try
            {
                entries = new DirectoryInfo(stack.Pop()).GetFileSystemInfos();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var entry in entries)
            {
                if (IsLink(entry))
                {
                    continue;
                }

                if (entry is DirectoryInfo)
                {
                    stack.Push(entry.FullName);
                }
                else if (entry is FileInfo file)
                {
                    found.Add((file.FullName, file.Length));
                }
            }
        }

        return found;
    }

    // Whether the shelf holds the worktree: Ok false when the snapshot could not be written, else the detail is the shared
    // tree, or empty when they differ.
    private async Task<ShelfStep> HeldAsync(ShelvePlan plan, string sha, ShelveStatus status)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(status);
        var parent = await _store.OutAsync(plan.Path, "rev-parse", "--verify", "-q", $"{sha}^").ConfigureAwait(false);
        if (parent != plan.Head)
        {
            return new ShelfStep(true, string.Empty);
        }

        var tree = plan.Head;
        if (status.Dirty)
        {
            var written = await SnapshotTreeAsync(plan.Path, plan.CarryIgnored).ConfigureAwait(false);
            if (!written.Ok)
            {
                return written;
            }

            tree = written.Detail;
        }

        var have = await _store.OutAsync(plan.Path, "rev-parse", $"{sha}^{{tree}}").ConfigureAwait(false);
        var want = await _store.OutAsync(plan.Path, "rev-parse", $"{tree}^{{tree}}").ConfigureAwait(false);
        return new ShelfStep(true, have.Length > 0 && have == want ? have : string.Empty);
    }

    // Removes the worktree, then its branch if nothing on it is local-only.
    private async Task<ShelfStep> RemoveAsync(ShelvePlan plan, bool force)
    {
        var target = PathResolution.Resolve(plan.Path);
        if (PathResolution.IsUnder(PathResolution.Resolve(Environment.CurrentDirectory), target))
        {
            Environment.CurrentDirectory = plan.Clone; // Windows will not delete a process's cwd.
        }

        IReadOnlyList<string> args = force ? ["worktree", "remove", "--force", plan.Path] : ["worktree", "remove", plan.Path];
        var removed = await _store.RunAsync(plan.Clone, args).ConfigureAwait(false);
        var detail = new StringBuilder("worktree removed");
        if (!removed.Ok)
        {
            if (!await AbandonedPartWayAsync(plan).ConfigureAwait(false))
            {
                return new ShelfStep(false, $"worktree remove: {removed.Detail}");
            }

            try
            {
                await removal.RemoveAsync(plan.Path).ConfigureAwait(false);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                return new ShelfStep(false, $"worktree remove: {removed.Detail}; finishing the delete: {error.Message}");
            }

            detail.Clear().Append("worktree removed (finished after git stopped part-way)");
        }

        if (Exists(plan.Path))
        {
            detail.Append(SweepLinkSkeleton(plan.Path));
        }

        if (plan.Branch.Length > 0)
        {
            var local = await _store.OutAsync(plan.Clone, "rev-list", "--count", plan.Branch, "--not", "--remotes").ConfigureAwait(false);
            var stillUsed = (await Worktree.ListAsync(git, plan.Clone).ConfigureAwait(false)).Any(tree => tree.Branch == plan.Branch);
            var deleted = local == "0" && !stillUsed && (await _store.RunAsync(plan.Clone, ["branch", "-D", plan.Branch]).ConfigureAwait(false)).Ok;
            detail.Append(deleted ? $", branch {plan.Branch} deleted" : $", branch {plan.Branch} kept");
        }

        return new ShelfStep(true, detail.ToString());
    }

    // Appends the result to the ledger. The ref and its trailers are the record; the ledger is a convenience.
    private ShelveResult Ledger(ShelveResult result)
    {
        if (ledger is null)
        {
            return result;
        }

        var line = new JsonObject { ["at"] = Iso(clock.GetUtcNow()) };
        foreach (var (key, value) in result.ToJson())
        {
            line[key] = value?.DeepClone();
        }

        try
        {
            File.AppendAllText(ledger, PyJson.DumpsLine(line) + "\n");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }

        return result;
    }
}
