using Rig.Image.Simplifier;

namespace Rig.Image.Tests;

public sealed class ScourDecimalTests
{
    [Theory]
    [InlineData("1.5", "2.25", "3.75")]
    [InlineData("0.1", "0.2", "0.3")]
    [InlineData("-1", "1", "0")]
    public void Addition_is_exact_at_the_context_precision(string left, string right, string expected)
    {
        Assert.Equal(ScourDecimal.Parse(expected), ScourDecimal.Parse(left) + ScourDecimal.Parse(right));
    }

    [Fact]
    public void Arithmetic_rounds_to_twenty_eight_significant_digits()
    {
        var third = ScourDecimal.Parse("1") / ScourDecimal.Parse("3");

        Assert.Equal(ScourDecimal.ContextPrecision, third.DigitCount);
        Assert.Equal("0.3333333333333333333333333333", third.ToPlainString());
        Assert.Equal("0.6666666666666666666666666667", (ScourDecimal.Parse("2") / ScourDecimal.Parse("3")).ToPlainString());
        Assert.Equal("-0.5", (ScourDecimal.Parse("-1") / ScourDecimal.Parse("2")).ToPlainString());
        Assert.Equal(ScourDecimal.Parse("6"), ScourDecimal.Parse("2") * ScourDecimal.Parse("3"));
        Assert.Equal(ScourDecimal.Parse("-1"), ScourDecimal.Parse("2") - ScourDecimal.Parse("3"));
    }

    [Fact]
    public void Division_by_zero_and_remainder_by_zero_are_arithmetic_errors()
    {
        var one = ScourDecimal.Parse("1");
        var zero = ScourDecimal.Parse("0");

        Assert.Throws<DivideByZeroException>(() => one / zero);
        Assert.Throws<DivideByZeroException>(() => one % zero);
        Assert.True(zero.IsZero);
        Assert.Equal(zero, zero / one);
    }

    [Fact]
    public void The_remainder_takes_the_sign_of_the_dividend()
    {
        Assert.Equal(ScourDecimal.Parse("-1"), ScourDecimal.Parse("-7") % ScourDecimal.Parse("3"));
        Assert.Equal(ScourDecimal.Parse("1"), ScourDecimal.Parse("7") % ScourDecimal.Parse("-3"));
        Assert.Equal(ScourDecimal.Parse("0.5"), ScourDecimal.Parse("360.5") % ScourDecimal.Parse("360"));
    }

    [Fact]
    public void Comparison_orders_by_value_not_spelling()
    {
        var a = ScourDecimal.Parse("1.0");
        var b = ScourDecimal.Parse("1");
        var c = ScourDecimal.Parse("2");

        Assert.True(a.Equals(b));
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.True(a < c);
        Assert.True(c > a);
        Assert.True(a <= b);
        Assert.True(a >= b);
        Assert.True(ScourDecimal.Parse("-2").IsNegative);
        Assert.Equal(c, ScourDecimal.Parse("-2").Abs());
        Assert.Equal(ScourDecimal.Parse("3"), (ScourDecimal)3);
    }

    [Fact]
    public void Negative_zero_keeps_its_sign_through_the_operations_that_keep_it_in_python()
    {
        var negativeZero = ScourDecimal.Parse("-0.00");

        Assert.Equal("-0", ScourNumbers.Unitless(negativeZero, rendererWorkaround: true));
        Assert.Equal("-0", ScourNumbers.Unitless(ScourDecimal.Parse("-0")));
        Assert.Equal("0", ScourNumbers.Unitless(ScourDecimal.Parse("0.00")));
        Assert.Equal("-0", negativeZero.Normalize().ToPlainString());
        Assert.Equal("-0", negativeZero.ToIntegral().ToPlainString());
        Assert.Equal("-0.0", ScourDecimal.Parse("-0.04").Quantize(-1).ToPlainString());
        Assert.Equal("-0", ScourDecimal.Parse("-0.4").ToIntegral().ToPlainString());
        Assert.Equal("-0", ScourDecimal.Parse("-0.0000000000000000000000000000001").Plus(0 + 1).ToIntegral().ToPlainString());
        Assert.Equal("-0.00", (negativeZero * ScourDecimal.Parse("5")).ToPlainString());
        Assert.Equal("-0.00", (negativeZero + negativeZero).ToPlainString());
        Assert.Equal("0.00", (negativeZero + ScourDecimal.Parse("0")).ToPlainString());
        Assert.Equal("0.00", (negativeZero - ScourDecimal.Parse("0")).ToPlainString());
        Assert.Equal("0.00", (-negativeZero).ToPlainString());
        Assert.Equal("0.00", negativeZero.Abs().ToPlainString());
        Assert.True((negativeZero / ScourDecimal.Parse("3")).SignedNegative);
        Assert.True((negativeZero % ScourDecimal.Parse("3")).SignedNegative);
        Assert.Equal("0", (ScourDecimal.Parse("2") - ScourDecimal.Parse("2")).ToPlainString());
    }

    [Fact]
    public void Quantizing_to_more_than_twenty_eight_digits_overflows()
    {
        Assert.Throws<OverflowException>(() => ScourDecimal.Parse("1e30").Quantize(-5));
        Assert.Equal("1.50", ScourDecimal.Parse("1.5").Quantize(-2).ToPlainString());
        Assert.Equal("2", ScourDecimal.Parse("1.5").Quantize(0).ToPlainString());
        Assert.Equal("2", ScourDecimal.Parse("2.5").Quantize(0).ToPlainString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData(".")]
    [InlineData("1e")]
    public void Text_that_is_not_a_decimal_is_a_format_error(string text)
    {
        Assert.Throws<FormatException>(() => ScourDecimal.Parse(text));
    }

    [Fact]
    public void A_double_becomes_the_decimal_of_its_shortest_spelling()
    {
        Assert.Equal(ScourDecimal.Parse("0.1"), ScourDecimal.FromDouble(0.1));
        Assert.Equal(ScourDecimal.Parse("30"), ScourDecimal.FromDouble(30.0));
    }

    [Fact]
    public void Exponents_outside_the_context_range_overflow_or_round_to_zero_without_huge_arithmetic()
    {
        Assert.Throws<OverflowException>(() => ScourDecimal.Parse("1e1000000").Plus(28));
        Assert.True(ScourDecimal.Parse("1e999999").Plus(28).Adjusted == 999_999);
        Assert.True(ScourDecimal.Parse("-1e-9999999").Plus(28).IsZero);
        Assert.True(ScourDecimal.Parse("-1e-9999999").Plus(28).SignedNegative);
        Assert.True((ScourDecimal.Parse("1e-9999999").Plus(28) + ScourDecimal.Parse("1")).Equals(1));
        Assert.True(ScourDecimal.Parse("0e9999999").Plus(28).IsZero);
    }

    [Theory]
    [InlineData("1.5", 1.5)]
    [InlineData(" -2e1 ", -20.0)]
    [InlineData(".5", 0.5)]
    public void Python_float_parses_numbers(string text, double expected)
    {
        Assert.Equal(expected, SvgLength.ParsePythonFloat(text));
    }

    [Fact]
    public void Python_float_accepts_nan_and_refuses_everything_else()
    {
        Assert.True(double.IsNaN(SvgLength.ParsePythonFloat("nan")));
        Assert.Throws<FormatException>(() => SvgLength.ParsePythonFloat("12px"));
        Assert.Throws<FormatException>(() => SvgLength.ParsePythonFloat(string.Empty));
    }
}
