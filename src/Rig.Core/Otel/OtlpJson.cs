using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Rig.Core.Text;

namespace Rig.Core.Otel;

/// <summary>
/// Typed reads of OTLP/JSON, a format other tools own: fields rig reads are checked strictly and anything else is
/// ignored. An int64 is a JSON integer or the decimal string proto3 JSON writes for it (rig's own writer does); a
/// bool, a fraction, or a string with anything but digits in it is rejected rather than coerced.
/// </summary>
internal static class OtlpJson
{
    private const string Whole = "a whole number or a string of digits";

    /// <summary>The last value under <paramref name="key"/>; the last of a repeated key wins; null when absent.</summary>
    public static JsonElement? Field(JsonElement element, string key)
    {
        JsonElement? found = null;
        foreach (var property in element.EnumerateObject())
        {
            found = property.NameEquals(key) ? property.Value : found;
        }

        return found;
    }

    /// <summary>An int64 at <paramref name="path"/>; null when absent or null.</summary>
    /// <exception cref="JsonInputException">It is not a whole number or a string of digits.</exception>
    public static long? Int64(JsonElement? value, string path) => value switch
    {
        null or { ValueKind: JsonValueKind.Null } => null,
        { ValueKind: JsonValueKind.Number } number when number.TryGetInt64(out var whole) => whole,
        { ValueKind: JsonValueKind.String } text when Digits(text.GetString()!, out var parsed) => parsed,
        { } other => throw JsonProblem.Expected(path, Whole, JsonProblem.Describe(other)),
    };

    /// <summary>An int64 node; null when absent or null.</summary>
    /// <exception cref="JsonInputException">It is not a whole number or a string of digits.</exception>
    public static long? Int64(JsonNode? node) => node switch
    {
        null => null,
        JsonValue value when value.GetValueKind() == JsonValueKind.Number && value.TryGetValue<long>(out var whole) => whole,
        JsonValue value when value.GetValueKind() == JsonValueKind.String && Digits(value.GetValue<string>(), out var parsed) => parsed,
        _ => throw JsonProblem.Expected(node, Whole),
    };

    /// <summary>A bool at <paramref name="path"/>; null when absent or null.</summary>
    /// <exception cref="JsonInputException">It is not true or false.</exception>
    public static bool? Bool(JsonElement? value, string path) => value switch
    {
        null or { ValueKind: JsonValueKind.Null } => null,
        { ValueKind: JsonValueKind.True } => true,
        { ValueKind: JsonValueKind.False } => false,
        { } other => throw JsonProblem.Expected(path, "true or false", JsonProblem.Describe(other)),
    };

    /// <summary>
    /// The <paramref name="key"/> field of <paramref name="owner"/> as an OTLP AnyValue decoded to a plain value: an array
    /// becomes a list and intValue a long. Null when the field is absent or the AnyValue holds none of the known kinds.
    /// </summary>
    /// <exception cref="JsonInputException">The AnyValue, or the kind it holds, has the wrong type.</exception>
    public static object? AnyValue(JsonObject owner, string key) =>
        owner[key] is null && !owner.ContainsKey(key) ? null : Decode(owner[key], JsonProblem.Child(owner.GetPath(), key));

    /// <summary>The <paramref name="key"/> list of attributes of <paramref name="owner"/> by name, each value decoded as <see cref="AnyValue"/> does; empty when absent.</summary>
    /// <exception cref="JsonInputException">It is not a list of objects with a string key and an AnyValue.</exception>
    public static Dictionary<string, object?> Attributes(JsonObject owner, string key)
    {
        var attributes = new Dictionary<string, object?>(StringComparer.Ordinal);
        var items = Items(owner, key);
        for (var index = 0; index < items.Count; index++)
        {
            if (items[index] is not JsonObject attribute)
            {
                throw JsonProblem.Expected(JsonProblem.Item(items.GetPath(), index), "an attribute object", JsonProblem.Describe(items[index]));
            }

            var name = attribute["key"] is JsonValue found && found.GetValueKind() == JsonValueKind.String
                ? found.GetValue<string>()
                : throw JsonProblem.Expected(attribute, "key", "a string", attribute["key"]);
            attributes[name] = OtlpJson.AnyValue(attribute, "value");
        }

        return attributes;
    }

    /// <summary>The <paramref name="key"/> list of <paramref name="owner"/>; empty when absent.</summary>
    /// <exception cref="JsonInputException">It is present and not a list.</exception>
    public static JsonArray Items(JsonObject owner, string key) =>
        owner[key] switch
        {
            null when !owner.ContainsKey(key) => [],
            JsonArray items => items,
            var other => throw JsonProblem.Expected(owner, key, "a list", other),
        };

    private static object? Decode(JsonNode? node, string path)
    {
        if (node is not JsonObject value)
        {
            throw JsonProblem.Expected(path, "an AnyValue object", JsonProblem.Describe(node));
        }

        if (value["arrayValue"] is { } array)
        {
            return Values(array);
        }

        if (value["stringValue"] is { } text)
        {
            return text is JsonValue { } word && word.GetValueKind() == JsonValueKind.String ? word.GetValue<string>() : throw JsonProblem.Expected(text, "a string");
        }

        if (value["boolValue"] is { } flag)
        {
            return flag.GetValueKind() is JsonValueKind.True or JsonValueKind.False ? flag.GetValue<bool>() : throw JsonProblem.Expected(flag, "true or false");
        }

        if (value["intValue"] is { } whole)
        {
            return Int64(whole);
        }

        return value["doubleValue"] is { } real
            ? real.GetValueKind() == JsonValueKind.Number ? real.GetValue<double>() : throw JsonProblem.Expected(real, "a number")
            : null;
    }

    private static List<object?> Values(JsonNode array)
    {
        if (array is not JsonObject box)
        {
            throw JsonProblem.Expected(array, "an object holding values");
        }

        return box["values"] switch
        {
            null => [],
            JsonArray items => [.. items.Select((item, index) => Decode(item, JsonProblem.Item(items.GetPath(), index)))],
            var other => throw JsonProblem.Expected(other, "a list"),
        };
    }

    // proto3 JSON writes an int64 as digits in a string, with a minus sign when negative.
    private static bool Digits(string text, out long value) =>
        long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
}
