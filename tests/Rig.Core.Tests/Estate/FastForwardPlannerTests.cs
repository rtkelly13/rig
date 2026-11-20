using Rig.Core.Estate;
using Rig.Core.Reconciliation;

namespace Rig.Core.Tests.Estate;

/// <summary><see cref="FastForwardPlanner"/> on facts, with no git and no filesystem.</summary>
public sealed class FastForwardPlannerTests
{
    private static readonly RootCheckoutFacts Clean = new(
        "/code/personal/repo",
        Branch: "main",
        DefaultBranch: "main",
        Target: "origin/main",
        HasRemote: true,
        Behind: 2);

    [Fact]
    public void A_clean_checkout_behind_its_origin_would_fast_forward()
    {
        var decision = FastForwardPlanner.Decide(Clean);

        Assert.Equal(ChangeKind.WouldChange, decision.Kind);
        Assert.Equal(FastForwardRefusal.None, decision.Refusal);
        Assert.Equal("origin/main", decision.Target);
        Assert.Equal("fast-forward 2 commit(s) to `origin/main`", decision.Detail);
    }

    [Fact]
    public void A_clean_checkout_level_with_its_origin_is_aligned()
    {
        var decision = FastForwardPlanner.Decide(Clean with { Behind = 0 });

        Assert.Equal(ChangeKind.Aligned, decision.Kind);
        Assert.Equal(FastForwardRefusal.None, decision.Refusal);
        Assert.Equal("up to date", decision.Detail);
    }

    [Theory]
    [MemberData(nameof(Refusals))]
    public void Every_unsafe_state_is_refused_with_a_typed_reason(FastForwardRefusal expected, RootCheckoutFacts facts)
    {
        var decision = FastForwardPlanner.Decide(facts);

        Assert.Equal(ChangeKind.Refused, decision.Kind);
        Assert.Equal(expected, decision.Refusal);
        Assert.NotEmpty(decision.Detail);
        Assert.Equal(string.Empty, decision.Target);
    }

    public static TheoryData<FastForwardRefusal, RootCheckoutFacts> Refusals => new()
    {
        { FastForwardRefusal.LinkedWorktree, Clean with { IsRootCheckout = false } },
        { FastForwardRefusal.Untrusted, Clean with { IsUntrusted = true } },
        { FastForwardRefusal.Dirty, Clean with { IsDirty = true } },
        { FastForwardRefusal.OperationInProgress, Clean with { Operation = "rebase" } },
        { FastForwardRefusal.Detached, Clean with { IsDetached = true, Branch = string.Empty } },
        { FastForwardRefusal.NotDefaultBranch, Clean with { Branch = "feature" } },
        { FastForwardRefusal.NoRemote, Clean with { HasRemote = false, Target = "origin/main" } },
        { FastForwardRefusal.Ahead, Clean with { Ahead = 1, Behind = 0 } },
        { FastForwardRefusal.Diverged, Clean with { Ahead = 1, Behind = 1 } },
    };

    [Fact]
    public void A_folder_that_cannot_be_read_is_an_error_not_a_change()
    {
        var missing = FastForwardPlanner.Decide(Clean with { Exists = false });
        var failed = FastForwardPlanner.Decide(Clean with { Error = "git status failed" });

        Assert.Equal(ChangeKind.Error, missing.Kind);
        Assert.Equal(FastForwardRefusal.Unreadable, missing.Refusal);
        Assert.Equal(ChangeKind.Error, failed.Kind);
        Assert.Equal("git status failed", failed.Detail);
    }

    [Fact]
    public void A_detached_checkout_is_refused_before_it_is_compared_with_a_missing_branch()
    {
        var decision = FastForwardPlanner.Decide(Clean with
        {
            IsDetached = true,
            Branch = string.Empty,
            HasRemote = false,
            Target = string.Empty,
        });

        Assert.Equal(FastForwardRefusal.Detached, decision.Refusal);
    }
}
