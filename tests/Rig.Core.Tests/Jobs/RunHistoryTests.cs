using Rig.Core.Jobs;

namespace Rig.Core.Tests.Jobs;

/// <summary>The run history reads <c>runs.ndjson</c> strictly: a line with a wrongly typed field is skipped and reported, never coerced.</summary>
public sealed class RunHistoryTests : IDisposable
{
    private const string Good = """{"job":"tick","run_id":"tick-20261011-080201","started":"2026-10-11T08:02:01Z","seconds":1.5,"returncode":0,"trigger":"timer","error":null,"output":"","trace_id":"","span_id":""}""";

    private readonly TempDir _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void A_line_written_by_the_history_reads_back_as_the_same_run()
    {
        var run = new JobRun("tick", "tick-20261011-080201", "2026-10-11T08:02:01Z", 1.5, 3221225477, "timer", "boom", "runs/tick/x.log", "t", "s");
        RunHistory.Append(_temp.Path, run);

        Assert.Equal(run, Assert.Single(RunHistory.ReadRuns(_temp.Path)));
    }

    [Theory]
    [InlineData("""{"job":"tick","run_id":"r","started":"s","seconds":"1.5","trigger":"timer"}""", "$.seconds: expected a number, found the string \"1.5\"")]
    [InlineData("""{"job":"tick","run_id":"r","started":"s","seconds":1,"returncode":"0","trigger":"timer"}""", "$.returncode: expected a whole number, found the string \"0\"")]
    [InlineData("""{"job":"tick","run_id":"r","started":"s","seconds":1,"returncode":0.0,"trigger":"timer"}""", "$.returncode: expected a whole number, found the number 0.0")]
    [InlineData("""{"job":"tick","run_id":"r","started":"s","seconds":1,"returncode":true,"trigger":"timer"}""", "$.returncode: expected a whole number, found true")]
    [InlineData("""{"job":7,"run_id":"r","started":"s","seconds":1,"trigger":"timer"}""", "$.job: expected a string, found the number 7")]
    [InlineData("""{"run_id":"r","started":"s","seconds":1,"trigger":"timer"}""", "$.job: expected a string, found nothing")]
    [InlineData("""{"job":"tick","run_id":"r","started":"s","seconds":1,"trigger":"timer","output":[]}""", "$.output: expected a string, found a list")]
    [InlineData("""[1]""", "$: expected an object, found a list")]
    public void A_line_with_a_wrongly_typed_field_is_skipped_and_reported_with_file_line_path_and_what_was_found(string bad, string detail)
    {
        File.WriteAllText(Path.Join(_temp.Path, RunHistory.HistoryName), Good + "\n" + bad + "\n");
        var problems = new List<string>();

        var runs = RunHistory.ReadRuns(_temp.Path, problem: problems.Add);

        Assert.Equal("tick-20261011-080201", Assert.Single(runs).RunId);
        Assert.Equal($"{Path.Join(_temp.Path, RunHistory.HistoryName)}:2: {detail}", Assert.Single(problems));
    }

    [Fact]
    public void A_line_that_is_not_json_is_reported_as_such()
    {
        File.WriteAllText(Path.Join(_temp.Path, RunHistory.HistoryName), "{not json\n");
        var problems = new List<string>();

        Assert.Empty(RunHistory.ReadRuns(_temp.Path, problem: problems.Add));

        Assert.Contains(":1: $: not valid JSON:", Assert.Single(problems), StringComparison.Ordinal);
    }
}
