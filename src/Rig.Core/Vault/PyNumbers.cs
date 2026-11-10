using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;

namespace Rig.Core.Vault;

/// <summary>Python's <c>int(text, base)</c>, for the bases PyYAML's int constructor uses.</summary>
public static class PyInt
{
    /// <summary>The integer <paramref name="text"/> spells in <paramref name="radix"/>, as Python's <c>int()</c> reads it.</summary>
    /// <exception cref="PyYamlException">Python would raise <c>ValueError</c>.</exception>
    public static BigInteger Parse(string text, int radix)
    {
        ArgumentNullException.ThrowIfNull(text);
        var value = PyText.Strip(text);
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
            throw new PyYamlException($"invalid literal for int() with base {radix}: '{text}'");
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
                throw new PyYamlException($"invalid literal for int() with base {radix}: '{text}'");
            }

            total = (total * radix) + digit;
        }

        return negative ? -total : total;
    }
}

/// <summary>Python's <c>float(text)</c> and <c>repr(float)</c>.</summary>
public static partial class PyFloat
{
    /// <summary>The number <paramref name="text"/> spells, as Python's <c>float()</c> reads it.</summary>
    /// <exception cref="PyYamlException">Python would raise <c>ValueError</c>.</exception>
    public static double Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var value = PyText.Strip(text);
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
            throw new PyYamlException($"could not convert string to float: '{text}'");
        }

        return double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    /// <summary>What Python's <c>repr()</c> (and so <c>str()</c>) prints for <paramref name="value"/>.</summary>
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

        // .NET's "R" is the shortest text that round-trips, as Python's repr is; only the layout differs.
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
