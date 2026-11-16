using System.Globalization;
using Rig.Core.Processes;
using Rig.Core.Reconciliation;
using Rig.Core.Roots;

namespace Rig.Core.Estate;

/// <summary>A kind of regenerable folder, and how it comes back.</summary>
/// <param name="Name">The category's name in reports.</param>
/// <param name="Names">Folder names, matched at the clone's root and one level below it.</param>
/// <param name="Regenerate">The command that brings it back.</param>
/// <param name="MayHoldWorktrees">Entries may be live checkouts, so each child is classified first.</param>
public sealed record ReclaimCategory(string Name, IReadOnlyList<string> Names, string Regenerate, bool MayHoldWorktrees = false);

/// <summary>One folder that could go.</summary>
/// <param name="Path">The folder.</param>
/// <param name="Category">Its category's name.</param>
/// <param name="Bytes">What it occupies on disk.</param>
/// <param name="Regenerate">How it comes back.</param>
/// <param name="IsWorktree">A registered worktree: git removes it, never a recursive delete.</param>
public sealed record Reclaimable(string Path, string Category, long Bytes, string Regenerate, bool IsWorktree = false) : IChange
{
    private ChangeKind? _kind;
    private string? _detail;

    /// <summary>The folder, as the reconciliation row's subject.</summary>
    public string Subject => Path;

    /// <summary>What reconciliation decided for this folder.</summary>
    public ChangeKind Kind
    {
        get => _kind ?? ChangeKind.WouldChange;
        set => _kind = value;
    }

    /// <summary>The command that brings it back, or the reason applying it failed.</summary>
    public string Detail
    {
        get => _detail ?? Regenerate;
        set => _detail = value;
    }
}

/// <summary>What <see cref="Reclaim"/> reconciles: one clone and the removal policy.</summary>
/// <param name="Repo">The clone to scan.</param>
/// <param name="Floor">Entries below this many bytes are ignored.</param>
/// <param name="Force">Relax the soft worktree refusals; never a hard one.</param>
/// <param name="Removal">Deletes a plain folder; required to apply a plan.</param>
/// <param name="Busy">The run's process snapshot; null skips the in-use check.</param>
public sealed record ReclaimTarget(
    string Repo,
    long Floor = Reclaim.ReportFloor,
    bool Force = false,
    TreeRemoval? Removal = null,
    Busy? Busy = null);

/// <summary>What one clone holds that regenerates.</summary>
/// <param name="Repo">The clone.</param>
/// <param name="Entries">Its regenerable folders, largest first.</param>
public sealed record RepoReclaim(string Repo, IReadOnlyList<Reclaimable> Entries) : IPlan<Reclaimable>
{
    /// <summary>Relax the soft worktree refusals; never a hard one.</summary>
    public bool Force { get; init; }

    /// <summary>Deletes a plain folder; required to apply the plan.</summary>
    public TreeRemoval? Removal { get; init; }

    /// <summary>The run's process snapshot; null skips the in-use check.</summary>
    public Busy? Busy { get; init; }

    /// <summary>The plan's typed change rows, largest first.</summary>
    public IReadOnlyList<Reclaimable> Changes => Entries;

    /// <summary>Removals that failed or could not be attempted.</summary>
    public IReadOnlyList<string> Errors =>
        [.. Entries.Where(entry => entry.Kind == ChangeKind.Error).Select(entry => $"{entry.Path}: {entry.Detail}")];

    /// <summary>Every entry's bytes.</summary>
    public long Total => Entries.Sum(entry => entry.Bytes);

    /// <summary>Bytes per category, largest first; a tie keeps the order the categories were first seen.</summary>
    public IReadOnlyList<KeyValuePair<string, long>> ByCategory()
    {
        var totals = new List<KeyValuePair<string, long>>();
        foreach (var entry in Entries)
        {
            var at = totals.FindIndex(pair => pair.Key == entry.Category);
            if (at < 0)
            {
                totals.Add(new(entry.Category, entry.Bytes));
            }
            else
            {
                totals[at] = new(entry.Category, totals[at].Value + entry.Bytes);
            }
        }

        return [.. totals.OrderByDescending(pair => pair.Value)];
    }
}

/// <summary>
/// Disk that regenerates from a command. Ported from <c>src/rig/estate/reclaim.py</c>.
/// </summary>
/// <remarks>
/// <c>temp/</c> is not homogeneous: worktrees live at <c>&lt;repo&gt;/temp/&lt;name&gt;</c> beside scratch files, so
/// each child is reported and classified on its own, and a registered worktree is marked for git to remove.
/// </remarks>
/// <param name="git">Lists the clone's worktrees.</param>
/// <param name="disk">Sizes each folder.</param>
public sealed class Reclaim(Git git, IDiskUsage disk) : IReconciler<ReclaimTarget, RepoReclaim, Reclaimable>
{
    /// <summary>Below this, an entry is noise in a report about gigabytes.</summary>
    public const long ReportFloor = 20L * 1024 * 1024;

    // A worktree holding a large node_modules takes a while to delete.
    private static readonly TimeSpan WorktreeRemoveTimeout = TimeSpan.FromMinutes(10);

    private readonly GitWorktreePort _worktrees = new(git);

    /// <summary>Every kind of regenerable folder, in report order.</summary>
    public static readonly IReadOnlyList<ReclaimCategory> Categories =
    [
        new("temp", ["temp"], "recreated as needed; worktrees via `git worktree add`", MayHoldWorktrees: true),
        new("node_modules", ["node_modules"], "pnpm install"),
        new("python venv", [".venv", "venv"], "uv sync"),
        new("js build output", ["dist", "build", ".next", "storybook-static", ".turbo"], "pnpm build"),
        new("rust/dotnet build output", ["target", "obj", "bin"], "cargo build / dotnet build"),
        new("tool caches", ["__pycache__", ".pytest_cache", ".ruff_cache", ".mypy_cache", "coverage"], "regenerated on next run"),
    ];

    /// <summary>
    /// The regenerable folders in <paramref name="repo"/>, at the root and one level down (the monorepo shape,
    /// <c>packages/*/node_modules</c>), plus registered worktrees living outside it. Each is kept when it reaches
    /// <paramref name="floor"/>.
    /// </summary>
    public async Task<RepoReclaim> ScanRepoAsync(string repo, long floor = ReportFloor, CancellationToken cancellationToken = default)
    {
        var trees = (await Worktree.ListAsync(git, repo, cancellationToken).ConfigureAwait(false)).Where(tree => !tree.IsMain).ToList();
        var worktrees = trees.Select(tree => PathResolution.Resolve(tree.Path)).ToHashSet(PathResolution.Comparer);
        var entries = new List<Reclaimable>();
        foreach (var category in Categories)
        {
            foreach (var name in category.Names)
            {
                foreach (var match in Matches(repo, name))
                {
                    if (category.MayHoldWorktrees)
                    {
                        entries.AddRange(await ExplodeTempAsync(match, worktrees, category, floor, cancellationToken).ConfigureAwait(false));
                        continue;
                    }

                    var size = await disk.SizeAsync(match, cancellationToken).ConfigureAwait(false);
                    if (size >= floor)
                    {
                        entries.Add(new Reclaimable(match, category.Name, size, category.Regenerate));
                    }
                }
            }
        }

        // Registered worktrees outside the clone (an estate-level temp/, say) hold disk the clone answers for.
        var root = PathResolution.Resolve(repo);
        foreach (var tree in trees)
        {
            var resolved = PathResolution.Resolve(tree.Path);
            if (PathResolution.IsUnder(resolved, root) || !Directory.Exists(resolved))
            {
                continue;
            }

            var size = await disk.SizeAsync(resolved, cancellationToken).ConfigureAwait(false);
            if (size >= floor)
            {
                entries.Add(new Reclaimable(tree.Path, "external worktree", size, "git worktree add", IsWorktree: true));
            }
        }

        return new RepoReclaim(repo, [.. entries.OrderByDescending(entry => entry.Bytes)]);
    }

    /// <summary>Scans the clone into a typed plan, carrying the removal policy the apply step needs.</summary>
    /// <param name="target">The clone and removal policy.</param>
    /// <param name="ct">Stops the scan.</param>
    public async Task<RepoReclaim> PlanAsync(ReclaimTarget target, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        var scan = await ScanRepoAsync(target.Repo, target.Floor, ct).ConfigureAwait(false);
        return scan with { Force = target.Force, Removal = target.Removal, Busy = target.Busy };
    }

    /// <summary>Walks the plan's regenerable entries, recording each removal's outcome on its row.</summary>
    /// <param name="plan">The plan <see cref="PlanAsync"/> produced.</param>
    /// <param name="ct">Stops between removals.</param>
    public async Task<IReadOnlyList<string>> ApplyAsync(RepoReclaim plan, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Removal is not { } removal)
        {
            return ["reclaim requires a removal port to apply"];
        }

        foreach (var entry in plan.Changes)
        {
            ct.ThrowIfCancellationRequested();
            if (entry.Kind != ChangeKind.WouldChange)
            {
                continue;
            }

            var status = await ApplyAsync(plan.Repo, entry, apply: true, plan.Force, removal, plan.Busy, ct).ConfigureAwait(false);
            entry.Kind = status switch
            {
                "removed" => ChangeKind.Changed,
                "would remove" => ChangeKind.WouldChange,
                "failed" => ChangeKind.Error,
                _ => ChangeKind.Refused,
            };
            entry.Detail = status;
        }

        return plan.Errors;
    }

    /// <summary>
    /// One entry's fate: <c>removed</c>, <c>would remove</c>, <c>failed</c>, or the reason it was refused.
    /// </summary>
    /// <remarks>
    /// Safety is per entry, not per clone: a commit is not endangered by deleting node_modules, so only a worktree
    /// entry, the one kind that can hold live state, is judged. Its facts go through <see cref="WorktreeRemovalJudge"/>,
    /// the same decision every other removal command uses: in use, unborn, foreign, locked and reflog-only refuse
    /// whatever <c>force</c> says, and ignored notes need an explicit discard that this command does not have.
    /// A status or history git cannot read counts against removal: an unjudged worktree is never removed unforced.
    /// </remarks>
    /// <param name="clone">The clone the entry is being reclaimed in.</param>
    /// <param name="entry">The folder.</param>
    /// <param name="apply">Delete; otherwise say what would go.</param>
    /// <param name="force">Relax the dirty and unpushed checks, never a hard reason.</param>
    /// <param name="removal">Deletes a plain folder.</param>
    /// <param name="busy">The run's process snapshot; null skips the in-use check.</param>
    /// <param name="cancellationToken">Stops a removal.</param>
    public async Task<string> ApplyAsync(
        string clone,
        Reclaimable entry,
        bool apply,
        bool force,
        TreeRemoval removal,
        Busy? busy = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(removal);
        if (entry.IsWorktree)
        {
            // git deletes a worktree's ignored files with it, and a worktree nested in its temp/ is one of them.
            var resolved = PathResolution.Resolve(entry.Path);
            var trees = await Worktree.ListAsync(git, clone, cancellationToken).ConfigureAwait(false);
            if (trees.Any(tree => Trim.IsStrictlyBelow(PathResolution.Resolve(tree.Path), resolved)))
            {
                return "holds a worktree";
            }

            // A separate clone, or another repository's worktree, in its ignored temp/ would go the same way.
            if (TreeMeasure.HasCheckoutBelow(entry.Path, ownGit: true))
            {
                return "holds a git checkout";
            }

            var tree = trees.FirstOrDefault(candidate => !candidate.IsMain && PathResolution.Same(PathResolution.Resolve(candidate.Path), resolved));
            if (tree is { } registered)
            {
                var facts = await _worktrees.ReadAsync(clone, registered, cancellationToken).ConfigureAwait(false);
                facts = facts with { InUse = busy?.WorkingIn(facts.Path) ?? false };
                var decision = WorktreeRemovalJudge.Judge(facts, new RemovalPolicy(DiscardUnpushedCommits: force));
                if (facts.OwnershipUnreadable)
                {
                    return "cannot read its git dir";
                }

                if (decision.Reasons.Contains(RemovalRefusalReason.ForeignClone))
                {
                    return "belongs to another clone";
                }

                if (decision.Reasons.Contains(RemovalRefusalReason.UnbornHead))
                {
                    return "unborn HEAD";
                }

                if (decision.Reasons.Contains(RemovalRefusalReason.Locked))
                {
                    return facts.LockReason.Length > 0 ? $"locked: {facts.LockReason}" : "locked";
                }

                if (decision.Reasons.Contains(RemovalRefusalReason.InUse))
                {
                    return "in use";
                }

                if (!force && facts.HasUncommittedChanges)
                {
                    return "dirty worktree";
                }

                if (!force && decision.Reasons.Contains(RemovalRefusalReason.UnpushedCommits))
                {
                    return "only copy";
                }

                if (decision.Reasons.Contains(RemovalRefusalReason.IgnoredNotes))
                {
                    return "ignored notes";
                }

                if (decision.Reasons.Contains(RemovalRefusalReason.ReflogOnlyCommit))
                {
                    return "reflog-only commits";
                }
            }
            else
            {
                // An entry git no longer lists cannot have its facts read; keep the old, unforced-only checks.
                if (!force && (await git.LinesAsync(entry.Path, "status", "--porcelain").ConfigureAwait(false)) is null or { Count: > 0 })
                {
                    return "dirty worktree";
                }

                if (!force && await OrphanedAsync(entry.Path).ConfigureAwait(false))
                {
                    return "only copy";
                }
            }

            if (!apply)
            {
                return "would remove";
            }

            var done = await git.RunAsync(clone, [.. Trim.LongPaths, "worktree", "remove", entry.Path], WorktreeRemoveTimeout, cancellationToken: cancellationToken).ConfigureAwait(false);
            return done.Ok ? "removed" : "failed";
        }

        if (!Trim.IsStrictlyBelow(entry.Path, clone))
        {
            return "outside clone";
        }

        // temp/ scratch can hold a clone or another repository's worktree, which no regenerate command brings back.
        if (entry.Category == "temp" && TreeMeasure.HasCheckoutBelow(entry.Path))
        {
            return "holds a git checkout";
        }

        if (!apply)
        {
            return "would remove";
        }

        try
        {
            await removal.RemoveAsync(entry.Path, cancellationToken).ConfigureAwait(false);
            return "removed";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return "failed";
        }
    }

    // Whether HEAD holds a commit on no remote; true when git cannot say.
    private async Task<bool> OrphanedAsync(string worktree)
    {
        var ran = await git.RunAsync(worktree, ["rev-list", "--count", "HEAD", "--not", "--remotes"]).ConfigureAwait(false);
        return !ran.Ok || !int.TryParse(ran.Out.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var count) || count > 0;
    }

    // `name` at the root, then `*/name`: real folders only, never a link to one.
    private static List<string> Matches(string repo, string name)
    {
        var found = new List<string>();
        void Consider(string candidate)
        {
            if (Directory.Exists(candidate) && new DirectoryInfo(candidate).LinkTarget is null)
            {
                found.Add(candidate);
            }
        }

        Consider(Path.Join(repo, name));
        foreach (var child in Children(repo, directoriesOnly: true))
        {
            Consider(Path.Join(child, name));
        }

        return found;
    }

    private static List<string> Children(string folder, bool directoriesOnly)
    {
        try
        {
            var children = directoriesOnly ? Directory.EnumerateDirectories(folder) : Directory.EnumerateFileSystemEntries(folder);
            return [.. children.Order(PathOrder.Instance)];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    // temp/ per child rather than one lump: some of it is live worktrees, some scratch, and each needs its own decision.
    private async Task<List<Reclaimable>> ExplodeTempAsync(
        string temp,
        HashSet<string> worktrees,
        ReclaimCategory category,
        long floor,
        CancellationToken cancellationToken)
    {
        var found = new List<Reclaimable>();
        foreach (var child in Children(temp, directoriesOnly: false))
        {
            if (new FileInfo(child).LinkTarget is not null)
            {
                continue;
            }

            var size = await disk.SizeAsync(child, cancellationToken).ConfigureAwait(false);
            if (size < floor)
            {
                continue;
            }

            var resolved = PathResolution.Resolve(child);
            var isTree = worktrees.Contains(resolved);
            if (!isTree && worktrees.Any(tree => PathResolution.IsUnder(tree, resolved)))
            {
                // A folder of worktrees (temp/rig/<name>) goes one worktree at a time, each with its own guards.
                found.AddRange(await ExplodeTempAsync(child, worktrees, category, floor, cancellationToken).ConfigureAwait(false));
                continue;
            }

            found.Add(new Reclaimable(child, category.Name, size, isTree ? "git worktree add" : category.Regenerate, isTree));
        }

        return found;
    }
}
