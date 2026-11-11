using System.Text.Json;
using System.Text.Json.Nodes;

namespace Rig.Core.Orca;

/// <summary>
/// Typed reads of a JSON object's fields, as pydantic decoded rig's Orca shapes: a field that is absent or null is
/// null, one of the wrong type throws <see cref="InvalidDataException"/>, and fields nothing reads are ignored.
/// </summary>
internal static class OrcaJson
{
    /// <summary>Parses <paramref name="text"/> as a JSON object.</summary>
    /// <exception cref="InvalidDataException">It is not JSON, or not an object.</exception>
    public static JsonObject Object(string text)
    {
        try
        {
            return JsonNode.Parse(text) as JsonObject ?? throw new InvalidDataException("expected a JSON object");
        }
        catch (JsonException e)
        {
            throw new InvalidDataException(e.Message, e);
        }
    }

    /// <summary>The <paramref name="key"/> object, or null.</summary>
    public static JsonObject? Object(JsonObject parent, string key) => Field(parent, key) switch
    {
        null => null,
        JsonObject value => value,
        _ => throw Wrong(key, "an object"),
    };

    /// <summary>The <paramref name="key"/> array, or null.</summary>
    public static JsonArray? Array(JsonObject parent, string key) => Field(parent, key) switch
    {
        null => null,
        JsonArray value => value,
        _ => throw Wrong(key, "a list"),
    };

    /// <summary>The <paramref name="key"/> string, or null.</summary>
    public static string? String(JsonObject parent, string key) => Field(parent, key) switch
    {
        null => null,
        JsonValue value when value.GetValueKind() == JsonValueKind.String => value.GetValue<string>(),
        _ => throw Wrong(key, "a string"),
    };

    /// <summary>The <paramref name="key"/> boolean, or null.</summary>
    public static bool? Bool(JsonObject parent, string key) => Field(parent, key)?.GetValueKind() switch
    {
        null => null,
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => throw Wrong(key, "a boolean"),
    };

    /// <summary>The <paramref name="key"/> whole number, or null.</summary>
    public static long? Integer(JsonObject parent, string key) => Field(parent, key) switch
    {
        null => null,
        JsonValue value when value.TryGetValue<long>(out var number) => number,
        JsonValue value when value.TryGetValue<double>(out var real) && real == Math.Floor(real) && Math.Abs(real) < long.MaxValue => (long)real,
        _ => throw Wrong(key, "a whole number"),
    };

    /// <summary>The <paramref name="key"/> map of strings, or null.</summary>
    public static Dictionary<string, string>? StringMap(JsonObject parent, string key)
    {
        if (Object(parent, key) is not { } map)
        {
            return null;
        }

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, _) in map)
        {
            result[name] = String(map, name) ?? throw Wrong($"{key}.{name}", "a string");
        }

        return result;
    }

    /// <summary>The <paramref name="key"/> string, which must be one of <paramref name="allowed"/>, or null.</summary>
    public static string? OneOf(JsonObject parent, string key, IReadOnlyCollection<string> allowed) => String(parent, key) switch
    {
        null => null,
        var value when allowed.Contains(value) => value,
        var value => throw new InvalidDataException($"{key}: \"{value}\" is not one of {string.Join(", ", allowed)}"),
    };

    /// <summary><paramref name="read"/>'s value, or null where it throws because the field has the wrong type.</summary>
    public static T? Lenient<T>(Func<T?> read)
        where T : class
    {
        try
        {
            return read();
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    private static JsonNode? Field(JsonObject parent, string key) => parent.TryGetPropertyValue(key, out var node) ? node : null;

    private static InvalidDataException Wrong(string key, string kind) => new($"{key}: expected {kind}");
}
