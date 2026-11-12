using Rig.Core.Estate;
using Rig.Core.Processes;
using Rig.Core.Roots;

namespace Rig.Core.Tests.Estate;

/// <summary>
/// Every command that removes worktrees, run against every worktree shape that has bitten the estate or that git
/// documents as holding state a removal drops. Ported from the Python matrix of #141; the gaps it found are #99.
/// </summary>
/// <remarks>
/// <para>
/// Each scenario builds a real clone, a bare origin and one linked worktree, plants unique canaries, and declares which
/// it may lose. Each entry point runs at its most aggressive ordinary flags. The oracle ranks every canary before and
/// after (on disk or reachable from a ref; only in a reflog; gone) and no command may lower a rank. It also records
/// which layer decided: a cell that survives on git's refusal alone, or on a crash, is not guarded.
/// </para>
/// <para>
/// Known gaps are listed in <see cref="Gaps"/>. A gap cell confirms the gap still happens and is then skipped, so it
/// shows in every run; once it stops happening the cell fails until its row comes out. Part of #30 F7.
/// </para>
/// </remarks>
public sealed class SafetyMatrixTests : IDisposable
{
    private const string Branch = "spike";

    private readonly TempDir _temp = new();

    public void Dispose() => _temp.Dispose();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>Which layer decided a worktree's fate.</summary>
    public enum Layer
    {
        /// <summary>The command refused.</summary>
        Tool,

        /// <summary>git declined <c>worktree remove</c>; the command reported a failure.</summary>
        Git,

        /// <summary>The command failed on something else before it reached a removal.</summary>
        Crash,

        /// <summary>The command never offered the worktree at all.</summary>
        Untouched,

        /// <summary>Removed, shelved or trimmed, and the canaries survived.</summary>
        Kept,
    }

    /// <summary>What a cell must do.</summary>
    public enum Expect
    {
        /// <summary>Canaries survive, whichever layer saved them.</summary>
        Safe,

        /// <summary>Canaries survive, and the command itself refused.</summary>
        Guarded,

        /// <summary>The control: the worktree is removed and nothing undeclared is lost.</summary>
        Removes,

        /// <summary>trim's control: every canary it may lose is gone, and the worktree stays.</summary>
        Trims,
    }

    /// <summary>How a cell went wrong.</summary>
    public enum Failure
    {
        /// <summary>A canary became less recoverable than it was before the command ran.</summary>
        CanaryLost,

        /// <summary>The work survived, but only because git declined or the command crashed.</summary>
        NotGuarded,

        /// <summary>A control did not do its job.</summary>
        Control,
    }

    // How recoverable one canary is. A command may never lower it.
    private enum Tier
    {
        Gone = 0,
        Reflog = 1, // only a reflog names it: gc expires it, and a worktree's goes with its admin dir
        Safe = 2, // on disk as planted, or reachable from a ref in some repo
    }

    private const string GapIgnored = "#99 1: ignored, non-regenerable files go with `git worktree remove`; only shelve checks them";
    private const string GapReclaim = "#99 3: reclaim has no unborn or foreign-owner guard of its own; only git's validation stops it";
    private const string GapShelve = "#99 3: shelve has no foreign-owner guard; it crashes on the null SHA, by luck";
    private const string GapBusy = "#99 2: no process-based in-use check outside tidy (shelve reads mtimes, and an idle of 0 drops even that)";
    private const string GapReflog = "#99 5: a commit only the reflog names is not counted as local work, and deleting the branch drops it";
    private const string GapLocked = "#99 4: a lock is left to git's refusal, reported as failed; only shelve treats it as a decision";

    private static readonly string[] Scenarios =
        ["finished", "dirty", "ignored_build", "ignored_notes", "unborn", "foreign", "busy", "locked", "reflog_only", "reflog_only_pushed", "relative"];

    private static readonly string[] EntryPoints = ["tidy", "shelve", "reclaim", "trim"];

    private static readonly string[] RelativePaths = ["--relative-paths"];

    /// <summary>What each cell must do; cells absent here are <see cref="Expect.Safe"/>.</summary>
    private static readonly Dictionary<(string Scenario, string Entry), Expect> Expected = Build();

    /// <summary>Known gaps: the failure each still produces, and why. Fixing one fails its cell until the row goes.</summary>
    private static readonly Dictionary<(string Scenario, string Entry), (Failure Failure, string Why)> Gaps = new()
    {
        [("ignored_notes", "tidy")] = (Failure.CanaryLost, GapIgnored),
        [("ignored_notes", "reclaim")] = (Failure.CanaryLost, GapIgnored),
        [("unborn", "reclaim")] = (Failure.NotGuarded, GapReclaim),
        [("foreign", "shelve")] = (Failure.NotGuarded, GapShelve),
        [("foreign", "reclaim")] = (Failure.NotGuarded, GapReclaim),
        [("busy", "shelve")] = (Failure.CanaryLost, GapBusy),
        [("busy", "reclaim")] = (Failure.CanaryLost, GapBusy),
        [("reflog_only", "tidy")] = (Failure.CanaryLost, GapReflog),
        [("reflog_only_pushed", "shelve")] = (Failure.CanaryLost, GapReflog),
        [("locked", "tidy")] = (Failure.NotGuarded, GapLocked),
        [("locked", "reclaim")] = (Failure.NotGuarded, GapLocked),
    };

    /// <summary>Every scenario × entry point.</summary>
    public static TheoryData<string, string> Cells()
    {
        var cells = new TheoryData<string, string>();
        foreach (var scenario in Scenarios)
        {
            foreach (var entry in EntryPoints)
            {
                cells.Add(scenario, entry);
            }
        }

        return cells;
    }

    [Theory]
    [MemberData(nameof(Cells))]
    public async Task No_command_loses_work(string scenario, string entry)
    {
        var world = await BuildAsync(scenario);
        var before = await RecoverabilityAsync(world);
        await AssertShapeAsync(scenario, world, before);

        var layer = entry switch
        {
            "tidy" => await RunTidyAsync(world),
            "shelve" => await RunShelveAsync(world),
            "reclaim" => await RunReclaimAsync(world),
            _ => await RunTrimAsync(world),
        };

        var verdict = await JudgeAsync(world, before, layer, Expected.GetValueOrDefault((scenario, entry), Expect.Safe));
        var known = Gaps.TryGetValue((scenario, entry), out var gap);
        if (verdict is { } failed)
        {
            if (known && failed.Failure == gap.Failure)
            {
                Assert.Skip($"known gap — {gap.Why} ({failed.Message})");
            }

            Assert.Fail($"{failed.Failure}: {failed.Message}");
        }

        Assert.False(known, $"known gap no longer reproduces; take ({scenario}, {entry}) out of Gaps: {gap.Why}");
    }

    private static Dictionary<(string, string), Expect> Build()
    {
        var expected = new Dictionary<(string, string), Expect>();
        string[] removers = ["tidy", "shelve", "reclaim"];
        foreach (var entry in removers)
        {
            foreach (var scenario in new[] { "finished", "ignored_build", "relative" })
            {
                expected[(scenario, entry)] = Expect.Removes;
            }

            foreach (var scenario in new[] { "unborn", "foreign", "locked" })
            {
                expected[(scenario, entry)] = Expect.Guarded;
            }
        }

        expected[("busy", "tidy")] = Expect.Guarded;
        expected[("ignored_notes", "shelve")] = Expect.Guarded;
        expected[("ignored_build", "trim")] = Expect.Trims;
        return expected;
    }

    // ---- a scenario: a clone, one worktree in it, and what must survive ------------------------------------------------

    private sealed class World(string root, string origin, string clone, string tree)
    {
        public string Root { get; } = root;

        public string Origin { get; } = origin;

        public string Clone { get; set; } = clone;

        public string Tree { get; } = tree;

        /// <summary>Each canary's path, and the blob id of what was planted there.</summary>
        public Dictionary<string, (string Content, string Blob)> Canaries { get; } = new(StringComparer.Ordinal);

        public HashSet<string> MayLose { get; } = new(StringComparer.Ordinal);

        /// <summary>The worktree itself must survive: in use, locked, another clone's.</summary>
        public bool KeepTree { get; set; }

        public List<string> Busy { get; } = [];

        public List<string> Repos { get; } = [];

        /// <summary>The head commit the merged PR recorded, as GitHub reports headRefOid.</summary>
        public string PrHead { get; set; } = string.Empty;

        public async Task<string> PlantAsync(string relative, bool loseOk = false)
        {
            var path = Path.Join(Tree, relative);
            _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var content = $"canary {relative} {Canaries.Count} {Guid.NewGuid():N}\n";
            await File.WriteAllTextAsync(path, content, Token);
            await Record(path, content);
            if (loseOk)
            {
                _ = MayLose.Add(path);
            }

            return path;
        }

        public async Task Record(string path, string content) =>
            Canaries[path] = (content, await Estate.Repos.GitAsync(Root, "hash-object", "--no-filters", path));
    }

    private async Task<World> BuildAsync(string scenario) => scenario switch
    {
        "finished" => await WorldAsync(),
        "dirty" => await DirtyAsync(),
        "ignored_build" => await PlantedAsync("node_modules/pkg/index.js", loseOk: true),
        "ignored_notes" => await PlantedAsync("TODO.md"),
        "unborn" => await UnbornAsync(),
        "foreign" => await ForeignAsync(),
        "busy" => await BusyAsync(),
        "locked" => await LockedAsync(),
        "reflog_only" => await ResetAwayAsync(await WorldAsync()),
        "reflog_only_pushed" => await ResetAwayAsync(await WorldAsync(squashed: false)),
        _ => await RelativeAsync(),
    };

    /// <summary>
    /// A clone and a finished worktree: <c>spike</c> was pushed, squash-merged into <c>main</c>, and its remote branch
    /// deleted. That is the state <c>--merged</c> exists for: the branch's own commits are on no remote, and only the
    /// merged PR says the work landed. Unsquashed, the branch stays on origin as after a merge commit.
    /// </summary>
    private async Task<World> WorldAsync(bool relative = false, bool squashed = true)
    {
        var root = PathResolution.Resolve(_temp.Path);
        var origin = Path.Join(root, "origin.git");
        _ = await Repos.GitAsync(root, "init", "-q", "--bare", "-b", "main", origin);
        var seed = await CloneAsync(root, "seed", origin);
        _ = await Repos.GitAsync(seed, "symbolic-ref", "HEAD", "refs/heads/main");
        await File.WriteAllTextAsync(Path.Join(seed, ".gitignore"), "node_modules/\nTODO.md\n", Token);
        await File.WriteAllTextAsync(Path.Join(seed, "README.md"), "hi\n", Token);
        _ = await Repos.GitAsync(seed, "add", ".");
        _ = await Repos.GitAsync(seed, "commit", "-qm", "first");
        _ = await Repos.GitAsync(seed, "push", "-q", "origin", "main");

        var clone = await CloneAsync(root, "clone", origin);
        var tree = Path.Join(clone, "temp", Branch);
        _ = await Repos.GitAsync(clone, ["worktree", "add", "-q", .. relative ? RelativePaths : [], "-b", Branch, tree]);
        var world = new World(root, origin, clone, tree);
        world.Repos.AddRange([clone, origin]);
        var work = await world.PlantAsync("work.md");
        _ = await Repos.GitAsync(tree, "add", ".");
        _ = await Repos.GitAsync(tree, "commit", "-qm", "spike work");
        _ = await Repos.GitAsync(tree, "push", "-q", "-u", "origin", Branch);
        world.PrHead = await Repos.GitAsync(tree, "rev-parse", "HEAD");
        if (!squashed)
        {
            return world;
        }

        await File.WriteAllTextAsync(Path.Join(seed, "work.md"), world.Canaries[work].Content, Token);
        _ = await Repos.GitAsync(seed, "add", ".");
        _ = await Repos.GitAsync(seed, "commit", "-qm", "spike work (#1)");
        _ = await Repos.GitAsync(seed, "push", "-q", "origin", "main", $":{Branch}");
        _ = await Repos.GitAsync(clone, "fetch", "-q", "--prune", "origin");
        return world;
    }

    private static async Task<string> CloneAsync(string root, string name, string origin)
    {
        var path = Path.Join(root, name);
        _ = await Repos.GitAsync(root, "clone", "-q", origin, path);
        _ = await Repos.GitAsync(path, "config", "user.email", "t.user@example.com");
        _ = await Repos.GitAsync(path, "config", "user.name", "T");
        _ = await Repos.GitAsync(path, "config", "commit.gpgsign", "false");
        _ = await Repos.GitAsync(path, "config", "core.autocrlf", "false");
        return path;
    }

    private async Task<World> DirtyAsync()
    {
        var world = await WorldAsync();
        _ = await world.PlantAsync("scratch.md");
        var work = Path.Join(world.Tree, "work.md");
        await File.WriteAllTextAsync(work, "edited, never committed\n", Token);
        await world.Record(work, "edited, never committed\n");
        return world;
    }

    private async Task<World> PlantedAsync(string relative, bool loseOk = false)
    {
        var world = await WorldAsync();
        _ = await world.PlantAsync(relative, loseOk);
        return world;
    }

    /// <summary>The branch ref is gone while the worktree has it checked out, as when a clone is replaced.</summary>
    private async Task<World> UnbornAsync()
    {
        var world = await WorldAsync();
        _ = await world.PlantAsync("local.md");
        _ = await Repos.GitAsync(world.Tree, "add", "local.md");
        _ = await Repos.GitAsync(world.Tree, "commit", "-qm", "local only");
        _ = await Repos.GitAsync(world.Clone, "update-ref", "-d", $"refs/heads/{Branch}");
        return world;
    }

    /// <summary>A stale clone carrying a copy of the real clone's registry entry; the commands run in the stale one.</summary>
    private async Task<World> ForeignAsync()
    {
        var world = await WorldAsync();
        _ = await world.PlantAsync("scratch.md");
        var stale = await CloneAsync(world.Root, "stale", world.Origin);
        CopyTree(Path.Join(world.Clone, ".git", "worktrees", Branch), Path.Join(stale, ".git", "worktrees", Branch));
        world.Repos.Add(stale);
        world.Clone = stale;
        world.KeepTree = true;
        return world;
    }

    /// <summary>A shell is working below the worktree. Only tidy can be told; the others have no seam for it.</summary>
    private async Task<World> BusyAsync()
    {
        var world = await WorldAsync();
        world.Busy.Add(Directory.CreateDirectory(Path.Join(world.Tree, "src")).FullName);
        world.KeepTree = true;
        return world;
    }

    private async Task<World> LockedAsync()
    {
        var world = await WorldAsync();
        _ = await Repos.GitAsync(world.Clone, "worktree", "lock", "--reason", "keep me", world.Tree);
        world.KeepTree = true;
        return world;
    }

    /// <summary>Commit a canary, then reset it away: it stays in the reflogs, on no ref and not on disk.</summary>
    private static async Task<World> ResetAwayAsync(World world)
    {
        var lost = await world.PlantAsync("dropped.md");
        _ = await Repos.GitAsync(world.Tree, "add", "dropped.md");
        _ = await Repos.GitAsync(world.Tree, "commit", "-qm", "about to be reset away");
        _ = await Repos.GitAsync(world.Tree, "reset", "-q", "--hard", "HEAD~1");
        Assert.False(File.Exists(lost));
        return world;
    }

    /// <summary>A finished worktree written with <c>--relative-paths</c> (git 2.48+). It must still be removed.</summary>
    private async Task<World> RelativeAsync()
    {
        var version = (await Repos.GitAsync(_temp.Path, "version")).Split(' ')[2].Split('.');
        var (major, minor) = (int.Parse(version[0], System.Globalization.CultureInfo.InvariantCulture), int.Parse(version[1], System.Globalization.CultureInfo.InvariantCulture));
        Assert.SkipWhen(major < 2 || (major == 2 && minor < 48), $"git {string.Join('.', version)} has no --relative-paths");
        return await WorldAsync(relative: true);
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

    private static async Task<Worktree?> RegisteredAsync(World world) =>
        (await Worktree.ListAsync(GitFor(world), world.Clone)).FirstOrDefault(tree => Same(tree.Path, world.Tree));

    private static bool Same(string path, string other) => PathResolution.Same(PathResolution.Resolve(path), PathResolution.Resolve(other));

    private static Git GitFor(World world) => new(Repos.Proc, world.Root);

    private static async Task<bool> GitOkAsync(string cwd, params string[] args) =>
        (await Repos.Proc.RunAsync(new ProcRequest(["git", .. args]) { WorkingDirectory = cwd }, Token)).Ok;

    private static async Task AssertIgnoredAndCleanAsync(World world, string relative)
    {
        Assert.True(await GitOkAsync(world.Tree, "check-ignore", "-q", relative), $"{relative} is not ignored");
        Assert.Empty(await Repos.GitAsync(world.Tree, "status", "--porcelain"));
    }

    private static string GitDir(World world) => File.ReadAllText(Path.Join(world.Tree, ".git")).Split("gitdir:", 2)[1].Trim();

    /// <summary>What a scenario must look like before the command runs, so a mis-built one fails here, not as a gap.</summary>
    private static async Task AssertShapeAsync(string scenario, World world, Dictionary<string, Tier> before)
    {
        Assert.True(Directory.Exists(world.Tree), "scenario did not build its worktree");
        Assert.All(before.Values, tier => Assert.NotEqual(Tier.Gone, tier));
        switch (scenario)
        {
            case "dirty":
                Assert.NotEmpty(await Repos.GitAsync(world.Tree, "status", "--porcelain"));
                break;
            case "ignored_build":
                await AssertIgnoredAndCleanAsync(world, "node_modules");
                break;
            case "ignored_notes":
                await AssertIgnoredAndCleanAsync(world, "TODO.md");
                break;
            case "unborn":
                Assert.True((await RegisteredAsync(world))?.Unborn, "HEAD is not unborn");
                break;
            case "foreign":
                Assert.DoesNotContain(Path.GetFileName(world.Clone), GitDir(world), StringComparison.Ordinal);
                Assert.NotNull(await RegisteredAsync(world));
                break;
            case "locked":
                Assert.True((await RegisteredAsync(world))?.Locked, "not locked");
                break;
            case "reflog_only" or "reflog_only_pushed":
                Assert.Contains(Tier.Reflog, before.Values);
                break;
            case "relative":
                Assert.False(Path.IsPathRooted(GitDir(world)), "gitdir is absolute");
                break;
            default:
                break;
        }
    }

    // ---- entry points: each returns the layer that decided ------------------------------------------------------------

    private static Dictionary<string, PullRequest> MergedPr(World world) =>
        new(StringComparer.Ordinal) { [Branch] = new PullRequest(1, "MERGED", Branch, world.PrHead) };

    /// <summary>Two passes, as documented: removing a worktree frees its branch for the second.</summary>
    private static async Task<Layer> RunTidyAsync(World world)
    {
        var git = GitFor(world);
        var tidy = new Tidy(git, new TreeRemoval(Repos.Proc));
        var merged = MergedPr(world);
        var outcomes = new List<TidyOutcome>();
        for (var pass = 0; pass < 2; pass++)
        {
            var report = await new RepoChecks(git, TimeProvider.System).InspectAsync(world.Clone, merged);
            var actions = await tidy.CleanRepoAsync(report, apply: true, force: true, mergedHeads: merged, busy: new Busy([.. world.Busy]), cancellationToken: Token);
            outcomes.AddRange(actions.Where(action => Same(action.Subject, world.Tree)).Select(action => action.Outcome));
        }

        return outcomes.Contains(TidyOutcome.Cleaned) ? Layer.Kept : outcomes.Contains(TidyOutcome.Failed) ? Layer.Git : Layer.Tool;
    }

    private static async Task<Layer> RunShelveAsync(World world)
    {
        var git = GitFor(world);
        var shelver = new Shelver(git, new TreeRemoval(Repos.Proc), TimeProvider.System, ledger: null);
        var tree = await RegisteredAsync(world);
        Assert.NotNull(tree);
        ShelveResult result;
        try
        {
            var plan = await shelver.PlanAsync(
                world.Clone,
                tree,
                await shelver.ShelfUserAsync(world.Clone),
                await new ShelfStore(git).RemoteShelvesAsync(world.Clone),
                MergedPr(world),
                new ShelveOptions(TimeSpan.Zero));
            result = await shelver.ApplyAsync(plan);
        }
        catch (InvalidOperationException)
        {
            return Layer.Crash;
        }

        return result.Outcome switch
        {
            "needs-decision" => Layer.Tool,
            "failed" => result.Detail.StartsWith("worktree remove", StringComparison.Ordinal) ? Layer.Git : Layer.Crash,
            _ => Layer.Kept,
        };
    }

    private static async Task<Layer> RunReclaimAsync(World world)
    {
        var reclaim = new Reclaim(GitFor(world), new DiskUsage(Repos.Proc));
        var entry = (await reclaim.ScanRepoAsync(world.Clone, floor: 0, Token)).Entries.FirstOrDefault(e => Same(e.Path, world.Tree));
        if (entry is null)
        {
            return Layer.Untouched;
        }

        var status = await reclaim.ApplyAsync(world.Clone, entry, apply: true, force: true, new TreeRemoval(Repos.Proc), Token);
        return status switch
        {
            "removed" => Layer.Kept,
            "failed" => Layer.Git,
            _ => Layer.Tool,
        };
    }

    private static async Task<Layer> RunTrimAsync(World world)
    {
        var trim = new Trim(GitFor(world), TimeProvider.System);
        foreach (var item in await trim.ScanAsync(world.Clone, idleDays: 0, floor: 0))
        {
            if ((await trim.RecheckAsync(item, idleDays: 0)).Length == 0)
            {
                _ = await Trim.RemoveAsync(item, new TreeRemoval(Repos.Proc), Token);
            }
        }

        return Layer.Kept;
    }

    // ---- the oracle --------------------------------------------------------------------------------------------------

    private static async Task<HashSet<string>> ObjectsAsync(string repo, params string[] walk)
    {
        var ran = await Repos.Proc.RunAsync(new ProcRequest(["git", "rev-list", "--objects", .. walk]) { WorkingDirectory = repo }, Token);
        return [.. ran.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.Split(' ', 2)[0].Trim())];
    }

    /// <summary>Each canary's tier. <c>--all</c> walks refs only; <c>--reflog</c> adds every worktree's reflog.</summary>
    private static async Task<Dictionary<string, Tier>> RecoverabilityAsync(World world)
    {
        HashSet<string> byRef = [], byReflog = [];
        foreach (var repo in world.Repos.Where(Directory.Exists))
        {
            byRef.UnionWith(await ObjectsAsync(repo, "--all"));
            byReflog.UnionWith(await ObjectsAsync(repo, "--all", "--reflog"));
        }

        var tiers = new Dictionary<string, Tier>(StringComparer.Ordinal);
        foreach (var (path, (content, blob)) in world.Canaries)
        {
            var onDisk = File.Exists(path) && await File.ReadAllTextAsync(path, Token) == content;
            tiers[path] = onDisk || byRef.Contains(blob) ? Tier.Safe : byReflog.Contains(blob) ? Tier.Reflog : Tier.Gone;
        }

        return tiers;
    }

    private static async Task<(Failure Failure, string Message)?> JudgeAsync(World world, Dictionary<string, Tier> before, Layer layer, Expect expect)
    {
        var after = await RecoverabilityAsync(world);
        var lost = world.Canaries.Keys
            .Where(path => !world.MayLose.Contains(path) && after[path] < before[path])
            .Select(path => $"{Path.GetRelativePath(world.Tree, path)} ({before[path]} -> {after[path]})")
            .ToList();
        if (lost.Count > 0)
        {
            return (Failure.CanaryLost, $"{layer}: lost {string.Join(", ", lost)}");
        }

        if (world.KeepTree && !Directory.Exists(world.Tree))
        {
            return (Failure.CanaryLost, $"{layer}: removed a worktree that had to stay");
        }

        if (expect == Expect.Guarded && layer is not (Layer.Tool or Layer.Untouched))
        {
            return (Failure.NotGuarded, $"survived on {layer} alone");
        }

        if (expect == Expect.Trims && world.MayLose.Any(Path.Exists))
        {
            return (Failure.Control, $"{layer}: trimmed nothing it may lose");
        }

        return expect == Expect.Removes && Directory.Exists(world.Tree)
            ? (Failure.Control, $"{layer}: refused a worktree it exists to remove")
            : null;
    }
}
