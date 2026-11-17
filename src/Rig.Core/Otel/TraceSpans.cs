using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Rig.Core.Text;
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
/// <param name="Origin">Where it was read from, e.g. <c>file.ndjson line 3</c>; empty when it was not read.</param>
/// <param name="SpanPath">The JSON path of the span in its line, so a fault found later names it.</param>
public sealed record FileSpan(string TraceId, string SpanId, JsonNode? Resource, JsonNode? Scope, JsonObject Span, long EndNs, bool Incomplete, string Origin = "", string SpanPath = "$");

/// <summary>
/// The OTLP/JSON trace files rig and its tools write, read and regrouped for the collector.
/// </summary>
public static class TraceSpans
{
    /// <summary>How many spans go in one request body.</summary>
    public const int BatchSpans = 500;

    /// <summary>
    /// Every span in the trace file at <paramref name="path"/>; none when it cannot be read. Each line or span skipped is
    /// reported to <paramref name="skipped"/> as <c>path line N: $.json.path: expected X, found Y</c>.
    /// </summary>
    public static IReadOnlyList<FileSpan> Read(string path, Action<string>? skipped = null)
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

        return Parse(text, skipped, path);
    }

    /// <summary>
    /// Every span in <paramref name="text"/>. A line that is not an envelope (including a torn last line) is skipped
    /// whole, as is a span without a string trace and span id or with a start or end time that is not a whole number.
    /// Each skip is reported to <paramref name="skipped"/> as <c>source line N: $.json.path: expected X, found Y</c>.
    /// </summary>
    public static IReadOnlyList<FileSpan> Parse(string text, Action<string>? skipped = null, string source = "trace data")
    {
        ArgumentNullException.ThrowIfNull(text);
        var spans = new List<FileSpan>();
        var number = 0;
        foreach (var line in UnicodeText.SplitLines(text))
        {
            number++;
            if (UnicodeText.Strip(line).Length > 0)
            {
                spans.AddRange(Envelope(line, $"{source} line {number.ToString(CultureInfo.InvariantCulture)}", skipped));
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

    // The spans of one line; none, reported, when the line is not an envelope. The envelope is decoded strictly: a
    // field of the wrong shape anywhere in it drops the whole line, and a span whose own fields are wrong drops that span.
    private static List<FileSpan> Envelope(string line, string origin, Action<string>? skipped)
    {
        try
        {
            return JsonProblem.Run(origin, () => Decode(line, origin, skipped));
        }
        catch (JsonInputException e)
        {
            skipped?.Invoke(e.Message);
            return [];
        }
    }

    private static List<FileSpan> Decode(string line, string origin, Action<string>? skipped)
    {
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement.Clone();
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw JsonProblem.Expected("$", "an object", JsonProblem.Describe(root));
        }

        var spans = new List<FileSpan>();
        foreach (var (resource, resourcePath) in Objects(root, "resourceSpans", "$"))
        {
            var resourceNode = Node(OtlpJson.Field(resource, "resource"));
            foreach (var (scope, scopePath) in Objects(resource, "scopeSpans", resourcePath))
            {
                var scopeNode = Node(OtlpJson.Field(scope, "scope"));
                foreach (var (raw, spanPath) in Objects(scope, "spans", scopePath))
                {
                    if (View(raw, spanPath, origin, resourceNode, scopeNode, skipped) is { } span)
                    {
                        spans.Add(span);
                    }
                }
            }
        }

        return spans;
    }

    // An absent list reads as empty; anything but a list of objects fails.
    private static List<(JsonElement Item, string Path)> Objects(JsonElement parent, string key, string path)
    {
        if (OtlpJson.Field(parent, key) is not { } value)
        {
            return [];
        }

        var listPath = JsonProblem.Child(path, key);
        return value.ValueKind == JsonValueKind.Array
            ? [.. value.EnumerateArray().Select((item, index) => item.ValueKind == JsonValueKind.Object
                ? (item, JsonProblem.Item(listPath, index))
                : throw JsonProblem.Expected(JsonProblem.Item(listPath, index), "an object", JsonProblem.Describe(item)))]
            : throw JsonProblem.Expected(listPath, "a list", JsonProblem.Describe(value));
    }

    private static FileSpan? View(JsonElement raw, string path, string origin, JsonNode? resource, JsonNode? scope, Action<string>? skipped)
    {
        try
        {
            var traceId = Id(raw, "traceId", path);
            var spanId = Id(raw, "spanId", path);
            var end = OtlpJson.Int64(OtlpJson.Field(raw, "endTimeUnixNano"), JsonProblem.Child(path, "endTimeUnixNano")) ?? 0;
            var start = OtlpJson.Int64(OtlpJson.Field(raw, "startTimeUnixNano"), JsonProblem.Child(path, "startTimeUnixNano")) ?? 0;
            return new FileSpan(traceId, spanId, resource?.DeepClone(), scope?.DeepClone(), (JsonObject)Node(raw)!, end != 0 ? end : start, Incomplete(raw, path), origin, path);
        }
        catch (JsonInputException e)
        {
            skipped?.Invoke(e.Within(origin).Message);
            return null;
        }
    }

    private static string Id(JsonElement raw, string key, string path) => OtlpJson.Field(raw, key) switch
    {
        { ValueKind: JsonValueKind.String } text when text.GetString() is { Length: > 0 } id => id,
        { } other => throw JsonProblem.Expected(JsonProblem.Child(path, key), "a non-empty string", JsonProblem.Describe(other)),
        null => throw JsonProblem.Expected(JsonProblem.Child(path, key), "a non-empty string", "nothing"),
    };

    // The span.incomplete attribute is a bool; attributes of any other name are not read.
    private static bool Incomplete(JsonElement raw, string path)
    {
        foreach (var (attribute, attributePath) in Objects(raw, "attributes", path))
        {
            if (OtlpJson.Field(attribute, "key") is { ValueKind: JsonValueKind.String } key
                && key.ValueEquals("span.incomplete")
                && OtlpJson.Field(attribute, "value") is { ValueKind: not JsonValueKind.Null } value)
            {
                var valuePath = JsonProblem.Child(attributePath, "value");
                if (value.ValueKind != JsonValueKind.Object)
                {
                    throw JsonProblem.Expected(valuePath, "an object", JsonProblem.Describe(value));
                }

                if (OtlpJson.Bool(OtlpJson.Field(value, "boolValue"), JsonProblem.Child(valuePath, "boolValue")) == true)
                {
                    return true;
                }
            }
        }

        return false;
    }

    // A node from an element, the last of a repeated key winning.
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

    // A grouping key with object keys sorted.
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
                    foreach (var (key, member) in obj.OrderBy(pair => pair.Key, UnicodeText.CodePointOrder))
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
