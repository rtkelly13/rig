using System.Globalization;
using System.Text;

namespace Rig.Cli.Gh.Threads;

/// <summary>
/// Python's <c>repr()</c> of a string, so messages that quoted a value with <c>!r</c> read the same from C#. Single quotes
/// unless the text holds a single quote and no double quote; backslashes, the quote and control characters escaped.
/// </summary>
internal static class PyRepr
{
    /// <summary>The repr of <paramref name="text"/>.</summary>
    public static string Of(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var quote = text.Contains('\'', StringComparison.Ordinal) && !text.Contains('"', StringComparison.Ordinal) ? '"' : '\'';
        var builder = new StringBuilder().Append(quote);
        foreach (var c in text)
        {
            _ = c switch
            {
                '\\' => builder.Append(@"\\"),
                '\n' => builder.Append(@"\n"),
                '\r' => builder.Append(@"\r"),
                '\t' => builder.Append(@"\t"),
                _ when c == quote => builder.Append('\\').Append(c),
                _ when c < ' ' || c == '\x7f' => builder.Append(CultureInfo.InvariantCulture, $"\\x{(int)c:x2}"),
                _ => builder.Append(c),
            };
        }

        return builder.Append(quote).ToString();
    }
}
