using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Rig.Core.Governance;

/// <summary>Python's <c>repr</c> and <c>str</c> of the few values governance messages print, and pydantic's compact JSON.</summary>
public static class PyRepr
{
    private static readonly JsonSerializerOptions Compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary><c>repr(text)</c>: single quotes, or double when the text holds a single quote and no double one.</summary>
    public static string Str(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var quote = text.Contains('\'', StringComparison.Ordinal) && !text.Contains('"', StringComparison.Ordinal) ? '"' : '\'';
        var built = new StringBuilder().Append(quote);
        foreach (var c in text)
        {
            _ = c switch
            {
                '\\' => built.Append(@"\\"),
                '\n' => built.Append(@"\n"),
                '\r' => built.Append(@"\r"),
                '\t' => built.Append(@"\t"),
                _ when c == quote => built.Append('\\').Append(c),
                _ when c < ' ' || c == '\x7f' => built.Append(CultureInfo.InvariantCulture, $"\\x{(int)c:x2}"),
                _ => built.Append(c),
            };
        }

        return built.Append(quote).ToString();
    }

    /// <summary><c>repr(list_of_str)</c>: <c>['a', 'b']</c>.</summary>
    public static string List(IEnumerable<string> items) => "[" + string.Join(", ", items.Select(Str)) + "]";

    /// <summary><c>str(value)</c> of a bool, an int or None.</summary>
    public static string Value(object? value) => value switch
    {
        null => "None",
        true => "True",
        false => "False",
        IFormattable number => number.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "None",
    };

    /// <summary>pydantic's <c>dump_json</c>: no spaces, non-ASCII kept as itself.</summary>
    public static string Json(JsonNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return node.ToJsonString(Compact);
    }
}
