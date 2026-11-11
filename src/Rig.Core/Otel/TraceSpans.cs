using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Rig.Core.Vault;

namespace Rig.Core.Otel;

/// <summary>One span read from a trace file, with the resource and scope it was written under.</summary>
/// <param name="TraceId">The span's trace.</param>
/// <param name="SpanId">The span's id within it.</param>
/// <param name="Resource">The resource it was written under, as written; null when absent.</param>
/// <param name="Scope">The scope it was written under, as written; null when absent.</param>
/// <param name="Span">The span, as written.</param>
/// <param name="EndNs">Its end time, else its start time, else 0.</param>
/// <param name="Incomplete">It was flushed before it ended (<c>span.incomplete</c>).</param>
public sealed record FileSpan(string TraceId, string SpanId, JsonNode? Resource, JsonNode? Scope, JsonObject Span, long EndNs, bool Incomplete);

/// <summary>
/// The OTLP/JSON trace files rig and its tools write, read and regrouped for the collector. Ported from the pure half of
/// <c>rig.otel_stack.backfill</c>: a span can be written twice (incomplete at a forced flush, then complete), and the
/// complete copy is the one sent.
/// </summary>
public static class TraceSpans
{
    /// <summary>How many spans go in one request body.</summary>
    public const int BatchSpans = 500;

    /// <summary>Every span in the trace file at <paramref name="path"/>; none when it cannot be read.</summary>
    public static IReadOnlyList<FileSpan> Read(string path)
    {
        string text;
        try
        {
            text = File.ReadAllText(path, Encoding.UTF8);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        return Parse(text);
    }

    /// <summary>
    /// Every span in <paramref name="text"/>. A line that is not an envelope (including a torn last line) is skipped
    /// whole, as is a span without a string trace and span id.
    /// </summary>
    public static IReadOnlyList<FileSpan> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var spans = new List<FileSpan>();
        foreach (var line in PyText.SplitLines(text))
        {
            if (PyText.Strip(line).Length > 0 && Envelope(line) is { } envelope)
            {
                spans.AddRange(envelope);
            }
        }

        return spans;
    }

    /// <summary>One copy per span: the complete one when both were written, otherwise the last.</summary>
    public static IReadOnlyList<FileSpan> BestCopies(IEnumerable<FileSpan> spans)
    {
        ArgumentNullException.ThrowIfNull(spans);
        var chosen = new OrderedDictionary<(string, string), FileSpan>();
        foreach (var span in spans)
        {
            var key = (span.TraceId, span.SpanId);
            if (!chosen.TryGetValue(key, out var held) || held.Incomplete || !span.Incomplete)
            {
                chosen[key] = span;
            }
        }

        return [.. chosen.Values];
    }

    /// <summary>OTLP/JSON request bodies for <paramref name="spans"/>, regrouped under their resource and scope, <paramref name="batch"/> spans each.</summary>
    public static IReadOnlyList<JsonObject> Envelopes(IReadOnlyList<FileSpan> spans, int batch = BatchSpans)
    {
        ArgumentNullException.ThrowIfNull(spans);
        ArgumentOutOfRangeException.ThrowIfLessThan(batch, 1);
        var bodies = new List<JsonObject>();
        foreach (var chunk in spans.Chunk(batch))
        {
            var grouped = new OrderedDictionary<string, (JsonNode? Resource, OrderedDictionary<string, (JsonNode? Scope, JsonArray Spans)> Scopes)>(StringComparer.Ordinal);
            foreach (var span in chunk)
            {
                var resourceKey = Canonical(span.Resource);
                if (!grouped.TryGetValue(resourceKey, out var resource))
                {
                    resource = (span.Resource, new(StringComparer.Ordinal));
                    grouped[resourceKey] = resource;
                }

                var scopeKey = Canonical(span.Scope);
                if (!resource.Scopes.TryGetValue(scopeKey, out var scope))
                {
                    scope = (span.Scope, []);
                    resource.Scopes[scopeKey] = scope;
                }

                scope.Spans.Add(span.Span.DeepClone());
            }

            var resourceSpans = new JsonArray();
            foreach (var (resource, scopes) in grouped.Values)
            {
                var scopeSpans = new JsonArray();
                foreach (var (scope, members) in scopes.Values)
                {
                    scopeSpans.Add(new JsonObject { ["scope"] = scope?.DeepClone(), ["spans"] = members });
                }

                resourceSpans.Add(new JsonObject { ["resource"] = resource?.DeepClone(), ["scopeSpans"] = scopeSpans });
            }

            bodies.Add(new JsonObject { ["resourceSpans"] = resourceSpans });
        }

        return bodies;
    }

    // The spans of one line, or null when the line is not an envelope. The Python decoded the envelope strictly: a
    // field of the wrong shape anywhere in it dropped the whole line.
    private static List<FileSpan>? Envelope(string line)
    {
        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(line);
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }

        if (root.ValueKind != JsonValueKind.Object || Objects(LaxJson.Field(root, "resourceSpans")) is not { } resources)
        {
            return null;
        }

        var spans = new List<FileSpan>();
        foreach (var resource in resources)
        {
            if (Objects(LaxJson.Field(resource, "scopeSpans")) is not { } scopes)
            {
                return null;
            }

            var resourceNode = Node(LaxJson.Field(resource, "resource"));
            foreach (var scope in scopes)
            {
                if (Objects(LaxJson.Field(scope, "spans")) is not { } raws)
                {
                    return null;
                }

                var scopeNode = Node(LaxJson.Field(scope, "scope"));
                spans.AddRange(raws.Select(raw => View(raw, resourceNode, scopeNode)).OfType<FileSpan>());
            }
        }

        return spans;
    }

    // An absent list reads as empty; anything but a list of objects fails.
    private static List<JsonElement>? Objects(JsonElement? value) => value switch
    {
        null => [],
        { ValueKind: JsonValueKind.Array } array when array.EnumerateArray().All(item => item.ValueKind == JsonValueKind.Object) => [.. array.EnumerateArray()],
        _ => null,
    };

    private static FileSpan? View(JsonElement raw, JsonNode? resource, JsonNode? scope)
    {
        if (LaxJson.Field(raw, "traceId") is not { ValueKind: JsonValueKind.String } trace
            || LaxJson.Field(raw, "spanId") is not { ValueKind: JsonValueKind.String } span
            || trace.GetString() is not { Length: > 0 } traceId
            || span.GetString() is not { Length: > 0 } spanId)
        {
            return null;
        }

        var end = LaxJson.Int(LaxJson.Field(raw, "endTimeUnixNano")) ?? 0;
        var start = LaxJson.Int(LaxJson.Field(raw, "startTimeUnixNano")) ?? 0;
        return new FileSpan(traceId, spanId, resource?.DeepClone(), scope?.DeepClone(), (JsonObject)Node(raw)!, end != 0 ? end : start, Incomplete(raw));
    }

    // Lenient: attributes of the wrong shape (or any attribute not an object) read as none.
    private static bool Incomplete(JsonElement raw) =>
        Objects(LaxJson.Field(raw, "attributes")) is { } attributes
        && attributes.Any(attribute =>
            LaxJson.Field(attribute, "key") is { ValueKind: JsonValueKind.String } key
            && key.ValueEquals("span.incomplete")
            && LaxJson.Field(attribute, "value") is { ValueKind: JsonValueKind.Object } value
            && LaxJson.Bool(LaxJson.Field(value, "boolValue")) == true);

    // A node from an element, the last of a repeated key winning as in Python's json.
    private static JsonNode? Node(JsonElement? element)
    {
        switch (element)
        {
            case null or { ValueKind: JsonValueKind.Null }:
                return null;
            case { ValueKind: JsonValueKind.Object } obj:
                var node = new JsonObject();
                foreach (var property in obj.EnumerateObject())
                {
                    node[property.Name] = Node(property.Value);
                }

                return node;
            case { ValueKind: JsonValueKind.Array } array:
                return new JsonArray([.. array.EnumerateArray().Select(item => Node(item))]);
            case { } value:
                return JsonValue.Create(value);
        }
    }

    // A grouping key with object keys sorted, as the Python grouped by json.dumps(sort_keys=True).
    private static string Canonical(JsonNode? node)
    {
        var text = new StringBuilder();
        Write(node);
        return text.ToString();

        void Write(JsonNode? value)
        {
            switch (value)
            {
                case null:
                    text.Append("null");
                    break;
                case JsonObject obj:
                    text.Append('{');
                    foreach (var (key, member) in obj.OrderBy(pair => pair.Key, PyText.CodePointOrder))
                    {
                        text.Append(JsonSerializer.Serialize(key)).Append(':');
                        Write(member);
                        text.Append(',');
                    }

                    text.Append('}');
                    break;
                case JsonArray array:
                    text.Append('[');
                    foreach (var item in array)
                    {
                        Write(item);
                        text.Append(',');
                    }

                    text.Append(']');
                    break;
                default:
                    text.Append(value.ToJsonString());
                    break;
            }
        }
    }
}
