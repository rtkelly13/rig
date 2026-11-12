using Rig.Core.Desktop;

namespace Rig.Core.Tests.Desktop;

/// <summary>The exact rationals placement geometry is measured in, behaving as Python's <c>fractions.Fraction</c>.</summary>
public sealed class FractionTests
{
    [Fact]
    public void A_fraction_is_kept_in_lowest_terms_with_a_positive_denominator()
    {
        Assert.Equal((2L, 3L), (new Fraction(4, 6).Numerator, new Fraction(4, 6).Denominator));
        Assert.Equal((-1L, 2L), (new Fraction(1, -2).Numerator, new Fraction(1, -2).Denominator));
        Assert.Equal(Fraction.Zero, new Fraction(0, 5));
        Assert.Equal(1L, default(Fraction).Denominator);
        Assert.Throws<DivideByZeroException>(() => new Fraction(1, 0));
    }

    [Fact]
    public void Arithmetic_is_exact()
    {
        var third = new Fraction(1, 3);

        Assert.Equal(Fraction.One, third + third + third);
        Assert.Equal(new Fraction(1, 6), new Fraction(1, 2) - third);
        Assert.Equal(new Fraction(1, 9), third * third);
        Assert.Equal(new Fraction(3, 2), new Fraction(1, 2) / third);
        Assert.Equal(new Fraction(2), Fraction.FromInt32(2));
        Assert.Throws<DivideByZeroException>(() => third / Fraction.Zero);
    }

    [Fact]
    public void Fractions_order_by_value()
    {
        var third = new Fraction(1, 3);
        var half = new Fraction(1, 2);

        var sameThird = new Fraction(2, 6);

        Assert.True(third < half && half > third && third <= sameThird && sameThird >= third);
        Assert.False(half <= third || third >= half);
        Assert.Equal(0, third.CompareTo((object)new Fraction(2, 6)));
        Assert.Equal(1, third.CompareTo(null));
        Assert.Throws<ArgumentException>(() => third.CompareTo("1/3"));
    }

    [Fact]
    public void Fractions_near_the_long_limits_still_order()
    {
        var larger = new Fraction(long.MaxValue - 1, long.MaxValue);
        var smaller = new Fraction(long.MaxValue - 2, long.MaxValue - 1);

        Assert.True(smaller < larger);
        Assert.Equal(1, larger.CompareTo(smaller));
    }

    [Fact]
    public void Arithmetic_overflows_only_when_the_reduced_result_does_not_fit()
    {
        var tiny = new Fraction(1, long.MaxValue);

        Assert.Equal(long.MinValue, new Fraction(long.MinValue).Numerator);
        Assert.Equal(new Fraction(long.MinValue / 2), new Fraction(long.MinValue, 2));
        Assert.Equal(new Fraction(2, long.MaxValue), tiny + tiny);
        Assert.Equal(Fraction.Zero, tiny - tiny);
        Assert.Equal(Fraction.One, new Fraction(long.MaxValue, 2) * new Fraction(2, long.MaxValue));
        Assert.Equal(Fraction.One, tiny / tiny);
        Assert.Throws<OverflowException>(() => new Fraction(long.MaxValue) + Fraction.One);
        Assert.Throws<OverflowException>(() => new Fraction(long.MinValue, -1));
    }

    [Fact]
    public void A_fraction_prints_and_converts_as_python_does()
    {
        Assert.Equal("1/3", new Fraction(1, 3).ToString());
        Assert.Equal("-2", new Fraction(-4, 2).ToString());
        Assert.Equal(0.5, new Fraction(1, 2).ToDouble());
    }
}
