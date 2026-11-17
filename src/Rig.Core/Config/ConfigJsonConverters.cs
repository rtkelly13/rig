using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rig.Core.Config;

/// <summary>
/// Reads <c>license</c>, which the policy allows as a bare type (<c>"MIT"</c>) or as <c>{"type": "MIT"}</c>. A bare
/// string round-trips as one; anything else is refused with the field named.
/// </summary>
public sealed class LicenseConfigJsonConverter : JsonConverter<LicenseConfig>
{
    /// <inheritdoc/>
    public override LicenseConfig Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return new LicenseConfig { Type = reader.GetString(), Bare = true };
        }

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("license is not a string or an object");
        }

        using var document = JsonDocument.ParseValue(ref reader);
        string? type = null;
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (!property.NameEquals("type"))
            {
                continue;
            }

            type = property.Value.ValueKind == JsonValueKind.String
                ? property.Value.GetString()
                : throw new JsonException("license type is not a string");
        }

        return new LicenseConfig { Type = type };
    }

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, LicenseConfig value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);
        if (value.Bare)
        {
            writer.WriteStringValue(value.Type);
            return;
        }

        writer.WriteStartObject();
        if (value.Type is not null)
        {
            writer.WriteString("type", value.Type);
        }

        writer.WriteEndObject();
    }
}

/// <summary>
/// Reads a command rule, which the policy allows as a bare pattern (<c>"git status*"</c>) or as an object naming the
/// pattern, its rationale and the systems it does not apply to.
/// </summary>
public sealed class CliRuleConfigJsonConverter : JsonConverter<CliRuleConfig>
{
    /// <inheritdoc/>
    public override bool HandleNull => true;

    /// <inheritdoc/>
    public override CliRuleConfig Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            throw new JsonException("a command rule must be a pattern or an object");
        }

        if (reader.TokenType == JsonTokenType.String)
        {
            return new CliRuleConfig { Pattern = reader.GetString() ?? string.Empty, Bare = true };
        }

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("a command rule must be a pattern or an object");
        }

        using var document = JsonDocument.ParseValue(ref reader);
        string? pattern = null;
        string rationale = string.Empty;
        IReadOnlyList<string> excluded = [];
        foreach (var property in document.RootElement.EnumerateObject())
        {
            switch (property.Name)
            {
                case "pattern":
                    pattern = Text(property.Value, "a command rule has no pattern");
                    break;
                case "rationale":
                    rationale = property.Value.ValueKind == JsonValueKind.Null
                        ? string.Empty
                        : Text(property.Value, "rationale must be text");
                    break;
                case "exclude_systems":
                    excluded = property.Value.ValueKind == JsonValueKind.Null
                        ? []
                        : Texts(property.Value, "exclude_systems must be a list of names");
                    break;
            }
        }

        return new CliRuleConfig
        {
            Pattern = pattern ?? throw new JsonException("a command rule has no pattern"),
            Rationale = rationale,
            ExcludeSystems = excluded,
        };
    }

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, CliRuleConfig value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);
        if (value.Bare)
        {
            writer.WriteStringValue(value.Pattern);
            return;
        }

        writer.WriteStartObject();
        writer.WriteString("pattern", value.Pattern);
        if (value.Rationale.Length > 0)
        {
            writer.WriteString("rationale", value.Rationale);
        }

        if (value.ExcludeSystems.Count > 0)
        {
            writer.WritePropertyName("exclude_systems");
            writer.WriteStartArray();
            foreach (var system in value.ExcludeSystems)
            {
                writer.WriteStringValue(system);
            }

            writer.WriteEndArray();
        }

        writer.WriteEndObject();
    }

    private static string Text(JsonElement element, string why) =>
        element.ValueKind == JsonValueKind.String ? element.GetString()! : throw new JsonException(why);

    private static List<string> Texts(JsonElement element, string why)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException(why);
        }

        var texts = new List<string>();
        foreach (var item in element.EnumerateArray())
        {
            texts.Add(item.ValueKind == JsonValueKind.String ? item.GetString()! : throw new JsonException(why));
        }

        return texts;
    }
}
