using System.Globalization;
using Rig.Image.Pathways;

namespace Rig.Image;

/// <summary>Plain-language guidance and the diff heatmap for a reference and render pair: the diagnostic half of image_drift's drift report.</summary>
internal static class DriftReport
{
    /// <summary>What one hotspot's dominant error mode means.</summary>
    public static string HotspotSummary(string mode) => mode switch
    {
        "GEOMETRY_EDGE" => "Edge / contour misalignment dominates error in this block",
        "CHROMATIC_COLOR" => "Color gradient or fill shade drift dominates error in this block",
        "MIXED" => "Combined geometric boundary and shading mismatch",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "expected GEOMETRY_EDGE, CHROMATIC_COLOR or MIXED"),
    };

    /// <summary>Engineering advice for refining the SVG builder, most specific cause first; never empty.</summary>
    public static IReadOnlyList<string> Recommendations(DriftNumbers drift)
    {
        var lum = drift.LuminanceRmsePct;
        var chrom = drift.ChrominanceRmsePct;
        var bias = drift.BoundaryDilationBias;
        var recommendations = new List<string>();
        if (chrom > 1.8 * lum && chrom > 1.0)
        {
            recommendations.Add(
                $"Color/Palette Drift Dominates (Chrominance: {Fixed(chrom, 2)}% vs Luminance: {Fixed(lum, 2)}%): "
                + "The geometric silhouette is structurally accurate. Focus on tuning <linearGradient> stop colors, "
                + "lighting specular highlights, or fill opacity in the builder.");
        }
        else if (lum > 1.8 * chrom && lum > 1.0)
        {
            recommendations.Add(
                $"Geometric / Edge Displacement Dominates (Luminance: {Fixed(lum, 2)}% vs Chrominance: {Fixed(chrom, 2)}%): "
                + "Color shades match well, but path contours or vertex positions drift from the raster. "
                + "Inspect polyline knots and curve tangents around the top spatial hotspots.");
        }

        if (bias > 0.12)
        {
            recommendations.Add(
                $"Positive Boundary Dilation (+{Fixed(bias, 2)}): Rendered vector features extend slightly beyond "
                + "ground-truth boundaries. Consider decreasing stroke-width by ~0.5px or slightly contracting coordinates.");
        }
        else if (bias < -0.12)
        {
            recommendations.Add(
                $"Negative Boundary Erosion ({Fixed(bias, 2)}): Rendered vector features are slightly thinner than the "
                + "source raster. Consider increasing stroke-width by ~0.5px or expanding coordinate bounds.");
        }

        if (drift.EdgeCorrelation < 90.0)
        {
            recommendations.Add(
                $"Edge Correlation Warning ({Fixed(drift.EdgeCorrelation, 1)}%): Gradient contours deviate from source raster. "
                + "Verify Bézier tangent smoothness and facet intersections.");
        }

        if (drift.Hotspots.Count > 0)
        {
            var top = drift.Hotspots[0];
            recommendations.Add(
                $"Primary Hotspot #{top.Rank} at [X:{top.X0}..{top.X1}, Y:{top.Y0}..{top.Y1}] "
                + $"({PyFloat.Repr(top.ErrorPct)}% error, {top.Mode}): {HotspotSummary(top.Mode)}. "
                + "Cross-reference this coordinate box with your generator's path definitions.");
        }

        if (recommendations.Count == 0)
        {
            recommendations.Add(
                $"Superb Fidelity (SSIM: {Fixed(drift.Ssim * 100, 2)}%, RMSE: {Fixed(drift.RmsePct, 2)}%): Rendered SVG and Source PNG match "
                + "within tight sub-pixel tolerance across both geometric and chromatic channels.");
        }

        return recommendations;
    }

    /// <summary>The diff heatmap: red is three times the mean channel difference, green and blue are the render's own, dimmed to 30%.</summary>
    public static RgbImage Heatmap(RgbImage reference, RgbImage render)
    {
        if (reference.Width != render.Width || reference.Height != render.Height)
        {
            throw new ArgumentException($"expected two images of one size, found {reference.Width}x{reference.Height} and {render.Width}x{render.Height}", nameof(render));
        }

        var a = reference.Pixels;
        var b = render.Pixels;
        var visual = new byte[a.Length];
        for (var i = 0; i < a.Length; i += 3)
        {
            var mean = (Math.Abs(a[i] - b[i]) + Math.Abs(a[i + 1] - b[i + 1]) + Math.Abs(a[i + 2] - b[i + 2])) / 3f;
            visual[i] = (byte)Math.Clamp(mean * 3f, 0f, 255f);
            visual[i + 1] = (byte)(b[i + 1] * 0.3f);
            visual[i + 2] = (byte)(b[i + 2] * 0.3f);
        }

        return new RgbImage(reference.Width, reference.Height, visual);
    }

    /// <summary>PSNR as the report prints it: <c>∞</c> for identical images.</summary>
    public static string Psnr(double psnrDb) => double.IsInfinity(psnrDb) ? "∞" : Fixed(psnrDb, 1) + " dB";

    private static string Fixed(double value, int digits) => value.ToString("F" + digits.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
}
