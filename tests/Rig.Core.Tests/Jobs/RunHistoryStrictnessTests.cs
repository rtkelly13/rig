using System.Text.Json.Serialization;
using Rig.Core.Jobs;
using Rig.Core.Otel;

namespace Rig.Core.Tests.Jobs;

/// <summary>What the typed readers of rig's own and of foreign JSON accept and refuse, beyond the messages the older tests pin.</summary>
public sealed class RunHistoryStrictnessTests : IDisposable
{
    private const string Required = "\"job\":\"tick\",\"run_id\":\"r\",\"started\":\"s\",\"seconds\":1,\"trigger\":\"timer\"";

    private readonly TempDir _temp = new();

    public void Dispose() => _temp.Dispose();

    [Theory]
    [InlineData("\"job\":\"again\"", "$.job: expected a field that appears once, found it repeated")]
    [InlineData("\"returncode\":\"12\"", "$.returncode: expected a whole number, found the string \"12\"")]
    [InlineData("\"returncode\":12.0", "$.returncode: expected a whole number, found the number 12.0")]
    [InlineData("\"returncode\":true", "$.returncode: expected a whole number, found true")]
    public void A_stored_run_that_is_not_the_history_s_own_shape_is_skipped_and_reported(string members, string problem)
    {
        File.WriteAllText(Path.Join(_temp.Path, RunHistory.HistoryName), "{" + Required + "," + members + "}\n");
        List<string> reported = [];

        Assert.Empty(RunHistory.ReadRuns(_temp.Path, problem: reported.Add));

        Assert.Equal([$"{Path.Join(_temp.Path, RunHistory.HistoryName)}:1: {problem}"], reported);
    }

    [Fact]
    public void The_run_history_context_ignores_what_it_does_not_define_and_refuses_a_repeat()
    {
        var options = RunHistoryJsonContext.Default.Options;

        Assert.Equal((JsonUnmappedMemberHandling.Skip, false, true, true), (options.UnmappedMemberHandling, options.AllowDuplicateProperties, options.RespectNullableAnnotations, options.RespectRequiredConstructorParameters));
    }

    [Fact]
    public void A_line_with_a_member_the_history_does_not_define_still_reads()
    {
        File.WriteAllText(Path.Join(_temp.Path, RunHistory.HistoryName), "{" + Required + ",\"extra\":{\"x\":[1]}}\n");

        Assert.Equal("tick", Assert.Single(RunHistory.ReadRuns(_temp.Path)).Job);
    }

    [Fact]
    public void A_line_with_no_version_reads_as_version_one()
    {
        File.WriteAllText(Path.Join(_temp.Path, RunHistory.HistoryName), "{" + Required + "}\n");

        Assert.Single(RunHistory.ReadRuns(_temp.Path));
    }

    [Theory]
    [InlineData(2, "the number 2")]
    [InlineData(0, "the number 0")]
    public void A_line_with_a_version_this_build_does_not_read_is_skipped_naming_version(int version, string found)
    {
        File.WriteAllText(Path.Join(_temp.Path, RunHistory.HistoryName), "{\"version\":" + version + "," + Required + "}\n");
        List<string> reported = [];

        Assert.Empty(RunHistory.ReadRuns(_temp.Path, problem: reported.Add));

        Assert.Equal([$"{Path.Join(_temp.Path, RunHistory.HistoryName)}:1: $.version: expected a version from 1 to 1, found {found}"], reported);
    }

    [Fact]
    public void A_version_of_the_wrong_type_is_skipped_as_a_type_error()
    {
        File.WriteAllText(Path.Join(_temp.Path, RunHistory.HistoryName), "{\"version\":\"1\"," + Required + "}\n");
        List<string> reported = [];

        Assert.Empty(RunHistory.ReadRuns(_temp.Path, problem: reported.Add));

        Assert.EndsWith("$.version: expected a whole number, found the string \"1\"", Assert.Single(reported), StringComparison.Ordinal);
    }

    [Fact]
    public void A_line_written_by_the_history_reads_back_unchanged()
    {
        var run = new JobRun("tick", "tick-20261011-080201", "2026-10-11T08:02:01Z", 1.5, 0, "timer", null, "", "", "");
        RunHistory.Append(_temp.Path, run);

        Assert.Equal(run, Assert.Single(RunHistory.ReadRuns(_temp.Path)));
    }

    [Fact]
    public void The_otlp_context_ignores_what_it_does_not_read_but_types_what_it_does()
    {
        var options = OtlpJsonContext.Default.Options;

        Assert.Equal((JsonUnmappedMemberHandling.Skip, true, true, true), (options.UnmappedMemberHandling, options.AllowDuplicateProperties, options.RespectNullableAnnotations, options.RespectRequiredConstructorParameters));
    }

    [Theory]
    [InlineData("""{"traceId": "t", "spanId": "s", "attributes": [{"key": "span.incomplete", "value": 5}]}""", "$.resourceSpans[0].scopeSpans[0].spans[0].attributes[0].value: expected an object, found the number 5")]
    [InlineData("""{"traceId": "t", "spanId": "s", "attributes": [{"key": "span.incomplete", "value": {"boolValue": "x"}}]}""", "$.resourceSpans[0].scopeSpans[0].spans[0].attributes[0].value.boolValue: expected true or false, found the string \"x\"")]
    [InlineData("""{"traceId": "t", "spanId": "s", "attributes": [null]}""", "$.resourceSpans[0].scopeSpans[0].spans[0].attributes[0]: expected an object, found null")]
    [InlineData("""{"traceId": "t", "spanId": "s", "startTimeUnixNano": "12abc"}""", "$.resourceSpans[0].scopeSpans[0].spans[0].startTimeUnixNano: expected a whole number or a string of digits, found the string \"12abc\"")]
    [InlineData("""5""", "$.resourceSpans[0].scopeSpans[0].spans[0]: expected an object, found the number 5")]
    public void A_span_of_the_wrong_shape_is_skipped_with_the_path_of_the_fault(string span, string problem)
    {
        List<string> skipped = [];

        Assert.Empty(TraceSpans.Parse($$"""{"resourceSpans": [{"scopeSpans": [{"spans": [{{span}}]}]}]}""", skipped.Add, "t.jsonl"));

        Assert.Equal([$"t.jsonl line 1: {problem}"], skipped);
    }

    [Fact]
    public void A_misshapen_span_costs_only_that_span()
    {
        List<string> skipped = [];

        var spans = TraceSpans.Parse("""{"resourceSpans": [{"scopeSpans": [{"spans": [{"traceId": "a", "spanId": "1"}, 5, {"traceId": "b", "spanId": "2"}]}]}]}""", skipped.Add);

        Assert.Equal(["a", "b"], spans.Select(span => span.TraceId));
        Assert.Single(skipped);
    }

    [Theory]
    [InlineData("\"12\"", 12)]
    [InlineData("12", 12)]
    [InlineData("\"-5\"", -5)]
    public void A_time_reads_as_an_int64_from_a_number_or_digits(string time, long nanos) =>
        Assert.Equal(nanos, Assert.Single(TraceSpans.Parse($$"""{"resourceSpans": [{"scopeSpans": [{"spans": [{"traceId": "t", "spanId": "s", "endTimeUnixNano": {{time}}}]}]}]}""")).EndNs);
}
