using System.Globalization;

namespace Rig.Core.Desktop;

/// <summary>
/// An exact rational number, always in lowest terms with a positive denominator, as Python's <c>fractions.Fraction</c>
/// is. Placement geometry works in fractions of a screen so that thirds tile exactly and coverage sums to one.
/// </summary>
public readonly record struct Fraction : IComparable<Fraction>, IComparable
{
    // Stored less one, so default(Fraction) is 0/1 rather than 0/0.
    private readonly long _denominatorLessOne;

    /// <summary>The fraction <paramref name="numerator"/>/<paramref name="denominator"/>, reduced.</summary>
    /// <exception cref="DivideByZeroException"><paramref name="denominator"/> is zero.</exception>
    public Fraction(long numerator, long denominator = 1)
    {
        if (denominator == 0)
        {
            throw new DivideByZeroException("Fraction denominator is zero");
        }

        if (denominator < 0)
        {
            numerator = checked(-numerator);
            denominator = checked(-denominator);
        }

        var divisor = Gcd(Math.Abs(numerator), denominator);
        Numerator = numerator / divisor;
        _denominatorLessOne = (denominator / divisor) - 1;
    }

    /// <summary>Zero.</summary>
    public static Fraction Zero => default;

    /// <summary>One.</summary>
    public static Fraction One => new(1);

    /// <summary>The numerator, carrying the sign.</summary>
    public long Numerator { get; }

    /// <summary>The denominator, always positive.</summary>
    public long Denominator => _denominatorLessOne + 1;

    /// <summary>A whole number as a fraction.</summary>
    public static implicit operator Fraction(int value) => FromInt32(value);

    /// <summary>The sum.</summary>
    public static Fraction operator +(Fraction left, Fraction right) => Add(left, right);

    /// <summary>The difference.</summary>
    public static Fraction operator -(Fraction left, Fraction right) => Subtract(left, right);

    /// <summary>The product.</summary>
    public static Fraction operator *(Fraction left, Fraction right) => Multiply(left, right);

    /// <summary>The quotient.</summary>
    public static Fraction operator /(Fraction left, Fraction right) => Divide(left, right);

    /// <summary>Whether <paramref name="left"/> is the smaller.</summary>
    public static bool operator <(Fraction left, Fraction right) => left.CompareTo(right) < 0;

    /// <summary>Whether <paramref name="left"/> is the larger.</summary>
    public static bool operator >(Fraction left, Fraction right) => left.CompareTo(right) > 0;

    /// <summary>Whether <paramref name="left"/> is no larger.</summary>
    public static bool operator <=(Fraction left, Fraction right) => left.CompareTo(right) <= 0;

    /// <summary>Whether <paramref name="left"/> is no smaller.</summary>
    public static bool operator >=(Fraction left, Fraction right) => left.CompareTo(right) >= 0;

    /// <summary>A whole number as a fraction.</summary>
    public static Fraction FromInt32(int value) => new(value);

    /// <summary>The sum.</summary>
    public static Fraction Add(Fraction left, Fraction right) =>
        new(checked((left.Numerator * right.Denominator) + (right.Numerator * left.Denominator)), checked(left.Denominator * right.Denominator));

    /// <summary>The difference.</summary>
    public static Fraction Subtract(Fraction left, Fraction right) =>
        new(checked((left.Numerator * right.Denominator) - (right.Numerator * left.Denominator)), checked(left.Denominator * right.Denominator));

    /// <summary>The product.</summary>
    public static Fraction Multiply(Fraction left, Fraction right) =>
        new(checked(left.Numerator * right.Numerator), checked(left.Denominator * right.Denominator));

    /// <summary>The quotient.</summary>
    /// <exception cref="DivideByZeroException"><paramref name="right"/> is zero.</exception>
    public static Fraction Divide(Fraction left, Fraction right) =>
        new(checked(left.Numerator * right.Denominator), checked(left.Denominator * right.Numerator));

    /// <inheritdoc />
    public int CompareTo(Fraction other) =>
        checked(Numerator * other.Denominator).CompareTo(checked(other.Numerator * Denominator));

    /// <inheritdoc />
    public int CompareTo(object? obj) => obj switch
    {
        null => 1,
        Fraction other => CompareTo(other),
        _ => throw new ArgumentException("Object is not a Fraction", nameof(obj)),
    };

    /// <summary>The nearest double, as Python's <c>float(Fraction)</c> gives.</summary>
    public double ToDouble() => (double)Numerator / Denominator;

    /// <summary><c>n/d</c>, or just <c>n</c> for a whole number, as Python's <c>str(Fraction)</c> writes it.</summary>
    public override string ToString() =>
        Denominator == 1
            ? Numerator.ToString(CultureInfo.InvariantCulture)
            : string.Create(CultureInfo.InvariantCulture, $"{Numerator}/{Denominator}");

    private static long Gcd(long a, long b)
    {
        while (b != 0)
        {
            (a, b) = (b, a % b);
        }

        return a;
    }
}
