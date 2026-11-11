using System.Text;
using System.Text.Json.Nodes;
using Rig.Core.Otel;

namespace Rig.Core.Tests.Otel;

/// <summary>
/// Holds <c>Rig.Core.Otel</c> to <c>rig.otel_stack</c>. <c>otel-oracle.json</c> records what the Python wrote for the
/// collector and ClickHouse configs, the forward URL check, the Windows netstat parse, the launch record, the pins, the
/// replay head hash and offsets file, a trace file's spans, best copies and request bodies, and the service plist.
/// </summary>
public sealed class OtelOracleTests
{
    private static readonly JsonObject Oracle =
        JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Otel", "otel-oracle.json")))!.AsObject();

    public static TheoryData<int> ConfigCases => [.. Enumerable.Range(0, Oracle["collector_config"]!.AsArray().Count)];

    public static TheoryData<int> ClickHouseCases => [.. Enumerable.Range(0, Oracle["clickhouse"]!.AsArray().Count)];

    [Theory]
    [MemberData(nameof(ConfigCases))]
    public void Collector_config_matches_the_python(int index)
    {
        var expected = Oracle["collector_config"]![index]!;

        var text = CollectorConfig.Render(expected["data"]!.GetValue<string>(), expected["forward"]?.GetValue<string>());

        Assert.Equal(expected["text"]!.GetValue<string>(), text);
    }

    [Fact]
    public void Yaml_quoting_matches_the_python()
    {
        foreach (var (text, quoted) in Oracle["yaml_quoted"]!.AsObject())
        {
            Assert.Equal(quoted!.GetValue<string>(), CollectorConfig.YamlQuoted(text));
        }
    }

    [Theory]
    [MemberData(nameof(ClickHouseCases))]
    public void Clickhouse_server_config_matches_the_python(int index)
    {
        var expected = Oracle["clickhouse"]![index]!;

        var xml = ClickHouseConfig.ServerXml(expected["data"]!.GetValue<string>(), expected["users"]!.GetValue<string>());

        Assert.Equal(expected["server"]!.GetValue<string>(), xml);
    }

    [Fact]
    public void Clickhouse_users_and_contrib_configs_match_the_python()
    {
        Assert.Equal(Oracle["users_xml"]!.GetValue<string>(), ClickHouseConfig.UsersXml());
        Assert.Equal(Oracle["contrib_yaml"]!.GetValue<string>(), ClickHouseConfig.CollectorYaml());
    }

    [Fact]
    public void Forward_urls_are_checked_as_the_python_did()
    {
        foreach (var (url, expected) in Oracle["forward"]!.AsObject())
        {
            if (expected!["ok"] is { } ok)
            {
                Assert.Equal(ok.GetValue<string>(), CollectorProcess.ValidateForward(url));
            }
            else
            {
                var error = Assert.Throws<FormatException>(() => CollectorProcess.ValidateForward(url));
                Assert.Equal(expected["error"]!.GetValue<string>(), error.Message);
            }
        }
    }

    [Fact]
    public void Netstat_listeners_match_the_python()
    {
        var netstat = Oracle["netstat"]!;
        var text = netstat["text"]!.GetValue<string>();

        foreach (var (port, pid) in netstat["ports"]!.AsObject())
        {
            Assert.Equal(pid?.GetValue<int>(), CollectorProcess.ParseNetstatListener(text, int.Parse(port, System.Globalization.CultureInfo.InvariantCulture)));
        }
    }

    [Fact]
    public void Collector_commands_are_recognised_as_the_python_did()
    {
        foreach (var (command, expected) in Oracle["is_collector"]!.AsObject())
        {
            Assert.True(expected!.GetValue<bool>() == CollectorProcess.IsCollector(command), command);
        }
    }

    [Fact]
    public void Launch_records_round_trip_as_the_python_did()
    {
        foreach (var (text, expected) in Oracle["launched"]!.AsObject())
        {
            var launched = Launched.Parse(text);
            if (expected is null)
            {
                Assert.Null(launched);
                continue;
            }

            Assert.NotNull(launched);
            Assert.Equal(expected["pid"]!.GetValue<int>(), launched.Pid);
            Assert.Equal(expected["binary"]!.GetValue<string>(), launched.Binary);
            Assert.Equal(expected["started"]!.GetValue<string>(), launched.Started);
            Assert.Equal(expected["text"]!.GetValue<string>(), launched.Serialise());
        }
    }

    [Fact]
    public void Pins_match_the_python()
    {
        Assert.Equal(OtelPins.Version, Oracle["version"]!.GetValue<string>());
        AssertSame(Oracle["releases"]!, OtelPins.Releases.ToDictionary(p => p.Key, p => (object)new { asset = p.Value.Asset, sha256 = p.Value.Sha256, binary = p.Value.Binary }));
        AssertSame(Oracle["clickhouse_pins"]!, Pins(OtelPins.ClickHouse));
        AssertSame(Oracle["contrib_pins"]!, Pins(OtelPins.Contrib));

        static Dictionary<string, object> Pins(IReadOnlyDictionary<string, Pin> pins) =>
            pins.ToDictionary(p => p.Key, p => (object)new { url = p.Value.Url.OriginalString, sha256 = p.Value.Sha256, binary = p.Value.Binary, member = p.Value.Member });
    }

    [Fact]
    public void Head_hashes_match_the_python()
    {
        foreach (var entry in Oracle["head_hash"]!.AsArray())
        {
            var raw = Convert.FromBase64String(entry!["raw"]!.GetValue<string>());

            Assert.Equal(entry["hash"]!.GetValue<string>(), ReplayOffsets.HeadHash(raw, entry["sent"]!.GetValue<long>()));
        }
    }

    [Fact]
    public void The_offsets_file_matches_the_python_and_reads_back()
    {
        var offsets = new Dictionary<string, ReplayOffset>(StringComparer.Ordinal)
        {
            ["99"] = new(99, 10, "ab"),
            ["7"] = new(7, 5),
            ["100"] = new(100, 0),
        };

        var text = ReplayOffsets.Render(offsets);

        Assert.Equal(Oracle["offsets_text"]!.GetValue<string>(), text);
        Assert.Equal(Oracle["offsets_empty"]!.GetValue<string>(), ReplayOffsets.Render(new Dictionary<string, ReplayOffset>()));
        Assert.Equal(offsets.OrderBy(p => p.Key, StringComparer.Ordinal), ReplayOffsets.Parse(text).OrderBy(p => p.Key, StringComparer.Ordinal));
    }

    [Fact]
    public void Trace_spans_match_the_python()
    {
        var backfill = Oracle["backfill"]!;
        var spans = TraceSpans.Parse(backfill["text"]!.GetValue<string>());

        AssertSpans(backfill["spans"]!.AsArray(), spans);
        AssertSpans(backfill["best"]!.AsArray(), TraceSpans.BestCopies(spans));
        AssertBodies(backfill["envelopes"]!.AsArray(), TraceSpans.Envelopes(TraceSpans.BestCopies(spans)));
        AssertBodies(backfill["envelopes2"]!.AsArray(), TraceSpans.Envelopes(TraceSpans.BestCopies(spans), batch: 2));
    }

    [Fact]
    public void The_service_plist_matches_the_python()
    {
        var expected = Convert.FromBase64String(Oracle["plist"]!.GetValue<string>());

        var plist = OtelService.Render("/opt/otel/bin/otelcol", "/opt/otel/otelcol.yaml", Oracle["plist_log"]!.GetValue<string>());

        Assert.Equal(Encoding.UTF8.GetString(expected), Encoding.UTF8.GetString(plist));
    }

    private static void AssertSpans(JsonArray expected, IReadOnlyList<FileSpan> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        foreach (var (want, span) in expected.Zip(actual))
        {
            var got = new JsonObject
            {
                ["trace_id"] = span.TraceId,
                ["span_id"] = span.SpanId,
                ["resource"] = span.Resource?.DeepClone(),
                ["scope"] = span.Scope?.DeepClone(),
                ["span"] = span.Span.DeepClone(),
                ["end_ns"] = span.EndNs,
                ["incomplete"] = span.Incomplete,
            };
            Assert.True(JsonNode.DeepEquals(want, got), $"expected {want!.ToJsonString()}\nactual   {got.ToJsonString()}");
        }
    }

    private static void AssertBodies(JsonArray expected, IReadOnlyList<JsonObject> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        foreach (var (want, body) in expected.Zip(actual))
        {
            Assert.True(JsonNode.DeepEquals(want, body), $"expected {want!.ToJsonString()}\nactual   {body.ToJsonString()}");
        }
    }

    private static void AssertSame(JsonNode expected, object actual) =>
        Assert.True(JsonNode.DeepEquals(expected, System.Text.Json.JsonSerializer.SerializeToNode(actual)), expected.ToJsonString());
}
