using System.Text.Json;
using System.Text.Json.Nodes;

namespace Rig.Core.Text;

/// <summary>
/// Renders the JSON rig writes to stdout, to files and to other tools. It is plain <see cref="System.Text.Json"/> —
/// two-space indentation or one line, LF line endings on every platform, unicode escaped unless the caller asks to
/// keep it — so what rig emits is what Python's <c>json.dumps</c> wrote: deterministic on Windows as well as Unix, and
/// nothing hand-walks the node tree to format it.
/// </summary>
public static class JsonOutput
{
    /// <summary><paramref name="node"/> with two-space indentation; non-ASCII is escaped unless <paramref name="keepNonAscii"/>.</summary>
    public static string Format(JsonNode? node, bool keepNonAscii = false) =>
        JsonSerializer.Serialize<JsonNode?>(node, keepNonAscii ? JsonOutputOptions.IndentedUnicode : JsonOutputOptions.Indented);

    /// <summary><paramref name="node"/> on one line; non-ASCII is escaped unless <paramref name="keepNonAscii"/>.</summary>
    public static string Line(JsonNode? node, bool keepNonAscii = false) =>
        JsonSerializer.Serialize<JsonNode?>(node, keepNonAscii ? JsonOutputOptions.CompactUnicode : JsonOutputOptions.Compact);

    /// <summary><paramref name="value"/> as the JSON number <see cref="System.Text.Json"/> writes for a double.</summary>
    public static string Number(double value) => JsonSerializer.Serialize(value);
}
