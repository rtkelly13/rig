using Rig.Core.Estate;
using Rig.Core.Processes;
using Rig.Core.Roots;
using Rig.Core.Tests.Otel;

namespace Rig.Core.Tests.Estate;

/// <summary>
/// A real clone with <c>main</c> pushed to a bare origin beside it, as <c>estate shelve</c> sees one. The first commit
/// holds <c>README.md</c> and <c>gone.txt</c> and ignores <c>.env</c> and <c>node_modules/</c>; the shelf user is
/// <c>t.user</c>. Ported from <c>tests/fakes/git.py</c>'s <c>clone_with_origin</c> and <c>tests/behaviour/steps/git.py</c>.
/// </summary>
internal sealed class ShelveFixture : IDisposable
{
    /// <summary>The clock every plan reads unless a test wants the real one.</summary>
    public static readonly DateTimeOffset Now = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

    /// <summary>No live-session guard.</summary>
    public static readonly ShelveOptions NoIdle = new(TimeSpan.Zero);

    private readonly TempDir _temp = new();

    /// <summary>Creates the folders; <see cref="InitAsync"/> builds the repositories.</summary>
    public ShelveFixture()
    {
        Root = PathResolution.Resolve(_temp.Path);
        Clone = Path.Join(Root, "clone");
        Origin = Path.Join(Root, "origin.git");
        Git = new Git(Repos.Proc, Root);
        Store = new ShelfStore(Git);
    }

    /// <summary>The temp folder holding the clone and origin.</summary>
    public string Root { get; }

    /// <summary>The clone.</summary>
    public string Clone { get; }

    /// <summary>The bare origin.</summary>
    public string Origin { get; }

    /// <summary>Real git.</summary>
    public Git Git { get; }

    /// <summary>The shelves on <see cref="Origin"/>.</summary>
    public ShelfStore Store { get; }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>Builds the origin and the clone.</summary>
    public async Task<ShelveFixture> InitAsync()
    {
        _ = await Repos.GitAsync(Root, "init", "-q", "--bare", "-b", "main", Origin);
        _ = Directory.CreateDirectory(Clone);
        _ = await Repos.GitAsync(Clone, "init", "-q", "-b", "main");
        _ = await Repos.GitAsync(Clone, "config", "user.email", "t.user@example.com");
        _ = await Repos.GitAsync(Clone, "config", "user.name", "T");
        _ = await Repos.GitAsync(Clone, "config", "commit.gpgsign", "false");
        _ = await Repos.GitAsync(Clone, "config", "core.autocrlf", "false");
        _ = await Repos.GitAsync(Clone, "remote", "add", "origin", Origin);
        await File.WriteAllTextAsync(Path.Join(Clone, ".gitignore"), ".env\nnode_modules/\n", Token);
        await File.WriteAllTextAsync(Path.Join(Clone, "README.md"), "hi\n", Token);
        await File.WriteAllTextAsync(Path.Join(Clone, "gone.txt"), "delete me\n", Token);
        _ = await Repos.GitAsync(Clone, "add", ".");
        _ = await Repos.GitAsync(Clone, "commit", "-qm", "first");
        _ = await Repos.GitAsync(Clone, "push", "-q", "-u", "origin", "main");
        return this;
    }

    /// <summary>A shelver over real git with a fixed clock and no ledger, unless given others.</summary>
    public Shelver Shelver(IProc? proc = null, TimeProvider? clock = null, string? ledger = null)
    {
        var through = proc ?? Repos.Proc;
        return new Shelver(new Git(through, Root), new TreeRemoval(through), clock ?? new ManualClock(Now), ledger);
    }

    /// <summary>A linked worktree on a new <paramref name="branch"/> under <c>.worktrees/</c>, pushed unless told not to.</summary>
    public async Task<string> AddWorktreeAsync(string branch, bool push = true)
    {
        var path = Path.Join(Clone, ".worktrees", branch);
        _ = await Repos.GitAsync(Clone, "worktree", "add", "-q", "-b", branch, path);
        if (push)
        {
            _ = await Repos.GitAsync(path, "push", "-q", "-u", "origin", branch);
        }

        return path;
    }

    /// <summary>One local commit adding <paramref name="name"/>.</summary>
    public static async Task CommitAsync(string path, string name, string text)
    {
        await File.WriteAllTextAsync(Path.Join(path, name), text, Token);
        _ = await Repos.GitAsync(path, "add", name);
        _ = await Repos.GitAsync(path, "commit", "-qm", $"add {name}");
    }

    /// <summary>One change of each kind: modified README.md, staged staged.txt, deleted gone.txt, untracked new.txt.</summary>
    public static async Task MakeDirtyAsync(string path)
    {
        await File.WriteAllTextAsync(Path.Join(path, "README.md"), "edited\n", Token);
        await File.WriteAllTextAsync(Path.Join(path, "staged.txt"), "staged\n", Token);
        _ = await Repos.GitAsync(path, "add", "staged.txt");
        File.Delete(Path.Join(path, "gone.txt"));
        await File.WriteAllTextAsync(Path.Join(path, "new.txt"), "untracked\n", Token);
    }

    /// <summary>The worktree at <paramref name="path"/>, as git lists it.</summary>
    public async Task<Worktree> TreeAsync(string path) =>
        (await Worktree.ListAsync(Git, Clone)).Single(tree => PathResolution.Same(PathResolution.Resolve(tree.Path), PathResolution.Resolve(path)));

    /// <summary>Plans shelving <paramref name="path"/>.</summary>
    public async Task<ShelvePlan> PlanAsync(string path, ShelveOptions? options = null, IReadOnlyDictionary<string, PullRequest>? prs = null, Shelver? shelver = null)
    {
        shelver ??= Shelver();
        return await shelver.PlanAsync(
            Clone,
            await TreeAsync(path),
            await shelver.ShelfUserAsync(Clone),
            await Store.RemoteShelvesAsync(Clone),
            prs ?? new Dictionary<string, PullRequest>(StringComparer.Ordinal),
            options ?? NoIdle);
    }

    /// <inheritdoc />
    public void Dispose() => _temp.Dispose();
}
