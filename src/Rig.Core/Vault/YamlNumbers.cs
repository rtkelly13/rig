using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;

namespace Rig.Core.Vault;

/// <summary>Integer parsing for the bases YAML 1.1 uses: 2, 8, 10 and 16.</summary>
public static class YamlInt
{
    /// <summary>The integer <paramref name="text"/> spells in <paramref name="radix"/>, with a sign and whitespace allowed.</summary>
    /// <exception cref="VaultYamlException">The text is not a number in that base.</exception>
    public static BigInteger Parse(string text, int radix)
    {
        var value = UnicodeText.Strip(text);
        var negative = value.StartsWith('-');
        if (value.Length > 0 && value[0] is '+' or '-')
        {
            value = value[1..];
        }

        var prefix = radix switch
        {
            2 => "0b",
            8 => "0o",
            16 => "0x",
            _ => null,
        };
        if (prefix is not null && value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            value = value[2..];
        }

        if (value.Length == 0)
        {
            throw new VaultYamlException($"invalid literal for int() with base {radix}: '{text}'");
        }

        var total = BigInteger.Zero;
        foreach (var c in value)
        {
            var digit = c switch
            {
                >= '0' and <= '9' => c - '0',
                >= 'a' and <= 'z' => c - 'a' + 10,
                >= 'A' and <= 'Z' => c - 'A' + 10,
                _ => int.MaxValue,
            };
            if (digit >= radix)
            {
                throw new VaultYamlException($"invalid literal for int() with base {radix}: '{text}'");
            }

            total = (total * radix) + digit;
        }

        return negative ? -total : total;
    }
}

/// <summary>Float parsing and the shortest round-trip rendering YAML values print as.</summary>
public static partial class YamlFloat
{
    /// <summary>The number <paramref name="text"/> spells, with a sign, <c>inf</c> and <c>nan</c> allowed.</summary>
    /// <exception cref="VaultYamlException">The text is not a number in that base.</exception>
    public static double Parse(string text)
    {
        var value = UnicodeText.Strip(text);
        var unsigned = value.Length > 0 && value[0] is '+' or '-' ? value[1..] : value;
        var sign = value.StartsWith('-') ? -1.0 : 1.0;
        switch (unsigned.ToLowerInvariant())
        {
            case "inf" or "infinity":
                return sign * double.PositiveInfinity;
            case "nan":
                return double.NaN;
        }

        if (!Decimal().IsMatch(value))
        {
            throw new VaultYamlException($"could not convert string to float: '{text}'");
        }

        return double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    /// <summary>The shortest text that round-trips <paramref name="value"/>, always with a point or exponent (<c>1.0</c>, <c>1e+16</c>).</summary>
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

        // .NET's "R" is the shortest text that round-trips, ; only the layout differs.
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

    [GeneratedRegex(@"^[+-]?(?:[0-9]+(?:\.[0-9]*)?|\.[0-9]+)(?:[eE][+-]?[0-9]+)?\z")]
    private static partial Regex Decimal();
}
