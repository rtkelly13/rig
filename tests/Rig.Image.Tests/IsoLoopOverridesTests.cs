using Rig.Image.Pathways;

namespace Rig.Image.Tests;

public sealed class IsoLoopOverridesTests
{
    [Fact]
    public void Every_numeric_parameter_is_overridable_by_name()
    {
        var overrides = new Dictionary<string, string>
        {
            ["canvas"] = "2048",
            ["background"] = "#000000",
            ["center"] = "1,2",
            ["loop_center"] = "3,4",
            ["r_outer"] = "10",
            ["r_mid"] = "9",
            ["r_inner"] = "8",
            ["depth"] = "7",
            ["gap"] = "6",
            ["beam_length"] = "5",
            ["beam_half_width"] = "4",
        };

        var result = new IsoLoopParams().WithOverrides(overrides);

        Assert.Equal(2048, result.Canvas);
        Assert.Equal("#000000", result.Background);
        Assert.Equal(new Point(1, 2), result.Center);
        Assert.Equal(new Point(3, 4), result.LoopCenter);
        Assert.Equal([10.0, 9.0, 8.0, 7.0, 6.0, 5.0, 4.0], [result.ROuter, result.RMid, result.RInner, result.Depth, result.Gap, result.BeamLength, result.BeamHalfWidth]);
    }

    [Fact]
    public void Lighting_cannot_be_overridden_from_text()
    {
        var exception = Assert.Throws<ParameterOverrideException>(() => new IsoLoopParams().WithOverrides(new Dictionary<string, string> { ["lighting"] = "x" }));

        Assert.Contains("colour scheme", exception.Message, StringComparison.Ordinal);
    }
}
