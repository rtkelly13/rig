using System.Text.Json;
using System.Text.Json.Serialization;
using Rig.Core.Text;

namespace Rig.Core.Otel;

/// <summary>
/// The compile-time metadata for reading OTLP/JSON trace lines, a format other tools own: members rig does not read are
/// ignored (and a repeated one keeps its last value), while every member it does read is typed strictly. Nulls are
/// refused where the record says non-null, and an int64 is read from a string only on the properties that say so.
/// </summary>
[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Strict,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
    AllowDuplicateProperties = true,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(OtlpLine))]
[JsonSerializable(typeof(OtlpSpan))]
[JsonSerializable(typeof(OtlpAnyValue))]
[JsonSerializable(typeof(ChromeSpan))]
[JsonSerializable(typeof(AnalyticsSpan))]
[JsonSerializable(typeof(OtlpResource))]
[JsonSerializable(typeof(OtlpScope))]
[JsonSerializable(typeof(Dictionary<string, ReplayEntry>))]
[JsonSerializable(typeof(Dictionary<string, MarkEntry>))]
[JsonSerializable(typeof(PrimaryState))]
internal sealed partial class OtlpJsonContext : JsonSerializerContext;

/// <summary>One trace file line: <c>{"resourceSpans": [...]}</c>.</summary>
internal sealed record OtlpLine
{
    public List<OtlpResourceSpans> ResourceSpans { get; set; } = [];
}

// Optional members are { get; set; }, not init: the generated code assigns every init-only member it constructs with, so an absent one
// would be set to null instead of keeping its initializer.
// The collection elements are structs because a struct cannot be null: a null element is rejected where a class element would slip through.
internal record struct OtlpResourceSpans
{
    public OtlpResourceSpans()
    {
    }

    public JsonElement? Resource { get; set; }

    public List<OtlpScopeSpans> ScopeSpans { get; set; } = [];
}

internal record struct OtlpScopeSpans
{
    public OtlpScopeSpans()
    {
    }

    public JsonElement? Scope { get; set; }

    // Each span is decoded on its own, so a span of the wrong shape costs that span and not the line.
    public List<JsonElement> Spans { get; set; } = [];
}

/// <summary>The members of a span rig reads.</summary>
internal sealed record OtlpSpan
{
    [JsonConverter(typeof(NonEmptyStringConverter))]
    public required string TraceId { get; init; }

    [JsonConverter(typeof(NonEmptyStringConverter))]
    public required string SpanId { get; init; }

    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public long? StartTimeUnixNano { get; set; }

    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public long? EndTimeUnixNano { get; set; }

    public List<OtlpAttribute> Attributes { get; set; } = [];

    /// <summary>Its end time, else its start time, else 0.</summary>
    [JsonIgnore]
    public long EndNs => EndTimeUnixNano is { } end and not 0 ? end : StartTimeUnixNano ?? 0;

}

internal record struct OtlpAttribute
{
    public OtlpAttribute()
    {
    }

    // Only an attribute named span.incomplete is read, so the key and value stay raw until it is found.
    public JsonElement? Key { get; set; }

    public JsonElement? Value { get; set; }
}

internal sealed record OtlpAnyValue
{
    public bool? BoolValue { get; set; }
}
