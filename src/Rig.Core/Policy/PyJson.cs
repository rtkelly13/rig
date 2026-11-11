using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Rig.Core.Policy;

/// <summary>
/// Python's <c>json.dumps(value, indent=2)</c>: the text rig wrote agents' settings files in before C#, so a file it
/// rewrites keeps its layout. Non-ASCII is escaped as lowercase <c>\uXXXX</c>, empty containers print as <c>{}</c>
/// and <c>[]</c>, and a fraction prints as Python's <c>repr</c> of the float would.
/// </summary>
public static class PyJson
{
    /// <summary><paramref name="node"/> as Python prints it; <paramref name="ensureAscii"/> false keeps non-ASCII as itself, as <c>ensure_ascii=False</c> does.</summary>
    public static string Dumps(JsonNode? node, bool ensureAscii = true)
    {
        var text = new StringBuilder();
        Write(text, node, 0, ensureAscii);
        return text.ToString();
    }

    /// <summary>Python's <c>repr</c> of <paramref name="value"/>: the shortest round-trip digits, exponent past 1e16 or below 1e-4.</summary>
    public static string FloatRepr(double value)
    {
        if (value == 0)
        {
            return double.IsNegative(value) ? "-0.0" : "0.0";
        }

        var sign = value < 0 ? "-" : string.Empty;
        var (digits, point) = Decompose(Math.Abs(value).ToString("R", CultureInfo.InvariantCulture));
        if (point is > -4 and <= 16)
        {
            return sign + (point <= 0
                ? "0." + new string('0', -point) + digits
                : point >= digits.Length
                    ? digits + new string('0', point - digits.Length) + ".0"
                    : digits[..point] + "." + digits[point..]);
        }

        var exponent = point - 1;
        var mantissa = digits.Length > 1 ? digits[..1] + "." + digits[1..] : digits;
        return $"{sign}{mantissa}e{(exponent < 0 ? '-' : '+')}{Math.Abs(exponent):00}";
    }

    // "1.25E-05" or "123.5" as its significant digits and where the point falls: 0.d1d2... x 10^point.
    private static (string Digits, int Point) Decompose(string roundTrip)
    {
        var e = roundTrip.IndexOfAny(['E', 'e']);
        var exponent = e < 0 ? 0 : int.Parse(roundTrip[(e + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        var mantissa = e < 0 ? roundTrip : roundTrip[..e];
        var dot = mantissa.IndexOf('.', StringComparison.Ordinal);
        var whole = dot < 0 ? mantissa.Length : dot;
        var all = mantissa.Replace(".", string.Empty, StringComparison.Ordinal);
        var leading = all.Length - all.TrimStart('0').Length;
        var digits = all.Trim('0');
        return (digits, whole - leading + exponent);
    }

    private static void Write(StringBuilder text, JsonNode? node, int depth, bool ascii)
    {
        switch (node)
        {
            case null:
                _ = text.Append("null");
                break;
            case JsonObject obj when obj.Count == 0:
                _ = text.Append("{}");
                break;
            case JsonObject obj:
                _ = text.Append('{');
                var first = true;
                foreach (var (key, value) in obj)
                {
                    _ = text.Append(first ? "\n" : ",\n").Append(' ', (depth + 1) * 2);
                    String(text, key, ascii);
                    _ = text.Append(": ");
                    Write(text, value, depth + 1, ascii);
                    first = false;
                }

                _ = text.Append('\n').Append(' ', depth * 2).Append('}');
                break;
            case JsonArray array when array.Count == 0:
                _ = text.Append("[]");
                break;
            case JsonArray array:
                _ = text.Append('[');
                for (var i = 0; i < array.Count; i++)
                {
                    _ = text.Append(i == 0 ? "\n" : ",\n").Append(' ', (depth + 1) * 2);
                    Write(text, array[i], depth + 1, ascii);
                }

                _ = text.Append('\n').Append(' ', depth * 2).Append(']');
                break;
            default:
                Scalar(text, (JsonValue)node, ascii);
                break;
        }
    }

    private static void Scalar(StringBuilder text, JsonValue value, bool ascii)
    {
        switch (value.GetValueKind())
        {
            case JsonValueKind.String:
                String(text, value.GetValue<string>(), ascii);
                break;
            case JsonValueKind.True:
                _ = text.Append("true");
                break;
            case JsonValueKind.False:
                _ = text.Append("false");
                break;
            case JsonValueKind.Number:
                var raw = value.ToJsonString();
                _ = text.Append(raw.IndexOfAny(['.', 'e', 'E']) >= 0
                    ? FloatRepr(double.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture))
                    : raw == "-0" ? "0" : raw);
                break;
            default:
                _ = text.Append("null");
                break;
        }
    }

    // ASCII mode escapes everything outside ' '..'~' (DEL included), as Python's ESCAPE_ASCII does.
    private static void String(StringBuilder text, string value, bool ascii)
    {
        _ = text.Append('"');
        foreach (var c in value)
        {
            _ = c switch
            {
                '"' => text.Append("\\\""),
                '\\' => text.Append("\\\\"),
                '\n' => text.Append("\\n"),
                '\r' => text.Append("\\r"),
                '\t' => text.Append("\\t"),
                '\b' => text.Append("\\b"),
                '\f' => text.Append("\\f"),
                < ' ' => text.Append(CultureInfo.InvariantCulture, $"\\u{(int)c:x4}"),
                >= '\x7f' when ascii => text.Append(CultureInfo.InvariantCulture, $"\\u{(int)c:x4}"),
                _ => text.Append(c),
            };
        }

        _ = text.Append('"');
    }
}
