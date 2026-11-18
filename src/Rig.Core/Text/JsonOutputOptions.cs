using System.Text.Encodings.Web;
using System.Text.Json;

namespace Rig.Core.Text;

/// <summary>
/// The serializer settings rig writes with: the spellings its files use (two-space indentation or
/// one line, non-ASCII escaped or kept), and the snake_case spelling the web app reads. Every one writes LF on every
/// platform, so a file rig writes is byte-identical on Windows and Unix (#260).
/// </summary>
/// <remarks>
/// The instances are shared and <see cref="JsonSerializerOptions"/> freezes on first use, so copy one with
/// <see cref="JsonSerializerOptions.JsonSerializerOptions(JsonSerializerOptions)"/> before changing a setting for one
/// site. Do not mutate these.
/// </remarks>
public static class JsonOutputOptions
{
    /// <summary>Two-space indentation, non-ASCII escaped: <c>json.dumps(value, indent=2)</c>.</summary>
    public static JsonSerializerOptions Indented { get; } = new()
    {
        WriteIndented = true,
        NewLine = "\n",
    };

    /// <summary>Two-space indentation, non-ASCII kept: the spelling of the vault and state files.</summary>
    public static JsonSerializerOptions IndentedUnicode { get; } = new()
    {
        WriteIndented = true,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>One line, non-ASCII escaped: <c>json.dumps(value)</c>.</summary>
    public static JsonSerializerOptions Compact { get; } = new() { NewLine = "\n" };

    /// <summary>One line, non-ASCII kept.</summary>
    public static JsonSerializerOptions CompactUnicode { get; } = new()
    {
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary><see cref="IndentedUnicode"/> with snake_case names, as <c>rig models</c> and <c>rig gh threads</c> print.</summary>
    public static JsonSerializerOptions SnakeCaseIndented { get; } = new(IndentedUnicode)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    /// <summary><see cref="CompactUnicode"/> with snake_case names, for the one-line form of the same payloads.</summary>
    public static JsonSerializerOptions SnakeCaseCompact { get; } = new(CompactUnicode)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };
}
