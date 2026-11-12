using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using Rig.Core.Chrome;
using Rig.Core.Otel;

namespace Rig.Core.Tests.Otel;

/// <summary>
/// The OTLP-JSON day files the C# tools write: the contract <c>rig.tracing</c> holds, read back by the trace readers.
/// Each test listens to a source of its own, so parallel tests never see each other's spans.
/// </summary>
public sealed class TraceFileWriterTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 3, 4, 23, 30, 0, TimeSpan.Zero);
    private readonly TempDir _temp = new();
    private readonly ActivitySource _source = new($"Rig.Tests.{Guid.NewGuid():N}");
    private readonly Dictionary<string, string?> _env = [];

    public void Dispose()
    {
        _source.Dispose();
        _temp.Dispose();
    }

    private TraceFileWriter Writer(string scope = "rig.tests") =>
        new("tool", new Dictionary<string, string> { [_source.Name] = scope }, name => _env.GetValueOrDefault(name), _temp.Path, new FixedTime(Now));

    private string DayFile => Path.Join(_temp.Path, "tool", "2026-03-04.ndjson");

    private static JsonNode Line(string path, int index = 0) => JsonNode.Parse(File.ReadAllLines(path)[index])!;

    private static JsonNode Spans(JsonNode envelope, int scope = 0) => envelope["resourceSpans"]![0]!["scopeSpans"]![scope]!["spans"]!;

    [Fact]
    public void A_finished_span_is_written_in_the_envelope_on_dispose()
    {
        using (Writer())
        {
            using var activity = _source.StartActivity("outer");
            activity!.SetTag("count", 3).SetTag("ok", true).SetTag("ratio", 0.5).SetTag("name", "x");
        }

        var envelope = Line(DayFile);
        var resource = envelope["resourceSpans"]![0]!;
        Assert.Equal("""[{"key":"service.name","value":{"stringValue":"tool"}}]""", resource["resource"]!["attributes"]!.ToJsonString());
        Assert.Equal("rig.tests", resource["scopeSpans"]![0]!["scope"]!["name"]!.GetValue<string>());
        var span = Spans(envelope)[0]!;
        Assert.Equal("outer", span["name"]!.GetValue<string>());
        Assert.Equal(string.Empty, span["parentSpanId"]!.GetValue<string>());
        Assert.Equal(32, span["traceId"]!.GetValue<string>().Length);
        Assert.Equal(16, span["spanId"]!.GetValue<string>().Length);
        Assert.Equal("""{"code":1}""", span["status"]!.ToJsonString());
        Assert.Equal(
            """[{"key":"count","value":{"intValue":"3"}},{"key":"ok","value":{"boolValue":true}},{"key":"ratio","value":{"doubleValue":0.5}},{"key":"name","value":{"stringValue":"x"}}]""",
            span["attributes"]!.ToJsonString());
        Assert.Null(span["events"]);
        Assert.True(long.Parse(span["endTimeUnixNano"]!.GetValue<string>(), CultureInfo.InvariantCulture) >= long.Parse(span["startTimeUnixNano"]!.GetValue<string>(), CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Children_name_their_parent_and_errors_carry_their_message_and_events()
    {
        using (Writer())
        {
            using var outer = _source.StartActivity("outer");
            using var inner = _source.StartActivity("inner");
            inner!.SetStatus(ActivityStatusCode.Error, "IOException: gone");
            inner.AddEvent(new ActivityEvent("exception", tags: new ActivityTagsCollection { ["exception.type"] = "IOException" }));
            inner.AddEvent(new ActivityEvent("bare"));
        }

        var spans = Spans(Line(DayFile));
        var child = spans[0]!;
        Assert.Equal(spans[1]!["spanId"]!.GetValue<string>(), child["parentSpanId"]!.GetValue<string>());
        Assert.Equal("""{"code":2,"message":"IOException: gone"}""", child["status"]!.ToJsonString());
        Assert.Equal("exception", child["events"]![0]!["name"]!.GetValue<string>());
        Assert.Equal("""[{"key":"exception.type","value":{"stringValue":"IOException"}}]""", child["events"]![0]!["attributes"]!.ToJsonString());
        Assert.Null(child["events"]![1]!["attributes"]);
        Assert.NotNull(child["events"]![1]!["timeUnixNano"]);
    }

    [Fact]
    public void Attribute_values_follow_the_python_encoding()
    {
        Assert.Equal("""{"intValue":"7"}""", Json(TraceFileWriter.AttributeValue(7L)));
        Assert.Equal("""{"intValue":"255"}""", Json(TraceFileWriter.AttributeValue((byte)255)));
        Assert.Equal("""{"doubleValue":1.5}""", Json(TraceFileWriter.AttributeValue(1.5f)));
        Assert.Equal("""{"doubleValue":2.25}""", Json(TraceFileWriter.AttributeValue(2.25m)));
        Assert.Equal("""{"arrayValue":{"values":[{"stringValue":"a"},{"intValue":"1"}]}}""", Json(TraceFileWriter.AttributeValue(new object[] { "a", 1 })));
        Assert.Equal("""{"stringValue":"Monday"}""", Json(TraceFileWriter.AttributeValue(DayOfWeek.Monday)));
        Assert.Equal("""{"stringValue":""}""", Json(TraceFileWriter.AttributeValue(null)));
    }

    private static string Json(Dictionary<string, object?> value) => System.Text.Json.JsonSerializer.Serialize(value);

    [Fact]
    public void Scopes_group_their_own_spans_in_one_line()
    {
        using var other = new ActivitySource($"Rig.Tests.{Guid.NewGuid():N}");
        using (new TraceFileWriter("tool", new Dictionary<string, string> { [_source.Name] = "one", [other.Name] = "two" }, _ => null, _temp.Path, new FixedTime(Now)))
        {
            _source.StartActivity("a")!.Dispose();
            other.StartActivity("b")!.Dispose();
            _source.StartActivity("c")!.Dispose();
        }

        var envelope = Line(DayFile);
        Assert.Single(File.ReadAllLines(DayFile));
        Assert.Equal(2, Spans(envelope, 0).AsArray().Count);
        Assert.Equal("two", envelope["resourceSpans"]![0]!["scopeSpans"]![1]!["scope"]!["name"]!.GetValue<string>());
    }

    [Fact]
    public void Each_flush_appends_one_line_and_an_empty_one_writes_nothing()
    {
        using var writer = Writer();
        writer.Flush();
        Assert.False(File.Exists(DayFile));

        _source.StartActivity("a")!.Dispose();
        writer.Flush();
        _source.StartActivity("b")!.Dispose();
        writer.Flush();

        Assert.Equal(2, File.ReadAllLines(DayFile).Length);
    }

    [Fact]
    public void A_flush_with_open_spans_snapshots_them_as_incomplete_and_the_reader_keeps_the_finished_copy()
    {
        using var writer = Writer(ChromeTraces.Scope);
        var run = _source.StartActivity(ChromeTraces.RunSpan)!;
        run.SetTag("chrome.trigger", "scheduled");
        writer.Flush(includeOpen: true);

        var snapshot = Spans(Line(DayFile))[0]!;
        Assert.Contains("""{"key":"span.incomplete","value":{"boolValue":true}}""", snapshot["attributes"]!.ToJsonString(), StringComparison.Ordinal);
        Assert.Equal(Now.ToUnixTimeMilliseconds() * 1_000_000, long.Parse(snapshot["endTimeUnixNano"]!.GetValue<string>(), CultureInfo.InvariantCulture));

        run.Dispose();
        writer.Flush(includeOpen: true);

        var spans = ChromeTraces.ParseLines(File.ReadAllLines(DayFile));
        var span = Assert.Single(spans);
        Assert.False(span.Incomplete);
        Assert.Equal("scheduled", Assert.Single(ChromeTraces.Runs(spans)).Trigger);
    }

    [Fact]
    public void A_full_buffer_writes_without_waiting_for_a_flush()
    {
        using var writer = Writer();
        for (var i = 0; i < TraceFileWriter.FlushAt; i++)
        {
            _source.StartActivity("a")!.Dispose();
        }

        Assert.Equal(TraceFileWriter.FlushAt, Spans(Line(DayFile)).AsArray().Count);
    }

    [Theory]
    [InlineData("0")]
    [InlineData(" False ")]
    [InlineData("no")]
    [InlineData("OFF")]
    public void Tracing_turned_off_writes_nothing(string value)
    {
        _env[TraceFileWriter.EnabledVariable] = value;
        using (var writer = Writer())
        {
            Assert.False(writer.Enabled);
            var open = _source.StartActivity("open");
            _source.StartActivity("a")!.Dispose();
            writer.Flush(includeOpen: true);
            open!.Dispose();
        }

        Assert.False(Directory.Exists(Path.Join(_temp.Path, "tool")));
    }

    [Fact]
    public void A_set_directory_takes_the_day_file_until_reset_and_the_dir_variable_beats_it()
    {
        using var writer = Writer();
        var chosen = _temp.Folder("chosen");
        _source.StartActivity("before")!.Dispose();

        writer.SetDirectory(chosen);
        Assert.True(File.Exists(DayFile));
        Assert.Equal(Path.Join(chosen, "2026-03-04.ndjson"), writer.TraceFile);

        _env[TraceFileWriter.DirVariable] = "D";
        Assert.Equal(Path.Join("D", "tool", "2026-03-04.ndjson"), writer.TraceFile);
        _env.Remove(TraceFileWriter.DirVariable);

        _env[TraceFileWriter.HomeVariable] = "H";
        Assert.Equal(Path.Join(chosen, "2026-03-04.ndjson"), writer.TraceFile);
        writer.SetDirectory(null);
        Assert.Equal(Path.Join("H", "tool", "2026-03-04.ndjson"), writer.TraceFile);
        _env[TraceFileWriter.HomeVariable] = string.Empty;
        Assert.Equal(DayFile, writer.TraceFile);
    }

    [Fact]
    public void A_write_that_fails_drops_the_buffer_without_throwing()
    {
        File.WriteAllText(_temp.File("tool"), "a file where the folder should be");
        using var writer = Writer();
        _source.StartActivity("a")!.Dispose();

        writer.Flush();

        Assert.True(File.Exists(Path.Join(_temp.Path, "tool")));
    }

    [Fact]
    public void Other_sources_are_not_written()
    {
        using var other = new ActivitySource($"Rig.Tests.{Guid.NewGuid():N}");
        using (Writer())
        {
            Assert.Null(other.StartActivity("ignored"));
        }

        Assert.False(File.Exists(DayFile));
    }

    [Fact]
    public void The_periodic_flush_writes_without_being_asked()
    {
        using var writer = Writer();
        writer.FlushPeriodically(TimeSpan.FromMilliseconds(10));
        writer.FlushPeriodically(TimeSpan.FromHours(1));
        _source.StartActivity("a")!.Dispose();

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!File.Exists(DayFile) && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(10);
        }

        Assert.True(File.Exists(DayFile));
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
