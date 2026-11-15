using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rig.Core.C4;

/// <summary>Deterministic JSON for the C4 CLI: the merged model, findings and zoom tree, at two-space indentation.</summary>
public static class C4Json
{
    /// <summary>The serializer options: snake-case names, lower-case enum values, unescaped readable text and <c>\n</c>.</summary>
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    /// <summary>Indented JSON for <paramref name="value"/> without a trailing newline.</summary>
    public static string Render<T>(T value) => JsonSerializer.Serialize(value, Options);
}
