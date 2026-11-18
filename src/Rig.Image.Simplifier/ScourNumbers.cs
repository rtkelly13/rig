using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;

namespace Rig.Image.Simplifier;

/// <summary>The unit of an SVG length, as scour numbers them.</summary>
internal enum LengthUnit
{
    /// <summary>A number followed by text that is not a unit; scour leaves such a length without a unit, so reading it fails.</summary>
    Missing = -2,

    /// <summary>Not a length.</summary>
    Invalid = -1,

    /// <summary>A bare number.</summary>
    None = 0,

    /// <summary>A percentage.</summary>
    Percent = 1,

    /// <summary>Pixels.</summary>
    Px = 2,

    /// <summary>Points.</summary>
    Pt = 3,

    /// <summary>Picas.</summary>
    Pc = 4,

    /// <summary>The font's em.</summary>
    Em = 5,

    /// <summary>The font's x-height.</summary>
    Ex = 6,

    /// <summary>Centimetres.</summary>
    Cm = 7,

    /// <summary>Millimetres.</summary>
    Mm = 8,

    /// <summary>Inches.</summary>
    In = 9,
}

/// <summary>A Python <c>decimal.Decimal</c>: a signed coefficient and a power of ten, with only the operations scour's number scouring uses.</summary>
internal readonly record struct ScourDecimal(BigInteger Coefficient, int Exponent, bool NegativeZero = false)
{
    private static readonly Regex DecimalText = new(@"^\s*([-+]?)([0-9]*)(?:\.([0-9]*))?(?:[eE]([-+]?[0-9]+))?\s*$", RegexOptions.CultureInvariant);

    /// <summary>The context's working precision: Python's default, 28 significant digits.</summary>
    public const int ContextPrecision = 28;

    /// <summary>A whole number.</summary>
    public static implicit operator ScourDecimal(int value) => new(value, 0);

    /// <summary>The sum, rounded to the context's precision.</summary>
    public static ScourDecimal operator +(ScourDecimal left, ScourDecimal right)
    {
        var exponent = Math.Min(left.Exponent, right.Exponent);
        var sum = left.Scaled(exponent) + right.Scaled(exponent);
        // A sum of two zeros keeps the sign they share; any other sum that cancels to zero is positive.
        return new ScourDecimal(sum, exponent, sum.IsZero && left.IsZero && right.IsZero && left.SignedNegative && right.SignedNegative).Plus(ContextPrecision);
    }

    /// <summary>The difference, rounded to the context's precision.</summary>
    public static ScourDecimal operator -(ScourDecimal left, ScourDecimal right) => left + (-right);

    /// <summary>The negation.</summary>
    public static ScourDecimal operator -(ScourDecimal value) => new(-value.Coefficient, value.Exponent);

    /// <summary>The product, rounded to the context's precision.</summary>
    public static ScourDecimal operator *(ScourDecimal left, ScourDecimal right) =>
        new ScourDecimal(left.Coefficient * right.Coefficient, left.Exponent + right.Exponent, left.IsZero | right.IsZero ? left.SignedNegative != right.SignedNegative : false).Plus(ContextPrecision);

    /// <summary>The quotient rounded to the context's precision. Division by zero is an <see cref="DivideByZeroException"/>, Python's ArithmeticError.</summary>
    public static ScourDecimal operator /(ScourDecimal left, ScourDecimal right)
    {
        if (right.Coefficient.IsZero)
        {
            throw new DivideByZeroException("Decimal division by zero.");
        }

        if (left.Coefficient.IsZero)
        {
            return new ScourDecimal(BigInteger.Zero, 0, left.SignedNegative != right.SignedNegative);
        }

        // Enough digits that the context's rounding sees the true remainder, and a sticky digit when the division is not exact.
        var shift = Math.Max(0, ContextPrecision + 2 - left.DigitCount + right.DigitCount);
        var quotient = BigInteger.DivRem(left.Coefficient * BigInteger.Pow(10, shift), right.Coefficient, out var remainder);
        var exponent = left.Exponent - right.Exponent - shift;
        if (remainder.IsZero)
        {
            // An exact quotient takes the ideal exponent, as Python gives 1/2 as 0.5.
            var ideal = left.Exponent - right.Exponent;
            while (exponent < ideal && (quotient % 10).IsZero)
            {
                quotient /= 10;
                exponent++;
            }
        }
        else
        {
            quotient = (quotient * 10) + (quotient.Sign < 0 ? -1 : 1);
            exponent--;
        }

        return new ScourDecimal(quotient, exponent).Plus(ContextPrecision);
    }

    /// <summary>The remainder, which takes the sign of the dividend as Python's <c>Decimal.__mod__</c> does.</summary>
    public static ScourDecimal operator %(ScourDecimal left, ScourDecimal right)
    {
        if (right.Coefficient.IsZero)
        {
            throw new DivideByZeroException("Decimal division by zero.");
        }

        var exponent = Math.Min(left.Exponent, right.Exponent);
        var remainder = BigInteger.Remainder(left.Scaled(exponent), right.Scaled(exponent));
        return new ScourDecimal(remainder, exponent, remainder.IsZero && left.SignedNegative);
    }

    /// <summary>Compares the numbers, not their spellings.</summary>
    public static bool operator <(ScourDecimal left, ScourDecimal right) => left.CompareTo(right) < 0;

    /// <summary>Compares the numbers, not their spellings.</summary>
    public static bool operator >(ScourDecimal left, ScourDecimal right) => left.CompareTo(right) > 0;

    /// <summary>Compares the numbers, not their spellings.</summary>
    public static bool operator <=(ScourDecimal left, ScourDecimal right) => left.CompareTo(right) <= 0;

    /// <summary>Compares the numbers, not their spellings.</summary>
    public static bool operator >=(ScourDecimal left, ScourDecimal right) => left.CompareTo(right) >= 0;

    /// <summary>True for zero in any spelling.</summary>
    public bool IsZero => Coefficient.IsZero;

    /// <summary>True when below zero.</summary>
    public bool IsNegative => Coefficient.Sign < 0;

    /// <summary>True when the sign bit is set: below zero, or the negative zero Python keeps.</summary>
    public bool SignedNegative => Coefficient.Sign < 0 || NegativeZero;

    /// <summary>The magnitude.</summary>
    public ScourDecimal Abs() => new(BigInteger.Abs(Coefficient), Exponent);

    /// <summary>Orders by numeric value.</summary>
    public int CompareTo(ScourDecimal other)
    {
        var exponent = Math.Min(Exponent, other.Exponent);
        return Scaled(exponent).CompareTo(other.Scaled(exponent));
    }

    /// <summary>Python's <c>Decimal.__eq__</c>: 1.0 equals 1.</summary>
    public bool Equals(ScourDecimal other) => CompareTo(other) == 0;

    /// <inheritdoc />
    public override int GetHashCode() => Normalize().GetHashCodeCore();

    /// <summary>The Python decimal for a float, spelled the way <c>str(float)</c> spells it.</summary>
    public static ScourDecimal FromDouble(double value) => Parse(value.ToString("R", CultureInfo.InvariantCulture));

    private int GetHashCodeCore() => HashCode.Combine(Coefficient, Exponent);

    private BigInteger Scaled(int exponent) => Coefficient * BigInteger.Pow(10, Exponent - exponent);

    /// <summary>The number of digits in the coefficient (1 for zero).</summary>
    public int DigitCount => BigInteger.Abs(Coefficient).ToString(CultureInfo.InvariantCulture).Length;

    /// <summary>Python's <c>Decimal.adjusted()</c>: the exponent of the leading digit.</summary>
    public int Adjusted => Exponent + DigitCount - 1;

    /// <summary>Parses Python's decimal syntax; anything else is a <see cref="FormatException"/> (Python raises InvalidOperation).</summary>
    public static ScourDecimal Parse(string text)
    {
        var match = DecimalText.Match(text);
        if (!match.Success || (match.Groups[2].Length == 0 && match.Groups[3].Length == 0))
        {
            throw new FormatException($"'{text}' is not a decimal number.");
        }

        var digits = match.Groups[2].Value + match.Groups[3].Value;
        var coefficient = BigInteger.Parse(digits, CultureInfo.InvariantCulture);
        var exponent = -match.Groups[3].Length;
        if (match.Groups[4].Length > 0)
        {
            exponent += int.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture);
        }

        var negative = match.Groups[1].Value == "-";
        return new ScourDecimal(negative ? -coefficient : coefficient, exponent, negative && coefficient.IsZero);
    }

    /// <summary>The number Python's <c>str(value)</c> spells for a length: an int when it is whole, the shortest repr otherwise.</summary>
    public static ScourDecimal FromLength(double value)
    {
        if (value == Math.Floor(value))
        {
            return new ScourDecimal(new BigInteger(value), 0);
        }

        return Parse(value.ToString("R", CultureInfo.InvariantCulture));
    }

    /// <summary>The context's <c>plus()</c>: rounds to this many significant digits, half to even.</summary>
    public ScourDecimal Plus(int precision)
    {
        // The default context holds exponents between -999999 and 999999: above that Python raises Overflow, and far below it rounds to zero.
        if (Adjusted > 999_999 && !Coefficient.IsZero)
        {
            throw new OverflowException("The decimal exceeds the context's largest exponent.");
        }

        if (Adjusted < -999_999 && !Coefficient.IsZero)
        {
            return new ScourDecimal(BigInteger.Zero, -1_000_026, SignedNegative);
        }

        var excess = DigitCount - precision;
        if (excess <= 0)
        {
            return this;
        }

        var rounded = new ScourDecimal(DivideHalfEven(Coefficient, BigInteger.Pow(10, excess)), Exponent + excess, Coefficient.Sign < 0);
        return rounded.DigitCount > precision ? new ScourDecimal(rounded.Coefficient / 10, rounded.Exponent + 1) : rounded;
    }

    /// <summary><c>to_integral_value()</c>: whole numbers are returned as they are, otherwise rounded half to even to the units place.</summary>
    public ScourDecimal ToIntegral() => Exponent >= 0 ? this : new ScourDecimal(DivideHalfEven(Coefficient, BigInteger.Pow(10, -Exponent)), 0, SignedNegative);

    /// <summary>True when the value has no fractional part.</summary>
    public bool IsWhole => Exponent >= 0 || (Coefficient % BigInteger.Pow(10, -Exponent)).IsZero;

    /// <summary><c>normalize()</c>: strips trailing zeros.</summary>
    public ScourDecimal Normalize()
    {
        if (Coefficient.IsZero)
        {
            return new ScourDecimal(BigInteger.Zero, 0, NegativeZero);
        }

        var coefficient = Coefficient;
        var exponent = Exponent;
        while ((coefficient % 10).IsZero)
        {
            coefficient /= 10;
            exponent++;
        }

        return new ScourDecimal(coefficient, exponent);
    }

    /// <summary><c>quantize()</c> to the exponent of another number, rounding half to even.</summary>
    public ScourDecimal Quantize(int exponent)
    {
        var quantized = Exponent >= exponent
            ? new ScourDecimal(Coefficient * BigInteger.Pow(10, Exponent - exponent), exponent, NegativeZero)
            : new ScourDecimal(DivideHalfEven(Coefficient, BigInteger.Pow(10, exponent - Exponent)), exponent, SignedNegative);

        // Python's quantize signals InvalidOperation, an ArithmeticError, when the result needs more digits than the context holds.
        return quantized.DigitCount > ContextPrecision ? throw new OverflowException("The quantized value needs more than 28 digits.") : quantized;
    }

    /// <summary>Python's <c>'{:f}'.format(value)</c>: positional notation, never an exponent.</summary>
    public string ToPlainString()
    {
        var digits = BigInteger.Abs(Coefficient).ToString(CultureInfo.InvariantCulture);
        string body;
        if (Exponent >= 0)
        {
            body = digits + new string('0', Exponent);
        }
        else if (digits.Length > -Exponent)
        {
            body = string.Concat(digits.AsSpan(0, digits.Length + Exponent), ".", digits.AsSpan(digits.Length + Exponent));
        }
        else
        {
            body = "0." + new string('0', -Exponent - digits.Length) + digits;
        }

        return SignedNegative ? "-" + body : body;
    }

    private static BigInteger DivideHalfEven(BigInteger value, BigInteger divisor)
    {
        var quotient = BigInteger.DivRem(BigInteger.Abs(value), divisor, out var remainder);
        var twice = remainder * 2;
        var comparison = twice.CompareTo(divisor);
        if (comparison > 0 || (comparison == 0 && !quotient.IsEven))
        {
            quotient += 1;
        }

        return value.Sign < 0 ? -quotient : quotient;
    }
}

/// <summary>An SVG length, parsed the way scour's <c>SVGLength</c> parses it.</summary>
internal readonly record struct SvgLength(double Value, LengthUnit Declared)
{
    /// <summary>The unit. Throws <see cref="InvalidOperationException"/> where Python's attribute lookup raises AttributeError.</summary>
    public LengthUnit Unit => Declared == LengthUnit.Missing ? throw new InvalidOperationException("The length has a number but no recognised unit.") : Declared;

    private static readonly Regex PythonFloat = new(@"^[-+]?(?:[0-9]+\.?[0-9]*|\.[0-9]+)(?:[eE][-+]?[0-9]+)?$", RegexOptions.CultureInvariant);
    private static readonly Regex SciNumber = new(@"^[-+]?([0-9]*\.?)?[0-9]+[eE][-+]?[0-9]+", RegexOptions.CultureInvariant);
    private static readonly Regex Number = new(@"^[-+]?([0-9]*\.?)?[0-9]+", RegexOptions.CultureInvariant);
    private static readonly Regex Exponent = new(@"[eE]([-+]?[0-9]+)", RegexOptions.CultureInvariant);
    private static readonly Regex UnitText = new(@"(em|ex|px|pt|pc|cm|mm|in|%)$", RegexOptions.CultureInvariant);

    /// <summary>
    /// Parses a length. A number too large for a double throws <see cref="OverflowException"/> where Python raises OverflowError, and a
    /// number followed by text that is not a unit throws <see cref="InvalidOperationException"/> where Python raises AttributeError.
    /// </summary>
    public static SvgLength Parse(string text)
    {
        if (TryPythonFloat(text, out var simple))
        {
            ThrowIfInfinite(simple);
            return new SvgLength(simple, LengthUnit.None);
        }

        var value = 0.0;
        var unitBegin = 0;
        if (SciNumber.IsMatch(text))
        {
            var number = Number.Match(text);
            var exponent = Exponent.Match(text, number.Index);
            value = double.Parse(number.Value, CultureInfo.InvariantCulture) * Math.Pow(10, double.Parse(exponent.Groups[1].Value, CultureInfo.InvariantCulture));
            unitBegin = exponent.Index + exponent.Length;
        }
        else
        {
            var number = Number.Match(text);
            if (number.Success)
            {
                value = double.Parse(number.Value, CultureInfo.InvariantCulture);
                unitBegin = number.Length;
            }
        }

        ThrowIfInfinite(value);
        if (unitBegin == 0)
        {
            return new SvgLength(0, LengthUnit.Invalid);
        }

        var unit = UnitText.Match(text, unitBegin);
        if (!unit.Success)
        {
            return new SvgLength(value, LengthUnit.Missing);
        }

        return new SvgLength(value, ParseUnit(unit.Value));
    }

    /// <summary>The unit's text, Python's <c>Unit.str</c>; an invalid length is spelled INVALID.</summary>
    public static string UnitString(LengthUnit unit) => unit switch
    {
        LengthUnit.None => string.Empty,
        LengthUnit.Percent => "%",
        LengthUnit.Px => "px",
        LengthUnit.Pt => "pt",
        LengthUnit.Pc => "pc",
        LengthUnit.Em => "em",
        LengthUnit.Ex => "ex",
        LengthUnit.Cm => "cm",
        LengthUnit.Mm => "mm",
        LengthUnit.In => "in",
        _ => "INVALID",
    };

    private static LengthUnit ParseUnit(string text) => text switch
    {
        "%" => LengthUnit.Percent,
        "px" => LengthUnit.Px,
        "pt" => LengthUnit.Pt,
        "pc" => LengthUnit.Pc,
        "em" => LengthUnit.Em,
        "ex" => LengthUnit.Ex,
        "cm" => LengthUnit.Cm,
        "mm" => LengthUnit.Mm,
        "in" => LengthUnit.In,
        _ => LengthUnit.Invalid,
    };

    private static void ThrowIfInfinite(double value)
    {
        if (double.IsInfinity(value))
        {
            throw new OverflowException("cannot convert an infinite length to an integer");
        }
    }

    /// <summary>Python's <c>float(text)</c>. Throws <see cref="FormatException"/> where Python raises ValueError.</summary>
    public static double ParsePythonFloat(string text)
    {
        if (TryPythonFloat(text, out var value))
        {
            return value;
        }

        if (text.Trim().TrimStart('+', '-').Equals("nan", StringComparison.OrdinalIgnoreCase))
        {
            return double.NaN;
        }

        throw new FormatException($"could not convert string to float: '{text}'");
    }

    /// <summary>Python's <c>float(text)</c> for the spellings a length can take; "nan" is a ValueError there once converted to an int, so it does not count.</summary>
    private static bool TryPythonFloat(string text, out double value)
    {
        var trimmed = text.Trim();
        if (PythonFloat.IsMatch(trimmed))
        {
            value = double.Parse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture);
            return true;
        }

        var unsigned = trimmed.TrimStart('+', '-');
        if (unsigned.Equals("inf", StringComparison.OrdinalIgnoreCase) || unsigned.Equals("infinity", StringComparison.OrdinalIgnoreCase))
        {
            value = trimmed.StartsWith('-') ? double.NegativeInfinity : double.PositiveInfinity;
            return true;
        }

        value = 0;
        return false;
    }
}

/// <summary>Scour's number scouring: lengths cut to a few significant digits, written in whichever of positional and scientific notation is shorter.</summary>
internal static class ScourNumbers
{
    /// <summary>The significant digits scour keeps by default.</summary>
    public const int Digits = 5;

    /// <summary><c>scourLength</c>: a length with its unit.</summary>
    public static string Length(string text)
    {
        var length = SvgLength.Parse(text);
        return Unitless(ScourDecimal.FromLength(length.Value)) + SvgLength.UnitString(length.Unit);
    }

    /// <summary><c>scourUnitlessLength</c> given the number's text (the viewBox path, where scour hands over the string untouched).</summary>
    public static string Unitless(string text) => Unitless(ScourDecimal.Parse(text));

    /// <summary>
    /// <c>scourUnitlessLength</c>. With <paramref name="rendererWorkaround"/> (what path and point data use) a leading zero stays, so a
    /// dot never follows a command letter.
    /// </summary>
    public static string Unitless(ScourDecimal initial, bool rendererWorkaround = false)
    {
        var created = initial.Plus(28);
        var length = created.Plus(Digits);
        length = length.IsWhole ? length.ToIntegral() : length.Normalize();

        // Re-quantize from the initial value so 123.4 becomes 123 rather than 120.
        var nonScientific = created.Quantize(Math.Min(length.Exponent, 0)).ToPlainString();
        if (!rendererWorkaround)
        {
            nonScientific = TrimLeadingZero(nonScientific);
        }

        if (nonScientific.Length <= 3)
        {
            return nonScientific;
        }

        var exponent = length.Adjusted;
        var shifted = new ScourDecimal(length.Coefficient, length.Exponent - exponent).Normalize();
        var scientific = shifted.ToPlainString() + "e" + exponent.ToString(CultureInfo.InvariantCulture);
        return scientific.Length < nonScientific.Length ? scientific : nonScientific;
    }

    private static string TrimLeadingZero(string text)
    {
        if (text.Length > 2 && text.StartsWith("0.", StringComparison.Ordinal))
        {
            return text[1..];
        }

        return text.Length > 3 && text.StartsWith("-0.", StringComparison.Ordinal) ? "-" + text[2..] : text;
    }
}
