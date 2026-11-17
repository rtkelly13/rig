using System.Text.Json;

namespace Rig.Image;

/// <summary>A document or fixture that does not have the shape the reader expects. The message names the file, the JSON path, what was expected and what was found.</summary>
public sealed class FixtureFormatException : Exception
{
    /// <summary>Initialises an empty exception.</summary>
    public FixtureFormatException()
    {
    }

    /// <summary>Initialises with a message.</summary>
    public FixtureFormatException(string message)
        : base(message)
    {
    }

    /// <summary>Initialises with a message and the underlying parse failure.</summary>
    public FixtureFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// A position in a JSON document that reads values strictly: a string is only a string, an integer only an integer with no
/// fraction, and every mismatch throws <see cref="FixtureFormatException"/> naming the file and the JSON path. Nothing is coerced.
/// </summary>
public sealed class StrictJson
{
    private readonly JsonElement _element;

    private StrictJson(string file, string path, JsonElement element)
    {
        File = file;
        Path = path;
        _element = element;
    }

    /// <summary>The file (or resource) the value came from, for messages.</summary>
    public string File { get; }

    /// <summary>The JSON path of this value, <c>$</c> for the root.</summary>
    public string Path { get; }

    /// <summary>Parses <paramref name="text"/> and returns its root.</summary>
    public static StrictJson Parse(string file, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        try
        {
            using var document = JsonDocument.Parse(text);
            return new StrictJson(file, "$", document.RootElement.Clone());
        }
        catch (JsonException exception)
        {
            throw new FixtureFormatException($"{file}: $: expected JSON, found text that does not parse ({exception.Message})", exception);
        }
    }

    /// <summary>The value of the named property of an object.</summary>
    public StrictJson Property(string name)
    {
        Expect(JsonValueKind.Object);
        if (!_element.TryGetProperty(name, out var value))
        {
            var found = string.Join(", ", _element.EnumerateObject().Select(p => p.Name));
            throw Mismatch($"property '{name}'", $"an object with properties [{found}]");
        }

        return new StrictJson(File, $"{Path}.{name}", value);
    }

    /// <summary>The names of an object's properties, in document order.</summary>
    public IReadOnlyList<string> PropertyNames()
    {
        Expect(JsonValueKind.Object);
        return [.. _element.EnumerateObject().Select(p => p.Name)];
    }

    /// <summary>The elements of an array.</summary>
    public IReadOnlyList<StrictJson> Items()
    {
        Expect(JsonValueKind.Array);
        var items = new List<StrictJson>();
        var index = 0;
        foreach (var item in _element.EnumerateArray())
        {
            items.Add(new StrictJson(File, $"{Path}[{index}]", item));
            index++;
        }

        return items;
    }

    /// <summary>This value as a string; anything else is an error.</summary>
    public string AsString()
    {
        Expect(JsonValueKind.String);
        return _element.GetString() ?? throw Mismatch("a string", "null");
    }

    /// <summary>This value as a string, or null for JSON <c>null</c>.</summary>
    public string? AsStringOrNull() => _element.ValueKind == JsonValueKind.Null ? null : AsString();

    /// <summary>This value as a boolean; anything else is an error.</summary>
    public bool AsBoolean()
    {
        if (_element.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw Mismatch("a boolean", Describe(_element));
        }

        return _element.GetBoolean();
    }

    /// <summary>This value as an int; a fraction, an exponent that makes it fractional or an overflow is an error.</summary>
    public int AsInteger()
    {
        Expect(JsonValueKind.Number);
        return _element.TryGetInt32(out var value) ? value : throw Mismatch("an integer", Describe(_element));
    }

    /// <summary>This value as a double; a non-number is an error.</summary>
    public double AsNumber()
    {
        Expect(JsonValueKind.Number);
        return _element.TryGetDouble(out var value) ? value : throw Mismatch("a finite number", Describe(_element));
    }

    private void Expect(JsonValueKind kind)
    {
        if (_element.ValueKind != kind)
        {
            throw Mismatch(kind switch
            {
                JsonValueKind.Object => "an object",
                JsonValueKind.Array => "an array",
                JsonValueKind.String => "a string",
                JsonValueKind.Number => "a number",
                _ => kind.ToString(),
            }, Describe(_element));
        }
    }

    private FixtureFormatException Mismatch(string expected, string found) =>
        new($"{File}: {Path}: expected {expected}, found {found}");

    private static string Describe(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => "an object",
        JsonValueKind.Array => "an array",
        JsonValueKind.String => $"the string \"{element.GetString()}\"",
        JsonValueKind.Number => $"the number {element.GetRawText()}",
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null => "null",
        _ => element.ValueKind.ToString(),
    };
}
