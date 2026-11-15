using System.Text.Json.Nodes;
using Rig.Core.Reconciliation;

namespace Rig.Core.Tests.Reconciliation;

/// <summary>The runner's two guarantees: dry runs never apply, and exit codes are deterministic.</summary>
public sealed class ReconciliationRunnerTests
{
    [Fact]
    public async Task A_dry_run_plans_and_renders_but_never_calls_apply()
    {
        var reconciler = new FakeReconciler(
            new TestPlan([new Row("/temp/old", ChangeKind.WouldChange, "rm -rf /temp/old")]));
        using var output = Writer();

        var run = await ReconciliationRunner.RunAsync(reconciler, "target", apply: false, json: false, output, TestContext.Current.CancellationToken);

        Assert.Equal(1, reconciler.Plans);
        Assert.Equal(0, reconciler.Applies);
        Assert.Equal(1, run.ExitCode);
        Assert.Equal(
            "Reconciliation (dry run)\nwould-change  /temp/old — rm -rf /temp/old\n",
            output.ToString());
    }

    [Fact]
    public async Task Applying_calls_apply_once_and_returns_a_clean_exit_code()
    {
        var reconciler = new FakeReconciler(
            new TestPlan([new Row("/temp/old", ChangeKind.WouldChange, "rm -rf /temp/old")]));
        using var output = Writer();

        var run = await ReconciliationRunner.RunAsync(reconciler, "target", apply: true, json: false, output, TestContext.Current.CancellationToken);

        Assert.Equal(1, reconciler.Plans);
        Assert.Equal(1, reconciler.Applies);
        Assert.True(run.Applied);
        Assert.Equal(0, run.ExitCode);
        Assert.StartsWith("Reconciliation (applied)\n", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Apply_errors_force_exit_two_and_are_rendered()
    {
        var reconciler = new FakeReconciler(
            new TestPlan([new Row("/temp/old", ChangeKind.WouldChange, "rm -rf /temp/old")]),
            applyErrors: ["/temp/old: permission denied"]);
        using var output = Writer();

        var run = await ReconciliationRunner.RunAsync(reconciler, "target", apply: true, json: false, output, TestContext.Current.CancellationToken);

        Assert.Equal(2, run.ExitCode);
        Assert.True(run.HasErrors);
        Assert.Contains("error  /temp/old: permission denied\n", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_clean_plan_renders_no_changes_and_exits_zero()
    {
        var reconciler = new FakeReconciler(new TestPlan([]));
        using var output = Writer();

        var run = await ReconciliationRunner.RunAsync(reconciler, "target", apply: false, json: false, output, TestContext.Current.CancellationToken);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal("Reconciliation (dry run)\nNo changes.\n", output.ToString());
    }

    [Fact]
    public async Task Json_reports_the_typed_plan_without_rendering_text()
    {
        var reconciler = new FakeReconciler(
            new TestPlan([
                new Row("/temp/old", ChangeKind.WouldChange, "rm -rf /temp/old"),
                new Row("/temp/keep", ChangeKind.Refused, "in use"),
            ]));
        using var output = Writer();

        var run = await ReconciliationRunner.RunAsync(reconciler, "target", apply: false, json: true, output, TestContext.Current.CancellationToken);
        var payload = JsonNode.Parse(output.ToString())!;

        Assert.Equal(1, run.ExitCode);
        Assert.False(payload["applied"]!.GetValue<bool>());
        Assert.Equal(1, payload["exitCode"]!.GetValue<int>());
        Assert.Equal(["would-change", "refused"], payload["changes"]!.AsArray().Select(change => change!["kind"]!.GetValue<string>()));
        Assert.Equal("/temp/old", payload["changes"]![0]!["subject"]!.GetValue<string>());
    }

    [Fact]
    public async Task Planning_errors_force_exit_two_and_are_included_in_run_and_json()
    {
        var plan = new TestPlan([], errors: ["decision needed: prompt user"]);
        var reconciler = new FakeReconciler(plan);
        using var output = Writer();

        var run = await ReconciliationRunner.RunAsync(reconciler, "target", apply: false, json: true, output, TestContext.Current.CancellationToken);

        Assert.Equal(2, run.ExitCode);
        Assert.True(run.HasErrors);
        Assert.Equal(["decision needed: prompt user"], run.Errors);
        var payload = JsonNode.Parse(output.ToString())!;
        Assert.Equal(2, payload["exitCode"]!.GetValue<int>());
        Assert.Equal(["decision needed: prompt user"], payload["errors"]!.AsArray().Select(e => e!.GetValue<string>()));
    }

    [Fact]
    public void Dry_run_and_apply_are_opposites()
    {
        _ = Assert.Throws<ArgumentException>(() => ReconciliationOptions.FromFlags(dryRun: true, apply: true, json: false));
        Assert.True(ReconciliationOptions.FromFlags(dryRun: true, apply: false, json: false).DryRun);
        Assert.False(ReconciliationOptions.For(apply: true).DryRun);
    }

    [Fact]
    public void Synchronous_run_executes_sink_only_when_not_dry_run()
    {
        var wouldChange = new Row("/temp/a", ChangeKind.WouldChange);
        var aligned = new Row("/temp/b", ChangeKind.Aligned);
        var plan = new TestPlan([wouldChange, aligned]);

        var appliedList = new List<Row>();
        var dryReport = ReconciliationRunner.Run(plan, dryRun: true, row => appliedList.Add(row));
        Assert.True(dryReport.DryRun);
        Assert.Equal(0, dryReport.Applied);
        Assert.Equal(1, dryReport.Drift);
        Assert.True(dryReport.MutatedNothing);
        Assert.Empty(appliedList);

        var applyReport = ReconciliationRunner.Run(plan, dryRun: false, row => appliedList.Add(row));
        Assert.False(applyReport.DryRun);
        Assert.Equal(1, applyReport.Applied);
        Assert.Equal(1, applyReport.Drift);
        Assert.False(applyReport.MutatedNothing);
        Assert.Single(appliedList);
        Assert.Same(wouldChange, appliedList[0]);
    }

    [Fact]
    public async Task Reconciliation_run_and_names_and_json_helpers()
    {
        var plan = new TestPlan([new Row("/temp/a", ChangeKind.Aligned), new Row("/temp/b", ChangeKind.Changed)]);
        var run = new ReconciliationRun<TestPlan, Row>(plan, [], Applied: true, ExitCode: 0);
        Assert.True(run.Succeeded);
        Assert.False(run.HasErrors);
        Assert.Contains("\"applied\": true", ReconciliationRunner.Json(run), StringComparison.Ordinal);

        Assert.Equal("aligned", ReconciliationRunner.Name(ChangeKind.Aligned));
        Assert.Equal("would-change", ReconciliationRunner.Name(ChangeKind.WouldChange));
        Assert.Equal("changed", ReconciliationRunner.Name(ChangeKind.Changed));
        Assert.Equal("refused", ReconciliationRunner.Name(ChangeKind.Refused));
        Assert.Equal("error", ReconciliationRunner.Name((ChangeKind)99));

        Assert.True(plan.HasDrift());
        Assert.Equal(0, plan.ExitCode(applied: true));
        Assert.Equal(1, plan.ExitCode(applied: false));

        var errorPlan = new TestPlan([], errors: ["err"]);
        Assert.False(errorPlan.HasDrift());
        Assert.Equal(2, errorPlan.ExitCode(applied: false));

        var emptyChangeRow = new Row("/temp/c", ChangeKind.WouldChange, "");
        var renderPlan = new TestPlan([emptyChangeRow], errors: ["render-err"]);
        var renderRun = new ReconciliationRun<TestPlan, Row>(renderPlan, ["render-err"], Applied: false, ExitCode: 2);
        using var renderWriter = Writer();
        await ReconciliationRunner.RenderAsync(renderRun, ReconciliationOptions.For(apply: false), renderWriter, TestContext.Current.CancellationToken);
        Assert.Contains("would-change  /temp/c\n", renderWriter.ToString(), StringComparison.Ordinal);
        Assert.Contains("error  render-err\n", renderWriter.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Guard_clauses_throw_argument_null_exceptions()
    {
        var plan = new TestPlan([]);
        var reconciler = new FakeReconciler(plan);
        var options = ReconciliationOptions.For();
        using var output = Writer();
        var run = new ReconciliationRun<TestPlan, Row>(plan, [], Applied: false, ExitCode: 0);

        var ct = TestContext.Current.CancellationToken;
        Assert.Throws<ArgumentNullException>(() => ReconciliationRunner.Run<Row>(null!, false, _ => { }));
        Assert.Throws<ArgumentNullException>(() => ReconciliationRunner.Run<Row>(plan, false, null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => ReconciliationRunner.RunAsync<string, TestPlan, Row>(null!, "t", options, output, ct));
        await Assert.ThrowsAsync<ArgumentNullException>(() => ReconciliationRunner.RunAsync(reconciler, "t", null!, output, ct));
        await Assert.ThrowsAsync<ArgumentNullException>(() => ReconciliationRunner.RunAsync(reconciler, "t", options, null!, ct));
        await Assert.ThrowsAsync<ArgumentNullException>(() => ReconciliationRunner.RenderAsync<TestPlan, Row>(null!, options, output, ct));
        await Assert.ThrowsAsync<ArgumentNullException>(() => ReconciliationRunner.RenderAsync<TestPlan, Row>(run, null!, output, ct));
        await Assert.ThrowsAsync<ArgumentNullException>(() => ReconciliationRunner.RenderAsync<TestPlan, Row>(run, options, null!, ct));
        Assert.Throws<ArgumentNullException>(() => ReconciliationRunner.ToJson<TestPlan, Row>(null!));
        Assert.Throws<ArgumentNullException>(() => PlanExtensions.HasDrift<Row>(null!));
        Assert.Throws<ArgumentNullException>(() => PlanExtensions.ExitCode<Row>(null!, false));
    }

    private static StringWriter Writer() => new() { NewLine = "\n" };

    private sealed record Row(string Subject, ChangeKind Kind, string Detail = "") : IChange;

    private sealed class TestPlan(IReadOnlyList<Row> changes, IReadOnlyList<string>? errors = null) : IPlan<Row>
    {
        public IReadOnlyList<Row> Changes { get; } = changes;

        public IReadOnlyList<string> Errors { get; } = errors ?? [];
    }

    private sealed class FakeReconciler(TestPlan plan, IReadOnlyList<string>? applyErrors = null)
        : IReconciler<string, TestPlan, Row>
    {
        public int Plans { get; private set; }

        public int Applies { get; private set; }

        public Task<TestPlan> PlanAsync(string target, CancellationToken ct)
        {
            Plans++;
            return Task.FromResult(plan);
        }

        public Task<IReadOnlyList<string>> ApplyAsync(TestPlan plan, CancellationToken ct)
        {
            Applies++;
            return Task.FromResult(applyErrors ?? []);
        }
    }
}
