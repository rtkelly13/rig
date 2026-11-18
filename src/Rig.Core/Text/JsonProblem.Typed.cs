using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Rig.Core.Text;

/// <summary>What a converter expects, in the words a rejection quotes, e.g. <c>a non-empty string</c>.</summary>
public interface IJsonPhrase
{
    /// <summary>The words that complete <c>expected ...</c>.</summary>
    string Phrase { get; }
}

/// <summary>A string that is not empty. Anything else, null and the empty string included, is rejected.</summary>
public sealed class NonEmptyStringConverter : JsonConverter<string>, IJsonPhrase
{
    /// <inheritdoc/>
    public string Phrase => "a non-empty string";

    /// <inheritdoc/>
    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String && reader.GetString() is { Length: > 0 } text ? text : throw new JsonException(Phrase);

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(value);
    }
}

// The typed side of the boundary: System.Text.Json reads the value into a record whose context is strict, and any
// rejection is turned into the same `$.path: expected X, found Y` a hand-written reader throws. The translation runs
// only after a failure, so a good read pays nothing for it.
public static partial class JsonProblem
{
    /// <summary>What an int64 read from a proto3 JSON string or number is called.</summary>
    internal const string WholeOrDigits = "a whole number or a string of digits";

    /// <summary>Reads <paramref name="text"/> as a <typeparamref name="T"/>, the record <paramref name="info"/> describes.</summary>
    /// <exception cref="JsonInputException">The text is not JSON, or its shape is not the record's.</exception>
    public static T Read<T>(string text, JsonTypeInfo<T> info)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(info);
        try
        {
            return JsonSerializer.Deserialize(text, info) ?? throw Expected("$", TypePhrase(info), "null");
        }
        catch (JsonException e)
        {
            throw Translate(e, text, info);
        }
    }

    /// <summary>Reads <paramref name="element"/> as a <typeparamref name="T"/>; a rejection's path is relative to the element.</summary>
    /// <exception cref="JsonInputException">The element's shape is not the record's.</exception>
    public static T Read<T>(JsonElement element, JsonTypeInfo<T> info)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(info);
        try
        {
            return element.Deserialize(info) ?? throw Expected("$", TypePhrase(info), "null");
        }
        catch (JsonException e)
        {
            throw Translate(e, element, info);
        }
    }

    // The failure is re-read as a document: if that fails the text was never JSON, otherwise the document is where the
    // offending value is found.
    private static JsonInputException Translate(JsonException failure, string text, JsonTypeInfo info)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            return Translate(failure, document.RootElement, info);
        }
        catch (JsonException syntax)
        {
            return new JsonInputException(syntax.Path ?? "$", $"not valid JSON: {Trimmed(syntax.Message)}", null, syntax);
        }
    }

    private static JsonInputException Translate(JsonException failure, JsonElement root, JsonTypeInfo info)
    {
        var steps = Steps(failure.Path ?? "$").ToList();
        var message = failure.Message;
        if (message.StartsWith("The JSON property '", StringComparison.Ordinal) && message.Contains("could not be mapped", StringComparison.Ordinal) && steps.Count > 0)
        {
            var spot = Locate(root, info, steps);
            return Expected(spot.Path, "a known field", spot.Value is { } member ? Describe(member) : "nothing");
        }

        if (message.StartsWith("Duplicate property '", StringComparison.Ordinal))
        {
            return Expected(Locate(root, info, steps).Path, "a field that appears once", "it repeated");
        }

        var required = message.IndexOf(MissingMarker, StringComparison.Ordinal);
        if (required >= 0)
        {
            return Missing(Locate(root, info, steps), FirstQuoted(message[(required + MissingMarker.Length)..]));
        }

        var at = Locate(root, info, steps);
        return at.Type is null
            ? new JsonInputException(at.Path, Trimmed(message), null, failure)
            : Expected(at.Path, Expectation(at), at.Value is { } found ? Describe(found) : "nothing");
    }

    private const string MissingMarker = "was missing required properties including: ";

    private static JsonInputException Missing(Spot parent, string name)
    {
        var property = parent.Type?.Properties.FirstOrDefault(candidate => candidate.Name == name);
        return Expected(Child(parent.Path, name), property is null ? "a value" : PropertyPhrase(property), "nothing");
    }

    private static string FirstQuoted(string text)
    {
        var open = text.IndexOf('\'', StringComparison.Ordinal);
        var close = open < 0 ? -1 : text.IndexOf('\'', open + 1);
        return close < 0 ? string.Empty : text[(open + 1)..close];
    }

    // A path step: a member name, or (Name null) a list index.
    private readonly record struct Step(string? Name, int Index);

    // Where a path leads: the metadata of the type there, the property when it is a member, the value, and its rendered path.
    private readonly record struct Spot(JsonTypeInfo? Type, JsonPropertyInfo? Property, JsonElement? Value, string Path);

    private static Spot Locate(JsonElement root, JsonTypeInfo info, IEnumerable<Step> steps) =>
        steps.Aggregate(new Spot(info, null, root, "$"), Advance);

    private static Spot Advance(Spot at, Step step) => step.Name is { } name ? Member(at, name) : Element(at, step.Index);

    private static Spot Member(Spot at, string name)
    {
        var property = at.Type is { Kind: JsonTypeInfoKind.Object } ? at.Type.Properties.FirstOrDefault(candidate => candidate.Name == name) : null;
        var type = property is null ? (at.Type is { Kind: JsonTypeInfoKind.Dictionary } ? ElementOf(at.Type) : null) : Info(at.Type!.Options, property.PropertyType);
        JsonElement? value = at.Value is { ValueKind: JsonValueKind.Object } owner && owner.TryGetProperty(name, out var member) ? member : null;
        return new Spot(type, property, value, Child(at.Path, name));
    }

    private static Spot Element(Spot at, int index)
    {
        JsonElement? value = at.Value is { ValueKind: JsonValueKind.Array } list && index < list.GetArrayLength() ? list[index] : null;
        return new Spot(at.Type is null ? null : ElementOf(at.Type), null, value, Item(at.Path, index));
    }

    private static JsonTypeInfo? ElementOf(JsonTypeInfo collection) => collection.ElementType is { } element ? Info(collection.Options, element) : null;

    private static JsonTypeInfo? Info(JsonSerializerOptions options, Type type) => options.TryGetTypeInfo(type, out var info) ? info : null;

    private static string Expectation(Spot at) => at.Property is { } property ? PropertyPhrase(property) : TypePhrase(at.Type);

    private static string PropertyPhrase(JsonPropertyInfo property)
    {
        var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
        return property.CustomConverter is IJsonPhrase phrase
            ? phrase.Phrase
            : property.NumberHandling is { } handling && handling.HasFlag(JsonNumberHandling.AllowReadingFromString) && TypePhrase(type) == "a whole number"
                ? WholeOrDigits
                : TypePhrase(type, Info(property.Options, property.PropertyType));
    }

    private static string TypePhrase(JsonTypeInfo? info) =>
        info is null ? "a value" : TypePhrase(Nullable.GetUnderlyingType(info.Type) ?? info.Type, info);

    private static string TypePhrase(Type type, JsonTypeInfo? info = null) => Type.GetTypeCode(type) switch
    {
        TypeCode.String => "a string",
        TypeCode.Boolean => "true or false",
        TypeCode.Byte or TypeCode.UInt16 or TypeCode.UInt32 or TypeCode.UInt64 => "a non-negative whole number",
        TypeCode.SByte or TypeCode.Int16 or TypeCode.Int32 or TypeCode.Int64 => "a whole number",
        TypeCode.Single or TypeCode.Double or TypeCode.Decimal => "a number",
        _ => info?.Kind switch
        {
            JsonTypeInfoKind.Enumerable => "a list",
            JsonTypeInfoKind.Object or JsonTypeInfoKind.Dictionary => "an object",
            _ => "a value",
        },
    };

    // System.Text.Json writes a path as $.name, $['odd name'] and [3]; the same steps are rendered again with Child and Item.
    private static IEnumerable<Step> Steps(string path)
    {
        var at = 1;
        while (at < path.Length)
        {
            if (path[at] == '.')
            {
                var end = path.IndexOfAny(['.', '['], at + 1);
                end = end < 0 ? path.Length : end;
                yield return new Step(path[(at + 1)..end], 0);
                at = end;
            }
            else if (at + 1 < path.Length && path[at + 1] == '\'')
            {
                var end = path.IndexOf("']", at + 2, StringComparison.Ordinal);
                if (end < 0)
                {
                    yield break;
                }

                yield return new Step(path[(at + 2)..end], 0);
                at = end + 2;
            }
            else
            {
                var end = path.IndexOf(']', at);
                if (end < 0 || !int.TryParse(path[(at + 1)..end], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var index))
                {
                    yield break;
                }

                yield return new Step(null, index);
                at = end + 1;
            }
        }
    }
}
