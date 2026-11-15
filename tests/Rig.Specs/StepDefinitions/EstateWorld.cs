using System.Diagnostics;
using Rig.Core.Estate;
using Rig.Core.Processes;
using Rig.Core.Roots;

namespace Rig.Specs.StepDefinitions;

/// <summary>The worktree shapes the safety specs name.</summary>
public enum WorktreeShape
{
    /// <summary>No changes, every commit on origin.</summary>
    Clean,

    /// <summary>No changes; every commit, including the branch's own, is on origin.</summary>
    Finished,

    /// <summary>A tracked edit and an untracked file nobody has committed.</summary>
    Dirty,

    /// <summary>An ignored <c>TODO.md</c>, which no command regenerates.</summary>
    IgnoredNotes,

    /// <summary>A shell, agent or terminal is working in it.</summary>
    InUse,

    /// <summary>Git was told to keep it.</summary>
    Locked,

    /// <summary>Its admin entry resolves into another clone.</summary>
    ForeignClone,
}

/// <summary>
/// One worktree under test, either as facts in memory or as a real clone, bare origin and linked worktree on disk.
/// Both executors feed the same production <see cref="WorktreeRemovalJudge"/> and the same assertions.
/// </summary>
public sealed class EstateWorld : IDisposable
{
    private const string Branch = "spike";

    private readonly SpecScratch _scratch = new();
    private readonly IProc _proc = new SystemProc();
    private SpecRepo? _repo;
    private string _canaryPath = string.Empty;
    private string _canaryContent = string.Empty;
    private string? _busy;
    private bool _real;

    /// <summary>What the removal port observed, or the equivalent facts the in-memory executor built.</summary>
    public WorktreeFacts Facts { get; private set; } = new("/specs/worktree");

    /// <summary>What the calling command allows.</summary>
    public RemovalPolicy Policy { get; set; } = RemovalPolicy.Strict;

    /// <summary>The judge's answer for <see cref="Facts"/> under <see cref="Policy"/>.</summary>
    public RemovalDecision Decision { get; private set; } = RemovalDecision.Allowed;

    /// <summary>How long the pure decision took, once <see cref="Judge"/> has run.</summary>
    public TimeSpan DecisionDuration { get; private set; }

    /// <summary>Whether the removal actually happened.</summary>
    public bool Removed { get; private set; }

    /// <summary>Whether the canary's content is on origin.</summary>
    public bool Published { get; private set; }

    /// <summary>A one-line account of the decision and removal, for a failed assertion.</summary>
    public string Describe() => $"remove={Decision.Remove}, reasons=[{string.Join(", ", Decision.Reasons)}], inUse={Facts.InUse}, changed={Facts.HasUncommittedChanges}, notes={Facts.HasIgnoredNotes}, locked={Facts.IsLocked}, foreign={Facts.IsForeignClone}, removed={Removed}, real={_real}";

    /// <summary>Builds <paramref name="shape"/> in memory or on disk.</summary>
    public async Task BuildAsync(WorktreeShape shape, bool real)
    {
        _real = real;
        if (!real)
        {
            Facts = MemoryFacts(shape);
            Published = true;
            return;
        }

        _repo = await SpecRepo.CreateAsync(_scratch, _proc, Branch);
        _canaryPath = Path.Join(_repo.Tree, "work.md");
        _canaryContent = "canary, committed and pushed\n";
        Published = true;

        switch (shape)
        {
            case WorktreeShape.Dirty:
                _canaryContent = "edited, never committed\n";
                await File.WriteAllTextAsync(_canaryPath, _canaryContent);
                await File.WriteAllTextAsync(Path.Join(_repo.Tree, "scratch.md"), "scratch, never added\n");
                break;
            case WorktreeShape.IgnoredNotes:
                _canaryPath = Path.Join(_repo.Tree, "TODO.md");
                _canaryContent = "notes nothing can regenerate\n";
                await File.WriteAllTextAsync(_canaryPath, _canaryContent);
                break;
            case WorktreeShape.InUse:
                _busy = Directory.CreateDirectory(Path.Join(_repo.Tree, "src")).FullName;
                break;
            case WorktreeShape.Locked:
                _ = await _repo.GitAsync(_repo.ClonePath, "worktree", "lock", "--reason", "kept for a person", _repo.Tree);
                break;
            case WorktreeShape.ForeignClone:
                _repo = await StaleCloneAsync(_repo);
                break;
            default:
                break;
        }

        Facts = await ReadFactsAsync(_repo);
    }

    /// <summary>Asks the judge, warming the pure call once so the measured decision is not the first one.</summary>
    public void Judge()
    {
        _ = WorktreeRemovalJudge.Judge(Facts, Policy);
        var started = Stopwatch.GetTimestamp();
        Decision = WorktreeRemovalJudge.Judge(Facts, Policy);
        DecisionDuration = Stopwatch.GetElapsedTime(started);
    }

    /// <summary>
    /// Tries the removal the way <c>git worktree remove</c> does without <c>--force</c>: a refused decision never
    /// reaches git, and git itself refuses a dirty tree. The in-memory executor applies the same two guards.
    /// </summary>
    public async Task RemoveAsync()
    {
        Judge();
        if (!Decision.Remove)
        {
            Removed = false;
            return;
        }

        if (!_real || _repo is null)
        {
            Removed = !Facts.HasUncommittedChanges;
            return;
        }

        var ran = await _repo.Git(_repo.ClonePath).RunAsync(_repo.ClonePath, ["worktree", "remove", _repo.Tree], TimeSpan.FromSeconds(60));
        Removed = ran.Ok;
    }

    /// <summary>
    /// Asserts the canary survived: on disk untouched when the worktree was kept, or reachable from origin when it was
    /// removed. The in-memory executor has no disk, so it asserts the decision never dropped work with no other copy.
    /// </summary>
    public async Task AssertCanaryAsync()
    {
        if (Removed)
        {
            if (_real && _repo is not null)
            {
                var ran = await _repo.Git(_repo.Origin).RunAsync(_repo.Origin, ["cat-file", "-e", _repo.Canary]);
                Assert.True(ran.Ok, $"the canary blob {_repo.Canary} is not reachable in origin: {ran.Err}");
            }
            else
            {
                Assert.True(Published, "the removal dropped work that no other copy held");
            }

            return;
        }

        if (_real)
        {
            Assert.True(File.Exists(_canaryPath), $"the canary {_canaryPath} was lost");
            Assert.Equal(_canaryContent, await File.ReadAllTextAsync(_canaryPath));
        }
    }

    /// <inheritdoc />
    public void Dispose() => _scratch.Dispose();

    private static WorktreeFacts MemoryFacts(WorktreeShape shape) => shape switch
    {
        WorktreeShape.Dirty => new WorktreeFacts("/specs/worktree", Branch: Branch) { HasUncommittedChanges = true },
        WorktreeShape.IgnoredNotes => new WorktreeFacts("/specs/worktree", Branch: Branch) { HasIgnoredNotes = true, IgnoredNotes = ["TODO.md"] },
        WorktreeShape.InUse => new WorktreeFacts("/specs/worktree", Branch: Branch, InUse: true),
        WorktreeShape.Locked => new WorktreeFacts("/specs/worktree", Branch: Branch, IsLocked: true, LockReason: "kept for a person"),
        WorktreeShape.ForeignClone => new WorktreeFacts("/specs/worktree", Branch: Branch, IsForeignClone: true),
        _ => new WorktreeFacts("/specs/worktree", Branch: Branch),
    };

    /// <summary>A second clone carrying a copy of the real clone's worktree registry entry.</summary>
    private static async Task<SpecRepo> StaleCloneAsync(SpecRepo repo)
    {
        var stale = Path.Join(repo.Scratch.Path, "stale");
        _ = await repo.GitAsync(repo.Scratch.Path, "clone", "-q", repo.Origin, stale);
        CopyTree(Path.Join(repo.ClonePath, ".git", "worktrees", Branch), Path.Join(stale, ".git", "worktrees", Branch));
        return repo with { ClonePath = stale };
    }

    private static void CopyTree(string from, string to)
    {
        _ = Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from))
        {
            File.Copy(file, Path.Join(to, Path.GetFileName(file)));
        }

        foreach (var folder in Directory.EnumerateDirectories(from))
        {
            CopyTree(folder, Path.Join(to, Path.GetFileName(folder)));
        }
    }

    private async Task<WorktreeFacts> ReadFactsAsync(SpecRepo repo)
    {
        var tree = await repo.RegisteredAsync();
        var facts = await new GitWorktreePort(repo.Git(repo.ClonePath)).ReadAsync(repo.ClonePath, tree);
        IReadOnlyList<string> busy = _busy is null ? [] : [PathResolution.Resolve(_busy)];
        return facts with { InUse = new Busy(busy).WorkingIn(repo.Tree) ?? false };
    }
}
