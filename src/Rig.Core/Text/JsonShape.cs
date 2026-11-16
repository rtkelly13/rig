using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Rig.Core.Text;

/// <summary>
/// Reads fields out of a <see cref="JsonNode"/> tree once, so no module hand-walks a payload. Two policies live here:
/// the strict accessors (<see cref="String"/>, <see cref="Int"/>, …) throw <see cref="FormatException"/> for a misshapen
/// field, as the Python's pydantic decode did for GraphQL; the <c>As*</c> and <c>Field*</c> accessors never throw and
/// answer null instead, for payloads another tool owns. A module picks one and keeps its own exception and wording —
/// <see cref="TryRead{T}"/> turns a strict miss into null for the enclosing object.
/// </summary>
[SuppressMessage("Naming", "CA1720:Identifier contains type name", Justification = "The JSON kinds are the vocabulary here: Object is a JsonObject, String its text, Int its number.")]
public static class JsonShape
{
    /// <summary>Runs <paramref name="read"/>; a misshapen reply throws <see cref="FormatException"/>.</summary>
    public static T Read<T>(Func<T> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        return read();
    }

    /// <summary>Runs <paramref name="read"/>, or null when what it reads is misshapen.</summary>
    public static T? TryRead<T>(Func<T> read)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(read);
        try
        {
            return read();
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary><paramref name="node"/> as an object, or null when it is anything else.</summary>
    public static JsonObject? AsObject(JsonNode? node) => node as JsonObject;

    /// <summary><paramref name="node"/> as an array, or null when it is anything else.</summary>
    public static JsonArray? AsArray(JsonNode? node) => node as JsonArray;

    /// <summary><paramref name="node"/> as a string, or null when it holds anything else.</summary>
    public static string? AsText(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    /// <summary><paramref name="node"/> as a whole number, or null when it holds anything else.</summary>
    public static long? AsInteger(JsonNode? node) => node is JsonValue value && value.TryGetValue<long>(out var number) ? number : null;

    /// <summary><paramref name="node"/> as a double, or null when it holds anything else.</summary>
    public static double? AsNumber(JsonNode? node) => node is JsonValue value && value.TryGetValue<double>(out var number) ? number : null;

    /// <summary><paramref name="node"/> as an <see cref="int"/>, or null when it is not a number that fits one.</summary>
    public static int? AsInt32(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.Number && value.TryGetValue<int>(out var number) ? number : null;

    /// <summary><paramref name="node"/> as a boolean, or null when it holds anything else.</summary>
    public static bool? AsBool(JsonNode? node) =>
        node?.GetValueKind() switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };

    /// <summary>The <paramref name="key"/> field, or null when the parent is not an object or the field is absent.</summary>
    public static JsonNode? Field(JsonObject? parent, string key) =>
        parent is not null && parent.TryGetPropertyValue(key, out var node) ? node : null;

    /// <summary>The <paramref name="key"/> field as an object, or null.</summary>
    public static JsonObject? FieldObject(JsonObject? parent, string key) => AsObject(Field(parent, key));

    /// <summary>The <paramref name="key"/> field as a string, or null.</summary>
    public static string? FieldText(JsonObject? parent, string key) => AsText(Field(parent, key));

    /// <summary>The <paramref name="key"/> field as a whole number, or null.</summary>
    public static long? FieldInteger(JsonObject? parent, string key) => AsInteger(Field(parent, key));

    /// <summary>The <paramref name="key"/> field as a boolean, or null.</summary>
    public static bool? FieldBool(JsonObject? parent, string key) => AsBool(Field(parent, key));

    /// <summary><paramref name="node"/> as an object.</summary>
    public static JsonObject Object(JsonNode? node) => node as JsonObject ?? throw Misshapen("an object");

    /// <summary>An optional object field: absent or null reads as null.</summary>
    public static JsonObject? OptionalObject(JsonObject parent, string key) =>
        Present(parent, key, out var node)
            ? node switch
            {
                null => null,
                JsonObject value => value,
                _ => throw Misshapen($"{key} as an object"),
            }
            : null;

    /// <summary>An object field with a default: absent reads as null, but a null is misshapen.</summary>
    public static JsonObject? DefaultedObject(JsonObject parent, string key) =>
        Present(parent, key, out var node) ? Object(node) : null;

    /// <summary>The objects in an array field, nulls skipped; absent reads as empty, a null array is misshapen.</summary>
    public static IReadOnlyList<JsonObject> Items(JsonObject parent, string key) =>
        [.. ItemsWithNulls(parent, key).OfType<JsonObject>()];

    /// <summary>The objects in an array field, nulls kept in place.</summary>
    public static IReadOnlyList<JsonObject?> ItemsWithNulls(JsonObject parent, string key)
    {
        if (!Present(parent, key, out var node))
        {
            return [];
        }

        var array = node as JsonArray ?? throw Misshapen($"{key} as an array");
        return [.. array.Select(item => item is null ? null : Object(item))];
    }

    /// <summary>An array field of strings and integers, each as text; absent reads as empty.</summary>
    public static IReadOnlyList<string> Scalars(JsonObject parent, string key)
    {
        if (!Present(parent, key, out var node))
        {
            return [];
        }

        var array = node as JsonArray ?? throw Misshapen($"{key} as an array");
        return [.. array.Select(item => item?.GetValueKind() switch
        {
            JsonValueKind.String => item.GetValue<string>(),
            JsonValueKind.Number when item.AsValue().TryGetValue<long>(out var number) => number.ToString(CultureInfo.InvariantCulture),
            _ => throw Misshapen($"{key} items as strings or integers"),
        })];
    }

    /// <summary>A string field with a default of "": a null is misshapen.</summary>
    public static string String(JsonObject parent, string key) =>
        Present(parent, key, out var node) ? AsText(node) ?? throw Misshapen($"{key} as a string") : string.Empty;

    /// <summary>An optional string field: absent or null reads as null.</summary>
    public static string? OptionalString(JsonObject parent, string key) =>
        Present(parent, key, out var node) && node is not null ? AsText(node) ?? throw Misshapen($"{key} as a string") : null;

    /// <summary>A required integer field.</summary>
    public static int Int(JsonObject parent, string key) =>
        OptionalInt(parent, key) ?? throw Misshapen($"{key} as an integer");

    /// <summary>An optional integer field: absent or null reads as null.</summary>
    public static int? OptionalInt(JsonObject parent, string key) =>
        Present(parent, key, out var node) && node is not null ? AsInt32(node) ?? throw Misshapen($"{key} as an integer") : null;

    /// <summary>A boolean field with a default of false: a null is misshapen.</summary>
    public static bool Bool(JsonObject parent, string key) =>
        Present(parent, key, out var node) && (AsBool(node) ?? throw Misshapen($"{key} as a boolean"));

    /// <summary>An optional boolean field: absent or null reads as null.</summary>
    public static bool? OptionalBool(JsonObject parent, string key) =>
        Present(parent, key, out var node) && node is not null ? AsBool(node) ?? throw Misshapen($"{key} as a boolean") : null;

    private static bool Present(JsonObject parent, string key, out JsonNode? node)
    {
        ArgumentNullException.ThrowIfNull(parent);
        return parent.TryGetPropertyValue(key, out node);
    }

    private static FormatException Misshapen(string expected) => new($"expected {expected}");
}
