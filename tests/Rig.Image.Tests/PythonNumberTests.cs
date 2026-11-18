using Rig.Image.Simplifier;

namespace Rig.Image.Tests;

public sealed class PythonNumberTests
{
    [Fact]
    public void Infinities_format_and_round_like_python()
    {
        Assert.Equal("inf", PythonNumber.FormatFixed(double.PositiveInfinity, 2));
        Assert.Equal("-inf", PythonNumber.FormatFixed(double.NegativeInfinity, 2));
        Assert.Equal(double.PositiveInfinity, PythonNumber.Round(double.PositiveInfinity, 2));
    }

    [Fact]
    public void Truncating_a_non_finite_number_overflows()
    {
        Assert.Throws<OverflowException>(() => PythonNumber.Truncate(double.NaN));
    }

    [Fact]
    public void Rounding_is_half_to_even_on_the_exact_value()
    {
        Assert.Equal(0.12, PythonNumber.Round(0.125, 2));
        Assert.Equal(2.67, PythonNumber.Round(2.675, 2));
    }
}
