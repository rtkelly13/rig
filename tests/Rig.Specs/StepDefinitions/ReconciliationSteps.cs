using Reqnroll;
using Rig.Core.Reconciliation;

namespace Rig.Specs.StepDefinitions;

/// <summary>Binds the reconciliation feature to <see cref="ReconciliationFixture"/> and the core runner.</summary>
[Binding]
public sealed class ReconciliationSteps(ScenarioContext scenario) : IDisposable
{
    private readonly ReconciliationFixture _fixture = new(SpecMode.Real(scenario));

    /// <summary>What the estate should look like.</summary>
    [Given(@"^the desired state is:$")]
    public void GivenTheDesiredStateIs(Table table) => _fixture.SetDesired(Rows(table));

    /// <summary>What exists now; a real scenario writes these to disk.</summary>
    [Given(@"^the observed state is:$")]
    public void GivenTheObservedStateIs(Table table) => _fixture.SetObserved(Rows(table));

    /// <summary>Compares desired with observed.</summary>
    [When(@"^the reconciliation plan is evaluated$")]
    public void WhenTheReconciliationPlanIsEvaluated() => _fixture.Evaluate();

    /// <summary>No mutations and no drift.</summary>
    [Then(@"^the plan is clean$")]
    public void ThenThePlanIsClean() => Assert.True(_fixture.Plan.IsClean);

    /// <summary>The plan holds no mutating change.</summary>
    [Then(@"^the plan reports no drift$")]
    public void ThenThePlanReportsNoDrift() => Assert.Equal(0, _fixture.Plan.DriftCount);

    /// <summary>The number of mutating changes.</summary>
    [Then(@"^the plan reports (\d+) changes?$")]
    public void ThenThePlanReportsChanges(int count) => Assert.Equal(count, _fixture.Plan.DriftCount);

    /// <summary>The plan creates the named target.</summary>
    [Then(@"^the plan creates ""(.*)""$")]
    public void ThenThePlanCreates(string target) => AssertKind(target, ChangeKind.Create);

    /// <summary>The plan updates the named target.</summary>
    [Then(@"^the plan updates ""(.*)""$")]
    public void ThenThePlanUpdates(string target) => AssertKind(target, ChangeKind.Update);

    /// <summary>The plan deletes the named target.</summary>
    [Then(@"^the plan deletes ""(.*)""$")]
    public void ThenThePlanDeletes(string target) => AssertKind(target, ChangeKind.Delete);

    /// <summary>Runs the pipeline with mutation disabled.</summary>
    [When(@"^the pipeline runs in dry-run mode$")]
    public void WhenThePipelineRunsInDryRunMode() => _fixture.Run(dryRun: true);

    /// <summary>The sink was never called.</summary>
    [Then(@"^no mutation reached the sink$")]
    public void ThenNoMutationReachedTheSink()
    {
        Assert.Equal(0, _fixture.Mutations);
        Assert.NotNull(_fixture.Report);
        Assert.True(_fixture.Report.MutatedNothing, "the dry run reports a mutation");
        Assert.True(_fixture.Report.DryRun, "the report does not call itself a dry run");
        Assert.Equal(_fixture.Plan.DriftCount, _fixture.Report.Drift);
    }

    /// <summary>Whatever the sink writes to is still exactly as it was.</summary>
    [Then(@"^the observed state is unchanged$")]
    public void ThenTheObservedStateIsUnchanged() => _fixture.AssertObservedUnchanged();

    /// <summary>Runs the pipeline for real.</summary>
    [When(@"^the pipeline applies the plan$")]
    public void WhenThePipelineAppliesThePlan() => _fixture.Run(dryRun: false);

    /// <summary>Observed now equals desired.</summary>
    [Then(@"^the observed state matches the desired state$")]
    public void ThenTheObservedStateMatchesTheDesiredState() => _fixture.AssertConverged();

    /// <summary>Every mutating change in the plan went through the sink, once.</summary>
    [Then(@"^every mutation went through the sink$")]
    public void ThenEveryMutationWentThroughTheSink()
    {
        Assert.NotNull(_fixture.Report);
        Assert.Equal(_fixture.Plan.DriftCount, _fixture.Mutations);
        Assert.Equal(_fixture.Plan.DriftCount, _fixture.Report.Applied);
        Assert.Equal(_fixture.Plan.DriftCount, _fixture.Report.Drift);
        Assert.False(_fixture.Report.DryRun, "an applied run still calls itself a dry run");
    }

    /// <inheritdoc />
    [AfterScenario]
    public void Dispose() => _fixture.Dispose();

    private static IReadOnlyList<KeyValuePair<string, string>> Rows(Table table) =>
        [.. table.Rows.Select(row => new KeyValuePair<string, string>(row["path"], row["content"]))];

    private void AssertKind(string target, ChangeKind kind)
    {
        var actual = _fixture.Plan.Changes.SingleOrDefault(change => change.Target == target)
            ?? throw new InvalidOperationException($"the plan names no change for {target}");
        Assert.Equal(kind, actual.Kind);
    }
}
