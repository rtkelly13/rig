using System.Runtime.InteropServices;
using System.Text;
using Rig.Core.Otel;

namespace Rig.Core.Tests.Otel;

/// <summary>The parts of <c>Rig.Core.Otel</c> the oracle does not reach: the file system, platforms, and the lenient edges.</summary>
public sealed class OtelCoreTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);

    private readonly TempDir temp = new();

    public void Dispose() => temp.Dispose();

    [Fact]
    public void Paths_sit_under_the_base_folder()
    {
        var paths = new OtelPaths(temp.Path);

        Assert.Equal(Path.Join(temp.Path, "otel", "data"), paths.Data);
        Assert.Equal(Path.Join(temp.Path, "otel", "otelcol.yaml"), paths.Config);
        Assert.Equal(Path.Join(temp.Path, "otel", "forward"), paths.Forward);
        Assert.Equal(Path.Join(temp.Path, "otel", "otelcol.pid"), paths.PidFile);
        Assert.Equal(Path.Join(temp.Path, "logs", "otelcol.log"), paths.Log);
        Assert.Equal(Path.Join(temp.Path, "otel", "clickhouse", "data"), paths.ClickHouseData);
        Assert.Equal(Path.Join(temp.Path, "logs", "clickhouse.log"), paths.ClickHouseLog);
        Assert.Equal(Path.Join(temp.Path, "logs", "otelcol-contrib.log"), paths.ContribLog);
        Assert.Equal(Path.Join(paths.Data, "traces.jsonl"), paths.ActiveFile("traces"));
        Assert.Equal(Path.Join("elsewhere", "logs.jsonl"), paths.ActiveFile("logs", "elsewhere"));
    }

    [Fact]
    public void Retention_takes_only_old_rotated_files()
    {
        var data = temp.Folder("data");
        var old = Touch(data, "traces-2026-11-01.jsonl", days: 8);
        Touch(data, "traces-2026-11-10.jsonl", days: 1);
        Touch(data, "traces.jsonl", days: 30);
        Touch(data, "other-2026-01-01.jsonl", days: 30);
        Touch(data, "logs-2026-01-01.txt", days: 30);
        var older = Touch(data, "logs-2026-01-01.jsonl", days: 30);

        Assert.Equal([older, old], Retention.Expired(data, Now));
        Assert.Equal([older, old], Retention.Prune(data, Now, apply: false));
        Assert.True(File.Exists(old));

        Assert.Equal([older, old], Retention.Prune(data, Now, apply: true));
        Assert.False(File.Exists(old));
        Assert.False(File.Exists(older));
        Assert.Empty(Retention.Expired(Path.Join(temp.Path, "missing"), Now));
    }

    [Fact]
    public void Platform_keys_name_the_pinned_builds()
    {
        Assert.Equal("darwin_arm64", OtelPins.PlatformKey("darwin", Architecture.Arm64));
        Assert.Equal("windows_amd64", OtelPins.PlatformKey("windows", Architecture.X64));
        Assert.Contains(OtelPins.PlatformKey(), OtelPins.Releases.Keys);

        var error = Assert.Throws<UnsupportedPlatformException>(() => OtelPins.PlatformKey("plan9", Architecture.X86));
        Assert.Equal($"no pinned otelcol {OtelPins.Version} build for plan9_x86", error.Message);
    }

    [Fact]
    public void Clickhouse_has_no_windows_pin()
    {
        var error = Assert.Throws<UnsupportedPlatformException>(() => OtelPins.ClickHousePin("windows_amd64"));

        Assert.Equal("ClickHouse has no native build for windows_amd64; on Windows run it under WSL or Docker", error.Message);
        Assert.Throws<UnsupportedPlatformException>(() => OtelPins.ContribPin("plan9_x86"));
        Assert.Equal("otelcol-contrib", OtelPins.ContribPin("darwin_arm64").Binary);
    }

    [Fact]
    public void Binaries_install_by_version_and_by_digest()
    {
        var pin = OtelPins.ClickHousePin("darwin_arm64");

        Assert.Equal(Path.Join("h", "bin", OtelPins.Version, "otelcol"), OtelPins.BinaryPath("h", "darwin_arm64"));
        Assert.Equal(Path.Join("h", "bin", OtelPins.Version), OtelPins.InstallDir("h"));
        Assert.Equal(Path.Join("h", "bin", pin.Sha256[..16], "clickhouse"), OtelPins.PinBinary(pin, "h"));
    }

    [Fact]
    public void Unsupported_platform_exception_has_the_usual_constructors()
    {
        var inner = new InvalidOperationException("x");

        Assert.NotNull(new UnsupportedPlatformException().Message);
        Assert.Same(inner, new UnsupportedPlatformException("m", inner).InnerException);
    }

    [Fact]
    public void Offsets_round_trip_through_a_file()
    {
        var path = Path.Join(temp.Path, "state", ReplayOffsets.StateFile);
        var offsets = new Dictionary<string, ReplayOffset>(StringComparer.Ordinal) { ["5"] = new(5, 12, "abc") };

        ReplayOffsets.Write(path, offsets);

        Assert.Equal(offsets, ReplayOffsets.Read(path));
        Assert.False(File.Exists(Path.ChangeExtension(path, ".tmp")));
        Assert.Empty(ReplayOffsets.Read(Path.Join(temp.Path, "missing.json")));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("not json")]
    [InlineData("""{"1": 5}""")]
    [InlineData("""{"1": {"sent": 1}}""")]
    [InlineData("""{"1": {"inode": 1}}""")]
    [InlineData("""{"1": {"inode": 1, "sent": 2, "head": null}}""")]
    [InlineData("""{"1": {"inode": 1, "sent": 2.5}}""")]
    [InlineData("""{"1": {"inode": 1, "sent": 2}, "2": {"inode": "x", "sent": 1}}""")]
    public void A_malformed_offsets_file_reads_as_none(string text) => Assert.Empty(ReplayOffsets.Parse(text));

    [Fact]
    public void Offsets_are_read_as_written()
    {
        var offsets = ReplayOffsets.Parse("""{"1": {"inode": 7, "sent": 1, "head": "h", "extra": "ignored"}, "2": {"inode": 2, "sent": 0}}""");

        Assert.Equal(new ReplayOffset(7, 1, "h"), offsets["1"]);
        Assert.Equal(new ReplayOffset(2, 0), offsets["2"]);
    }

    [Theory]
    [InlineData("""{"1": {"inode": " 7 ", "sent": 1}}""", "state.json: $.1.inode: expected a non-negative whole number, found the string \" 7 \"")]
    [InlineData("""{"1": {"inode": 2.0, "sent": 0}}""", "state.json: $.1.inode: expected a non-negative whole number, found the number 2.0")]
    [InlineData("""{"1": {"inode": 1, "sent": true}}""", "state.json: $.1.sent: expected a whole number, found true")]
    [InlineData("""{"1": {"inode": 1, "sent": 2}, "2": {"inode": 1}}""", "state.json: $.2.sent: expected a whole number, found nothing")]
    [InlineData("""{"1": {"inode": 1, "sent": 2, "head": 5}}""", "state.json: $.1.head: expected a string, found the number 5")]
    [InlineData("""{"1": 3}""", "state.json: $.1: expected an object, found the number 3")]
    [InlineData("""[]""", "state.json: $: expected an object, found a list")]
    public void A_malformed_offsets_file_reports_what_is_wrong_where(string text, string problem)
    {
        List<string> problems = [];

        Assert.Empty(ReplayOffsets.Parse(text, problems.Add, "state.json"));

        Assert.Equal([problem], problems);
    }

    [Fact]
    public void An_offsets_file_that_is_not_json_is_reported()
    {
        List<string> problems = [];

        Assert.Empty(ReplayOffsets.Parse("{\"torn\": ", problems.Add, "state.json"));

        Assert.StartsWith("state.json: $: not valid JSON: ", Assert.Single(problems), StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_is_the_same_while_its_start_is()
    {
        var raw = Encoding.UTF8.GetBytes("line one\nline two\n");

        Assert.True(ReplayOffsets.SameFile(new ReplayOffset(1, 9, ReplayOffsets.HeadHash(raw, 9)), raw));
        Assert.True(ReplayOffsets.SameFile(new ReplayOffset(1, 9), raw));
        Assert.False(ReplayOffsets.SameFile(new ReplayOffset(1, 9, "stale"), raw));
        Assert.False(ReplayOffsets.SameFile(new ReplayOffset(1, raw.Length + 1), raw));
        Assert.False(ReplayOffsets.SameFile(null, raw));
        Assert.False(ReplayOffsets.SameFile(new ReplayOffset(1, -1), raw));
    }

    [Fact]
    public void Spans_read_from_a_file_and_not_from_a_missing_one()
    {
        var path = temp.File("traces.jsonl");
        File.WriteAllText(path, """{"resourceSpans": [{"scopeSpans": [{"spans": [{"traceId": "t", "spanId": "s", "endTimeUnixNano": 1}]}]}]}""");

        var span = Assert.Single(TraceSpans.Read(path));

        Assert.Equal(1, span.EndNs);
        Assert.Empty(TraceSpans.Read(Path.Join(temp.Path, "missing.jsonl")));
    }

    [Theory]
    [InlineData("""{"traceId": "t", "spanId": "s", "endTimeUnixNano": 1.0}""", "$.resourceSpans[0].scopeSpans[0].spans[0].endTimeUnixNano: expected a whole number or a string of digits, found the number 1.0")]
    [InlineData("""{"traceId": "t", "spanId": "s", "startTimeUnixNano": true}""", "$.resourceSpans[0].scopeSpans[0].spans[0].startTimeUnixNano: expected a whole number or a string of digits, found true")]
    [InlineData("""{"traceId": "t", "spanId": "s", "endTimeUnixNano": "x"}""", "$.resourceSpans[0].scopeSpans[0].spans[0].endTimeUnixNano: expected a whole number or a string of digits, found the string \"x\"")]
    [InlineData("""{"traceId": 5, "spanId": "s"}""", "$.resourceSpans[0].scopeSpans[0].spans[0].traceId: expected a non-empty string, found the number 5")]
    [InlineData("""{"traceId": "t"}""", "$.resourceSpans[0].scopeSpans[0].spans[0].spanId: expected a non-empty string, found nothing")]
    [InlineData("""{"traceId": "t", "spanId": "s", "attributes": "nope"}""", "$.resourceSpans[0].scopeSpans[0].spans[0].attributes: expected a list, found the string \"nope\"")]
    public void A_span_of_the_wrong_type_is_skipped_and_reported_with_its_line(string span, string problem)
    {
        var path = temp.File("traces.jsonl");
        File.WriteAllText(path, """{"other": 1}""" + "\n" + $$"""{"resourceSpans": [{"scopeSpans": [{"spans": [{{span}}]}]}]}""" + "\n");
        List<string> skipped = [];

        Assert.Empty(TraceSpans.Read(path, skipped.Add));

        Assert.Equal([$"{path} line 2: {problem}"], skipped);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("null", false)]
    public void Incomplete_reads_the_flag(string flag, bool incomplete)
    {
        var line = $$$"""{"resourceSpans": [{"scopeSpans": [{"spans": [{"traceId": "t", "spanId": "s", "attributes": [{"key": "span.incomplete", "value": {"boolValue": {{{flag}}}}}]}]}]}]}""";

        Assert.Equal(incomplete, Assert.Single(TraceSpans.Parse(line)).Incomplete);
    }

    [Theory]
    [InlineData("1", "found the number 1")]
    [InlineData("0", "found the number 0")]
    [InlineData("\"yes\"", "found the string \"yes\"")]
    public void A_flag_that_is_not_a_bool_skips_the_span_and_says_why(string flag, string found)
    {
        var line = $$$"""{"resourceSpans": [{"scopeSpans": [{"spans": [{"traceId": "t", "spanId": "s", "attributes": [{"key": "span.incomplete", "value": {"boolValue": {{{flag}}}}}]}]}]}]}""";
        List<string> skipped = [];

        Assert.Empty(TraceSpans.Parse(line, skipped.Add, "traces.jsonl"));

        Assert.Equal([$"traces.jsonl line 1: $.resourceSpans[0].scopeSpans[0].spans[0].attributes[0].value.boolValue: expected true or false, {found}"], skipped);
    }

    [Fact]
    public void Repeated_keys_keep_the_last_as_recorded()
    {
        var span = Assert.Single(TraceSpans.Parse("""{"resourceSpans": [{"scopeSpans": [{"spans": [{"traceId": "a", "traceId": "b", "spanId": "s", "n": 1, "n": 2}]}]}]}"""));

        Assert.Equal("b", span.TraceId);
        Assert.Equal(2, span.Span["n"]!.GetValue<int>());
    }

    [Fact]
    public void Envelopes_need_a_positive_batch() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => TraceSpans.Envelopes([], batch: 0));

    [Fact]
    public void Envelopes_group_scopes_regardless_of_key_order()
    {
        var spans = TraceSpans.Parse("""
            {"resourceSpans": [{"resource": {"a": 1, "b": [true, null]}, "scopeSpans": [{"scope": {"name": "x", "version": "1"}, "spans": [{"traceId": "t", "spanId": "1"}]}]}]}
            {"resourceSpans": [{"resource": {"b": [true, null], "a": 1}, "scopeSpans": [{"scope": {"version": "1", "name": "x"}, "spans": [{"traceId": "t", "spanId": "2"}]}]}]}
            """);

        var body = Assert.Single(TraceSpans.Envelopes(spans));

        var resource = Assert.Single(body["resourceSpans"]!.AsArray());
        var scope = Assert.Single(resource!["scopeSpans"]!.AsArray());
        Assert.Equal(2, scope!["spans"]!.AsArray().Count);
    }

    [Fact]
    public void A_folder_where_a_file_should_be_reads_as_nothing()
    {
        Assert.Empty(TraceSpans.Read(temp.Path));
        Assert.Empty(ReplayOffsets.Read(temp.Path));
    }

    [Theory]
    [InlineData("""{"resourceSpans": [{"scopeSpans": "x"}]}""")]
    [InlineData("""{"resourceSpans": [{"scopeSpans": [{"spans": {}}]}]}""")]
    [InlineData("""{"resourceSpans": [1]}""")]
    public void A_misshapen_envelope_drops_the_whole_line(string line) =>
        Assert.Empty(TraceSpans.Parse($$"""
            {{line}}
            """ + "\n"));

    [Theory]
    [InlineData("http://[]:1")]
    [InlineData("http://[::1]")]
    [InlineData("http://h:99999")]
    [InlineData("http://h:1x")]
    [InlineData("http://u@:1")]
    public void Forward_refuses_a_host_or_port_it_cannot_read(string endpoint) =>
        Assert.Throws<FormatException>(() => CollectorProcess.ValidateForward(endpoint));

    private static string Touch(string folder, string name, int days)
    {
        var path = Path.Join(folder, name);
        File.WriteAllText(path, "{}\n");
        File.SetLastWriteTimeUtc(path, (Now - TimeSpan.FromDays(days)).UtcDateTime);
        return path;
    }
}
