using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Rig.Core.Text;

namespace Rig.Core.Otel;

/// <summary>
/// An OTLP AnyValue read to a plain value: an array becomes a list and intValue a long. Null when the AnyValue holds none of
/// the known kinds. A format other tools own, so the kinds are tried in a fixed order, the first present one is checked
/// strictly, and the rest is ignored. An int64 is a JSON integer or the decimal string proto3 JSON writes for it; a bool, a
/// fraction, or a string with anything but digits in it is rejected rather than coerced.
/// </summary>
internal sealed class AnyValueConverter : JsonConverter<object?>
{
    public override bool HandleNull => true;

    public override object? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        return Decode(document.RootElement, "$");
    }

    public override void Write(Utf8JsonWriter writer, object? value, JsonSerializerOptions options) =>
        throw new NotSupportedException("An AnyValue is only read.");

    private static object? Decode(JsonElement value, string path) =>
        value.ValueKind != JsonValueKind.Object
            ? throw Nested(path, "an AnyValue object", value)
            : Kind(value, "arrayValue") is { } array ? Values(array, JsonProblem.Child(path, "arrayValue"))
            : Kind(value, "stringValue") is { } text ? Text(text, JsonProblem.Child(path, "stringValue"))
            : Kind(value, "boolValue") is { } flag ? Flag(flag, JsonProblem.Child(path, "boolValue"))
            : Kind(value, "intValue") is { } whole ? Whole(whole, JsonProblem.Child(path, "intValue"))
            : Kind(value, "doubleValue") is { } real ? Real(real, JsonProblem.Child(path, "doubleValue"))
            : null;

    // A kind that is present: JSON null counts as absent.
    private static JsonElement? Kind(JsonElement value, string name) =>
        value.TryGetProperty(name, out var found) && found.ValueKind != JsonValueKind.Null ? found : null;

    private static List<object?> Values(JsonElement array, string path) =>
        array.ValueKind != JsonValueKind.Object ? throw Nested(path, "an object holding values", array)
        : Kind(array, "values") is not { } items ? []
        : items.ValueKind != JsonValueKind.Array ? throw Nested(JsonProblem.Child(path, "values"), "a list", items)
        : [.. items.EnumerateArray().Select((item, index) => Decode(item, JsonProblem.Item(JsonProblem.Child(path, "values"), index)))];

    private static string Text(JsonElement text, string path) =>
        text.ValueKind == JsonValueKind.String ? text.GetString()! : throw Nested(path, "a string", text);

    private static bool Flag(JsonElement flag, string path) =>
        flag.ValueKind is JsonValueKind.True or JsonValueKind.False ? flag.GetBoolean() : throw Nested(path, "true or false", flag);

    private static long Whole(JsonElement whole, string path) => whole.ValueKind switch
    {
        JsonValueKind.Number when whole.TryGetInt64(out var number) => number,
        JsonValueKind.String when long.TryParse(whole.GetString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var digits) => digits,
        _ => throw Nested(path, JsonProblem.WholeOrDigits, whole),
    };

    private static double Real(JsonElement real, string path) =>
        real.ValueKind == JsonValueKind.Number ? real.GetDouble() : throw Nested(path, "a number", real);

    private static JsonNestedException Nested(string path, string expected, JsonElement found) =>
        new(path, expected, JsonProblem.Describe(found));
}

/// <summary>An OTLP attribute: a string key and an AnyValue, absent when the attribute carries none.</summary>
internal record struct OtlpKeyValue
{
    public OtlpKeyValue()
    {
    }

    public required string Key { get; set; }

    [JsonConverter(typeof(AnyValueConverter))]
    public object? Value { get; set; }
}

/// <summary>A resource: its attributes, which name the service.</summary>
internal sealed record OtlpResource
{
    public List<OtlpKeyValue> Attributes { get; set; } = [];
}

internal static class OtlpAttributes
{
    /// <summary>The attributes by name; a repeated name keeps its last value.</summary>
    public static Dictionary<string, object?> ByName(IEnumerable<OtlpKeyValue> attributes) =>
        attributes.Aggregate(new Dictionary<string, object?>(StringComparer.Ordinal), (byName, attribute) =>
        {
            byName[attribute.Key] = attribute.Value;
            return byName;
        });
}
