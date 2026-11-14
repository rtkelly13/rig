using System.Globalization;
using System.Text.Json.Nodes;

namespace Rig.Core.Tests.Observability;

/// <summary>
/// Builds the OTLP-JSON day files the tracers write, for the analytics tests in both projects: one envelope per line,
/// one folder per tool beneath the traces root.
/// </summary>
public static class TraceFixtures
{
    /// <summary>The Unix nanosecond timestamp fixture spans are measured from: 2026-03-04 10:00:00 UTC.</summary>
    public static readonly long Base = new DateTimeOffset(2026, 3, 4, 10, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds() * 1_000_000;

    /// <summary>The nanosecond string <paramref name="seconds"/> after <see cref="Base"/>.</summary>
    public static string Nanos(double seconds) => (Base + (long)(seconds * 1_000_000_000)).ToString(CultureInfo.InvariantCulture);

    /// <summary>One attribute value in OTLP-JSON, as <c>rig.tracing</c> writes it.</summary>
    public static JsonObject Value(object value) => value switch
    {
        bool flag => new JsonObject { ["boolValue"] = flag },
        int whole => new JsonObject { ["intValue"] = whole.ToString(CultureInfo.InvariantCulture) },
        long whole => new JsonObject { ["intValue"] = whole.ToString(CultureInfo.InvariantCulture) },
        double real => new JsonObject { ["doubleValue"] = real },
        string text => new JsonObject { ["stringValue"] = text },
        IEnumerable<object> items => new JsonObject
        {
            ["arrayValue"] = new JsonObject { ["values"] = new JsonArray([.. items.Select(Value)]) },
        },
        _ => new JsonObject { ["stringValue"] = (string)value },
    };

    /// <summary>An attribute list from pairs.</summary>
    public static JsonArray Attributes(params (string Key, object Value)[] attributes) =>
        [.. attributes.Select(pair => (JsonNode)new JsonObject { ["key"] = pair.Key, ["value"] = Value(pair.Value) })];

    /// <summary>A span body. Status is omitted when <paramref name="status"/> is null.</summary>
    public static JsonObject Span(
        string name,
        string spanId,
        string traceId = "trace-0001",
        string parent = "",
        double start = 0,
        double end = 1,
        int? status = 1,
        string message = "",
        (string Name, double At, (string Key, object Value)[] Detail)[]? events = null,
        params (string Key, object Value)[] attributes)
    {
        var span = new JsonObject
        {
            ["traceId"] = traceId,
            ["spanId"] = spanId,
            ["parentSpanId"] = parent,
            ["name"] = name,
            ["startTimeUnixNano"] = Nanos(start),
            ["endTimeUnixNano"] = Nanos(end),
            ["attributes"] = Attributes(attributes),
        };
        if (status is { } code)
        {
            span["status"] = new JsonObject { ["code"] = code, ["message"] = message };
        }

        if (events is not null)
        {
            span["events"] = new JsonArray(
            [
                .. events.Select(happened => (JsonNode)new JsonObject
                {
                    ["name"] = happened.Name,
                    ["timeUnixNano"] = Nanos(happened.At),
                    ["attributes"] = Attributes(happened.Detail),
                }),
            ]);
        }

        return span;
    }

    /// <summary>One <c>resourceSpans</c> line naming <paramref name="tool"/>.</summary>
    public static string Envelope(string tool, params JsonObject[] spans) => Envelope(tool, "rig.proc.git", spans);

    /// <summary>One <c>resourceSpans</c> line with the given service name and scope.</summary>
    public static string Envelope(string? tool, string scope, params JsonObject[] spans)
    {
        JsonNode? resource = tool is null
            ? null
            : new JsonObject { ["attributes"] = Attributes(("service.name", tool)) };
        return new JsonObject
        {
            ["resourceSpans"] = new JsonArray(
                new JsonObject
                {
                    ["resource"] = resource,
                    ["scopeSpans"] = new JsonArray(
                        new JsonObject { ["scope"] = new JsonObject { ["name"] = scope }, ["spans"] = new JsonArray(spans) }),
                }),
        }.ToJsonString();
    }

    /// <summary>Writes one envelope to <c>&lt;root&gt;/&lt;tool&gt;/&lt;day&gt;.ndjson</c> and returns its path.</summary>
    public static string Write(string root, string tool, string day, params JsonObject[] spans) =>
        Write(root, tool, tool, day, spans);

    /// <summary>Writes one envelope to <c>&lt;root&gt;/&lt;folder&gt;/&lt;day&gt;.ndjson</c> and returns its path.</summary>
    public static string Write(string root, string folder, string tool, string day, params JsonObject[] spans)
    {
        var folderPath = Directory.CreateDirectory(Path.Join(root, folder)).FullName;
        var path = Path.Join(folderPath, day + ".ndjson");
        File.WriteAllText(path, Envelope(tool, spans) + "\n");
        return path;
    }
}
