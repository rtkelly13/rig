using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Rig.Core.Text;

/// <summary>
/// Quoting of the few values rig's messages print (single-quoted strings, bracketed lists, <c>True</c>, <c>None</c>), and
/// compact JSON. One copy, so a message reads the same whichever module writes it.
/// </summary>
public static class QuotedText
{
    private static readonly Regex ShellSafe = new(@"^[\w@%+=:,./-]+$", RegexOptions.CultureInvariant | RegexOptions.ECMAScript, TimeSpan.FromSeconds(1));

    /// <summary>
    /// A word quoted for a POSIX shell, as Python's <c>shlex.quote</c> does it: bare when every character is an ASCII
    /// letter, digit or one of <c>_@%+=:,./-</c>, else single-quoted.
    /// </summary>
    public static string Shell(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Length == 0 ? "''" : ShellSafe.IsMatch(text) ? text : "'" + text.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
    }

    /// <summary>The text quoted: single quotes, or double when the text holds a single quote and no double one.</summary>
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

    /// <summary>A list of strings, quoted: <c>['a', 'b']</c>.</summary>
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

    /// <summary>Compact JSON: no spaces, non-ASCII kept as itself.</summary>
    public static string Json(JsonNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return node.ToJsonString(JsonOutputOptions.CompactUnicode);
    }
}
