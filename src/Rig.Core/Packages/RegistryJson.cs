using System.Text.Json.Nodes;
using Rig.Core.Text;

namespace Rig.Core.Packages;

/// <summary>
/// Lenient reads of registry JSON: a field of an object, else null. Indexing a <see cref="JsonValue"/> or
/// <see cref="JsonArray"/> by name throws, so an odd optional field in NuGet's or npm's payload would abort a whole
/// audit instead of losing itself. Only the registry parsers use it: estate configuration is read through
/// <see cref="Config.ConfigLoader"/> and the source-generated <see cref="Config.EstateConfigJsonContext"/> instead.
/// </summary>
internal static class RegistryJson
{
    /// <summary>The <paramref name="key"/> field when <paramref name="node"/> is an object, else null.</summary>
    public static JsonNode? Field(this JsonNode? node, string key) => JsonShape.Field(node as JsonObject, key);
}
