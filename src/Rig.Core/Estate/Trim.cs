using Rig.Core.Processes;
using Rig.Core.Reconciliation;
using Rig.Core.Roots;
using Rig.Core.Storage;
using Rig.Core.Worktrees;

namespace Rig.Core.Estate;

/// <summary>One folder inside an idle worktree, and whether it may go.</summary>
/// <param name="Worktree">The worktree holding it.</param>
/// <param name="Path">The folder.</param>
/// <param name="Category">What kind of regenerable output it is.</param>
/// <param name="Size">What deleting it frees.</param>
/// <param name="Refused">Why it may not be removed; empty when it may.</param>
public sealed record Trimmable(string Worktree, string Path, string Category, TreeMeasure Size, string Refused = "") : IChange
{
    private ChangeKind? _kind;
    private string? _detail;

    /// <summary>The folder, as the reconciliation row's subject.</summary>
    public string Subject => Path;

    /// <summary>What reconciliation decided for this folder.</summary>
    public ChangeKind Kind
    {
        get => _kind ?? (Refused.Length > 0 ? ChangeKind.Refused : ChangeKind.WouldChange);
        set => _kind = value;
    }

    /// <summary>The refusal, or what applying the removal reported.</summary>
    public string Detail
    {
        get => _detail ?? Refused;
        set => _detail = value;
    }
}

/// <summary>What <see cref="Trim"/> reconciles: one clone and its idleness policy.</summary>
/// <param name="Repo">The clone whose linked worktrees are scanned.</param>
/// <param name="IdleDays">Days without git activity before a worktree is idle.</param>
/// <param name="Floor">Candidates below this many freeable bytes are dropped.</param>
/// <param name="Removal">Deletes a folder; required to apply a plan.</param>
public sealed record TrimTarget(
    string Repo,
    int IdleDays = Trim.DefaultIdleDays,
    long Floor = Trim.DefaultFloor,
    TreeRemoval? Removal = null);

/// <summary>The typed plan <see cref="Trim.PlanAsync"/> produces and <see cref="Trim.ApplyAsync"/> walks.</summary>
public sealed class TrimPlan : IPlan<Trimmable>
{
    /// <summary>The clone and policy the plan was built from.</summary>
    public required TrimTarget Target { get; init; }

    /// <summary>Regenerable folders inside idle worktrees, the most freeable first.</summary>
    public required IReadOnlyList<Trimmable> Items { get; init; }

    /// <summary>Every candidate, the most freeable first.</summary>
    public IReadOnlyList<Trimmable> Changes => Items;

    /// <summary>Removals that failed or could not be attempted.</summary>
    public IReadOnlyList<string> Errors =>
        [.. Items.Where(item => item.Kind == ChangeKind.Error).Select(item => $"{item.Path}: {item.Detail}")];
}

/// <summary>
/// Regenerable output inside worktrees nobody has used for a week.
/// </summary>
/// <remarks>
/// <para>
/// Idle is judged from git's own bookkeeping, not file times: the newest of the worktree's <c>index</c>, <c>HEAD</c>
/// and <c>logs/HEAD</c> moves on every commit, checkout, stage and status refresh, while a build rewrites
/// <c>node_modules</c> long after anyone looked at the branch. Bookkeeping that cannot be read counts as now, so a
/// worktree that cannot be judged is never idle.
/// </para>
/// <para>
/// Each folder proves its own safety. It is refused when git does not ignore it or tracks a file inside it, when it
/// resolves outside its worktree, or when a <c>.git</c> lives anywhere below it. <c>bin</c> and <c>obj</c> count only
/// beside a .NET project, <c>packages</c> only beside a <c>.sln</c>, <c>target</c> only beside <c>Cargo.toml</c>.
/// Locked worktrees, the main checkout and a worktree's own <c>temp/</c> are never touched.
/// </para>
/// </remarks>
/// <param name="git">Lists worktrees and asks what git tracks and ignores.</param>
/// <param name="time">The clock idleness is measured against.</param>
public sealed class Trim(Git git, TimeProvider time) : IReconciler<TrimTarget, TrimPlan, Trimmable>
{
    /// <summary>Days without git activity before a worktree is idle.</summary>
    public const int DefaultIdleDays = 7;

    /// <summary>Below this many freeable bytes a folder is not worth listing.</summary>
    public const long DefaultFloor = 10L * 1024 * 1024;

    /// <summary>How deep below a worktree a candidate may sit.</summary>
    public const int MaxDepth = 3;

    /// <summary>
    /// Prepended to every git command that deletes or moves files: Windows git stops at 260 characters without it,
    /// part-way through a tree, leaving a worktree unregistered and half-deleted.
    /// </summary>
    public static readonly IReadOnlyList<string> LongPaths = ["-c", "core.longpaths=true"];

    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(60);

    private static readonly Dictionary<string, string> ByName = new(StringComparer.Ordinal)
    {
        ["node_modules"] = "node_modules",
        [".venv"] = "python venv",
        ["venv"] = "python venv",
        ["dist"] = "js build output",
        [".next"] = "js build output",
        [".turbo"] = "js build output",
        ["storybook-static"] = "js build output",
        [".pytest_cache"] = "tool caches",
        [".mypy_cache"] = "tool caches",
        [".ruff_cache"] = "tool caches",
        [".tox"] = "tool caches",
        [".nox"] = "tool caches",
    };

    private static readonly string[] DotnetProjects = ["*.csproj", "*.fsproj", "*.vbproj"];

    private static readonly HashSet<string> NeverDescend = new(StringComparer.Ordinal) { ".git", ".worktrees", "temp" };

    /// <summary>The regenerable category <paramref name="path"/> belongs to, or null when it is not one.</summary>
    public static string? CategoryOf(string path)
    {
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path)) ?? string.Empty;
        if (ByName.TryGetValue(name, out var category))
        {
            return category;
        }

        return name switch
        {
            "bin" or "obj" when DotnetProjects.Any(pattern => Has(parent, pattern)) => "dotnet build output",
            "packages" when Has(parent, "*.sln") => "dotnet build output",
            "target" when File.Exists(Path.Join(parent, "Cargo.toml")) => "rust build output",
            _ => null,
        };
    }

    private static bool Has(string folder, string pattern)
    {
        return FileGuard.Try(() => Directory.EnumerateFiles(folder, pattern).Any(), false);
    }

    /// <summary>Every candidate folder within <see cref="MaxDepth"/> of <paramref name="root"/>, without entering one.</summary>
    public static IReadOnlyList<(string Path, string Category)> Candidates(string root)
    {
        var found = new List<(string, string)>();
        var stack = new Stack<(string Folder, int Depth)>([(root, 0)]);
        while (stack.TryPop(out var current))
        {
            var children = (TreeMeasure.Entries(current.Folder) ?? []).OfType<DirectoryInfo>()
                .Where(child => !child.Attributes.HasFlag(FileAttributes.ReparsePoint) && !NeverDescend.Contains(child.Name))
                .Select(child => child.FullName)
                .Order(PathOrder.Instance);
            foreach (var child in children)
            {
                if (CategoryOf(child) is { } category)
                {
                    found.Add((child, category));
                }
                else if (current.Depth + 1 < MaxDepth)
                {
                    stack.Push((child, current.Depth + 1));
                }
            }
        }

        return found;
    }

    /// <summary>When anyone last used <paramref name="worktree"/>, from git's admin files for it; now when they cannot be read.</summary>
    public DateTimeOffset LastActivity(string worktree)
    {
        var admin = RepoChecks.WorktreeAdminDir(worktree) ?? Path.Join(worktree, ".git");
        if (!Directory.Exists(admin))
        {
            return time.GetUtcNow();
        }

        var stamps = new[] { "index", "HEAD", Path.Join("logs", "HEAD") }
            .Select(name => Path.Join(admin, name))
            .Where(File.Exists)
            .Select(file => new DateTimeOffset(File.GetLastWriteTimeUtc(file), TimeSpan.Zero))
            .ToList();
        return stamps.Count == 0 ? time.GetUtcNow() : stamps.Max();
    }

    /// <summary>Linked worktrees of <paramref name="clone"/> that are unlocked, present, and idle <paramref name="idleDays"/> or more.</summary>
    public async Task<IReadOnlyList<string>> IdleWorktreesAsync(string clone, int idleDays = DefaultIdleDays)
    {
        var idle = new List<string>();
        foreach (var tree in await Worktree.ListAsync(git, clone).ConfigureAwait(false))
        {
            if (tree.IsMain || tree.Bare || tree.Locked || tree.Prunable || !Directory.Exists(tree.Path))
            {
                continue;
            }

            if (IsIdle(tree.Path, idleDays))
            {
                idle.Add(tree.Path);
            }
        }

        return idle;
    }

    private bool IsIdle(string worktree, int idleDays) => time.GetUtcNow() - LastActivity(worktree) >= TimeSpan.FromDays(idleDays);

    /// <summary>Regenerable folders inside <paramref name="clone"/>'s idle worktrees, the most freeable first.</summary>
    public async Task<IReadOnlyList<Trimmable>> ScanAsync(string clone, int idleDays = DefaultIdleDays, long floor = DefaultFloor)
    {
        var found = new List<Trimmable>();
        foreach (var worktree in await IdleWorktreesAsync(clone, idleDays).ConfigureAwait(false))
        {
            found.AddRange(await JudgeAsync(worktree, Candidates(worktree), floor).ConfigureAwait(false));
        }

        return [.. found.OrderByDescending(item => item.Size.Freeable)];
    }

    /// <summary>Scans idle worktrees into a typed plan, carrying the removal port the apply step needs.</summary>
    /// <param name="target">The clone and idleness policy.</param>
    /// <param name="ct">Stops the scan.</param>
    public async Task<TrimPlan> PlanAsync(TrimTarget target, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        ct.ThrowIfCancellationRequested();
        return new TrimPlan
        {
            Target = target,
            Items = await ScanAsync(target.Repo, target.IdleDays, target.Floor).ConfigureAwait(false),
        };
    }

    /// <summary>Rechecks and removes each planned folder, recording the outcome on its row.</summary>
    /// <param name="plan">The plan <see cref="PlanAsync"/> produced.</param>
    /// <param name="ct">Stops between removals.</param>
    public async Task<IReadOnlyList<string>> ApplyAsync(TrimPlan plan, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Target.Removal is not { } removal)
        {
            return ["trim requires a removal port to apply"];
        }

        foreach (var item in plan.Changes)
        {
            ct.ThrowIfCancellationRequested();
            if (item.Kind != ChangeKind.WouldChange)
            {
                continue;
            }

            var refused = item.Refused.Length > 0
                ? item.Refused
                : await RecheckAsync(item, plan.Target.IdleDays).ConfigureAwait(false);
            if (refused.Length > 0)
            {
                item.Kind = ChangeKind.Refused;
                item.Detail = refused;
                continue;
            }

            var status = await RemoveAsync(item, removal, ct).ConfigureAwait(false);
            item.Kind = status switch
            {
                "removed" => ChangeKind.Changed,
                _ when status.StartsWith("failed", StringComparison.Ordinal) => ChangeKind.Error,
                _ => ChangeKind.Refused,
            };
            item.Detail = status;
        }

        return plan.Errors;
    }

    /// <summary>Measures and rules on each candidate folder under <paramref name="root"/>; one below <paramref name="floor"/> is dropped.</summary>
    public async Task<IReadOnlyList<Trimmable>> JudgeAsync(string root, IReadOnlyList<(string Path, string Category)> found, long floor)
    {
        var relatives = found.Select(item => Relative(root, item.Path)).ToList();
        var ignored = relatives.Count == 0
            ? []
            : (await GitLinesAsync(root, ["check-ignore", "--", .. relatives]).ConfigureAwait(false) ?? []).ToHashSet(StringComparer.Ordinal);
        var judged = new List<Trimmable>();
        foreach (var ((path, category), relative) in found.Zip(relatives))
        {
            var size = TreeMeasure.Measure(path);
            if (size.HoldsCheckout)
            {
                judged.Add(new Trimmable(root, path, category, size, "holds a git checkout"));
                continue;
            }

            if (size.Freeable < floor)
            {
                continue;
            }

            judged.Add(new Trimmable(root, path, category, size, await RefusalAsync(root, path, ignored.Contains(relative)).ConfigureAwait(false)));
        }

        return judged;
    }

    /// <summary>Why <paramref name="path"/> may not be removed from the checkout at <paramref name="root"/>; empty when it may.</summary>
    public async Task<string> RefusalAsync(string root, string path, bool ignored)
    {
        if (!IsStrictlyBelow(path, root))
        {
            return "resolves outside the worktree";
        }

        var tracked = await GitLinesAsync(root, ["ls-files", "--", Relative(root, path)]).ConfigureAwait(false);
        if (tracked is null || tracked.Count > 0)
        {
            return "contains tracked files";
        }

        return ignored ? string.Empty : "not gitignored";
    }

    /// <summary>
    /// Why <paramref name="item"/>, found by an earlier scan, may no longer be removed; empty when it still may. Every
    /// test the scan ran is run again against the disk as it is now.
    /// </summary>
    public async Task<string> RecheckAsync(Trimmable item, int idleDays = DefaultIdleDays)
    {
        ArgumentNullException.ThrowIfNull(item);
        var folder = new DirectoryInfo(item.Path);
        if (folder.LinkTarget is not null)
        {
            return "is a link";
        }

        if (!folder.Exists)
        {
            return "no longer exists";
        }

        var registration = await RegistrationRefusalAsync(item.Worktree).ConfigureAwait(false);
        if (registration.Length > 0)
        {
            return registration;
        }

        if (CategoryOf(item.Path) != item.Category)
        {
            return "no longer regenerable output";
        }

        if (!IsIdle(item.Worktree, idleDays))
        {
            return "worktree is in use again";
        }

        if (TreeMeasure.Measure(item.Path).HoldsCheckout)
        {
            return "holds a git checkout";
        }

        if (!IsStrictlyBelow(item.Path, item.Worktree))
        {
            return "resolves outside the worktree";
        }

        var relative = Relative(item.Worktree, item.Path);
        var ignored = await GitLinesAsync(item.Worktree, ["check-ignore", "--", relative]).ConfigureAwait(false);
        return await RefusalAsync(item.Worktree, item.Path, ignored?.Contains(relative) == true).ConfigureAwait(false);
    }

    // A lock does not touch the activity files LastActivity reads, so the registration is read again.
    private async Task<string> RegistrationRefusalAsync(string worktree)
    {
        var tree = (await Worktree.ListAsync(git, worktree).ConfigureAwait(false))
            .FirstOrDefault(tree => PathResolution.Same(PathResolution.Resolve(tree.Path), PathResolution.Resolve(worktree)));
        return tree switch
        {
            null => "worktree is no longer registered",
            { Locked: true } => "worktree is locked",
            { IsMain: true } or { Bare: true } or { Prunable: true } => "no longer a linked worktree",
            _ => string.Empty,
        };
    }

    /// <summary>Deletes one candidate: <c>removed</c>, <c>refused</c>, or <c>failed: reason</c>.</summary>
    public static async Task<string> RemoveAsync(Trimmable item, TreeRemoval removal, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(removal);
        if (item.Refused.Length > 0)
        {
            return "refused";
        }

        try
        {
            await removal.RemoveAsync(item.Path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return $"failed: {e.Message}";
        }

        return "removed";
    }

    /// <summary>Whether <paramref name="path"/>, links resolved, lies inside <paramref name="root"/> and is not it.</summary>
    public static bool IsStrictlyBelow(string path, string root)
    {
        var resolved = PathResolution.Resolve(path);
        var folder = PathResolution.Resolve(root);
        return !PathResolution.Same(resolved, folder) && PathResolution.IsUnder(resolved, folder);
    }

    private static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');

    // Output lines of git; null when git failed, where exit 1 is a valid "no match".
    private async Task<IReadOnlyList<string>?> GitLinesAsync(string cwd, IReadOnlyList<string> args)
    {
        var ran = await git.RunAsync(cwd, args, GitTimeout).ConfigureAwait(false);
        return ran.Code is 0 or 1
            ? [.. ran.Out.Split('\n').Select(line => line.Trim().TrimEnd('/')).Where(line => line.Length > 0)]
            : null;
    }
}
