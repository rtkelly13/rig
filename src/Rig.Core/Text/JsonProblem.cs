using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Rig.Core.Text;

/// <summary>
/// A JSON input rig rejected. The message always reads <c>source: $.json.path: expected X, found Y</c>, so every
/// reader reports a bad value the same way. <see cref="Input"/> is the file or command the JSON came from; it is
/// null until <see cref="Within"/> names it, since the code that finds the fault rarely knows where its input came from.
/// </summary>
public sealed class JsonInputException : Exception
{
    /// <summary>Creates a rejection with no detail.</summary>
    public JsonInputException()
        : this("$", "rejected")
    {
    }

    /// <summary>Creates a rejection with <paramref name="message"/>.</summary>
    public JsonInputException(string message)
        : this("$", message)
    {
    }

    /// <summary>Creates a rejection with <paramref name="message"/> caused by <paramref name="inner"/>.</summary>
    public JsonInputException(string message, Exception inner)
        : this("$", message, null, inner)
    {
    }

    /// <summary>Creates the rejection of the value at <paramref name="path"/>.</summary>
    /// <param name="path">The JSON path, e.g. <c>$.windows[2].placement</c>.</param>
    /// <param name="detail">What was expected and what was found.</param>
    /// <param name="source">The file or command the JSON came from; null when not yet known.</param>
    /// <param name="inner">The parser's own exception, when there is one.</param>
    public JsonInputException(string path, string detail, string? source = null, Exception? inner = null)
        : base(source is null ? $"{path}: {detail}" : $"{source}: {path}: {detail}", inner)
    {
        Path = path;
        Detail = detail;
        Input = source;
    }

    /// <summary>The file or command the JSON came from; null when not yet known.</summary>
    public string? Input { get; }

    /// <summary>The JSON path of the rejected value.</summary>
    public string Path { get; }

    /// <summary>What was expected and what was found.</summary>
    public string Detail { get; }

    /// <summary>This rejection, naming <paramref name="source"/>.</summary>
    public JsonInputException Within(string source) => new(Path, Detail, source, InnerException);

    /// <summary>This rejection with its path placed under <paramref name="prefix"/> (a path such as <c>$.spans[3]</c>) and named <paramref name="source"/>.</summary>
    public JsonInputException Under(string source, string prefix) => new(prefix + Path[1..], Detail, source, InnerException);
}

/// <summary>
/// Builds and wraps <see cref="JsonInputException"/>s so no reader words its own. A reader throws
/// <see cref="Expected(string, string, string)"/> where a value has the wrong type; the one place that knows the source
/// calls <see cref="Run{T}"/> or <see cref="Parse"/>, which also turn the parser's <see cref="JsonException"/> into the same shape.
/// </summary>
public static class JsonProblem
{
    private const int QuotedLimit = 40;

    private static readonly string[] ParserTails = [" Path:", " LineNumber:"];

    /// <summary>Parses <paramref name="text"/>; bad JSON is a <see cref="JsonInputException"/> naming <paramref name="source"/>.</summary>
    public static JsonNode? Parse(string source, string text) => Run(source, () => JsonNode.Parse(text));

    /// <summary>Runs <paramref name="read"/>, naming <paramref name="source"/> in any rejection it throws.</summary>
    public static T Run<T>(string source, Func<T> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        try
        {
            return read();
        }
        catch (JsonInputException e) when (e.Input is null)
        {
            throw e.Within(source);
        }
        catch (JsonException e)
        {
            throw new JsonInputException(e.Path ?? "$", $"not valid JSON: {Trimmed(e.Message)}", source, e);
        }
    }

    /// <summary>A rejection of the value at <paramref name="path"/>.</summary>
    /// <param name="path">The JSON path.</param>
    /// <param name="expected">What the reader needs, e.g. <c>a whole number</c>.</param>
    /// <param name="found">What the input holds, as <see cref="Describe(JsonNode?)"/> words it.</param>
    public static JsonInputException Expected(string path, string expected, string found) => new(path, $"expected {expected}, found {found}");

    /// <summary>A rejection of <paramref name="found"/>, which has the wrong type.</summary>
    public static JsonInputException Expected(JsonNode found, string expected)
    {
        ArgumentNullException.ThrowIfNull(found);
        return Expected(found.GetPath(), expected, Describe(found));
    }

    /// <summary>A rejection of the <paramref name="key"/> field of <paramref name="parent"/>, which has the wrong type.</summary>
    public static JsonInputException Expected(JsonObject parent, string key, string expected, JsonNode? found)
    {
        ArgumentNullException.ThrowIfNull(parent);
        return Expected(Child(parent.GetPath(), key), expected, Describe(found));
    }

    /// <summary>A rejection of the <paramref name="key"/> field of <paramref name="parent"/>, which is missing.</summary>
    public static JsonInputException Missing(JsonObject parent, string key, string expected)
    {
        ArgumentNullException.ThrowIfNull(parent);
        return Expected(Child(parent.GetPath(), key), expected, "nothing");
    }

    /// <summary>The whole number field <paramref name="key"/> of <paramref name="parent"/>, at <paramref name="path"/>.</summary>
    /// <exception cref="JsonInputException">It is absent, or is anything but a JSON integer.</exception>
    public static long WholeNumber(JsonElement parent, string key, string path) =>
        parent.TryGetProperty(key, out var value)
            ? value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var whole) ? whole : throw Expected(Child(path, key), "a whole number", Describe(value))
            : throw Expected(Child(path, key), "a whole number", "nothing");

    /// <summary>The non-negative whole number field <paramref name="key"/> of <paramref name="parent"/>, which may pass <see cref="long.MaxValue"/>.</summary>
    /// <exception cref="JsonInputException">It is absent, negative, or is anything but a JSON integer.</exception>
    public static ulong UnsignedInteger(JsonElement parent, string key, string path) =>
        parent.TryGetProperty(key, out var value)
            ? value.ValueKind == JsonValueKind.Number && value.TryGetUInt64(out var whole) ? whole : throw Expected(Child(path, key), "a non-negative whole number", Describe(value))
            : throw Expected(Child(path, key), "a non-negative whole number", "nothing");

    /// <summary>The string field <paramref name="key"/> of <paramref name="parent"/>; null when absent.</summary>
    /// <exception cref="JsonInputException">It is present and is anything but a string, null included.</exception>
    public static string? OptionalString(JsonElement parent, string key, string path) =>
        parent.TryGetProperty(key, out var value)
            ? value.ValueKind == JsonValueKind.String ? value.GetString() : throw Expected(Child(path, key), "a string", Describe(value))
            : null;

    /// <summary>The path of <paramref name="key"/> under <paramref name="path"/>.</summary>
    public static string Child(string path, string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return key.Length > 0 && key.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') ? $"{path}.{key}" : $"{path}['{key}']";
    }

    /// <summary>The path of item <paramref name="index"/> under <paramref name="path"/>.</summary>
    public static string Item(string path, int index) => $"{path}[{index.ToString(CultureInfo.InvariantCulture)}]";

    /// <summary>What a value is, in words an error can quote: <c>the string "800"</c>, <c>true</c>, <c>an object</c>.</summary>
    public static string Describe(JsonNode? node) => node is null ? "null" : Describe(node.GetValueKind(), () => node.ToJsonString(), () => node.GetValue<string>());

    /// <inheritdoc cref="Describe(JsonNode?)"/>
    public static string Describe(JsonElement element) => Describe(element.ValueKind, element.GetRawText, () => element.GetString()!);

    /// <summary>What a decoded attribute value is, in the same words.</summary>
    public static string Describe(object? value) => value switch
    {
        null => "null",
        string text => $"the string {Quote(text)}",
        bool flag => flag ? "true" : "false",
        long or int or double => $"the number {Convert.ToString(value, CultureInfo.InvariantCulture)}",
        _ => "a list",
    };

    private static string Describe(JsonValueKind kind, Func<string> raw, Func<string> text) => kind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => "null",
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Number => $"the number {raw()}",
        JsonValueKind.String => $"the string {Quote(text())}",
        JsonValueKind.Array => "a list",
        _ => "an object",
    };

    private static string Quote(string text) => text.Length > QuotedLimit ? $"\"{text[..QuotedLimit]}...\"" : $"\"{text}\"";

    // The parser appends " Path: $ | LineNumber: 0 | ..." (or just the line) to its message; the path is reported on its own.
    private static string Trimmed(string message)
    {
        var cut = ParserTails.Select(marker => message.IndexOf(marker, StringComparison.Ordinal)).Where(at => at >= 0).DefaultIfEmpty(-1).Min();
        return cut < 0 ? message : message[..cut];
    }
}
