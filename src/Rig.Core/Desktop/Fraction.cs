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
    /// <exception cref="OverflowException">The reduced fraction does not fit in <see cref="long"/>.</exception>
    public Fraction(long numerator, long denominator = 1)
        : this(Reduce(numerator, denominator))
    {
    }

    private Fraction((long Numerator, long Denominator) reduced)
    {
        Numerator = reduced.Numerator;
        _denominatorLessOne = reduced.Denominator - 1;
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

    // The arithmetic works in 128 bits, where products of two longs cannot overflow, and fails only when the reduced
    // result does not fit.

    /// <summary>The sum.</summary>
    public static Fraction Add(Fraction left, Fraction right) =>
        new(Reduce(((Int128)left.Numerator * right.Denominator) + ((Int128)right.Numerator * left.Denominator), (Int128)left.Denominator * right.Denominator));

    /// <summary>The difference.</summary>
    public static Fraction Subtract(Fraction left, Fraction right) =>
        new(Reduce(((Int128)left.Numerator * right.Denominator) - ((Int128)right.Numerator * left.Denominator), (Int128)left.Denominator * right.Denominator));

    /// <summary>The product.</summary>
    public static Fraction Multiply(Fraction left, Fraction right) =>
        new(Reduce((Int128)left.Numerator * right.Numerator, (Int128)left.Denominator * right.Denominator));

    /// <summary>The quotient.</summary>
    /// <exception cref="DivideByZeroException"><paramref name="right"/> is zero.</exception>
    public static Fraction Divide(Fraction left, Fraction right) =>
        new(Reduce((Int128)left.Numerator * right.Denominator, (Int128)left.Denominator * right.Numerator));

    /// <inheritdoc />
    public int CompareTo(Fraction other) =>
        // 128-bit cross-products cannot overflow, so any two valid fractions order.
        ((Int128)Numerator * other.Denominator).CompareTo((Int128)other.Numerator * Denominator);

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

    private static (long Numerator, long Denominator) Reduce(Int128 numerator, Int128 denominator)
    {
        if (denominator == 0)
        {
            throw new DivideByZeroException("Fraction denominator is zero");
        }

        if (denominator < 0)
        {
            (numerator, denominator) = (-numerator, -denominator);
        }

        var divisor = Gcd(Int128.Abs(numerator), denominator);
        return (checked((long)(numerator / divisor)), checked((long)(denominator / divisor)));
    }

    private static Int128 Gcd(Int128 a, Int128 b)
    {
        while (b != 0)
        {
            (a, b) = (b, a % b);
        }

        return a;
    }
}
