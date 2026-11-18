using System.Globalization;

namespace Rig.Image;

/// <summary>One cell of the 5 by 5 error grid: the mean absolute difference over a block of the canvas.</summary>
/// <param name="Row">Row of the cell, from 0.</param>
/// <param name="Column">Column of the cell, from 0.</param>
/// <param name="Error">Mean over the cell of the per-pixel mean of the absolute channel differences.</param>
public sealed record GridCell(int Row, int Column, double Error);

/// <summary>One of the worst cells of the 8 by 8 error map and what kind of error dominates it.</summary>
/// <param name="Rank">1 for the worst.</param>
/// <param name="X0">Left edge of the cell.</param>
/// <param name="Y0">Top edge of the cell.</param>
/// <param name="X1">Right edge of the cell.</param>
/// <param name="Y1">Bottom edge of the cell.</param>
/// <param name="ErrorPct">The cell's RMS colour error as a percentage, rounded to two places like the Python report.</param>
/// <param name="Mode"><c>GEOMETRY_EDGE</c>, <c>CHROMATIC_COLOR</c> or <c>MIXED</c>.</param>
public sealed record Hotspot(int Rank, int X0, int Y0, int X1, int Y1, double ErrorPct, string Mode);

/// <summary>Everything the original compute_drift and analyze_decomposed_drift report for one reference and render pair.</summary>
public sealed record DriftNumbers(
    double RmsePct,
    double Ssim,
    double PsnrDb,
    IReadOnlyList<GridCell> Grid,
    double LuminanceRmsePct,
    double ChrominanceRmsePct,
    double ColorToGeometryRatio,
    double EdgeCorrelation,
    double BoundaryDilationBias,
    IReadOnlyList<Hotspot> Hotspots);

/// <summary>A candidate's score: half the full frame, half the mean of its per-feature crops.</summary>
/// <param name="CompositePct">The weighted score; lower is better.</param>
/// <param name="FullFramePct">RMSE over the whole canvas.</param>
/// <param name="PerFeaturePct">RMSE over each feature crop, keyed by region.</param>
public sealed record Score(double CompositePct, double FullFramePct, IReadOnlyDictionary<string, double> PerFeaturePct);

/// <summary>A port of the original metrics.py, diagnostics.py and numeric.py over two images on the same canvas.</summary>
public static class DriftMetrics
{
    /// <summary>How much of a composite is the full frame.</summary>
    public const double FrameWeight = 0.5;

    private const int GridSize = 5;
    private const int HotspotDivisions = 8;
    private const int HotspotCount = 4;
    private const double HotspotFloorPct = 0.2;
    private const double ModeRatio = 1.4;
    private const double EdgeGradient = 20.0;
    private const int SsimBlocks = 16;

    /// <summary>compute_drift plus analyze_decomposed_drift for two images of the same size.</summary>
    public static DriftNumbers Drift(RgbImage reference, RgbImage render)
    {
        EnsureSameSize(reference, render);
        var a = Channels(reference);
        var b = Channels(render);
        var abs = new double[a.Length];
        for (var i = 0; i < abs.Length; i++)
        {
            abs[i] = Math.Abs(a[i] - b[i]);
        }

        var (yr, cbr, crr) = YCbCr(a);
        var (yn, cbn, crn) = YCbCr(b);
        var pixels = yr.Length;
        var luminance = new double[pixels];
        var chrominance = new double[pixels];
        for (var p = 0; p < pixels; p++)
        {
            luminance[p] = Math.Abs(yr[p] - yn[p]);
            var dcb = cbr[p] - cbn[p];
            var dcr = crr[p] - crn[p];
            chrominance[p] = Math.Sqrt((dcb * dcb) + (dcr * dcr));
        }

        var luminanceRmse = Rms(luminance) / 255.0 * 100.0;
        var chrominanceRmse = Rms(chrominance) / (255.0 * Math.Sqrt(2.0)) * 100.0;
        var gradientReference = GradientMagnitude(yr, reference.Height, reference.Width);
        var gradientRender = GradientMagnitude(yn, reference.Height, reference.Width);
        return new DriftNumbers(
            Rms(abs) / 255.0 * 100.0,
            Ssim(a, b, reference.Height, reference.Width),
            Psnr(a, b),
            Grid(abs, reference.Height, reference.Width),
            luminanceRmse,
            chrominanceRmse,
            chrominanceRmse / Math.Max(0.01, luminanceRmse),
            CorrelationPct(gradientReference, gradientRender),
            BoundaryBias(yr, yn, gradientReference, gradientRender),
            Hotspots(a, b, yr, yn, cbr, cbn, crr, crn, reference.Height, reference.Width));
    }

    /// <summary>RMSE of two images as a percentage of full scale.</summary>
    public static double RmsePct(RgbImage reference, RgbImage render)
    {
        EnsureSameSize(reference, render);
        var a = Channels(reference);
        var b = Channels(render);
        var abs = new double[a.Length];
        for (var i = 0; i < abs.Length; i++)
        {
            abs[i] = Math.Abs(a[i] - b[i]);
        }

        return Rms(abs) / 255.0 * 100.0;
    }

    /// <summary>The structural similarity of two images, as the original compute_ssim.</summary>
    public static double Ssim(RgbImage reference, RgbImage render)
    {
        EnsureSameSize(reference, render);
        return Ssim(Channels(reference), Channels(render), reference.Height, reference.Width);
    }

    /// <summary>score_candidate: the full frame and each feature crop, combined by <see cref="FrameWeight"/>.</summary>
    public static Score ScoreCandidate(RgbImage reference, RgbImage render, IReadOnlyList<FeatureBox> boxes)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(render);
        ArgumentNullException.ThrowIfNull(boxes);
        var full = RmsePct(reference, render);
        var perFeature = new Dictionary<string, double>();
        foreach (var box in boxes)
        {
            perFeature[box.Key] = RmsePct(
                reference.Crop(box.X0, box.Y0, box.X1, box.Y1),
                render.Crop(box.X0, box.Y0, box.X1, box.Y1));
        }

        var composite = perFeature.Count > 0
            ? (FrameWeight * full) + ((1.0 - FrameWeight) * perFeature.Values.Average())
            : full;
        return new Score(composite, full, perFeature);
    }

    private static void EnsureSameSize(RgbImage a, RgbImage b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        if (a.Width != b.Width || a.Height != b.Height)
        {
            throw new ArgumentException($"expected two images of the same size, found {a.Width}x{a.Height} and {b.Width}x{b.Height}");
        }
    }

    private static double[] Channels(RgbImage image)
    {
        var data = new double[image.Pixels.Length];
        for (var i = 0; i < data.Length; i++)
        {
            data[i] = image.Pixels[i];
        }

        return data;
    }

    private static double Rms(double[] values)
    {
        var sum = 0.0;
        foreach (var value in values)
        {
            sum += value * value;
        }

        return Math.Sqrt(sum / values.Length);
    }

    private static double[] Gray(double[] rgb)
    {
        var gray = new double[rgb.Length / 3];
        for (var p = 0; p < gray.Length; p++)
        {
            gray[p] = (0.299 * rgb[p * 3]) + (0.587 * rgb[(p * 3) + 1]) + (0.114 * rgb[(p * 3) + 2]);
        }

        return gray;
    }

    private static double Ssim(double[] a, double[] b, int height, int width)
    {
        double[] g1 = Gray(a), g2 = Gray(b);
        const double C1 = 0.01 * 255.0 * (0.01 * 255.0);
        const double C2 = 0.03 * 255.0 * (0.03 * 255.0);
        int blockHeight = Math.Max(1, height / SsimBlocks), blockWidth = Math.Max(1, width / SsimBlocks);
        var total = 0.0;
        var count = 0;
        var n = blockHeight * blockWidth;
        for (var y = 0; y <= height - blockHeight; y += blockHeight)
        {
            for (var x = 0; x <= width - blockWidth; x += blockWidth)
            {
                double s1 = 0, s2 = 0;
                for (var j = 0; j < blockHeight; j++)
                {
                    for (var i = 0; i < blockWidth; i++)
                    {
                        s1 += g1[((y + j) * width) + x + i];
                        s2 += g2[((y + j) * width) + x + i];
                    }
                }

                double m1 = s1 / n, m2 = s2 / n, v1 = 0, v2 = 0, covariance = 0;
                for (var j = 0; j < blockHeight; j++)
                {
                    for (var i = 0; i < blockWidth; i++)
                    {
                        double d1 = g1[((y + j) * width) + x + i] - m1, d2 = g2[((y + j) * width) + x + i] - m2;
                        v1 += d1 * d1;
                        v2 += d2 * d2;
                        covariance += d1 * d2;
                    }
                }

                v1 /= n;
                v2 /= n;
                covariance /= n;
                var denominator = ((m1 * m1) + (m2 * m2) + C1) * (v1 + v2 + C2);
                if (denominator > 0)
                {
                    total += ((2 * m1 * m2) + C1) * ((2 * covariance) + C2) / denominator;
                    count++;
                }
            }
        }

        return count > 0 ? total / count : 1.0;
    }

    private static double Psnr(double[] a, double[] b)
    {
        var sum = 0.0;
        for (var i = 0; i < a.Length; i++)
        {
            var d = a[i] - b[i];
            sum += d * d;
        }

        var mse = sum / a.Length;
        return mse <= 1e-10 ? double.PositiveInfinity : 20.0 * Math.Log10(255.0 / Math.Sqrt(mse));
    }

    private static List<GridCell> Grid(double[] abs, int height, int width)
    {
        int cellHeight = Math.Max(1, height / GridSize), cellWidth = Math.Max(1, width / GridSize);
        var cells = new List<GridCell>();
        for (var row = 0; row < GridSize; row++)
        {
            for (var column = 0; column < GridSize; column++)
            {
                int r0 = row * cellHeight, r1 = Math.Min(height, (row + 1) * cellHeight);
                int c0 = column * cellWidth, c1 = Math.Min(width, (column + 1) * cellWidth);
                if (r0 >= r1 || c0 >= c1)
                {
                    continue;
                }

                var sum = 0.0;
                for (var y = r0; y < r1; y++)
                {
                    for (var x = c0; x < c1; x++)
                    {
                        var p = ((y * width) + x) * 3;
                        sum += (abs[p] + abs[p + 1] + abs[p + 2]) / 3.0;
                    }
                }

                cells.Add(new GridCell(row, column, sum / ((r1 - r0) * (c1 - c0))));
            }
        }

        return cells;
    }

    private static (double[] Y, double[] Cb, double[] Cr) YCbCr(double[] rgb)
    {
        var n = rgb.Length / 3;
        double[] y = new double[n], cb = new double[n], cr = new double[n];
        for (var p = 0; p < n; p++)
        {
            double r = rgb[p * 3], g = rgb[(p * 3) + 1], b = rgb[(p * 3) + 2];
            y[p] = (0.299 * r) + (0.587 * g) + (0.114 * b);
            cb[p] = (-0.168736 * r) - (0.331264 * g) + (0.5 * b) + 128.0;
            cr[p] = (0.5 * r) - (0.418688 * g) - (0.081312 * b) + 128.0;
        }

        return (y, cb, cr);
    }

    /// <summary>numpy.gradient magnitude of one channel: central differences inside, one-sided on the border.</summary>
    private static double[] GradientMagnitude(double[] f, int height, int width)
    {
        var magnitude = new double[f.Length];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var gy = y == 0 ? f[width + x] - f[x]
                    : y == height - 1 ? f[(y * width) + x] - f[((y - 1) * width) + x]
                    : (f[((y + 1) * width) + x] - f[((y - 1) * width) + x]) / 2.0;
                var gx = x == 0 ? f[(y * width) + 1] - f[y * width]
                    : x == width - 1 ? f[(y * width) + x] - f[(y * width) + x - 1]
                    : (f[(y * width) + x + 1] - f[(y * width) + x - 1]) / 2.0;
                magnitude[(y * width) + x] = Math.Sqrt((gy * gy) + (gx * gx));
            }
        }

        return magnitude;
    }

    private static double CorrelationPct(double[] a, double[] b)
    {
        double meanA = a.Average(), meanB = b.Average(), saa = 0, sbb = 0, sab = 0;
        for (var i = 0; i < a.Length; i++)
        {
            double da = a[i] - meanA, db = b[i] - meanB;
            saa += da * da;
            sbb += db * db;
            sab += da * db;
        }

        var denominator = Math.Sqrt(saa * sbb);
        return denominator > 0 ? sab / denominator * 100.0 : 100.0;
    }

    private static double BoundaryBias(double[] yr, double[] yn, double[] gradientReference, double[] gradientRender)
    {
        var sum = 0.0;
        var count = 0;
        for (var p = 0; p < yr.Length; p++)
        {
            if (gradientReference[p] > EdgeGradient || gradientRender[p] > EdgeGradient)
            {
                sum += (yn[p] - yr[p]) / 255.0;
                count++;
            }
        }

        return count > 0 ? Math.Clamp(sum / count * 10.0, -1.0, 1.0) : 0.0;
    }

    private static List<Hotspot> Hotspots(
        double[] a, double[] b, double[] ya, double[] yb, double[] cba, double[] cbb, double[] cra, double[] crb, int height, int width)
    {
        int cellHeight = Math.Max(1, height / HotspotDivisions), cellWidth = Math.Max(1, width / HotspotDivisions);
        var cells = new List<(int X0, int Y0, int X1, int Y1, double Error, double Luminance, double Chrominance)>();
        for (var row = 0; row < HotspotDivisions; row++)
        {
            for (var column = 0; column < HotspotDivisions; column++)
            {
                int y0 = row * cellHeight, y1 = Math.Min(height, (row + 1) * cellHeight);
                int x0 = column * cellWidth, x1 = Math.Min(width, (column + 1) * cellWidth);
                if (y0 >= y1 || x0 >= x1)
                {
                    continue;
                }

                double pixelError = 0, luminanceError = 0, chrominanceError = 0;
                for (var y = y0; y < y1; y++)
                {
                    for (var x = x0; x < x1; x++)
                    {
                        var p = (y * width) + x;
                        var squares = 0.0;
                        for (var k = 0; k < 3; k++)
                        {
                            var d = a[(p * 3) + k] - b[(p * 3) + k];
                            squares += d * d;
                        }

                        pixelError += Math.Sqrt(squares / 3.0);
                        luminanceError += Math.Abs(ya[p] - yb[p]);
                        double dcb = cba[p] - cbb[p], dcr = cra[p] - crb[p];
                        chrominanceError += Math.Sqrt(((dcb * dcb) + (dcr * dcr)) / 2.0);
                    }
                }

                double n = (y1 - y0) * (x1 - x0);
                cells.Add((x0, y0, x1, y1, pixelError / n / 255.0 * 100.0, luminanceError / n / 255.0 * 100.0, chrominanceError / n / 255.0 * 100.0));
            }
        }

        // Python's list.sort(reverse=True) is stable and so is OrderByDescending.
        var worst = cells.OrderByDescending(c => c.Error).Take(HotspotCount).ToList();
        var hotspots = new List<Hotspot>();
        for (var i = 0; i < worst.Count; i++)
        {
            var cell = worst[i];
            if (cell.Error < HotspotFloorPct)
            {
                break;
            }

            var mode = cell.Luminance > ModeRatio * cell.Chrominance ? "GEOMETRY_EDGE"
                : cell.Chrominance > ModeRatio * cell.Luminance ? "CHROMATIC_COLOR" : "MIXED";
            hotspots.Add(new Hotspot(i + 1, cell.X0, cell.Y0, cell.X1, cell.Y1, Math.Round(cell.Error, 2, MidpointRounding.ToEven), mode));
        }

        return hotspots;
    }

    /// <summary>Formats <paramref name="value"/> with <paramref name="digits"/> decimals, invariant culture.</summary>
    internal static string Fixed(double value, int digits) => value.ToString("F" + digits.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

    /// <summary>Formats <paramref name="value"/> like Python's <c>{:+.Nf}</c>.</summary>
    internal static string Signed(double value, int digits) => (value >= 0 ? "+" : string.Empty) + Fixed(value, digits);
}
