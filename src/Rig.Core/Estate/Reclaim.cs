using Rig.Core.Processes;
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
public sealed record Reclaimable(string Path, string Category, long Bytes, string Regenerate, bool IsWorktree = false);

/// <summary>What one clone holds that regenerates.</summary>
/// <param name="Repo">The clone.</param>
/// <param name="Entries">Its regenerable folders, largest first.</param>
public sealed record RepoReclaim(string Repo, IReadOnlyList<Reclaimable> Entries)
{
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
public sealed class Reclaim(Git git, IDiskUsage disk)
{
    /// <summary>Below this, an entry is noise in a report about gigabytes.</summary>
    public const long ReportFloor = 20L * 1024 * 1024;

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
        var trees = (await Worktree.ListAsync(git, repo).ConfigureAwait(false)).Where(tree => !tree.IsMain).ToList();
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
