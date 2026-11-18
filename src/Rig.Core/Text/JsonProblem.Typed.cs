using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;

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

    /// <summary>
    /// Rejects a format version below 1 or above the <paramref name="newest"/> this build reads. A format rig owns carries its
    /// version as an integer <c>version</c> member that reads as 1 when absent.
    /// </summary>
    /// <exception cref="JsonInputException">The version is outside 1 to <paramref name="newest"/>.</exception>
    public static void RequireVersion(int found, int newest)
    {
        if (found < 1 || found > newest)
        {
            throw Expected("$.version", $"a version from 1 to {newest.ToString(CultureInfo.InvariantCulture)}", $"the number {found.ToString(CultureInfo.InvariantCulture)}");
        }
    }

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
        catch (Exception e) when (e is JsonException or NotSupportedException)
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
        catch (Exception e) when (e is JsonException or NotSupportedException)
        {
            throw Translate(e, element, info);
        }
    }

    // The failure is re-read as a document: if that fails the text was never JSON, otherwise the document is where the
    // offending value is found.
    private static JsonInputException Translate(Exception failure, string text, JsonTypeInfo info)
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

    private static JsonInputException Translate(Exception failure, JsonElement root, JsonTypeInfo info)
    {
        if (Violation(root, info, "$") is { } structural)
        {
            return structural;
        }

        var at = Locate(root, info, Steps((failure as JsonException)?.Path ?? "$"));
        return at.Type is null || failure is not JsonException
            ? new JsonInputException(at.Path, Trimmed(failure.Message), null, failure)
            : Expected(at.Path, Expectation(at), at.Value is { } found ? Describe(found) : "nothing");
    }

    // The structure rig checks itself, from the metadata and the document alone, so no message of the runtime is read: a
    // repeated member, an unknown one where the type disallows them, a required one that is absent, a polymorphic object
    // whose discriminator is absent or unknown. The first found, or null.
    private static JsonInputException? Violation(JsonElement value, JsonTypeInfo? info, string path)
    {
        var members = value.ValueKind == JsonValueKind.Object ? value.EnumerateObject().ToList() : [];
        var tag = info?.PolymorphismOptions?.TypeDiscriminatorPropertyName;
        var shape = tag is null ? info : members.Where(member => member.Name == tag).Select(member => Derived(info!, member.Value)).FirstOrDefault();
        return (value.ValueKind, shape) switch
        {
            (JsonValueKind.Array, { } list) when ElementOf(list) is { } element => value.EnumerateArray()
                .Select((item, index) => Violation(item, element, Item(path, index)))
                .FirstOrDefault(found => found is not null),
            (JsonValueKind.Object, { } known) => Members(members, known, tag, path),
            (JsonValueKind.Object, null) when tag is not null => Expected(Child(path, tag), "a known type", members.Where(member => member.Name == tag).Select(member => Describe(member.Value)).FirstOrDefault() ?? "nothing"),
            _ => null,
        };
    }

    private static JsonInputException? Members(List<JsonProperty> members, JsonTypeInfo info, string? tag, string path)
    {
        var properties = info.Kind == JsonTypeInfoKind.Object ? info.Properties : [];
        var unknown = (info.UnmappedMemberHandling ?? info.Options.UnmappedMemberHandling) == JsonUnmappedMemberHandling.Disallow && info.Kind == JsonTypeInfoKind.Object;
        return members.GroupBy(member => member.Name).Where(group => group.Count() > 1 && !info.Options.AllowDuplicateProperties)
                .Select(group => Expected(Child(path, group.Key), "a field that appears once", "it repeated")).FirstOrDefault()
            ?? members.Where(member => unknown && member.Name != tag && properties.All(property => property.Name != member.Name))
                .Select(member => Expected(Child(path, member.Name), "a known field", Describe(member.Value))).FirstOrDefault()
            ?? properties.Where(property => property.IsRequired && members.All(member => member.Name != property.Name))
                .Select(property => Expected(Child(path, property.Name), PropertyPhrase(property), "nothing")).FirstOrDefault()
            ?? members.Select(member => Violation(member.Value, Member(new Spot(info, null, null, path), member.Name).Type, Child(path, member.Name)))
                .FirstOrDefault(found => found is not null);
    }

    private static JsonTypeInfo? Derived(JsonTypeInfo declared, JsonElement tag) =>
        declared.PolymorphismOptions!.DerivedTypes
            .Where(derived => derived.TypeDiscriminator?.ToString() == tag.ToString())
            .Select(derived => Info(declared.Options, derived.DerivedType))
            .FirstOrDefault();

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
    private static IEnumerable<Step> Steps(string path) =>
        PathStep().Matches(path).Select(step => step.Groups["index"].Success
            ? new Step(null, int.Parse(step.Groups["index"].Value, CultureInfo.InvariantCulture))
            : new Step(step.Groups["plain"].Success ? step.Groups["plain"].Value : step.Groups["quoted"].Value, 0));

    [GeneratedRegex(@"\.(?<plain>[^.\[]+)|\['(?<quoted>.*?)'\]|\[(?<index>\d+)\]", RegexOptions.CultureInvariant)]
    private static partial Regex PathStep();
}
