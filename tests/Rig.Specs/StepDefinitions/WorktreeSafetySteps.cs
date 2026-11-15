using Reqnroll;
using Rig.Core.Estate;

namespace Rig.Specs.StepDefinitions;

/// <summary>Binds the worktree-safety feature to <see cref="EstateWorld"/> in whichever mode the scenario selects.</summary>
[Binding]
public sealed class WorktreeSafetySteps(ScenarioContext scenario) : IDisposable
{
    private readonly EstateWorld _world = new();

    /// <summary>Builds the named shape, in memory by default and on disk for a <c>@real</c> scenario.</summary>
    [Given(@"^a worktree that is (.+)$")]
    public async Task GivenAWorktreeThatIs(string shape) => await _world.BuildAsync(ParseShape(shape), SpecMode.Real(scenario));

    /// <summary>Builds the shape on disk even without <c>@real</c>, so the executor is explicit in the feature.</summary>
    [Given(@"^a real worktree that is (.+)$")]
    public async Task GivenARealWorktreeThatIs(string shape) => await _world.BuildAsync(ParseShape(shape), real: true);

    /// <summary>Sets what the calling command is willing to give up.</summary>
    [Given(@"^the removal policy is (.+)$")]
    public void GivenTheRemovalPolicyIs(string policy) => _world.Policy = ParsePolicy(policy);

    /// <summary>Asks the production judge.</summary>
    [When(@"^the estate asks whether the worktree may be removed$")]
    public void WhenTheEstateAsksWhetherTheWorktreeMayBeRemoved() => _world.Judge();

    /// <summary>Asserts the judge's answer.</summary>
    [Then(@"^the decision is (allowed|guarded)$")]
    public void ThenTheDecisionIs(string decision) => Assert.Equal(decision == "allowed", _world.Decision.Remove);

    /// <summary>Asserts every reason the judge gave, in its documented order.</summary>
    [Then(@"^the refusal reasons are (.+)$")]
    public void ThenTheRefusalReasonsAre(string reasons) =>
        Assert.Equal(ParseReasons(reasons), _world.Decision.Reasons);

    /// <summary>The in-memory promise: a pure decision costs microseconds, not a git call.</summary>
    [Then(@"^the decision is reached in under (\d+) milliseconds$")]
    public void ThenTheDecisionIsReachedInUnder(int milliseconds) =>
        Assert.True(
            _world.DecisionDuration < TimeSpan.FromMilliseconds(milliseconds),
            $"the decision took {_world.DecisionDuration.TotalMilliseconds:F3} ms, over the {milliseconds} ms budget");

    /// <summary>Tries the removal the way the commands do.</summary>
    [When(@"^the estate removes the worktree$")]
    public async Task WhenTheEstateRemovesTheWorktree() => await _world.RemoveAsync();

    /// <summary>Asserts whether the worktree is still there.</summary>
    [Then(@"^the worktree is (removed|kept)$")]
    public void ThenTheWorktreeIs(string state)
    {
        var expected = state == "removed";
        Assert.True(expected == _world.Removed, $"the worktree was expected {state}: {_world.Describe()}");
    }

    /// <summary>Asserts the canary survived the decision, the removal, or both.</summary>
    [Then(@"^the canary survives$")]
    public async Task ThenTheCanarySurvives() => await _world.AssertCanaryAsync();

    /// <inheritdoc />
    [AfterScenario]
    public void Dispose() => _world.Dispose();

    private static WorktreeShape ParseShape(string shape) => shape switch
    {
        "clean" => WorktreeShape.Clean,
        "finished" => WorktreeShape.Finished,
        "dirty" => WorktreeShape.Dirty,
        "ignored notes" => WorktreeShape.IgnoredNotes,
        "in use" => WorktreeShape.InUse,
        "locked" => WorktreeShape.Locked,
        "foreign clone" => WorktreeShape.ForeignClone,
        _ => throw new InvalidOperationException($"the feature names a worktree shape the specs do not know: {shape}"),
    };

    private static RemovalPolicy ParsePolicy(string policy) => policy switch
    {
        "strict" => RemovalPolicy.Strict,
        "discard-ignored" => new RemovalPolicy(DiscardIgnoredNotes: true),
        "force" => new RemovalPolicy(DiscardUnpushedCommits: true),
        "everything" => new RemovalPolicy(DiscardIgnoredNotes: true, DiscardUnpushedCommits: true),
        _ => throw new InvalidOperationException($"the feature names a removal policy the specs do not know: {policy}"),
    };

    private static IReadOnlyList<RemovalRefusalReason> ParseReasons(string reasons) =>
        reasons == "none"
            ? []
            : [.. reasons.Split(", ", StringSplitOptions.RemoveEmptyEntries).Select(reason => Enum.Parse<RemovalRefusalReason>(reason, ignoreCase: false))];
}
