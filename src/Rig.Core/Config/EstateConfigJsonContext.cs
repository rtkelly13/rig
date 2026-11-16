using System.Text.Json.Serialization;

namespace Rig.Core.Config;

/// <summary>
/// The compile-time serialization metadata for every estate configuration file. Source generation keeps the loaders
/// reflection-free, so they are trim- and Native AOT-safe (ADR 0023), and names the properties as the files spell
/// them: snake_case.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    RespectNullableAnnotations = true)]
[JsonSerializable(typeof(ManagedProjectsConfig))]
[JsonSerializable(typeof(ManagedProjectEntry))]
[JsonSerializable(typeof(RepoPoliciesConfig))]
[JsonSerializable(typeof(CliAllowlistsConfig))]
[JsonSerializable(typeof(CliRuleConfig))]
[JsonSerializable(typeof(EstateScopesConfig))]
[JsonSerializable(typeof(List<string>))]
internal sealed partial class EstateConfigJsonContext : JsonSerializerContext;
