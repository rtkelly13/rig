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
    [InlineData("\"extra\":1", "$.extra: expected a known field, found the number 1")]
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
    public void The_run_history_context_is_strict_about_what_it_does_not_define()
    {
        var options = RunHistoryJsonContext.Default.Options;

        Assert.Equal((JsonUnmappedMemberHandling.Disallow, false, true, true), (options.UnmappedMemberHandling, options.AllowDuplicateProperties, options.RespectNullableAnnotations, options.RespectRequiredConstructorParameters));
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
