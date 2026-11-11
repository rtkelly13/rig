using CsCheck;
using Rig.Core.Desktop;

namespace Rig.Core.Tests.Desktop;

/// <summary>
/// The rules every slot obeys for any screen and layout: one layout's slots tile their parent exactly, a split stays
/// inside its slot, pixels and shares agree when the screen divides evenly, coverage matches a cell count, and
/// similarity behaves like intersection over union. Replaces <c>tests/desktop_manager/test_placement_properties.py</c>.
/// </summary>
public sealed class GeometryPropertyTests
{
    private const int Grid = 12;

    // Every slot of one layout; two-thirds overlaps by design, so is left out.
    private static readonly Gen<PlacementLayout[]> Tilings = Gen.OneOf(
        Gen.Const(Tiling("full")),
        Gen.Const(Tiling("halves", "vertical")),
        Gen.Const(Tiling("halves", "horizontal")),
        Gen.Const(Tiling("thirds")),
        Gen.Const(Tiling("quarters")),
        Gen.Int[1, 8].Select(count => Tiling("columns", count: count)));

    private static readonly Gen<Rect> Screens = Gen.Select(Gen.Int[-8000, 8000], Gen.Int[-8000, 8000], Gen.Int[1, 8000], Gen.Int[1, 8000], (left, top, width, height) => new Rect(left, top, width, height));

    // Rectangles on a 12x12 grid of the unit square, so a brute-force cell count is exact.
    private static readonly Gen<UnitRect> GridRects = Gen.Select(Gen.Int[0, Grid - 1], Gen.Int[0, Grid - 1], Gen.Int[1, Grid], Gen.Int[1, Grid])
        .Where((x, y, w, h) => x + w <= Grid && y + h <= Grid)
        .Select((x, y, w, h) => new UnitRect(new(x, Grid), new(y, Grid), new(w, Grid), new(h, Grid)));

    [Fact]
    public void Every_layout_tiles_the_screen_exactly() =>
        Tilings.Sample(slots =>
        {
            var rects = slots.Select(slot => Geometry.NormalizedPlacementRect(new(1, slot))).ToList();

            Assert.Equal(new Coverage(Fraction.Zero, Fraction.Zero), Geometry.CoverageSummary(rects));
            Assert.Equal(Fraction.One, rects.Aggregate(Fraction.Zero, (sum, rect) => sum + Area(rect)));
        });

    [Fact]
    public void Splitting_a_slot_tiles_that_slot_exactly() =>
        Gen.Select(Tilings.SelectMany(slots => Gen.Int[0, slots.Length - 1].Select(index => slots[index])), Tilings).Sample((outer, inner) =>
        {
            var parent = Geometry.NormalizedPlacementRect(new(1, outer));
            var children = inner.Select(split => Geometry.NormalizedPlacementRect(new(1, outer, split))).ToList();

            Assert.All(children, child => Assert.True(Contains(parent, child)));
            Assert.Equal(new Coverage(Fraction.Zero, Fraction.Zero), Geometry.CoverageSummary([.. children.Select(child => Rescale(child, parent))]));
        });

    [Fact]
    public void Two_thirds_slots_cover_the_screen_and_share_the_middle_third()
    {
        var rects = Enumerable.Range(1, 2).Select(slot => Geometry.NormalizedPlacementRect(new(1, new("two-thirds", slot)))).ToList();

        Assert.Equal(new Coverage(Fraction.Zero, new Fraction(1, 3)), Geometry.CoverageSummary(rects));
    }

    [Fact]
    public void Pixel_slots_partition_the_screen_with_no_gap_or_overlap() =>
        Gen.Select(Screens, Tilings).Where((screen, slots) => screen.Width >= slots.Length && screen.Height >= slots.Length).Sample((screen, slots) =>
        {
            var rects = slots.Select(slot => Geometry.LayoutRect(screen, slot)).ToList();

            Assert.All(rects, rect => Assert.True(rect.Width > 0 && rect.Height > 0 && Contains(screen, rect)));
            Assert.Equal((long)screen.Width * screen.Height, rects.Sum(rect => (long)rect.Width * rect.Height));
            for (var i = 0; i < rects.Count; i++)
            {
                for (var j = i + 1; j < rects.Count; j++)
                {
                    Assert.Equal(0.0, Geometry.RectSimilarity(rects[i], rects[j]));
                }
            }
        });

    [Fact]
    public void Pixel_and_share_layouts_agree_when_the_screen_divides_evenly() =>
        Gen.Select(Tilings, Gen.Int[1, 40], Gen.Int[-4000, 4000]).Sample((slots, multiple, origin) =>
        {
            var side = Lcm(slots.Length, 2) * multiple;
            var screen = new Rect(origin, -origin, side, side);

            foreach (var slot in slots)
            {
                var unit = Geometry.NormalizedPlacementRect(new(1, slot));
                Assert.Equal(new Rect(origin + Scale(unit.Left, side), -origin + Scale(unit.Top, side), Scale(unit.Width, side), Scale(unit.Height, side)), Geometry.LayoutRect(screen, slot));
            }
        });

    [Fact]
    public void Coverage_matches_a_brute_force_cell_count() =>
        GridRects.List[0, 6].Sample(rects => Assert.Equal(CellCount(rects), Geometry.CoverageSummary(rects)));

    [Fact]
    public void Coverage_ignores_window_order() =>
        GridRects.List[1, 6].Sample(rects => Assert.Equal(Geometry.CoverageSummary(rects), Geometry.CoverageSummary([.. Enumerable.Reverse(rects)])));

    [Fact]
    public void Similarity_is_symmetric_bounded_and_one_only_for_the_same_rect() =>
        Gen.Select(Screens, Screens).Sample((a, b) =>
        {
            var score = Geometry.RectSimilarity(a, b);

            Assert.InRange(score, 0.0, 1.0);
            Assert.Equal(score, Geometry.RectSimilarity(b, a));
            Assert.Equal(1.0, Geometry.RectSimilarity(a, a));
            Assert.True(a == b || score < 1.0);
        });

    [Fact]
    public void Similarity_is_unchanged_by_moving_both_rects_together() =>
        Gen.Select(Screens, Screens, Gen.Int[-5000, 5000], Gen.Int[-5000, 5000]).Sample((a, b, dx, dy) =>
            Assert.Equal(
                Geometry.RectSimilarity(a, b),
                Geometry.RectSimilarity(a with { Left = a.Left + dx, Top = a.Top + dy }, b with { Left = b.Left + dx, Top = b.Top + dy }),
                12));

    [Fact]
    public void Rects_that_merely_touch_are_not_similar() =>
        Gen.Select(Screens, Gen.Int[0, 4000]).Sample((a, gap) => Assert.Equal(0.0, Geometry.RectSimilarity(a, a with { Left = a.Right + gap })));

    [Fact]
    public void Columns_are_the_most_that_fit_and_never_fewer_than_one() =>
        Gen.Select(Gen.Int[0, 20_000], Gen.Int[1, 5000].Nullable()).Sample((width, minWidth) =>
        {
            var columns = Geometry.ColumnsForWidth(width, minWidth);
            var floor = minWidth ?? Geometry.DefaultColumnMinWidth;

            Assert.True(columns >= 1);
            Assert.True(width < floor ? columns == 1 : columns * floor <= width && width < (columns + 1) * floor);
        });

    [Fact]
    public void Wider_screens_never_hold_fewer_columns() =>
        Gen.Select(Gen.Int[0, 20_000], Gen.Int[0, 20_000], Gen.Int[1, 5000]).Sample((a, b, minWidth) =>
            Assert.True(Geometry.ColumnsForWidth(Math.Min(a, b), minWidth) <= Geometry.ColumnsForWidth(Math.Max(a, b), minWidth)));

    private static PlacementLayout[] Tiling(string mode, string? orientation = null, int? count = null)
    {
        var slots = mode switch
        {
            "full" => 1,
            "halves" => 2,
            "thirds" => 3,
            "quarters" => 4,
            _ => count!.Value,
        };
        return [.. Enumerable.Range(1, slots).Select(slot => new PlacementLayout(mode, slot, orientation, Count: count))];
    }

    private static Fraction Area(UnitRect rect) => rect.Width * rect.Height;

    private static bool Contains(UnitRect outer, UnitRect inner) =>
        outer.Left <= inner.Left && outer.Top <= inner.Top && inner.Right <= outer.Right && inner.Bottom <= outer.Bottom;

    private static bool Contains(Rect outer, Rect inner) =>
        outer.Left <= inner.Left && outer.Top <= inner.Top && inner.Right <= outer.Right && inner.Bottom <= outer.Bottom;

    private static UnitRect Rescale(UnitRect rect, UnitRect parent) =>
        new((rect.Left - parent.Left) / parent.Width, (rect.Top - parent.Top) / parent.Height, rect.Width / parent.Width, rect.Height / parent.Height);

    private static int Scale(Fraction share, int side)
    {
        var scaled = share * side;
        Assert.Equal(1, scaled.Denominator);
        return (int)scaled.Numerator;
    }

    private static int Lcm(int a, int b)
    {
        int Gcd(int x, int y) => y == 0 ? x : Gcd(y, x % y);
        return a / Gcd(a, b) * b;
    }

    private static Coverage CellCount(List<UnitRect> rects)
    {
        int empty = 0, overlap = 0;
        for (var cx = 0; cx < Grid; cx++)
        {
            for (var cy = 0; cy < Grid; cy++)
            {
                Fraction x = new(cx, Grid), y = new(cy, Grid);
                var hits = rects.Count(rect => rect.Left <= x && x < rect.Right && rect.Top <= y && y < rect.Bottom);
                empty += hits == 0 ? 1 : 0;
                overlap += hits > 1 ? 1 : 0;
            }
        }

        return new(new(empty, Grid * Grid), new(overlap, Grid * Grid));
    }
}
