using System.Globalization;
using System.Text;

namespace Rig.Core.Services;

/// <summary>
/// Writes an XML property list byte for byte as Python's <c>plistlib.dumps</c> did: keys sorted, tab indents, and only
/// the value kinds a LaunchAgent uses (strings, whole numbers, booleans, lists of strings and string-to-string dictionaries).
/// </summary>
public static class Plist
{
    private const string Header =
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n"
        + "<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n"
        + "<plist version=\"1.0\">\n";

    /// <summary>The plist for a dictionary of <paramref name="entries"/>.</summary>
    /// <exception cref="ArgumentException">A value is of a kind not handled here, or a string holds a control character XML cannot carry.</exception>
    public static byte[] Render(IReadOnlyDictionary<string, object> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var xml = new StringBuilder(Header).Append("<dict>\n");
        foreach (var (key, value) in entries.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            xml.Append('\t').Append("<key>").Append(Escape(key)).Append("</key>\n");
            Value(xml, value, 1);
        }

        xml.Append("</dict>\n</plist>\n");
        return new UTF8Encoding(false).GetBytes(xml.ToString());
    }

    private static void Value(StringBuilder xml, object value, int depth)
    {
        var indent = new string('\t', depth);
        switch (value)
        {
            case string text:
                xml.Append(indent).Append("<string>").Append(Escape(text)).Append("</string>\n");
                break;
            case bool flag:
                xml.Append(indent).Append(flag ? "<true/>" : "<false/>").Append('\n');
                break;
            case int number:
                xml.Append(indent).Append("<integer>").Append(number.ToString(CultureInfo.InvariantCulture)).Append("</integer>\n");
                break;
            case IReadOnlyDictionary<string, string> map:
                xml.Append(indent).Append("<dict>\n");
                foreach (var (key, item) in map.OrderBy(e => e.Key, StringComparer.Ordinal))
                {
                    xml.Append(indent).Append('\t').Append("<key>").Append(Escape(key)).Append("</key>\n");
                    Value(xml, item, depth + 1);
                }

                xml.Append(indent).Append("</dict>\n");
                break;
            case IEnumerable<string> items:
                xml.Append(indent).Append("<array>\n");
                foreach (var item in items)
                {
                    Value(xml, item, depth + 1);
                }

                xml.Append(indent).Append("</array>\n");
                break;
            default:
                throw new ArgumentException($"plist values here are strings, ints, bools, string lists or string dictionaries, not {value.GetType().Name}", nameof(value));
        }
    }

    // plistlib refused the C0 controls other than tab and newline, folded carriage returns into newlines, and escaped & < >.
    private static string Escape(string text)
    {
        if (text.Any(c => c is < ' ' and not '\t' and not '\n' and not '\r'))
        {
            throw new ArgumentException("strings with control characters are not supported in plists", nameof(text));
        }

        return text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);
    }
}
