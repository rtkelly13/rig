using System.Text.Json;
using System.Text.Json.Serialization;
using Rig.Core.Text;

namespace Rig.Core.Otel;

// The two readers of a span's body each keep their own meaning for an absent, null or mistyped field, so each has its own
// shape: the trace reader (Chrome) treats an absent field as empty and a null one as an error, the analytics reader treats
// both as empty. Optional members are { get; set; } so an absent one keeps its initializer, and the elements of a list are
// structs so a null element is rejected.

/// <summary>A span as the trace reader reads it.</summary>
internal sealed record ChromeSpan
{
    public string ParentSpanId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public long StartTimeUnixNano { get; set; }

    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public long EndTimeUnixNano { get; set; }

    public ChromeStatus Status { get; set; } = new();

    public List<OtlpKeyValue> Attributes { get; set; } = [];

    public List<ChromeEvent> Events { get; set; } = [];
}

internal sealed record ChromeStatus
{
    public long? Code { get; set; }

    // Only an error carries a message that is read, so a message of the wrong type is rejected only then.
    public JsonElement? Message { get; set; }
}

internal record struct ChromeEvent
{
    public ChromeEvent()
    {
    }

    public string Name { get; set; } = string.Empty;

    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public long TimeUnixNano { get; set; }

    public List<OtlpKeyValue> Attributes { get; set; } = [];
}

/// <summary>A span as the analytics reader reads it.</summary>
internal sealed record AnalyticsSpan
{
    public string? ParentSpanId { get; set; }

    public string? Name { get; set; }

    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public long? StartTimeUnixNano { get; set; }

    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public long? EndTimeUnixNano { get; set; }

    public AnalyticsStatus? Status { get; set; }

    public List<OtlpKeyValue> Attributes { get; set; } = [];

    public List<AnalyticsEvent> Events { get; set; } = [];
}

internal sealed record AnalyticsStatus
{
    [JsonConverter(typeof(StatusCodeConverter))]
    public int? Code { get; set; }

    public string? Message { get; set; }
}

internal record struct AnalyticsEvent
{
    public AnalyticsEvent()
    {
    }

    public string? Name { get; set; }

    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public long? TimeUnixNano { get; set; }

    public List<OtlpKeyValue> Attributes { get; set; } = [];
}

internal sealed record OtlpScope
{
    public string? Name { get; set; }
}

/// <summary>An OTLP status code: 0, 1 or 2 as a number, or the enum's name. Null for a code that is not an error.</summary>
internal sealed class StatusCodeConverter : JsonConverter<int?>, IJsonPhrase
{
    public string Phrase => "a whole number or STATUS_CODE_ERROR, STATUS_CODE_OK or STATUS_CODE_UNSET";

    public override bool HandleNull => true;

    public override int? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.Null => null,
            JsonTokenType.Number when reader.TryGetInt32(out var code) => code,
            JsonTokenType.String when reader.GetString() is "Error" or "STATUS_CODE_ERROR" => 2,
            JsonTokenType.String when reader.GetString() is "STATUS_CODE_OK" or "STATUS_CODE_UNSET" => null,
            _ => throw new JsonException(Phrase),
        };

    public override void Write(Utf8JsonWriter writer, int? value, JsonSerializerOptions options) =>
        throw new NotSupportedException("A status code is only read.");
}
