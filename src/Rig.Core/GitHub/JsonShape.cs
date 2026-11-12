using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Rig.Core.GitHub;

/// <summary>
/// Reads GraphQL replies the way the Python's pydantic decode did: an absent field takes its default, a null takes it
/// only where the field is optional, and anything of the wrong kind throws <see cref="FormatException"/>, which
/// <see cref="TryRead{T}"/> turns into null for the enclosing object.
/// </summary>
internal static class JsonShape
{
    /// <summary>Runs <paramref name="read"/>; a misshapen reply throws <see cref="FormatException"/>.</summary>
    public static T Read<T>(Func<T> read) => read();

    /// <summary>Runs <paramref name="read"/>, or null when what it reads is misshapen.</summary>
    public static T? TryRead<T>(Func<T> read)
        where T : class
    {
        try
        {
            return read();
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary><paramref name="node"/> as an object.</summary>
    public static JsonObject Object(JsonNode? node) => node as JsonObject ?? throw Misshapen("an object");

    /// <summary>An optional object field: absent or null reads as null.</summary>
    public static JsonObject? OptionalObject(JsonObject parent, string key) =>
        parent[key] switch
        {
            null => null,
            JsonObject value => value,
            _ => throw Misshapen($"{key} as an object"),
        };

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
        Present(parent, key, out var node) ? Text(node) ?? throw Misshapen($"{key} as a string") : string.Empty;

    /// <summary>An optional string field: absent or null reads as null.</summary>
    public static string? OptionalString(JsonObject parent, string key) =>
        parent[key] is { } node ? Text(node) ?? throw Misshapen($"{key} as a string") : null;

    /// <summary>A required integer field.</summary>
    public static int Int(JsonObject parent, string key) =>
        OptionalInt(parent, key) ?? throw Misshapen($"{key} as an integer");

    /// <summary>An optional integer field: absent or null reads as null.</summary>
    public static int? OptionalInt(JsonObject parent, string key) =>
        parent[key] switch
        {
            null => null,
            JsonValue value when value.GetValueKind() == JsonValueKind.Number && value.TryGetValue<int>(out var number) => number,
            _ => throw Misshapen($"{key} as an integer"),
        };

    /// <summary>A boolean field with a default of false: a null is misshapen.</summary>
    public static bool Bool(JsonObject parent, string key) =>
        Present(parent, key, out var node) && (Flag(node) ?? throw Misshapen($"{key} as a boolean"));

    /// <summary>An optional boolean field: absent or null reads as null.</summary>
    public static bool? OptionalBool(JsonObject parent, string key) =>
        parent[key] is { } node ? Flag(node) ?? throw Misshapen($"{key} as a boolean") : null;

    private static bool Present(JsonObject parent, string key, out JsonNode? node) => parent.TryGetPropertyValue(key, out node);

    private static string? Text(JsonNode? node) => node?.GetValueKind() == JsonValueKind.String ? node.GetValue<string>() : null;

    private static bool? Flag(JsonNode? node) =>
        node?.GetValueKind() switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };

    private static FormatException Misshapen(string expected) => new($"expected {expected}");
}
