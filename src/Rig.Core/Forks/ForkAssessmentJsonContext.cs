using System.Text.Json.Serialization;

namespace Rig.Core.Forks;

/// <summary>
/// Source-generated JSON for the fork assessment: its configuration in, its report out, both snake_case (ADR 0023).
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    RespectNullableAnnotations = true)]
[JsonSerializable(typeof(ForkAssessmentConfig))]
[JsonSerializable(typeof(ForkAssessment))]
internal sealed partial class ForkAssessmentJsonContext : JsonSerializerContext;
