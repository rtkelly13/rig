using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Rig.Core.Chrome;
using Rig.Core.Observability;
using Rig.Core.Otel;
using Rig.Core.Tests.Observability;

namespace Rig.Core.Tests.Otel;

/// <summary>Every field the trace readers (the analytics view and the Chrome trace) read is typed strictly, and each says where.</summary>
public sealed class OtlpReaderStrictnessTests
{
    private const string Good = """{"traceId":"t","spanId":"s","name":"n","startTimeUnixNano":"100","endTimeUnixNano":"200"}""";

    // Both readers reject these the same way.
    [Theory]
    [InlineData("""{"name":7}""", "$.name: expected a string, found the number 7")]
    [InlineData("""{"parentSpanId":true}""", "$.parentSpanId: expected a string, found true")]
    [InlineData("""{"startTimeUnixNano":"x"}""", "$.startTimeUnixNano: expected a whole number or a string of digits, found the string \"x\"")]
    [InlineData("""{"endTimeUnixNano":2.5}""", "$.endTimeUnixNano: expected a whole number or a string of digits, found the number 2.5")]
    [InlineData("""{"status":"bad"}""", "$.status: expected an object, found the string \"bad\"")]
    [InlineData("""{"attributes":5}""", "$.attributes: expected a list, found the number 5")]
    [InlineData("""{"events":5}""", "$.events: expected a list, found the number 5")]
    [InlineData("""{"events":[null]}""", "$.events[0]: expected an object, found null")]
    [InlineData("""{"events":[{"name":5}]}""", "$.events[0].name: expected a string, found the number 5")]
    [InlineData("""{"events":[{"timeUnixNano":true}]}""", "$.events[0].timeUnixNano: expected a whole number or a string of digits, found true")]
    [InlineData("""{"events":[{"attributes":5}]}""", "$.events[0].attributes: expected a list, found the number 5")]
    [InlineData("""{"attributes":[null]}""", "$.attributes[0]: expected an object, found null")]
    [InlineData("""{"attributes":[{"value":{"stringValue":"x"}}]}""", "$.attributes[0].key: expected a string, found nothing")]
    [InlineData("""{"attributes":[{"key":null}]}""", "$.attributes[0].key: expected a string, found null")]
    [InlineData("""{"attributes":[{"key":"k","value":null}]}""", "$.attributes[0].value: expected an AnyValue object, found null")]
    [InlineData("""{"attributes":[{"key":"k","value":[]}]}""", "$.attributes[0].value: expected an AnyValue object, found a list")]
    [InlineData("""{"attributes":[{"key":"k","value":{"arrayValue":5}}]}""", "$.attributes[0].value.arrayValue: expected an object holding values, found the number 5")]
    [InlineData("""{"attributes":[{"key":"k","value":{"arrayValue":{"values":5}}}]}""", "$.attributes[0].value.arrayValue.values: expected a list, found the number 5")]
    [InlineData("""{"attributes":[{"key":"k","value":{"arrayValue":{"values":[{"stringValue":"a"},7]}}}]}""", "$.attributes[0].value.arrayValue.values[1]: expected an AnyValue object, found the number 7")]
    [InlineData("""{"attributes":[{"key":"k","value":{"stringValue":5}}]}""", "$.attributes[0].value.stringValue: expected a string, found the number 5")]
    [InlineData("""{"attributes":[{"key":"k","value":{"boolValue":"b"}}]}""", "$.attributes[0].value.boolValue: expected true or false, found the string \"b\"")]
    [InlineData("""{"attributes":[{"key":"k","value":{"intValue":1.5}}]}""", "$.attributes[0].value.intValue: expected a whole number or a string of digits, found the number 1.5")]
    [InlineData("""{"attributes":[{"key":"k","value":{"intValue":"1e2"}}]}""", "$.attributes[0].value.intValue: expected a whole number or a string of digits, found the string \"1e2\"")]
    [InlineData("""{"attributes":[{"key":"k","value":{"doubleValue":"y"}}]}""", "$.attributes[0].value.doubleValue: expected a number, found the string \"y\"")]
    public void A_field_of_the_wrong_type_skips_the_span_and_says_where(string fields, string problem)
    {
        var span = Merge(fields);

        Assert.EndsWith(problem[1..], Assert.Single(AnalyticsSkips(span)), StringComparison.Ordinal);
        Assert.EndsWith(problem[1..], Assert.Single(ChromeSkips(span)), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"status":null}""", "$.status: expected an object, found null")]
    [InlineData("""{"name":null}""", "$.name: expected a string, found null")]
    [InlineData("""{"parentSpanId":null}""", "$.parentSpanId: expected a string, found null")]
    [InlineData("""{"startTimeUnixNano":null}""", "$.startTimeUnixNano: expected a whole number or a string of digits, found null")]
    [InlineData("""{"endTimeUnixNano":null}""", "$.endTimeUnixNano: expected a whole number or a string of digits, found null")]
    [InlineData("""{"status":{"code":"2"}}""", "$.status.code: expected a whole number, found the string \"2\"")]
    [InlineData("""{"status":{"code":2,"message":3}}""", "$.status.message: expected a string, found the number 3")]
    public void The_trace_reader_rejects_a_null_and_reads_a_status_code_as_a_number_only(string fields, string problem)
    {
        Assert.EndsWith(problem[1..], Assert.Single(ChromeSkips(Merge(fields))), StringComparison.Ordinal);
    }

    [Fact]
    public void The_trace_reader_reads_an_absent_field_as_empty_and_a_message_only_of_an_error()
    {
        var spans = Chrome("""{"traceId":"t","spanId":"s","status":{"code":1,"message":3}}""");

        var span = Assert.Single(spans);
        Assert.Equal(("", "", 0L, 0L, ""), (span.ParentId, span.Name, span.StartNs, span.EndNs, span.Error));
    }

    [Theory]
    [InlineData("""{"status":{"code":2.0}}""", "$.status.code: expected a whole number or STATUS_CODE_ERROR, STATUS_CODE_OK or STATUS_CODE_UNSET, found the number 2.0")]
    [InlineData("""{"status":{"code":"Failed"}}""", "$.status.code: expected a whole number or STATUS_CODE_ERROR, STATUS_CODE_OK or STATUS_CODE_UNSET, found the string \"Failed\"")]
    [InlineData("""{"status":{"message":3}}""", "$.status.message: expected a string, found the number 3")]
    public void The_analytics_reader_reads_a_status_code_as_a_number_or_a_name(string fields, string problem)
    {
        Assert.EndsWith(problem[1..], Assert.Single(AnalyticsSkips(Merge(fields))), StringComparison.Ordinal);
    }

    [Fact]
    public void The_analytics_reader_reads_a_null_as_empty_and_the_status_names()
    {
        var spans = Analytics(Merge("""{"name":null,"parentSpanId":null,"startTimeUnixNano":null,"endTimeUnixNano":null,"status":null,"events":[{"name":null,"timeUnixNano":null}]}"""));
        var named = Analytics(Merge("""{"status":{"code":"STATUS_CODE_ERROR","message":null}}"""));
        var ok = Analytics(Merge("""{"status":{"code":"STATUS_CODE_OK"}}"""));

        var span = Assert.Single(spans);
        Assert.Equal(("", "", 0L, 0L, null), (span.Name, span.ParentSpanId, span.StartNs, span.EndNs, span.StatusCode));
        Assert.Equal(("", 0L), (Assert.Single(span.Events).Name, span.Events[0].AtNs));
        Assert.Equal((2, ""), (Assert.Single(named).StatusCode, named[0].StatusMessage));
        Assert.Null(Assert.Single(ok).StatusCode);
    }

    [Fact]
    public void A_repeated_member_keeps_its_last_value_and_a_repeated_attribute_does_too()
    {
        var span = """{"traceId":"t","spanId":"s","name":"a","name":"b","attributes":[{"key":"k","value":{"stringValue":"x"}},{"key":"k","value":{"stringValue":"y"}},{"key":"n","value":{"stringValue":"p","stringValue":"q"}}]}""";

        var analytics = Assert.Single(Analytics(span));
        var chrome = Assert.Single(Chrome(span));

        Assert.Equal(["b", "b"], [analytics.Name, chrome.Name]);
        Assert.Equal(["y", "y"], [(string?)analytics.Attributes["k"], (string?)chrome.Attributes["k"]]);
        Assert.Equal("q", analytics.Attributes["n"]);
    }

    [Fact]
    public void An_any_value_decodes_the_first_kind_present_and_reads_a_null_kind_as_absent()
    {
        var span = Assert.Single(Analytics(Merge("""
            {"attributes":[
              {"key":"list","value":{"arrayValue":{"values":[{"stringValue":"a"},{"intValue":"-4"},{"boolValue":true},{"doubleValue":1.5},{}]},"stringValue":"ignored"}},
              {"key":"text","value":{"stringValue":"s","intValue":"bad"}},
              {"key":"skipped","value":{"stringValue":null,"intValue":"4"}},
              {"key":"none","value":{"other":1}},
              {"key":"absent"}]}
            """)));

        Assert.Equal(["a", -4L, true, 1.5, null], (List<object?>)span.Attributes["list"]!);
        Assert.Equal("s", span.Attributes["text"]);
        Assert.Equal(4L, span.Attributes["skipped"]);
        Assert.Null(span.Attributes["none"]);
        Assert.Null(span.Attributes["absent"]);
    }

    [Fact]
    public void The_service_and_scope_are_read_strictly_when_they_are_objects()
    {
        var bad = Wrap(Good, """{"attributes":[5]}""", """{"name":5}""");
        var scope = Wrap(Good, "{}", """{"name":5}""");
        var fine = Wrap(Good, "5", "5");

        Assert.EndsWith(".attributes[0]: expected an object, found the number 5", Assert.Single(AnalyticsSkipsOf(bad)), StringComparison.Ordinal);
        Assert.EndsWith(".name: expected a string, found the number 5", Assert.Single(AnalyticsSkipsOf(scope)), StringComparison.Ordinal);
        Assert.Empty(AnalyticsSkipsOf(fine));
    }

    [Fact]
    public void The_converters_only_read()
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);

        _ = Assert.Throws<NotSupportedException>(() => new AnyValueConverter().Write(writer, "x", new JsonSerializerOptions()));
        _ = Assert.Throws<NotSupportedException>(() => new StatusCodeConverter().Write(writer, 2, new JsonSerializerOptions()));
    }

    [Theory]
    [InlineData("""{"a":null}""", "$.a: expected an object, found null")]
    [InlineData("""{"a":{"inode":1}}""", "$.a.sent: expected a whole number, found nothing")]
    [InlineData("""{"a":{"sent":1}}""", "$.a.inode: expected a non-negative whole number, found nothing")]
    [InlineData("""{"a":{"inode":-1,"sent":1}}""", "$.a.inode: expected a non-negative whole number, found the number -1")]
    [InlineData("""{"a":{"inode":1,"sent":"1"}}""", "$.a.sent: expected a whole number, found the string \"1\"")]
    [InlineData("""{"a":{"inode":1,"sent":1,"head":5}}""", "$.a.head: expected a string, found the number 5")]
    public void A_replay_entry_is_read_strictly(string text, string problem)
    {
        List<string> problems = [];

        var offsets = ReplayOffsets.Parse(text, problems.Add, "state");

        Assert.Empty(offsets);
        Assert.Equal("state: " + problem, Assert.Single(problems));
    }

    [Fact]
    public void A_replay_entry_keeps_the_last_of_a_repeated_path_and_ignores_what_it_does_not_read()
    {
        var offsets = ReplayOffsets.Parse("""{"a":{"inode":1,"sent":2},"a":{"inode":3,"sent":4,"extra":[1]}}""");

        Assert.Equal(new ReplayOffset(3, 4), offsets["a"]);
    }

    [Theory]
    [InlineData("""{"a":null}""", "$.a: expected an object, found null")]
    [InlineData("""{"a":{"size":1}}""", "$.a.mtime_ns: expected a whole number, found nothing")]
    [InlineData("""{"a":{"mtime_ns":1}}""", "$.a.size: expected a whole number, found nothing")]
    [InlineData("""{"a":{"size":1.5,"mtime_ns":1}}""", "$.a.size: expected a whole number, found the number 1.5")]
    public void A_file_mark_is_read_strictly(string text, string problem)
    {
        var path = Path.Join(Path.GetTempPath(), "marks-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, text, Encoding.UTF8);
        List<string> problems = [];

        var marks = Backfill.ReadMarks(path, problems.Add);
        File.Delete(path);

        Assert.Empty(marks);
        Assert.EndsWith(problem, Assert.Single(problems), StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_mark_reads_its_size_and_modification_time()
    {
        var path = Path.Join(Path.GetTempPath(), "marks-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, """{"a":{"size":5,"mtime_ns":9,"extra":null}}""", Encoding.UTF8);

        var marks = Backfill.ReadMarks(path);
        File.Delete(path);

        Assert.Equal(new FileMark(5, 9), marks["a"]);
    }

    [Theory]
    [InlineData("""{"table":null,"rows":1,"bytes_on_disk":2}""", "$.table: expected a string, found null")]
    [InlineData("""{"table":"t","rows":-1,"bytes_on_disk":2}""", "$.rows: expected a non-negative whole number, found the number -1")]
    [InlineData("""{"table":"t","rows":1,"bytes_on_disk":"2"}""", "$.bytes_on_disk: expected a non-negative whole number, found the string \"2\"")]
    [InlineData("""{"rows":1,"bytes_on_disk":2}""", "$.table: expected a string, found nothing")]
    [InlineData("null", "$: expected an object, found null")]
    public async Task A_table_row_is_read_strictly(string row, string problem)
    {
        using var handler = new FakeClickHouse { Stats = row };
        using var http = new HttpClient(handler);

        var error = await Assert.ThrowsAsync<ClickHouseException>(() => new ClickHouseClient(http).TableStatsAsync(TestContext.Current.CancellationToken));

        Assert.Equal("ClickHouse returned rows that could not be read: " + problem, error.Message);
    }

    [Fact]
    public async Task A_row_keeps_its_last_repeated_member_and_ignores_the_ones_it_does_not_read()
    {
        using var handler = new FakeClickHouse { Stats = "\n{\"table\":\"a\",\"table\":\"b\",\"rows\":1,\"bytes_on_disk\":2,\"x\":[]}\n\n" };
        using var http = new HttpClient(handler);

        var stats = await new ClickHouseClient(http).TableStatsAsync(TestContext.Current.CancellationToken);

        Assert.Equal([new TableStats("b", 1, 2)], stats);
    }

    private static string Merge(string fields) =>
        JsonNode.Parse(fields)!.AsObject().Aggregate(JsonNode.Parse(Good)!.AsObject(), (span, field) =>
        {
            span[field.Key] = field.Value?.DeepClone();
            return span;
        }).ToJsonString();

    private static string Wrap(string span, string resource, string scope) =>
        $$"""{"resourceSpans":[{"resource":{{resource}},"scopeSpans":[{"scope":{{scope}},"spans":[{{span}}]}]}]}""";

    private static List<string> AnalyticsSkips(string span) => AnalyticsSkipsOf(Wrap(span, "{}", "{}"));

    private static List<string> AnalyticsSkipsOf(string line)
    {
        List<string> skipped = [];
        _ = AnalyticsOf(line, skipped);
        return skipped;
    }

    private static List<TraceSpanInfo> Analytics(string span) => AnalyticsOf(Wrap(span, "{}", "{}"), []);

    private static List<TraceSpanInfo> AnalyticsOf(string line, List<string> skipped)
    {
        using var dir = new TempDir();
        var folder = Directory.CreateDirectory(Path.Join(dir.Path, "tool")).FullName;
        File.WriteAllText(Path.Join(folder, "2026-03-04.ndjson"), line + "\n", Encoding.UTF8);
        return [.. TraceAnalytics.Load(dir.Path, skipped: skipped.Add).Spans];
    }

    private static List<string> ChromeSkips(string span)
    {
        List<string> skipped = [];
        _ = ChromeTraces.ParseLines([Wrap(span, "{}", "{}")], skipped.Add, "src");
        return skipped;
    }

    private static List<TraceSpan> Chrome(string span) => [.. ChromeTraces.ParseLines([Wrap(span, "{}", "{}")], null, "src")];
}
