using System.Text.Json.Nodes;

namespace Rig.Core.Packages;

/// <summary>
/// Lenient reads of registry JSON: a field of an object, else null. Indexing a <see cref="JsonValue"/> or
/// <see cref="JsonArray"/> by name throws, so an odd optional field would abort a whole audit instead of losing itself.
/// </summary>
internal static class JsonWalk
{
    /// <summary>The <paramref name="key"/> field when <paramref name="node"/> is an object, else null.</summary>
    public static JsonNode? At(this JsonNode? node, string key) => node is JsonObject obj ? obj[key] : null;
}
