using Rig.Core.Estate;
using Rig.Core.Processes;
using Rig.Core.Reconciliation;

namespace Rig.Core.Tests.Estate;

/// <summary><see cref="FastForward"/> over a real bare origin and a clone in a temp folder.</summary>
public sealed class FastForwardTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_clean_checkout_fast_forwards_and_the_second_run_is_idempotent()
    {
        using var root = new TempDir();
        var (_, seed, clone) = await FixtureAsync(root);
        var before = await Repos.GitAsync(clone, "rev-parse", "HEAD");
        await AdvanceOriginAsync(seed);

        var fastForward = FastForwardAt(root);
        var plan = await fastForward.PlanAsync(new FastForwardTarget([clone]), Token);

        var change = Assert.Single(plan.Changes);
        Assert.Equal(ChangeKind.WouldChange, change.Kind);
        Assert.Equal("origin/main", change.Target);
        Assert.Equal(before, await Repos.GitAsync(clone, "rev-parse", "HEAD"));

        var errors = await fastForward.ApplyAsync(plan, Token);

        Assert.Empty(errors);
        Assert.Equal(ChangeKind.Changed, change.Kind);
        Assert.Equal(await Repos.GitAsync(clone, "rev-parse", "origin/main"), await Repos.GitAsync(clone, "rev-parse", "HEAD"));

        var again = await fastForward.PlanAsync(new FastForwardTarget([clone]), Token);
        Assert.Equal(ChangeKind.Aligned, Assert.Single(again.Changes).Kind);
        Assert.Empty(await fastForward.ApplyAsync(again, Token));
    }

    [Fact]
    public async Task An_already_current_checkout_is_aligned_and_unchanged()
    {
        using var root = new TempDir();
        var (_, _, clone) = await FixtureAsync(root);
        var before = await Repos.GitAsync(clone, "rev-parse", "HEAD");

        var fastForward = FastForwardAt(root);
        var plan = await fastForward.PlanAsync(new FastForwardTarget([clone]), Token);

        Assert.Equal(ChangeKind.Aligned, Assert.Single(plan.Changes).Kind);
        Assert.Empty(await fastForward.ApplyAsync(plan, Token));
        Assert.Equal(before, await Repos.GitAsync(clone, "rev-parse", "HEAD"));
    }

    [Fact]
    public async Task A_dirty_checkout_is_refused_without_moving_or_discarding_anything()
    {
        using var root = new TempDir();
        var (_, seed, clone) = await FixtureAsync(root);
        await AdvanceOriginAsync(seed);
        var before = await Repos.GitAsync(clone, "rev-parse", "HEAD");
        await File.WriteAllTextAsync(Path.Join(clone, "first.txt"), "unsaved", Token);

        var fastForward = FastForwardAt(root);
        var plan = await fastForward.PlanAsync(new FastForwardTarget([clone]), Token);

        var change = Assert.Single(plan.Changes);
        Assert.Equal(ChangeKind.Refused, change.Kind);
        Assert.Equal(FastForwardRefusal.Dirty, change.Decision.Refusal);
        Assert.Empty(await fastForward.ApplyAsync(plan, Token));
        Assert.Equal(before, await Repos.GitAsync(clone, "rev-parse", "HEAD"));
        Assert.Equal("unsaved", await File.ReadAllTextAsync(Path.Join(clone, "first.txt"), Token));
    }

    [Fact]
    public async Task A_checkout_on_another_branch_is_refused()
    {
        using var root = new TempDir();
        var (_, seed, clone) = await FixtureAsync(root);
        await AdvanceOriginAsync(seed);
        _ = await Repos.GitAsync(clone, "switch", "-q", "-c", "feature");
        var before = await Repos.GitAsync(clone, "rev-parse", "HEAD");

        var plan = await FastForwardAt(root).PlanAsync(new FastForwardTarget([clone]), Token);

        var change = Assert.Single(plan.Changes);
        Assert.Equal(FastForwardRefusal.NotDefaultBranch, change.Decision.Refusal);
        Assert.Equal(before, await Repos.GitAsync(clone, "rev-parse", "HEAD"));
    }

    [Fact]
    public async Task A_diverged_checkout_is_refused()
    {
        using var root = new TempDir();
        var (_, seed, clone) = await FixtureAsync(root);
        await AdvanceOriginAsync(seed);
        await Repos.CommitAsync(clone, "local");
        var before = await Repos.GitAsync(clone, "rev-parse", "HEAD");

        var plan = await FastForwardAt(root).PlanAsync(new FastForwardTarget([clone]), Token);

        var change = Assert.Single(plan.Changes);
        Assert.Equal(ChangeKind.Refused, change.Kind);
        Assert.Equal(FastForwardRefusal.Diverged, change.Decision.Refusal);
        Assert.Contains("1 ahead, 1 behind", change.Detail, StringComparison.Ordinal);
        Assert.Equal(before, await Repos.GitAsync(clone, "rev-parse", "HEAD"));
    }

    [Fact]
    public async Task A_linked_worktree_is_refused()
    {
        using var root = new TempDir();
        var (_, seed, clone) = await FixtureAsync(root);
        await AdvanceOriginAsync(seed);
        var worktree = Path.Join(root.Path, "worktree");
        _ = await Repos.GitAsync(clone, "worktree", "add", "-q", "-b", "wt", worktree);

        var plan = await FastForwardAt(root).PlanAsync(new FastForwardTarget([worktree]), Token);

        var change = Assert.Single(plan.Changes);
        Assert.Equal(ChangeKind.Refused, change.Kind);
        Assert.Equal(FastForwardRefusal.LinkedWorktree, change.Decision.Refusal);
    }

    [Fact]
    public async Task A_checkout_under_an_untrusted_folder_is_refused()
    {
        using var root = new TempDir();
        var repo = await Repos.InitAsync(root.Folder("external/repo"));

        var plan = await FastForwardAt(root).PlanAsync(new FastForwardTarget([repo]), Token);

        var change = Assert.Single(plan.Changes);
        Assert.Equal(ChangeKind.Refused, change.Kind);
        Assert.Equal(FastForwardRefusal.Untrusted, change.Decision.Refusal);
    }

    [Fact]
    public async Task A_failed_fetch_is_reported_as_an_error()
    {
        using var root = new TempDir();
        var (_, _, clone) = await FixtureAsync(root);
        _ = await Repos.GitAsync(clone, "remote", "set-url", "origin", Path.Join(root.Path, "gone.git"));

        var plan = await FastForwardAt(root).PlanAsync(new FastForwardTarget([clone]), Token);

        Assert.Equal(ChangeKind.Error, Assert.Single(plan.Changes).Kind);
        Assert.Contains($"{clone}: git fetch --prune origin failed", Assert.Single(plan.Errors), StringComparison.Ordinal);
    }

    private static FastForward FastForwardAt(TempDir root) => new(new Git(Repos.Proc, root.Path));

    // A bare origin with one commit on main, and a clone tracking it.
    private static async Task<(string Origin, string Seed, string Clone)> FixtureAsync(TempDir root)
    {
        var origin = Path.Join(root.Path, "origin.git");
        _ = await Repos.GitAsync(root.Path, "init", "-q", "--bare", "-b", "main", origin);
        var seed = await Repos.InitAsync(root.Folder("seed"), origin);
        _ = await Repos.GitAsync(seed, "push", "-q", "-u", "origin", "main");
        var clone = Path.Join(root.Path, "clone");
        _ = await Repos.GitAsync(root.Path, "clone", "-q", origin, clone);
        return (origin, seed, clone);
    }

    // A commit the clone does not have yet.
    private static async Task AdvanceOriginAsync(string seed)
    {
        await Repos.CommitAsync(seed, "second");
        _ = await Repos.GitAsync(seed, "push", "-q", "origin", "main");
    }
}
