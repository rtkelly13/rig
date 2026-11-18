using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rig.Core.Jobs;

/// <summary>
/// The compile-time metadata for reading <c>runs.ndjson</c>, a format rig owns: a member it does not define, or one
/// repeated, is rejected rather than ignored, and a value of the wrong type is never coerced.
/// </summary>
[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Strict,
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    AllowDuplicateProperties = false,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(StoredRun))]
internal sealed partial class RunHistoryJsonContext : JsonSerializerContext;

/// <summary>One line of <c>runs.ndjson</c> as it is stored: job, run_id, started, seconds and trigger are required; the rest may be absent or null.</summary>
internal sealed record StoredRun(
    string Job,
    string RunId,
    string Started,
    double Seconds,
    string Trigger,
    [property: JsonPropertyName("returncode")] long? ReturnCode = null,
    string? Error = null,
    string? Output = null,
    string? TraceId = null,
    string? SpanId = null)
{
    /// <summary>The run, with an absent output or trace id as the empty string.</summary>
    public JobRun ToRun() => new(Job, RunId, Started, Seconds, ReturnCode, Trigger, Error, Output ?? string.Empty, TraceId ?? string.Empty, SpanId ?? string.Empty);
}
