using Rig.Core.Estate;
using Rig.Core.Processes;

namespace Rig.Specs.StepDefinitions;

/// <summary>
/// A worktree with uncommitted work, a shelf that holds it, and the unshelve that brings it back. In memory it models
/// the round trip over the production carry rules; on disk it runs the real <see cref="Shelver"/> against a bare origin.
/// </summary>
public sealed class ShelfFixture : IDisposable
{
    private const string Branch = "spike";
    private const string Edited = "edited, never committed\n";
    private const string Scratch = "scratch, never added\n";

    private readonly SpecScratch _scratch = new();
    private readonly IProc _proc = new SystemProc();
    private readonly Dictionary<string, string> _work = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _shelfWork = new(StringComparer.Ordinal);
    private readonly HashSet<string> _ignored = new(StringComparer.Ordinal);
    private SpecRepo? _repo;
    private Shelf? _shelf;
    private string _shelveDetail = string.Empty;
    private bool _real;
    private bool _present = true;

    /// <summary>True while the worktree folder (or the in-memory stand-in) still exists.</summary>
    public bool Present => _real && _repo is not null ? Directory.Exists(_repo.Tree) : _present;

    /// <summary>Creates the worktree with a tracked edit and an untracked scratch file.</summary>
    public async Task BuildAsync(bool real)
    {
        _real = real;
        if (!real)
        {
            _work["work.md"] = Edited;
            _work["scratch.md"] = Scratch;
            return;
        }

        _repo = await SpecRepo.CreateAsync(_scratch, _proc, Branch);
        await File.WriteAllTextAsync(Path.Join(_repo.Tree, "work.md"), Edited);
        await File.WriteAllTextAsync(Path.Join(_repo.Tree, "scratch.md"), Scratch);
    }

    /// <summary>Adds an ignored folder that a command regenerates, so no snapshot must carry it.</summary>
    public void AddRegenerableBuildOutput()
    {
        _ignored.Add("node_modules/pkg/index.js");
        if (_real && _repo is not null)
        {
            _ = Directory.CreateDirectory(Path.Join(_repo.Tree, "node_modules", "pkg"));
            File.WriteAllText(Path.Join(_repo.Tree, "node_modules", "pkg", "index.js"), "generated, regenerates\n");
        }
    }

    /// <summary>Shelves the worktree: the real snapshot and push, or the same decision over the in-memory model.</summary>
    public async Task ShelveAsync()
    {
        if (!_real)
        {
            foreach (var (path, content) in _work)
            {
                _shelfWork[path] = content;
            }

            foreach (var path in _ignored.Where(path => !Shelver.IsRegenerable(path)))
            {
                _shelfWork[path] = string.Empty;
            }

            _present = false;
            return;
        }

        var repo = _repo ?? throw new InvalidOperationException("the worktree was not built");
        var git = repo.Git(repo.ClonePath);
        var shelver = new Shelver(git, new TreeRemoval(_proc), TimeProvider.System, ledger: null);
        var plan = await shelver.PlanAsync(
            repo.ClonePath,
            await repo.RegisteredAsync(),
            await shelver.ShelfUserAsync(repo.ClonePath),
            await new ShelfStore(git).RemoteShelvesAsync(repo.ClonePath),
            new Dictionary<string, PullRequest>(StringComparer.Ordinal),
            new ShelveOptions(TimeSpan.Zero),
            new Busy([]));
        Assert.Equal(Shelver.Snapshot, plan.Action);
        Assert.Empty(plan.DecisionNeeded);

        var result = await shelver.ApplyAsync(plan);
        Assert.True(result.Outcome == "shelved", $"shelving failed: {result.Outcome} {result.Detail}");
        _shelveDetail = result.Detail;
        _shelf = (await new ShelfStore(git).ListAsync(repo.ClonePath)).Single(shelf => shelf.Trailers.Branch == Branch);
    }

    /// <summary>Restores the shelf, recreating the worktree as unstaged changes.</summary>
    public async Task RestoreAsync()
    {
        if (!_real)
        {
            _work.Clear();
            foreach (var (path, content) in _shelfWork)
            {
                _work[path] = content;
            }

            _present = true;
            return;
        }

        var repo = _repo ?? throw new InvalidOperationException("the worktree was not built");
        var shelf = _shelf ?? throw new InvalidOperationException("no shelf was taken");
        var step = await new ShelfStore(repo.Git(repo.ClonePath)).UnshelveAsync(shelf);
        Assert.True(step.Ok, $"unshelving failed: {step.Detail}");
    }

    /// <summary>Asserts the worktree is no longer there.</summary>
    public void AssertGone() =>
        Assert.False(Present, $"the worktree is still there after shelving ({_shelveDetail}, tree={_repo?.Tree})");

    /// <summary>Asserts origin holds a shelf carrying the worktree's work.</summary>
    public void AssertShelfHoldsWork()
    {
        if (_real)
        {
            Assert.NotNull(_shelf);
            Assert.True(_shelf.Snapshot, "the shelf is not a snapshot");
            return;
        }

        Assert.Equal(Edited, _shelfWork.GetValueOrDefault("work.md"));
        Assert.Equal(Scratch, _shelfWork.GetValueOrDefault("scratch.md"));
    }

    /// <summary>Asserts the tracked edit came back with the same bytes.</summary>
    public async Task AssertTrackedEditBack()
    {
        if (_real && _repo is not null)
        {
            Assert.Equal(Edited, await File.ReadAllTextAsync(Path.Join(_repo.Tree, "work.md")));
            return;
        }

        Assert.Equal(Edited, _work.GetValueOrDefault("work.md"));
    }

    /// <summary>Asserts the untracked file came back.</summary>
    public void AssertUntrackedBack()
    {
        if (_real && _repo is not null)
        {
            Assert.True(File.Exists(Path.Join(_repo.Tree, "scratch.md")), "the untracked file was not restored");
            return;
        }

        Assert.Equal(Scratch, _work.GetValueOrDefault("scratch.md"));
    }

    /// <summary>Asserts the ignored build output was not carried into the shelf or back out of it.</summary>
    public void AssertIgnoredNotCarried()
    {
        if (_real && _repo is not null)
        {
            Assert.False(Directory.Exists(Path.Join(_repo.Tree, "node_modules")), "the ignored build output came back");
            return;
        }

        Assert.DoesNotContain(_shelfWork.Keys, path => path.StartsWith("node_modules", StringComparison.Ordinal));
    }

    /// <summary>Asserts the regenerable ignored path is classified as something a command may regenerate.</summary>
    public void AssertBuildOutputRegenerates() =>
        Assert.All(_ignored, path => Assert.True(Shelver.IsRegenerable(path), $"{path} is not classified as regenerable"));

    /// <inheritdoc />
    public void Dispose() => _scratch.Dispose();
}
