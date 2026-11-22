using System.Globalization;

namespace Rig.Core.Markup;

/// <summary>The shortest round-trip rendering of a float: always a point or an exponent (<c>1.0</c>, <c>1e+16</c>), <c>inf</c> and <c>nan</c> in lower case.</summary>
public static class YamlFloat
{
    /// <summary>The shortest text that round-trips <paramref name="value"/>.</summary>
    public static string Repr(double value)
    {
        if (double.IsNaN(value))
        {
            return "nan";
        }

        if (double.IsInfinity(value))
        {
            return value > 0 ? "inf" : "-inf";
        }

        var sign = double.IsNegative(value) ? "-" : string.Empty;
        if (value == 0)
        {
            return sign + "0.0";
        }

        // .NET's "R" is the shortest text that round-trips; only the layout differs.
        var shortest = Math.Abs(value).ToString("R", CultureInfo.InvariantCulture);
        var parts = shortest.Split('E');
        var exponent = parts.Length > 1 ? int.Parse(parts[1], CultureInfo.InvariantCulture) : 0;
        var mantissa = parts[0].Split('.');
        var whole = mantissa[0];
        var digits = whole + (mantissa.Length > 1 ? mantissa[1] : string.Empty);
        var leading = digits.Length - digits.TrimStart('0').Length;
        digits = digits.Trim('0');
        var point = whole.Length - 1 + exponent - leading;
        if (point is >= -4 and < 16)
        {
            if (point < 0)
            {
                return $"{sign}0.{new string('0', -point - 1)}{digits}";
            }

            var integer = digits.Length > point ? digits[..(point + 1)] : digits.PadRight(point + 1, '0');
            var fraction = digits.Length > point + 1 ? digits[(point + 1)..] : "0";
            return $"{sign}{integer}.{fraction}";
        }

        var rest = digits.Length > 1 ? "." + digits[1..] : string.Empty;
        var power = point < 0 ? $"-{-point:00}" : $"+{point:00}";
        return $"{sign}{digits[0]}{rest}e{power}";
    }
}
