using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Rig.Core.Text;

// For a format another tool owns, where a repeated member is not an error: the last occurrence is the value, as it was
// before the typed readers. System.Text.Json validates every occurrence, so a bad earlier one would reject a good last one;
// the read is therefore retried on the document with each repeated member reduced to its last occurrence, and only after
// the first read fails, so a document without repeats pays nothing.
public static partial class JsonProblem
{
    /// <summary>
    /// Reads <paramref name="text"/> as a <typeparamref name="T"/> like <see cref="Read{T}(string, JsonTypeInfo{T})"/>, except that a
    /// repeated member anywhere in it is read as its last occurrence.
    /// </summary>
    /// <exception cref="JsonInputException">The text is not JSON, or its shape is not the record's even with the last occurrences.</exception>
    public static T ReadLastWins<T>(string text, JsonTypeInfo<T> info)
        where T : class
    {
        try
        {
            return Read(text, info);
        }
        catch (JsonInputException) when (RepeatsIn(text))
        {
            using var document = JsonDocument.Parse(text);
            return Read(LastOccurrences(document.RootElement), info);
        }
    }

    /// <summary>
    /// Reads <paramref name="element"/> as a <typeparamref name="T"/> like <see cref="Read{T}(JsonElement, JsonTypeInfo{T})"/>, except that a
    /// repeated member anywhere in it is read as its last occurrence.
    /// </summary>
    /// <exception cref="JsonInputException">The element's shape is not the record's even with the last occurrences.</exception>
    public static T ReadLastWins<T>(JsonElement element, JsonTypeInfo<T> info)
        where T : class
    {
        try
        {
            return Read(element, info);
        }
        catch (JsonInputException) when (Repeats(element))
        {
            return Read(LastOccurrences(element), info);
        }
    }

    private static bool RepeatsIn(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            return Repeats(document.RootElement);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool Repeats(JsonElement element) =>
        element.ValueKind switch
        {
            JsonValueKind.Object => element.EnumerateObject().Select(member => member.Name).Distinct(StringComparer.Ordinal).Count() < element.EnumerateObject().Count()
                || element.EnumerateObject().Any(member => Repeats(member.Value)),
            JsonValueKind.Array => element.EnumerateArray().Any(Repeats),
            _ => false,
        };

    // The element with each object's repeated members reduced to the last occurrence, kept at the position it was written.
    private static JsonElement LastOccurrences(JsonElement element)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            WriteLastOccurrences(element, writer);
        }

        using var rewritten = JsonDocument.Parse(buffer.ToArray());
        return rewritten.RootElement.Clone();
    }

    private static void WriteLastOccurrences(JsonElement element, Utf8JsonWriter writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var members = element.EnumerateObject().ToList();
                writer.WriteStartObject();
                foreach (var (member, index) in members.Select((member, index) => (member, index)))
                {
                    if (members.FindLastIndex(other => other.NameEquals(member.Name)) == index)
                    {
                        writer.WritePropertyName(member.Name);
                        WriteLastOccurrences(member.Value, writer);
                    }
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteLastOccurrences(item, writer);
                }

                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }
}
