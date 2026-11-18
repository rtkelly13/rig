using Rig.Core.Estate;
using Rig.Core.Reconciliation;

namespace Rig.Core.Tests.Reconciliation;

/// <summary>The shared plan/change vocabulary every reconciler now speaks.</summary>
public sealed class ReconciliationContractTests
{
    [Fact]
    public void A_plan_reports_drift_for_would_change_and_changed_rows_only()
    {
        var aligned = new Row("a", ChangeKind.Aligned);
        var refused = new Row("b", ChangeKind.Refused);
        var would = new Row("c", ChangeKind.WouldChange);
        var changed = new Row("d", ChangeKind.Changed);

        Assert.False(((IPlan<Row>)new TestPlan([aligned, refused])).HasDrift);
        Assert.True(((IPlan<Row>)new TestPlan([aligned, would])).HasDrift);
        Assert.True(((IPlan<Row>)new TestPlan([refused, changed])).HasDrift);
    }

    [Fact]
    public void Exit_codes_follow_error_then_unapplied_drift_then_clean()
    {
        Assert.Equal(0, ((IPlan<Row>)new TestPlan([new Row("a", ChangeKind.Aligned)])).ExitCode(applied: false));
        Assert.Equal(1, ((IPlan<Row>)new TestPlan([new Row("a", ChangeKind.WouldChange)])).ExitCode(applied: false));
        Assert.Equal(0, ((IPlan<Row>)new TestPlan([new Row("a", ChangeKind.WouldChange)])).ExitCode(applied: true));
        Assert.Equal(2, ((IPlan<Row>)new TestPlan([new Row("a", ChangeKind.Aligned)], ["cannot read state"])).ExitCode(applied: true));
    }

    [Fact]
    public void Tidy_rows_map_outcomes_to_the_standard_kinds()
    {
        Assert.Equal(ChangeKind.Changed, new TidyAction("temp-stale", "x", TidyOutcome.Cleaned).Kind);
        Assert.Equal(ChangeKind.WouldChange, new TidyAction("temp-stale", "x", TidyOutcome.WouldClean).Kind);
        Assert.Equal(ChangeKind.Refused, new TidyAction("temp-stale", "x", TidyOutcome.Refused).Kind);
        Assert.Equal(ChangeKind.Error, new TidyAction("temp-stale", "x", TidyOutcome.Failed).Kind);
    }

    [Fact]
    public void Reclaim_entries_are_typed_rows_and_the_scan_is_a_plan()
    {
        var entry = new Reclaimable("/clone/node_modules", "node_modules", 100, "pnpm install");
        var plan = new RepoReclaim("/clone", [entry]);

        Assert.Equal("/clone/node_modules", entry.Subject);
        Assert.Equal(ChangeKind.WouldChange, entry.Kind);
        Assert.Equal("pnpm install", entry.Detail);
        Assert.Same(entry, Assert.Single(plan.Changes));
        Assert.True(((IPlan<Reclaimable>)plan).HasDrift);
    }

    [Fact]
    public void Trim_rows_carry_their_refusal_as_the_detail()
    {
        var free = new Trimmable("/clone/temp/wt", "/clone/temp/wt/node_modules", "node_modules", new TreeMeasure(1, 1));
        var refused = free with { Refused = "not gitignored" };

        Assert.Equal(ChangeKind.WouldChange, free.Kind);
        Assert.Equal(ChangeKind.Refused, refused.Kind);
        Assert.Equal("not gitignored", refused.Detail);
    }

    [Fact]
    public void Shelve_results_map_outcomes_to_the_standard_kinds()
    {
        var plan = new ShelvePlan
        {
            Clone = "/clone",
            Path = "/clone/.worktrees/one",
            Name = "one",
            Ref = "refs/heads/shelved/t/one",
            Branch = "one",
            Head = "abc",
            Action = Shelver.Remove,
        };

        Assert.Equal(ChangeKind.Changed, new ShelveResult(plan, "shelved").Kind);
        Assert.Equal(ChangeKind.Changed, new ShelveResult(plan, "removed").Kind);
        Assert.Equal(ChangeKind.Refused, new ShelveResult(plan, "needs-decision").Kind);
        Assert.Equal(ChangeKind.Error, new ShelveResult(plan, "failed").Kind);
        Assert.Equal("/clone/.worktrees/one", new ShelveResult(plan, "shelved").Subject);
    }


    [Fact]
    public void ShelveResult_and_TidyAction_fallback_and_details()
    {
        var plan = new ShelvePlan { Clone = "/c", Path = "/c/p", Name = "n", Ref = "r", Branch = "b", Head = "h", Action = Shelver.Remove };
        var result = new ShelveResult(plan, "other-outcome");
        Assert.Equal(ChangeKind.WouldChange, result.Kind);
        Assert.Equal("other-outcome", result.Outcome);

        var readError = new ShelveResult(plan, "needs-decision", "could not read the worktree: corrupt pack");
        Assert.Equal(ChangeKind.Error, readError.Kind);

        var tidy = new TidyAction("temp", "target", (TidyOutcome)99);
        Assert.Equal(ChangeKind.Error, tidy.Kind);
        Assert.Equal("temp", tidy.Check);
        Assert.Equal("target", tidy.Subject);
    }

    [Fact]
    public void ReconciliationOptions_properties_and_factory()
    {
        var opts = ReconciliationOptions.For(apply: true, json: true);
        Assert.True(opts.Apply);
        Assert.True(opts.Json);
        Assert.False(opts.DryRun);

        var dry = ReconciliationOptions.FromFlags(dryRun: true, apply: false, json: false);
        Assert.False(dry.Apply);
        Assert.True(dry.DryRun);

        Assert.Throws<ArgumentException>(() => ReconciliationOptions.FromFlags(dryRun: true, apply: true, json: false));
    }

    [Fact]
    public void ReconciliationRun_and_Report_properties()
    {
        var plan = new TestPlan([new Row("a", ChangeKind.WouldChange)]);
        var run = new ReconciliationRun<TestPlan, Row>(plan, ["err"], Applied: false, ExitCode: 2);
        Assert.False(run.Succeeded);
        Assert.True(run.HasErrors);

        var cleanRun = new ReconciliationRun<TestPlan, Row>(plan, [], Applied: true, ExitCode: 0);
        Assert.True(cleanRun.Succeeded);
        Assert.False(cleanRun.HasErrors);

        var report = new ReconciliationReport([new Row("a", ChangeKind.WouldChange)], Applied: 0, DryRun: true);
        Assert.Equal(1, report.Drift);
        Assert.True(report.MutatedNothing);
    }

    [Fact]
    public void Explicit_IPlan_interface_methods_work()
    {
        IPlan<Row> planWithDrift = new TestPlan([new Row("item", ChangeKind.WouldChange, "drift")]);
        IPlan untypedDrift = planWithDrift;
        Assert.True(untypedDrift.HasDrift);
        Assert.Equal(1, planWithDrift.ExitCode(applied: false));
        Assert.Equal(0, planWithDrift.ExitCode(applied: true));

        IPlan<Row> planWithError = new TestPlan([], errors: ["something failed"]);
        IPlan untypedError = planWithError;
        Assert.False(untypedError.HasDrift);
        Assert.Equal(2, planWithError.ExitCode(applied: false));
        Assert.Equal(2, planWithError.ExitCode(applied: true));

        IPlan<Row> emptyPlan = new TestPlan([]);
        IPlan untypedEmpty = emptyPlan;
        Assert.False(untypedEmpty.HasDrift);
        Assert.Equal(0, emptyPlan.ExitCode(applied: false));
        Assert.Equal(0, emptyPlan.ExitCode(applied: true));
    }

    private sealed record Row(string Subject, ChangeKind Kind, string Detail = "") : IChange;

    private sealed class TestPlan(IReadOnlyList<Row> changes, IReadOnlyList<string>? errors = null) : IPlan<Row>
    {
        public IReadOnlyList<Row> Changes { get; } = changes;

        public IReadOnlyList<string> Errors { get; } = errors ?? [];
    }
}
