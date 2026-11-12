using Rig.Core.Desktop;

namespace Rig.Core.Tests.Desktop;

/// <summary>
/// Where each slot sits, as a share of the screen and in pixels, and which slot a window fills. Replaces the geometry
/// half of <c>tests/desktop_manager/test_placement_pure.py</c>.
/// </summary>
public sealed class GeometryTests
{
    private static readonly DisplayArea LeftScreen = new(1, new Rect(0, 0, 300, 120));
    private static readonly DisplayArea RightScreen = new(2, new Rect(300, 0, 400, 200));

    /// <summary>A placement in each mode and the share of the screen Python gives it.</summary>
    public static TheoryData<PlacementSpec, UnitRect> UnitSlots => new()
    {
        { Spec(new("full")), U(0, 1, 0, 1, 1, 1, 1, 1) },
        { Spec(new("halves", 1, "vertical")), U(0, 1, 0, 1, 1, 2, 1, 1) },
        { Spec(new("halves", 2, "vertical")), U(1, 2, 0, 1, 1, 2, 1, 1) },
        { Spec(new("halves", 2)), U(1, 2, 0, 1, 1, 2, 1, 1) },
        { Spec(new("halves", 2, "horizontal")), U(0, 1, 1, 2, 1, 1, 1, 2) },
        { Spec(new("thirds", 1)), U(0, 1, 0, 1, 1, 3, 1, 1) },
        { Spec(new("thirds", 3)), U(2, 3, 0, 1, 1, 3, 1, 1) },
        { Spec(new("two-thirds", 1)), U(0, 1, 0, 1, 2, 3, 1, 1) },
        { Spec(new("two-thirds", 2)), U(1, 3, 0, 1, 2, 3, 1, 1) },
        { Spec(new("quarters", 1)), U(0, 1, 0, 1, 1, 2, 1, 2) },
        { Spec(new("quarters", 2)), U(1, 2, 0, 1, 1, 2, 1, 2) },
        { Spec(new("quarters", 3)), U(1, 2, 1, 2, 1, 2, 1, 2) },
        { Spec(new("quarters", 4)), U(0, 1, 1, 2, 1, 2, 1, 2) },
        { Spec(new("columns", 2, Count: 4)), U(1, 4, 0, 1, 1, 4, 1, 1) },
        { Spec(new("columns", 3)), U(2, 3, 0, 1, 1, 3, 1, 1) },
        { Spec(new("halves", 1, "vertical"), new("halves", 2, "vertical")), U(1, 4, 0, 1, 1, 4, 1, 1) },
    };

    /// <summary>A placement on the right screen and the pixels Python gives it.</summary>
    public static TheoryData<PlacementSpec, Rect> PixelSlots => new()
    {
        { Spec(new("full"), screen: 2), new(300, 0, 400, 200) },
        { Spec(new("thirds", 3), screen: 2), new(566, 0, 134, 200) },
        { Spec(new("halves", 2, "vertical"), new("halves", 1, "horizontal"), 2), new(500, 0, 200, 100) },
        { Spec(new("two-thirds", 1)), new(300, 0, 266, 200) },
        { Spec(new("two-thirds", 2)), new(433, 0, 267, 200) },
        { Spec(new("quarters", 3)), new(500, 100, 200, 100) },
        { Spec(new("quarters", 4)), new(300, 100, 200, 100) },
    };

    /// <summary>A placement with a slot its mode does not have, and Python's error.</summary>
    public static TheoryData<PlacementLayout, string> BadSlots => new()
    {
        { new("thirds", 4), "Horizontal fraction index out of range: 4/3" },
        { new("halves", 0, "horizontal"), "Vertical fraction index out of range: 0/2" },
        { new("halves", 1, "diagonal"), "Unknown halves orientation: diagonal" },
        { new("two-thirds", 3), "Two-thirds index out of range: 3" },
        { new("quarters", 5), "Quarter index out of range: 5" },
        { new("bogus"), "Unknown layout mode: bogus" },
    };

    [Theory]
    [MemberData(nameof(UnitSlots))]
    public void Each_slot_takes_its_share_of_the_screen(PlacementSpec placement, UnitRect expected) =>
        Assert.Equal(expected, Geometry.NormalizedPlacementRect(placement));

    [Theory]
    [MemberData(nameof(PixelSlots))]
    public void Each_slot_is_laid_out_in_whole_pixels_on_the_given_screen(PlacementSpec placement, Rect expected) =>
        Assert.Equal(expected, Geometry.PlacementRectIn(RightScreen.Bounds, placement));

    [Theory]
    [MemberData(nameof(BadSlots))]
    public void A_slot_the_mode_does_not_have_is_an_error(PlacementLayout layout, string message)
    {
        Assert.Equal(message, Assert.Throws<ArgumentException>(() => Geometry.NormalizedPlacementRect(Spec(layout))).Message);
        Assert.Equal(message, Assert.Throws<ArgumentException>(() => Geometry.PlacementRectIn(RightScreen.Bounds, Spec(layout))).Message);
    }

    [Fact]
    public void The_last_slot_takes_the_pixels_left_over()
    {
        Assert.Equal(new Rect(666, 0, 335, 10), Geometry.LayoutRect(new(0, 0, 1001, 10), new("thirds", 3)));
        Assert.Equal(new Rect(333, 0, 668, 10), Geometry.LayoutRect(new(0, 0, 1001, 10), new("two-thirds", 2)));
        Assert.Equal(new Rect(1706, 0, 854, 1392), Geometry.LayoutRect(new(0, 0, 2560, 1392), new("columns", 3, MinWidth: 800)));
        Assert.Equal(new Rect(0, 400, 10, 400), Geometry.LayoutRect(new(0, 0, 10, 800), new("halves", 2, "horizontal")));
    }

    [Fact]
    public void A_parent_narrower_than_one_pixel_a_slot_gives_the_last_slot_a_negative_width_as_python_does() =>
        Assert.Equal(-1, Geometry.LayoutRect(new(0, 0, 1, 1), new("thirds", 3)).Width);

    [Fact]
    public void Coverage_counts_the_empty_and_doubly_covered_share()
    {
        var third = new Fraction(1, 3);
        Assert.Equal(new Coverage(Fraction.Zero, Fraction.Zero), Geometry.CoverageSummary([UnitRect.Full]));
        Assert.Equal(new Coverage(Fraction.One, Fraction.Zero), Geometry.CoverageSummary([]));
        Assert.Equal(new Coverage(Fraction.Zero, Fraction.One), Geometry.CoverageSummary([UnitRect.Full, UnitRect.Full]));
        Assert.Equal(
            new Coverage(third, Fraction.Zero),
            Geometry.CoverageSummary([new(Fraction.Zero, Fraction.Zero, third, Fraction.One), new(third * 2, Fraction.Zero, third, Fraction.One)]));
    }

    [Fact]
    public void Similarity_is_intersection_over_union()
    {
        var square = new Rect(0, 0, 100, 100);

        Assert.Equal(1.0, Geometry.RectSimilarity(square, square));
        Assert.Equal(0.0, Geometry.RectSimilarity(square, square with { Left = 200 }));
        Assert.Equal(1.0 / 3, Geometry.RectSimilarity(square, square with { Left = 50 }), 12);
        Assert.Equal(0.0, Geometry.RectSimilarity(new(0, 0, 0, 100), new(0, 0, 0, 100)));
    }

    [Fact]
    public void Columns_are_as_many_as_fit_and_at_least_one()
    {
        Assert.Equal(3, Geometry.ColumnsForWidth(2560, 800));
        Assert.Equal(2, Geometry.ColumnsForWidth(1920, 800));
        Assert.Equal(1, Geometry.ColumnsForWidth(500, 800));
        Assert.Equal(3, Geometry.ColumnsForWidth(2560, null));
        Assert.Equal(3, Geometry.ColumnsForWidth(2560, 0));
    }

    [Fact]
    public void The_candidates_are_every_slot_of_the_fixed_modes_and_every_half_split()
    {
        Assert.Equal(["full", "halves", "thirds", "two-thirds", "quarters"], Geometry.CandidateLayouts.Select(layout => layout.Mode).Distinct());
        Assert.Equal(14, Geometry.CandidateLayouts.Count);
        Assert.Equal(
            [("horizontal", 1), ("horizontal", 2), ("vertical", 1), ("vertical", 2)],
            Geometry.CandidateSubdivideLayouts.Select(layout => (layout.Orientation, layout.Slot)));
    }

    [Fact]
    public void A_window_is_placed_in_the_slot_it_fills()
    {
        Assert.Equal(new PlacementSpec(1, new("thirds", 2)), Geometry.InferPlacement(new(100, 0, 100, 120), [LeftScreen, RightScreen]));
        Assert.Equal(new PlacementSpec(2, new("full")), Geometry.InferPlacement(new(300, 0, 400, 200), [LeftScreen, RightScreen]));
    }

    [Fact]
    public void A_half_of_a_half_ties_with_a_quarter_and_the_plainer_quarter_wins() =>
        Assert.Equal(new PlacementSpec(2, new("quarters", 3)), Geometry.InferPlacement(new(500, 100, 200, 100), [LeftScreen, RightScreen]));

    [Fact]
    public void A_split_slot_is_inferred_when_no_whole_slot_fits()
    {
        var screen = new DisplayArea(1, new Rect(0, 0, 1000, 600));

        Assert.Equal(
            new PlacementSpec(1, new("quarters", 1), new("halves", 1, "vertical")),
            Geometry.InferPlacement(new(0, 0, 250, 300), [screen]));
    }

    [Fact]
    public void A_window_that_fills_no_slot_well_enough_is_not_placed()
    {
        Assert.Null(Geometry.InferPlacement(new(10, 10, 20, 20), [LeftScreen, RightScreen]));
        Assert.Null(Geometry.InferPlacement(new(5000, 5000, 20, 20), [LeftScreen]));
        Assert.Null(Geometry.InferPlacement(new(0, 0, 20, 20), []));
    }

    private static PlacementSpec Spec(PlacementLayout layout, PlacementLayout? subdivide = null, int screen = 1) => new(screen, layout, subdivide);

    private static UnitRect U(long leftN, long leftD, long topN, long topD, long widthN, long widthD, long heightN, long heightD) =>
        new(new(leftN, leftD), new(topN, topD), new(widthN, widthD), new(heightN, heightD));
}
