using Rig.Image.Pathways;

namespace Rig.Image.Tests;

/// <summary>Rules of the regularizer that the Python oracle does not share.</summary>
public sealed class EdgeRegularityTests
{
    [Theory]
    [InlineData(-1e-17, 2, "0.00")]
    [InlineData(1e-17, 2, "0.00")]
    [InlineData(-0.004, 2, "0.00")]
    [InlineData(-0.4, 0, "0")]
    [InlineData(-0.0, 2, "0.00")]
    [InlineData(-0.006, 2, "-0.01")]
    [InlineData(-1.5, 2, "-1.50")]
    public void A_coordinate_that_rounds_to_zero_carries_no_sign(double value, int places, string expected)
    {
        Assert.Equal(expected, EdgeRegularity.Coordinate(value, places));
    }

    [Fact]
    public void A_ring_point_left_a_hair_below_zero_by_the_rotation_is_written_as_zero()
    {
        var regularized = EdgeRegularity.RegularizeSvg("<svg><path d=\"M -0.000000000000001,0 L 10,0 L 10,10 Z\" /></svg>");

        Assert.Equal("<svg><path d=\"M 0.00,0.00 L 10.00,0.00 L 10.00,10.00 Z\" /></svg>", regularized);
    }
}
