using System.Globalization;
using System.Text.RegularExpressions;
using Rig.Image.Simplifier;

namespace Rig.Image.Pathways;

/// <summary>The kind of irregular edge.</summary>
public enum DefectKind
{
    /// <summary>A segment shorter than the minimum: tracer stepping.</summary>
    MicroSegment,

    /// <summary>An edge a little off its axis.</summary>
    OffAxis,

    /// <summary>A vertex that carries no direction change.</summary>
    Collinear,

    /// <summary>A run of short segments that alternate direction.</summary>
    Staircase,
}

/// <summary>One irregular edge, located well enough to find it in the file.</summary>
/// <param name="Kind">What is wrong.</param>
/// <param name="PathIndex">Which geometry element, in document order.</param>
/// <param name="SegmentIndex">Which segment of its ring.</param>
/// <param name="Start">Where the edge starts.</param>
/// <param name="End">Where the edge ends.</param>
/// <param name="Length">The edge's length.</param>
/// <param name="Angle">The edge's undirected direction in degrees.</param>
/// <param name="Deviation">Degrees away from the nearest canonical axis.</param>
public sealed record EdgeDefect(DefectKind Kind, int PathIndex, int SegmentIndex, Point Start, Point End, double Length, double Angle, double Deviation)
{
    /// <summary>The defect as one line, worded like image_drift's.</summary>
    public string Describe() =>
        $"path {PathIndex} seg {SegmentIndex}: {EdgeRegularity.KindName(Kind)} "
        + $"({PythonNumber.FormatFixed(Start.X, 0)},{PythonNumber.FormatFixed(Start.Y, 0)})->({PythonNumber.FormatFixed(End.X, 0)},{PythonNumber.FormatFixed(End.Y, 0)}) "
        + $"len={PythonNumber.FormatFixed(Length, 1)}px angle={PythonNumber.FormatFixed(Angle, 1)}° dev={EdgeRegularity.Signed(Deviation, 1)}°";
}

/// <summary>Edge-quality summary for one SVG document.</summary>
public sealed class EdgeReport
{
    /// <summary>Decimal places the published score is rounded to.</summary>
    public const int ConformancePrecision = 6;

    /// <summary>How many edges were examined.</summary>
    public int TotalEdges { get; set; }

    /// <summary>Their summed length.</summary>
    public double TotalLength { get; set; }

    /// <summary>The summed length of those on an axis.</summary>
    public double ConformingLength { get; set; }

    /// <summary>The defects found.</summary>
    public IReadOnlyList<EdgeDefect> Defects => _defects;

    private readonly List<EdgeDefect> _defects = [];

    /// <summary>Share of edge length lying on a canonical axis; higher is better.</summary>
    public double AxisConformancePct => TotalLength <= 0 ? 100.0 : PythonNumber.Round(100.0 * ConformingLength / TotalLength, ConformancePrecision);

    internal void Add(EdgeDefect defect) => _defects.Add(defect);

    /// <summary>How many defects are of <paramref name="kind"/>.</summary>
    public int Count(DefectKind kind) => Defects.Count(d => d.Kind == kind);
}

/// <summary>Detects and repairs jagged edges in an axis-constrained vector mark (image_drift's <c>edge_regularity</c>).</summary>
public static partial class EdgeRegularity
{
    /// <summary>The three isometric projection axes.</summary>
    public static readonly IReadOnlyList<double> IsometricAxes = [30.0, 90.0, 150.0];

    /// <summary>The full pointy-top hexagonal lattice, the default.</summary>
    public static readonly IReadOnlyList<double> HexagonalAxes = [0.0, 30.0, 60.0, 90.0, 120.0, 150.0];

    /// <summary>How far off-axis an edge may be and still be treated as jitter to snap.</summary>
    public const double DefaultAngleTol = 6.0;

    /// <summary>How far off-axis an edge may be and still count as conforming.</summary>
    public const double DefaultExactTol = 0.5;

    /// <summary>Segments shorter than this are tracer stepping.</summary>
    public const double DefaultMinSegment = 5.0;

    /// <summary>How far a turn may be from straight and be dropped as collinear when regularizing.</summary>
    public const double DefaultCollinearTol = 2.0;

    /// <summary>Decimal places the regularized coordinates are written with.</summary>
    public const int DefaultPrecision = 2;

    /// <summary>The kind as image_drift names it.</summary>
    public static string KindName(DefectKind kind) => kind switch
    {
        DefectKind.MicroSegment => "micro_segment",
        DefectKind.OffAxis => "off_axis",
        DefectKind.Collinear => "collinear",
        DefectKind.Staircase => "staircase",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "expected a defect kind"),
    };

    /// <summary>
    /// Python's <c>f"{value:.{places}f}"</c> without the sign on a value that rounds to zero. A ring rotated onto the
    /// axes leaves residues near 1e-17 whose sign comes from the platform's sin and cos (glibc and the Windows CRT differ
    /// in the last bit), so keeping Python's <c>-0.00</c> made the regularized SVG differ between Linux and Windows.
    /// </summary>
    internal static string Coordinate(double value, int places)
    {
        var text = PythonNumber.FormatFixed(value, places);
        return text.StartsWith('-') && text.AsSpan(1).IndexOfAnyExcept('0', '.') < 0 ? text[1..] : text;
    }

    /// <summary>Python's <c>f"{value:+.{places}f}"</c>.</summary>
    internal static string Signed(double value, int places)
    {
        var text = PythonNumber.FormatFixed(value, places);
        return text.StartsWith('-') ? text : "+" + text;
    }

    [GeneratedRegex("(<path\\b[^>]*\\bd=\")([^\"]*)(\")")]
    private static partial Regex PathPattern();

    [GeneratedRegex("(<polygon\\b[^>]*\\bpoints=\")([^\"]*)(\")")]
    private static partial Regex PolygonPattern();

    [GeneratedRegex("(-?[0-9.]+)[, ](-?[0-9.]+)")]
    private static partial Regex PointPattern();

    [GeneratedRegex("(?=[Mm])")]
    private static partial Regex SubpathSplit();

    [GeneratedRegex("[CcSsQqTtAa]")]
    private static partial Regex CurveCommand();

    /// <summary>Python's float modulo: the result takes the divisor's sign.</summary>
    private static double Mod(double value, double divisor)
    {
        var remainder = value % divisor;
        return remainder != 0 && (remainder < 0) != (divisor < 0) ? remainder + divisor : remainder;
    }

    private static double Length(Point a, Point b) => Math.Sqrt(((b.X - a.X) * (b.X - a.X)) + ((b.Y - a.Y) * (b.Y - a.Y)));

    private static double Angle(Point a, Point b) => Mod(HexGeometry.Degrees(Math.Atan2(b.Y - a.Y, b.X - a.X)), 180.0);

    private static (double Degrees, double Axis) Deviation(double angle, IReadOnlyList<double> axes)
    {
        var bestAxis = axes[0];
        var bestDeviation = 999.0;
        foreach (var axis in axes)
        {
            var raw = Mod(angle - axis + 90.0, 180.0) - 90.0;
            if (Math.Abs(raw) < Math.Abs(bestDeviation))
            {
                bestAxis = axis;
                bestDeviation = raw;
            }
        }

        return (bestDeviation, bestAxis);
    }

    /// <summary>Parses a <c>&lt;polygon points&gt;</c> attribute into one ring.</summary>
    public static IReadOnlyList<Point> ParsePoints(string points)
    {
        return [.. PointPattern().Matches(points).Select(m => new Point(
            double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
            double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture)))];
    }

    /// <summary>Splits a path's <c>d</c> into closed point rings.</summary>
    public static IReadOnlyList<IReadOnlyList<Point>> ParseSubpaths(string d)
    {
        return [.. SubpathSplit().Split(d).Select(ParsePoints).Where(ring => ring.Count >= 3)];
    }

    /// <summary>Whether a path uses curve commands, which this module does not handle.</summary>
    public static bool HasCurves(string d) => CurveCommand().IsMatch(d);

    private static List<(Match Match, bool IsPolygon)> GeometryMatches(string svg)
    {
        var found = PathPattern().Matches(svg).Select(m => (Match: m, IsPolygon: false))
            .Concat(PolygonPattern().Matches(svg).Select(m => (Match: m, IsPolygon: true)));
        return [.. found.OrderBy(g => g.Match.Index)];
    }

    /// <summary>Measures how well an SVG's edges conform to its canonical axes.</summary>
    public static EdgeReport Analyze(string svg, IReadOnlyList<double>? axes = null, double exactTol = DefaultExactTol, double angleTol = DefaultAngleTol, double minSegment = DefaultMinSegment)
    {
        var axisSet = axes ?? HexagonalAxes;
        var report = new EdgeReport();
        var pathIndex = -1;
        foreach (var (match, isPolygon) in GeometryMatches(svg))
        {
            pathIndex++;
            var d = match.Groups[2].Value;
            if (!isPolygon && HasCurves(d))
            {
                continue;
            }

            var rings = isPolygon ? [ParsePoints(d)] : ParseSubpaths(d);
            foreach (var ring in rings.Where(r => r.Count >= 3))
            {
                for (var segment = 0; segment < ring.Count; segment++)
                {
                    var a = ring[segment];
                    var b = ring[(segment + 1) % ring.Count];
                    var length = Length(a, b);
                    if (length <= 0)
                    {
                        continue;
                    }

                    var angle = Angle(a, b);
                    var deviation = Deviation(angle, axisSet).Degrees;
                    report.TotalEdges++;
                    report.TotalLength += length;
                    if (Math.Abs(deviation) <= exactTol)
                    {
                        report.ConformingLength += length;
                    }

                    DefectKind? kind = null;
                    if (length < minSegment)
                    {
                        kind = DefectKind.MicroSegment;
                    }
                    else if (exactTol < Math.Abs(deviation) && Math.Abs(deviation) <= angleTol)
                    {
                        kind = DefectKind.OffAxis;
                    }

                    if (kind is not null)
                    {
                        report.Add(new EdgeDefect(kind.Value, pathIndex, segment, a, b, length, angle, deviation));
                    }
                }

                var run = 0;
                for (var i = 0; i + 2 < ring.Count; i++)
                {
                    var a = ring[i];
                    var b = ring[i + 1];
                    var c = ring[i + 2];
                    var isShort = Length(a, b) < minSegment;
                    var turn = Math.Abs(Angle(a, b) - Angle(b, c));
                    if (isShort && Math.Min(turn, 180.0 - turn) > 20.0)
                    {
                        run++;
                        if (run >= 2)
                        {
                            report.Add(new EdgeDefect(
                                DefectKind.Staircase,
                                pathIndex,
                                FirstIndex(ring, a),
                                a,
                                c,
                                Length(a, c),
                                Angle(a, c),
                                Deviation(Angle(a, c), axisSet).Degrees));
                        }
                    }
                    else
                    {
                        run = 0;
                    }
                }
            }
        }

        return report;
    }

    /// <summary>Python's <c>list.index</c>: where the first equal point sits.</summary>
    private static int FirstIndex(IReadOnlyList<Point> ring, Point point)
    {
        var index = 0;
        while (!ring[index].Equals(point))
        {
            index++;
        }

        return index;
    }

    private static List<Point> DropMicroAndCollinear(IReadOnlyList<Point> ring, double minSegment, double collinearTol)
    {
        var points = new List<Point>(ring);
        var changed = true;
        while (changed && points.Count > 3)
        {
            changed = false;
            for (var i = 0; i < points.Count; i++)
            {
                var previous = points[(i - 1 + points.Count) % points.Count];
                var current = points[i];
                var next = points[(i + 1) % points.Count];
                if (Length(previous, current) < minSegment)
                {
                    points.RemoveAt(i);
                    changed = true;
                    break;
                }

                var turn = Math.Abs(Angle(previous, current) - Angle(current, next));
                if (Math.Min(turn, 180.0 - turn) < collinearTol)
                {
                    points.RemoveAt(i);
                    changed = true;
                    break;
                }
            }
        }

        return points;
    }

    private static Point? Intersect(Point p1, Point d1, Point p2, Point d2)
    {
        var det = (d1.X * -d2.Y) - (-d2.X * d1.Y);
        if (Math.Abs(det) < 1e-9)
        {
            return null;
        }

        var rx = p2.X - p1.X;
        var ry = p2.Y - p1.Y;
        var t = ((rx * -d2.Y) - (-d2.X * ry)) / det;
        return p1.Offset(d1, t);
    }

    /// <summary>Snaps a ring's near-axis edges onto their axes and rebuilds its corners.</summary>
    internal static IReadOnlyList<Point> RegularizeRing(IReadOnlyList<Point> ring, IReadOnlyList<double> axes, double angleTol, double minSegment, double collinearTol)
    {
        var points = DropMicroAndCollinear(ring, minSegment, collinearTol);
        if (points.Count < 3)
        {
            return ring;
        }

        var lines = new List<(Point Through, Point Direction)>();
        for (var i = 0; i < points.Count; i++)
        {
            var a = points[i];
            var b = points[(i + 1) % points.Count];
            var angle = Angle(a, b);
            var (degrees, axis) = Deviation(angle, axes);
            var use = Math.Abs(degrees) <= angleTol ? axis : angle;
            var radians = HexGeometry.Radians(use);
            lines.Add((new Point((a.X + b.X) / 2.0, (a.Y + b.Y) / 2.0), new Point(Math.Cos(radians), Math.Sin(radians))));
        }

        var result = new List<Point>(points.Count);
        for (var i = 0; i < points.Count; i++)
        {
            var previous = lines[(i - 1 + lines.Count) % lines.Count];
            var current = lines[i];
            result.Add(Intersect(previous.Through, previous.Direction, current.Through, current.Direction) ?? points[i]);
        }

        return result;
    }

    /// <summary>Returns <paramref name="svg"/> with its polygonal paths regularized onto the axes.</summary>
    public static string RegularizeSvg(string svg, IReadOnlyList<double>? axes = null, double angleTol = DefaultAngleTol, double minSegment = DefaultMinSegment, double collinearTol = DefaultCollinearTol, int precision = DefaultPrecision)
    {
        var axisSet = axes ?? HexagonalAxes;

        string Format(Point p) => $"{Coordinate(p.X, precision)},{Coordinate(p.Y, precision)}";

        string RewritePath(Match match)
        {
            var d = match.Groups[2].Value;
            if (HasCurves(d))
            {
                return match.Value;
            }

            var rings = ParseSubpaths(d).Where(r => r.Count >= 3).ToList();
            if (rings.Count == 0)
            {
                return match.Value;
            }

            var parts = rings.Select(ring => "M " + string.Join(" L ", RegularizeRing(ring, axisSet, angleTol, minSegment, collinearTol).Select(Format)) + " Z");
            return match.Groups[1].Value + string.Join(' ', parts) + match.Groups[3].Value;
        }

        string RewritePolygon(Match match)
        {
            var ring = ParsePoints(match.Groups[2].Value);
            if (ring.Count < 3)
            {
                return match.Value;
            }

            return match.Groups[1].Value + string.Join(' ', RegularizeRing(ring, axisSet, angleTol, minSegment, collinearTol).Select(Format)) + match.Groups[3].Value;
        }

        return PolygonPattern().Replace(PathPattern().Replace(svg, RewritePath), RewritePolygon);
    }
}
