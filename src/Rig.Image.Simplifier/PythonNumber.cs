using System.Globalization;
using System.Numerics;

namespace Rig.Image.Simplifier;

/// <summary>
/// Python's number formatting, exactly. <c>f"{value:.2f}"</c> rounds the exact binary value half to even, so 0.125 at two places is
/// 0.12 and 2.675 is 2.67 (it is really 2.67499...). .NET's "F2" rounds differently on exact ties, so the digits are produced here from
/// the double's mantissa and exponent with integer arithmetic.
/// </summary>
public static class PythonNumber
{
    /// <summary>Python's <c>f"{value:.{places}f}"</c>.</summary>
    public static string FormatFixed(double value, int places)
    {
        if (double.IsPositiveInfinity(value))
        {
            return "inf";
        }

        if (double.IsNegativeInfinity(value))
        {
            return "-inf";
        }

        var bits = BitConverter.DoubleToInt64Bits(value);
        var negative = bits < 0;
        var exponentBits = (int)((bits >> 52) & 0x7FF);
        var fraction = bits & 0xFFFFFFFFFFFFFL;
        var mantissa = exponentBits == 0 ? fraction : fraction | (1L << 52);
        var exponent = (exponentBits == 0 ? 1 : exponentBits) - 1075;

        // value = mantissa * 2^exponent; scaled = value * 10^places, rounded half to even.
        var numerator = new BigInteger(mantissa) * BigInteger.Pow(10, places);
        BigInteger scaled;
        if (exponent >= 0)
        {
            scaled = numerator << exponent;
        }
        else
        {
            var denominator = BigInteger.One << -exponent;
            scaled = BigInteger.DivRem(numerator, denominator, out var remainder);
            var twice = remainder * 2;
            var comparison = twice.CompareTo(denominator);
            if (comparison > 0 || (comparison == 0 && !scaled.IsEven))
            {
                scaled += 1;
            }
        }

        var digits = scaled.ToString(CultureInfo.InvariantCulture);
        if (places > 0)
        {
            digits = digits.PadLeft(places + 1, '0');
            digits = string.Concat(digits.AsSpan(0, digits.Length - places), ".", digits.AsSpan(digits.Length - places));
        }

        return negative ? "-" + digits : digits;
    }

    /// <summary>Python's <c>round(value, places)</c>: the same half-to-even on the exact value, then back to a double.</summary>
    public static double Round(double value, int places)
    {
        if (!double.IsFinite(value))
        {
            return value;
        }

        return double.Parse(FormatFixed(value, places), CultureInfo.InvariantCulture);
    }

    /// <summary>Python's <c>int(value)</c> as text: truncation toward zero, any magnitude.</summary>
    public static string Truncate(double value)
    {
        if (!double.IsFinite(value))
        {
            throw new OverflowException("cannot convert a non-finite float to an integer");
        }

        return new BigInteger(Math.Truncate(value)).ToString(CultureInfo.InvariantCulture);
    }
}
