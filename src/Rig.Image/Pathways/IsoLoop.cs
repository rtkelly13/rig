using System.Globalization;
using System.Text;
using Rig.Image.Simplifier;

namespace Rig.Image.Pathways;

/// <summary>Which colour ramp a facet takes.</summary>
public enum Orientation
{
    /// <summary>A face the light hits.</summary>
    Top,

    /// <summary>A face turned left.</summary>
    Left,

    /// <summary>A face turned right.</summary>
    Right,

    /// <summary>A loop's lit interior lip.</summary>
    Inner,
}

/// <summary>An ordered colour ramp applied along a facet's lighting vector.</summary>
/// <param name="Stops">The colours, first to last.</param>
public sealed record GradientRamp(IReadOnlyList<string> Stops)
{
    /// <summary>Stop offsets evenly spaced over 0 to 1, rounded to four places.</summary>
    public IReadOnlyList<double> Offsets()
    {
        if (Stops.Count == 1)
        {
            return [0.0];
        }

        var step = 1.0 / (Stops.Count - 1);
        return [.. Enumerable.Range(0, Stops.Count).Select(i => PythonNumber.Round(i * step, 4))];
    }
}

/// <summary>Per-orientation colour ramps and the direction the light comes from.</summary>
/// <param name="Top">Ramp for top faces.</param>
/// <param name="Left">Ramp for left faces.</param>
/// <param name="Right">Ramp for right faces.</param>
/// <param name="Inner">Ramp for inner faces.</param>
/// <param name="LightDirectionDeg">Direction the light comes from, in degrees.</param>
public sealed record FaceLighting(GradientRamp Top, GradientRamp Left, GradientRamp Right, GradientRamp Inner, double LightDirectionDeg = -60.0)
{
    /// <summary>The ramp for a facet of this orientation.</summary>
    public GradientRamp RampFor(Orientation orientation) => orientation switch
    {
        Orientation.Top => Top,
        Orientation.Left => Left,
        Orientation.Right => Right,
        Orientation.Inner => Inner,
        _ => throw new ArgumentOutOfRangeException(nameof(orientation), orientation, "expected a facet orientation"),
    };
}

/// <summary>The parameter a fitting step names does not exist, or a value does not parse.</summary>
public sealed class ParameterOverrideException : ArgumentException
{
    /// <summary>Creates the exception.</summary>
    public ParameterOverrideException()
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">Which parameter and why.</param>
    public ParameterOverrideException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and its cause.</summary>
    /// <param name="message">Which parameter and why.</param>
    /// <param name="innerException">The cause.</param>
    public ParameterOverrideException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Named geometry for the interlocking isometric loops and crossover beams (the original <c>IsoLoopParams</c>).</summary>
public sealed record IsoLoopParams
{
    /// <summary>The default colour ramps.</summary>
    public static readonly FaceLighting DefaultLighting = new(
        new GradientRamp(["#5de7e9", "#35c8e1", "#1b8ec8"]),
        new GradientRamp(["#14b6c3", "#198ec3", "#1e6dc1"]),
        new GradientRamp(["#2069bd", "#1c54ac", "#173f96"]),
        new GradientRamp(["#147eb6", "#1b6ab8", "#244aa4"]));

    /// <summary>Canvas width and height.</summary>
    public int Canvas { get; init; } = 1024;

    /// <summary>The centre of the mark.</summary>
    public Point Center { get; init; } = new(512.0, 512.0);

    /// <summary>The first loop's centre; the second is its half turn about <see cref="Center"/>.</summary>
    public Point LoopCenter { get; init; } = new(440.0, 440.0);

    /// <summary>Outer ring radius.</summary>
    public double ROuter { get; init; } = 158.0;

    /// <summary>Middle ring radius.</summary>
    public double RMid { get; init; } = 114.0;

    /// <summary>Inner ring radius.</summary>
    public double RInner { get; init; } = 72.0;

    /// <summary>Prism depth.</summary>
    public double Depth { get; init; } = 46.0;

    /// <summary>The gap the under beam leaves around the over beam.</summary>
    public double Gap { get; init; } = 6.0;

    /// <summary>Beam length.</summary>
    public double BeamLength { get; init; } = 420.0;

    /// <summary>Half the beam width.</summary>
    public double BeamHalfWidth { get; init; } = 36.0;

    /// <summary>The background colour.</summary>
    public string Background { get; init; } = "#101923";

    /// <summary>The lighting.</summary>
    public FaceLighting Lighting { get; init; } = DefaultLighting;

    /// <summary>The parameter names, Python's field order.</summary>
    public static IReadOnlyList<string> Names { get; } = ["canvas", "center", "loop_center", "r_outer", "r_mid", "r_inner", "depth", "gap", "beam_length", "beam_half_width", "background", "lighting"];

    /// <summary>A copy with <paramref name="overrides"/> applied, each value coerced to its field's type.</summary>
    internal IsoLoopParams WithOverrides(IReadOnlyDictionary<string, string> overrides)
    {
        var result = this;
        foreach (var (key, raw) in overrides)
        {
            result = key switch
            {
                "canvas" => result with { Canvas = ParseInt(key, raw) },
                "background" => result with { Background = raw },
                "center" => result with { Center = ParsePoint(key, raw) },
                "loop_center" => result with { LoopCenter = ParsePoint(key, raw) },
                "r_outer" => result with { ROuter = ParseNumber(key, raw) },
                "r_mid" => result with { RMid = ParseNumber(key, raw) },
                "r_inner" => result with { RInner = ParseNumber(key, raw) },
                "depth" => result with { Depth = ParseNumber(key, raw) },
                "gap" => result with { Gap = ParseNumber(key, raw) },
                "beam_length" => result with { BeamLength = ParseNumber(key, raw) },
                "beam_half_width" => result with { BeamHalfWidth = ParseNumber(key, raw) },
                "lighting" => throw new ParameterOverrideException("Parameter 'lighting' is a colour scheme and cannot be overridden from text"),
                _ => throw new ParameterOverrideException($"Unknown parameter '{key}'. Known: {string.Join(", ", Names.Order(StringComparer.Ordinal))}"),
            };
        }

        return result;
    }

    private static int ParseInt(string key, string raw) =>
        int.TryParse(raw.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new ParameterOverrideException($"Parameter '{key}' expects an integer, found '{raw}'");

    private static double ParseNumber(string key, string raw) =>
        double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new ParameterOverrideException($"Parameter '{key}' expects a number, found '{raw}'");

    private static Point ParsePoint(string key, string raw)
    {
        var parts = raw.Split(',');
        if (parts.Length != 2)
        {
            throw new ParameterOverrideException($"Parameter '{key}' expects 'x,y', found '{raw}'");
        }

        return new Point(ParseNumber(key, parts[0]), ParseNumber(key, parts[1]));
    }
}

/// <summary>The parametric labs builder (the original <c>labs_parametric</c>): the mark as named geometry, with per-facet lighting.</summary>
public static class IsoLoopBuilder
{
    /// <summary>The variant used when none is named.</summary>
    public const string DefaultPreset = "baseline";

    /// <summary>The named parameter sets.</summary>
    public static IReadOnlyDictionary<string, IsoLoopParams> Presets { get; } = new Dictionary<string, IsoLoopParams>
    {
        ["baseline"] = new IsoLoopParams(),
        ["chunky"] = new IsoLoopParams { RInner = 64.0, Depth = 54.0, BeamHalfWidth = 44.0 },
        ["fine"] = new IsoLoopParams { RInner = 80.0, Depth = 38.0, BeamHalfWidth = 30.0 },
    };

    /// <summary>Each preset's label and description.</summary>
    public static IReadOnlyDictionary<string, Variant> Variants { get; } = Presets.ToDictionary(
        pair => pair.Key,
        pair => new Variant(
            string.Concat(pair.Key[..1].ToUpperInvariant(), pair.Key.AsSpan(1)),
            $"Parametric candidate: r_outer={PyFloat.Repr(pair.Value.ROuter)}, r_inner={PyFloat.Repr(pair.Value.RInner)}, "
            + $"depth={PyFloat.Repr(pair.Value.Depth)}, beam_half_width={PyFloat.Repr(pair.Value.BeamHalfWidth)}."));

    /// <summary>Classifies a facet by where its centroid sits.</summary>
    internal static Orientation FacetOrientation(IReadOnlyList<Point> points, Point center)
    {
        var cx = points.Sum(p => p.X) / points.Count;
        var cy = points.Sum(p => p.Y) / points.Count;
        var dx = cx - center.X;
        var dy = cy - center.Y;
        if (dy < -Math.Abs(dx) * 0.5)
        {
            return Orientation.Top;
        }

        if (Math.Abs(dx) < 0.25 * Math.Max(Math.Abs(dy), 1.0))
        {
            return Orientation.Inner;
        }

        return dx < 0 ? Orientation.Left : Orientation.Right;
    }

    /// <summary>Renders a parametric candidate as raw SVG text; <paramref name="parameters"/> wins over <paramref name="variant"/>.</summary>
    public static string GenerateSvg(string? variant = DefaultPreset, IsoLoopParams? parameters = null)
    {
        var cfg = parameters ?? Presets[VariantResolution.Resolve(variant, [.. Variants.Keys], DefaultPreset)];
        var cos30 = Math.Sqrt(3) / 2.0;
        var u = new Point(cos30, 0.5);
        var v = new Point(-cos30, 0.5);
        var w = new Point(0.0, 1.0);
        var light = cfg.Lighting.LightDirectionDeg;
        var facets = new List<(IReadOnlyList<Point> Points, Orientation Orientation)>();

        foreach (var loopCenter in new[] { cfg.LoopCenter, new Point((2 * cfg.Center.X) - cfg.LoopCenter.X, (2 * cfg.Center.Y) - cfg.LoopCenter.Y) })
        {
            var outer = HexGeometry.Vertices(cfg.ROuter, loopCenter);
            var mid = HexGeometry.Vertices(cfg.RMid, loopCenter);
            var inner = HexGeometry.Vertices(cfg.RInner, loopCenter);

            for (var i = 0; i < 6; i++)
            {
                var j = (i + 1) % 6;
                facets.Add(([outer[i], outer[j], mid[j], mid[i]], FacetOrientation([outer[i], mid[j]], cfg.Center)));
            }

            for (var i = 0; i < 6; i++)
            {
                var j = (i + 1) % 6;
                facets.Add(([mid[i], mid[j], inner[j], inner[i]], Orientation.Inner));
            }

            var outerDown = outer.Select(p => p.Offset(w, cfg.Depth)).ToArray();
            foreach (var i in new[] { 2, 3, 4 })
            {
                var j = (i + 1) % 6;
                facets.Add(([outer[i], outer[j], outerDown[j], outerDown[i]], i == 2 ? Orientation.Right : Orientation.Left));
            }
        }

        var halfLength = cfg.BeamLength / 2.0;
        var halfWidth = cfg.BeamHalfWidth;
        var stop = halfWidth + cfg.Gap;

        Point[] BeamFace(Point axis, Point cross, double start, double end)
        {
            var a = cfg.Center.Offset(axis, start);
            var b = cfg.Center.Offset(axis, end);
            return [a.Offset(cross, -halfWidth), b.Offset(cross, -halfWidth), b.Offset(cross, halfWidth), a.Offset(cross, halfWidth)];
        }

        foreach (var (start, end) in new[] { (-halfLength, -stop), (stop, halfLength) })
        {
            var face = BeamFace(v, u, start, end);
            facets.Add((face, Orientation.Right));
            facets.Add(([face[3], face[2], face[2].Offset(w, cfg.Depth), face[3].Offset(w, cfg.Depth)], Orientation.Left));
        }

        var over = BeamFace(u, v, -halfLength, halfLength);
        facets.Add((over, Orientation.Top));
        facets.Add(([over[3], over[2], over[2].Offset(w, cfg.Depth), over[3].Offset(w, cfg.Depth)], Orientation.Left));

        var defs = new List<string>();
        var body = new List<string>();
        for (var index = 0; index < facets.Count; index++)
        {
            var (points, orientation) = facets[index];
            var ramp = cfg.Lighting.RampFor(orientation);
            var vector = GradientVector.Of(points, light);
            var stops = new StringBuilder();
            var offsets = ramp.Offsets();
            for (var i = 0; i < ramp.Stops.Count; i++)
            {
                stops.Append(CultureInfo.InvariantCulture, $"<stop offset=\"{PyFloat.Repr(offsets[i])}\" stop-color=\"{ramp.Stops[i]}\" />");
            }

            defs.Add(
                $"<linearGradient id=\"f{index}\" x1=\"{PyFloat.Repr(vector.X1)}\" y1=\"{PyFloat.Repr(vector.Y1)}\" x2=\"{PyFloat.Repr(vector.X2)}\" y2=\"{PyFloat.Repr(vector.Y2)}\" "
                + $"gradientUnits=\"userSpaceOnUse\">{stops}</linearGradient>");
            body.Add($"<polygon points=\"{string.Join(' ', points.Select(p => $"{PythonNumber.FormatFixed(p.X, 2)},{PythonNumber.FormatFixed(p.Y, 2)}"))}\" fill=\"url(#f{index})\" />");
        }

        var c = cfg.Canvas.ToString(CultureInfo.InvariantCulture);
        return $"<svg xmlns=\"http://www.w3.org/2000/svg\" version=\"1\" width=\"{c}\" height=\"{c}\" viewBox=\"0 0 {c} {c}\">\n"
            + "<defs>\n" + string.Join('\n', defs) + "\n</defs>\n"
            + $"<path d=\"M0,0 L{c},0 L{c},{c} L0,{c} Z\" fill=\"{cfg.Background}\" />\n" + string.Join('\n', body) + "\n</svg>";
    }

    /// <summary>A gradient's start and end in user space.</summary>
    /// <param name="X1">Start x.</param>
    /// <param name="Y1">Start y.</param>
    /// <param name="X2">End x.</param>
    /// <param name="Y2">End y.</param>
    internal readonly record struct GradientVector(double X1, double Y1, double X2, double Y2)
    {
        /// <summary>Projects a facet's own extent onto the light direction.</summary>
        public static GradientVector Of(IReadOnlyList<Point> points, double lightDegrees)
        {
            var ux = Math.Cos(HexGeometry.Radians(lightDegrees));
            var uy = Math.Sin(HexGeometry.Radians(lightDegrees));
            var projections = points.Select(p => (p.X * ux) + (p.Y * uy)).ToArray();
            var lo = projections.Min();
            var hi = projections.Max();
            var cx = points.Sum(p => p.X) / points.Count;
            var cy = points.Sum(p => p.Y) / points.Count;
            var mid = (lo + hi) / 2.0;
            return new GradientVector(
                PythonNumber.Round(cx + (ux * (lo - mid)), 2),
                PythonNumber.Round(cy + (uy * (lo - mid)), 2),
                PythonNumber.Round(cx + (ux * (hi - mid)), 2),
                PythonNumber.Round(cy + (uy * (hi - mid)), 2));
        }
    }
}
