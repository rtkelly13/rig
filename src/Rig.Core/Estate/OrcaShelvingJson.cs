using System.Text.Json.Serialization;

namespace Rig.Core.Estate;

/// <summary>Versioned policy and durable triage wire types for daily shelving.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(OrcaShelvingPolicy))]
[JsonSerializable(typeof(OrcaShelvingRow))]
[JsonSerializable(typeof(List<OrcaShelvingRow>))]
public sealed partial class OrcaShelvingJson : JsonSerializerContext;
