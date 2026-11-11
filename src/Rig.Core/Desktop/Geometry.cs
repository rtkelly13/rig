namespace Rig.Core.Desktop;

/// <summary>A live screen: its number and its work area. Ported from <c>desktop_manager.models.DisplayArea</c>.</summary>
/// <param name="ScreenNumber">The screen, numbered from 1.</param>
/// <param name="Bounds">The screen's work area in pixels.</param>
public sealed record DisplayArea(int ScreenNumber, Rect Bounds);

/// <summary>A placement as fractions of its screen's work area. Ported from <c>desktop_manager.geometry.UnitRect</c>.</summary>
/// <param name="Left">The left edge, from 0 to 1.</param>
/// <param name="Top">The top edge, from 0 to 1.</param>
/// <param name="Width">The width, as a fraction of the screen's.</param>
/// <param name="Height">The height, as a fraction of the screen's.</param>
public sealed record UnitRect(Fraction Left, Fraction Top, Fraction Width, Fraction Height)
{
    /// <summary>The whole screen.</summary>
    public static UnitRect Full { get; } = new(Fraction.Zero, Fraction.Zero, Fraction.One, Fraction.One);

    /// <summary>The right edge.</summary>
    public Fraction Right => Left + Width;

    /// <summary>The bottom edge.</summary>
    public Fraction Bottom => Top + Height;
}

/// <summary>How much of a screen no window covers, and how much more than one does, as fractions of the screen.</summary>
/// <param name="Empty">The area no window covers.</param>
/// <param name="Overlap">The area two or more windows cover.</param>
public sealed record Coverage(Fraction Empty, Fraction Overlap);

/// <summary>
/// Placement geometry: where a slot sits on a screen, and how well a window fills it. Pure functions over
/// <see cref="Rect"/> and fractions of a screen; the caller reads the monitors and windows. Ported from
/// <c>desktop_manager.geometry</c>.
/// </summary>
public static class Geometry
{
    /// <summary>The narrowest a column is when its layout does not say.</summary>
    public const int DefaultColumnMinWidth = 800;

    /// <summary>How closely a window must fill a slot before it counts as placed there.</summary>
    public const double InferredPlacementSimilarity = 0.82;

    // Quarter slots run clockwise from the top left: (column, row) for slots 1 to 4.
    private static readonly (int Column, int Row)[] Quarters = [(1, 1), (2, 1), (2, 2), (1, 2)];

    /// <summary>The layouts a window's position is matched against: every slot of every fixed mode.</summary>
    public static IReadOnlyList<PlacementLayout> CandidateLayouts { get; } =
    [
        new("full"),
        new("halves", 1, "vertical"),
        new("halves", 2, "vertical"),
        new("halves", 1, "horizontal"),
        new("halves", 2, "horizontal"),
        new("thirds", 1),
        new("thirds", 2),
        new("thirds", 3),
        new("two-thirds", 1),
        new("two-thirds", 2),
        new("quarters", 1),
        new("quarters", 2),
        new("quarters", 3),
        new("quarters", 4),
    ];

    /// <summary>The splits of a slot a window's position is matched against.</summary>
    public static IReadOnlyList<PlacementLayout> CandidateSubdivideLayouts { get; } =
    [
        new("halves", 1, "horizontal"),
        new("halves", 2, "horizontal"),
        new("halves", 1, "vertical"),
        new("halves", 2, "vertical"),
    ];

    /// <summary>The share of the screen <paramref name="placement"/> takes up.</summary>
    /// <exception cref="ArgumentException">The placement names an unknown mode, orientation or slot.</exception>
    public static UnitRect NormalizedPlacementRect(PlacementSpec placement)
    {
        ArgumentNullException.ThrowIfNull(placement);
        var rect = UnitLayout(UnitRect.Full, placement.Layout);
        return placement.Subdivide is null ? rect : UnitLayout(rect, placement.Subdivide);
    }

    /// <summary>How much of the unit screen <paramref name="rects"/> leave empty, and how much two or more of them share.</summary>
    public static Coverage CoverageSummary(IReadOnlyCollection<UnitRect> rects)
    {
        ArgumentNullException.ThrowIfNull(rects);
        var xs = new SortedSet<Fraction> { Fraction.Zero, Fraction.One };
        var ys = new SortedSet<Fraction> { Fraction.Zero, Fraction.One };
        foreach (var rect in rects)
        {
            _ = xs.Add(rect.Left);
            _ = xs.Add(rect.Right);
            _ = ys.Add(rect.Top);
            _ = ys.Add(rect.Bottom);
        }

        var empty = Fraction.Zero;
        var overlap = Fraction.Zero;
        foreach (var (x0, x1) in xs.Zip(xs.Skip(1)))
        {
            foreach (var (y0, y1) in ys.Zip(ys.Skip(1)))
            {
                var area = (x1 - x0) * (y1 - y0);
                var covering = rects.Count(rect => rect.Left <= x0 && rect.Top <= y0 && rect.Right >= x1 && rect.Bottom >= y1);
                if (covering == 0)
                {
                    empty += area;
                }
                else if (covering > 1)
                {
                    overlap += area;
                }
            }
        }

        return new(empty, overlap);
    }

    /// <summary>The pixel rectangle <paramref name="placement"/> takes up on a screen whose work area is <paramref name="screen"/>.</summary>
    /// <exception cref="ArgumentException">The placement names an unknown mode, orientation or slot.</exception>
    public static Rect PlacementRectIn(Rect screen, PlacementSpec placement)
    {
        ArgumentNullException.ThrowIfNull(placement);
        var rect = LayoutRect(screen, placement.Layout);
        return placement.Subdivide is null ? rect : LayoutRect(rect, placement.Subdivide);
    }

    /// <summary>
    /// The pixel rectangle <paramref name="layout"/> takes up within <paramref name="parent"/>. Slots are whole pixels:
    /// each is the parent's size divided down, and the last slot takes whatever is left over.
    /// </summary>
    /// <exception cref="ArgumentException">The layout names an unknown mode, orientation or slot.</exception>
    public static Rect LayoutRect(Rect parent, PlacementLayout layout)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(layout);
        switch (layout.Mode)
        {
            case "full":
                return parent;
            case "halves" when Orientation(layout) == "vertical":
                return WithColumns(parent, HorizontalFraction(parent.Left, parent.Width, 2, layout.Slot));
            case "halves":
                return WithRows(parent, VerticalFraction(parent.Top, parent.Height, 2, layout.Slot));
            case "thirds":
                return WithColumns(parent, HorizontalFraction(parent.Left, parent.Width, 3, layout.Slot));
            case "two-thirds":
                return WithColumns(parent, HorizontalTwoThirds(parent.Left, parent.Width, layout.Slot));
            case "quarters":
                var (column, row) = Quarter(layout.Slot);
                var columns = HorizontalFraction(parent.Left, parent.Width, 2, column);
                var rows = VerticalFraction(parent.Top, parent.Height, 2, row);
                return new(columns.Start, rows.Start, columns.Length, rows.Length);
            case "columns":
                var count = layout.Count is { } fixedCount and not 0 ? fixedCount : ColumnsForWidth(parent.Width, layout.MinWidth);
                return WithColumns(parent, HorizontalFraction(parent.Left, parent.Width, Math.Max(count, layout.Slot), layout.Slot));
            default:
                throw UnknownMode(layout);
        }
    }

    /// <summary>
    /// The placement a window at <paramref name="rect"/> most closely fills on any of <paramref name="screens"/>, or
    /// <see langword="null"/> when none is at least <see cref="InferredPlacementSimilarity"/> similar. A tie goes to the
    /// plainer placement: a whole slot before a split of one, and the earlier screen and candidate.
    /// </summary>
    public static PlacementSpec? InferPlacement(Rect rect, IEnumerable<DisplayArea> screens)
    {
        ArgumentNullException.ThrowIfNull(rect);
        ArgumentNullException.ThrowIfNull(screens);
        PlacementSpec? best = null;
        var bestScore = 0.0;
        foreach (var screen in screens)
        {
            if (BestLayout(rect, screen.Bounds) is not { } match)
            {
                continue;
            }

            var (layout, score) = match;
            if (score > bestScore)
            {
                (best, bestScore) = (new(screen.ScreenNumber, layout), score);
            }

            var slot = LayoutRect(screen.Bounds, layout);
            foreach (var subdivide in CandidateSubdivideLayouts)
            {
                var subdivided = RectSimilarity(rect, LayoutRect(slot, subdivide));
                if (subdivided > bestScore)
                {
                    (best, bestScore) = (new(screen.ScreenNumber, layout, subdivide), subdivided);
                }
            }
        }

        return bestScore >= InferredPlacementSimilarity ? best : null;
    }

    /// <summary>
    /// The intersection over union of two rectangles: 1 for the same rectangle, 0 for ones that do not overlap or
    /// only touch.
    /// </summary>
    public static double RectSimilarity(Rect left, Rect right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        long interLeft = Math.Max(left.Left, right.Left);
        long interTop = Math.Max(left.Top, right.Top);
        long interRight = Math.Min(left.Right, right.Right);
        long interBottom = Math.Min(left.Bottom, right.Bottom);
        if (interRight <= interLeft || interBottom <= interTop)
        {
            return 0.0;
        }

        var intersection = (interRight - interLeft) * (interBottom - interTop);
        var union = ((long)left.Width * left.Height) + ((long)right.Width * right.Height) - intersection;
        return union <= 0 ? 0.0 : (double)intersection / union;
    }

    /// <summary>How many columns at least <paramref name="minWidth"/> pixels wide fit across <paramref name="width"/>; always at least one.</summary>
    public static int ColumnsForWidth(int width, int? minWidth) =>
        Math.Max(1, FloorDiv(width, minWidth is { } min and not 0 ? min : DefaultColumnMinWidth));

    private static (PlacementLayout Layout, double Score)? BestLayout(Rect rect, Rect screen)
    {
        PlacementLayout? best = null;
        var bestScore = 0.0;
        foreach (var layout in CandidateLayouts)
        {
            var score = RectSimilarity(rect, LayoutRect(screen, layout));
            if (score > bestScore)
            {
                (best, bestScore) = (layout, score);
            }
        }

        return best is null ? null : (best, bestScore);
    }

    private static UnitRect UnitLayout(UnitRect rect, PlacementLayout layout)
    {
        switch (layout.Mode)
        {
            case "full":
                return rect;
            case "halves" when Orientation(layout) == "vertical":
                return UnitColumns(rect, UnitSlice(rect.Left, rect.Width, 2, layout.Slot, "Horizontal"));
            case "halves":
                return UnitRows(rect, UnitSlice(rect.Top, rect.Height, 2, layout.Slot, "Vertical"));
            case "thirds":
                return UnitColumns(rect, UnitSlice(rect.Left, rect.Width, 3, layout.Slot, "Horizontal"));
            case "two-thirds":
                var third = rect.Width / 3;
                return layout.Slot switch
                {
                    1 => rect with { Width = third * 2 },
                    2 => rect with { Left = rect.Left + third, Width = third * 2 },
                    _ => throw TwoThirdsOutOfRange(layout.Slot),
                };
            case "quarters":
                var (column, row) = Quarter(layout.Slot);
                return UnitRows(UnitColumns(rect, UnitSlice(rect.Left, rect.Width, 2, column, "Horizontal")), UnitSlice(rect.Top, rect.Height, 2, row, "Vertical"));
            case "columns":
                var count = Math.Max(layout.Count ?? 0, layout.Slot);
                return UnitColumns(rect, UnitSlice(rect.Left, rect.Width, count, layout.Slot, "Horizontal"));
            default:
                throw UnknownMode(layout);
        }
    }

    private static (Fraction Start, Fraction Length) UnitSlice(Fraction start, Fraction length, int denominator, int index, string axis)
    {
        if (index < 1 || index > denominator)
        {
            throw new ArgumentException($"{axis} fraction index out of range: {index}/{denominator}");
        }

        var slice = length / denominator;
        return (start + (slice * (index - 1)), slice);
    }

    private static UnitRect UnitColumns(UnitRect rect, (Fraction Start, Fraction Length) span) => rect with { Left = span.Start, Width = span.Length };

    private static UnitRect UnitRows(UnitRect rect, (Fraction Start, Fraction Length) span) => rect with { Top = span.Start, Height = span.Length };

    private static (int Start, int Length) HorizontalFraction(int left, int width, int denominator, int index) =>
        PixelSlice(left, width, denominator, index, "Horizontal");

    private static (int Start, int Length) VerticalFraction(int top, int height, int denominator, int index) =>
        PixelSlice(top, height, denominator, index, "Vertical");

    // A parent narrower than one pixel per slot gives the last slot a negative length, as Python's does: a known defect
    // kept so the port changes no placement.
    private static (int Start, int Length) PixelSlice(int start, int length, int denominator, int index, string axis)
    {
        if (index < 1 || index > denominator)
        {
            throw new ArgumentException($"{axis} fraction index out of range: {index}/{denominator}");
        }

        var slice = Math.Max(1, FloorDiv(length, denominator));
        var position = start + (slice * (index - 1));
        return index == denominator ? (position, length - (slice * (denominator - 1))) : (position, slice);
    }

    private static (int Start, int Length) HorizontalTwoThirds(int left, int width, int index)
    {
        var third = Math.Max(1, FloorDiv(width, 3));
        return index switch
        {
            1 => (left, third * 2),
            2 => (left + third, width - third),
            _ => throw TwoThirdsOutOfRange(index),
        };
    }

    private static Rect WithColumns(Rect rect, (int Start, int Length) span) => rect with { Left = span.Start, Width = span.Length };

    private static Rect WithRows(Rect rect, (int Start, int Length) span) => rect with { Top = span.Start, Height = span.Length };

    private static (int Column, int Row) Quarter(int slot) =>
        slot is >= 1 and <= 4 ? Quarters[slot - 1] : throw new ArgumentException($"Quarter index out of range: {slot}");

    private static string Orientation(PlacementLayout layout)
    {
        var orientation = string.IsNullOrEmpty(layout.Orientation) ? "vertical" : layout.Orientation;
        return orientation is "vertical" or "horizontal"
            ? orientation
            : throw new ArgumentException($"Unknown halves orientation: {orientation}");
    }

    private static ArgumentException TwoThirdsOutOfRange(int slot) => new($"Two-thirds index out of range: {slot}");

    private static ArgumentException UnknownMode(PlacementLayout layout) => new($"Unknown layout mode: {layout.Mode}");

    // Python's floor division, which rounds toward negative infinity where C#'s rounds toward zero.
    private static int FloorDiv(int value, int divisor) => (int)Math.Floor((double)value / divisor);
}
