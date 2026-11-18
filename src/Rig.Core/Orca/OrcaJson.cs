using System.Text.Json;
using System.Text.Json.Nodes;
using Rig.Core.Text;

namespace Rig.Core.Orca;

/// <summary>
/// Typed reads of a JSON object's fields: a field that is absent or null is null, one of the wrong type throws
/// <see cref="InvalidDataException"/> worded by <see cref="JsonProblem"/> (<c>$.repos[2].id: expected a string, found a number</c>),
/// and fields nothing reads are ignored. Orca and Orca's settings are another tool's formats, so unknown fields are fine.
/// </summary>
internal static class OrcaJson
{
    /// <summary>Parses <paramref name="text"/>, which came from <paramref name="source"/>, as a JSON object.</summary>
    /// <exception cref="InvalidDataException">It is not JSON, or not an object; the message names <paramref name="source"/>.</exception>
    public static JsonObject Object(string text, string source)
    {
        try
        {
            var node = JsonProblem.Parse(source, text);
            return node as JsonObject ?? throw JsonProblem.Expected("$", "an object", JsonProblem.Describe(node)).Within(source);
        }
        catch (JsonInputException e)
        {
            throw new InvalidDataException(e.Message, e);
        }
    }

    /// <summary>Runs <paramref name="read"/>, naming <paramref name="source"/> in a rejection that does not yet name one.</summary>
    public static T Run<T>(string source, Func<T> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        try
        {
            return read();
        }
        catch (InvalidDataException e) when (e.InnerException is JsonInputException { Input: null } problem)
        {
            throw Reject(problem.Within(source));
        }
    }

    /// <summary>The string field <paramref name="key"/>, which must be present.</summary>
    /// <exception cref="InvalidDataException">It is absent, null, or not a string.</exception>
    public static string Required(JsonObject parent, string key) =>
        String(parent, key) ?? throw Reject(JsonProblem.Missing(parent, key, "a string"));

    /// <summary>The same rejection <see cref="JsonProblem"/> words, as the exception callers of this class catch.</summary>
    public static InvalidDataException Reject(JsonInputException problem)
    {
        ArgumentNullException.ThrowIfNull(problem);
        return new InvalidDataException(problem.Message, problem);
    }

    /// <summary>The <paramref name="key"/> object, or null.</summary>
    public static JsonObject? Object(JsonObject parent, string key) =>
        JsonShape.Field(parent, key) is { } node ? JsonShape.AsObject(node) ?? throw Wrong(parent, key, "an object") : null;

    /// <summary>The <paramref name="key"/> array, or null.</summary>
    public static JsonArray? Array(JsonObject parent, string key) =>
        JsonShape.Field(parent, key) is { } node ? JsonShape.AsArray(node) ?? throw Wrong(parent, key, "a list") : null;

    /// <summary>The <paramref name="key"/> string, or null.</summary>
    public static string? String(JsonObject parent, string key) =>
        JsonShape.Field(parent, key) is { } node ? JsonShape.AsText(node) ?? throw Wrong(parent, key, "a string") : null;

    /// <summary>The <paramref name="key"/> boolean, or null.</summary>
    public static bool? Bool(JsonObject parent, string key) =>
        JsonShape.Field(parent, key) is { } node ? JsonShape.AsBool(node) ?? throw Wrong(parent, key, "a boolean") : null;

    /// <summary>The <paramref name="key"/> whole number, or null.</summary>
    public static long? Integer(JsonObject parent, string key) =>
        JsonShape.Field(parent, key) is { } node
            ? JsonShape.AsInteger(node) ?? throw Wrong(parent, key, "a whole number")
            : null;

    /// <summary>The <paramref name="key"/> number, which may have a fraction, or null.</summary>
    public static double? Number(JsonObject parent, string key) =>
        JsonShape.Field(parent, key) is { } node
            ? node is JsonValue value && value.GetValueKind() == JsonValueKind.Number ? value.GetValue<double>() : throw Wrong(parent, key, "a number")
            : null;

    /// <summary>The objects in <paramref name="items"/>.</summary>
    /// <exception cref="InvalidDataException">An entry is not an object.</exception>
    public static IEnumerable<JsonObject> Objects(JsonArray items)
    {
        ArgumentNullException.ThrowIfNull(items);
        for (var index = 0; index < items.Count; index++)
        {
            yield return items[index] as JsonObject
                ?? throw Reject(JsonProblem.Expected(JsonProblem.Item(items.GetPath(), index), "an object", JsonProblem.Describe(items[index])));
        }
    }

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
            result[name] = JsonShape.FieldText(map, name) ?? throw Wrong(map, name, "a string");
        }

        return result;
    }

    /// <summary>The <paramref name="key"/> string, which must be one of <paramref name="allowed"/>, or null.</summary>
    public static string? OneOf(JsonObject parent, string key, IReadOnlyCollection<string> allowed) => String(parent, key) switch
    {
        null => null,
        var value when allowed.Contains(value) => value,
        _ => throw Reject(JsonProblem.Expected(parent, key, $"one of {string.Join(", ", allowed)}", parent[key])),
    };

    private static InvalidDataException Wrong(JsonObject parent, string key, string kind) =>
        Reject(JsonProblem.Expected(parent, key, kind, JsonShape.Field(parent, key)));
}
