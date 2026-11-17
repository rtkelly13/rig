namespace ImageStackEval;

/// <summary>An RGB image as doubles on the 0..255 scale, row-major, three channels per pixel.</summary>
public sealed record RgbImage(int Width, int Height, double[] Data)
{
    public double At(int y, int x, int c) => Data[((y * Width) + x) * 3 + c];

    /// <summary>PIL crop box semantics: left and top inclusive, right and bottom exclusive.</summary>
    public RgbImage Crop(int left, int top, int right, int bottom)
    {
        int w = right - left, h = bottom - top;
        var data = new double[w * h * 3];
        for (int y = 0; y < h; y++)
        {
            Array.Copy(Data, (((top + y) * Width) + left) * 3, data, y * w * 3, w * 3);
        }

        return new RgbImage(w, h, data);
    }
}

public sealed record Hotspot(int Rank, int X0, int Y0, int X1, int Y1, double ErrorPct, string Mode);

public sealed record DriftNumbers(
    double RmsePct,
    double Ssim,
    double PsnrDb,
    double LuminanceRmsePct,
    double ChrominanceRmsePct,
    double ColorToGeometryRatio,
    double EdgeCorrelation,
    double BoundaryDilationBias,
    IReadOnlyList<Hotspot> Hotspots);

public sealed record Score(double CompositePct, double FullFramePct, IReadOnlyDictionary<string, double> PerFeaturePct);

public sealed record FeatureBox(string Key, int X0, int Y0, int X1, int Y1);

/// <summary>A line-for-line port of image_drift/metrics.py, diagnostics.py, numeric.py and promotion.py.</summary>
public static class Metrics
{
    public const double FrameWeight = 0.5;
    public const double TieThresholdPct = 0.5;
    public const double MaxFeatureRegressionPct = 0.25;

    private static double Rms(double[] values)
    {
        double sum = 0;
        foreach (double v in values)
        {
            sum += v * v;
        }

        return Math.Sqrt(sum / values.Length);
    }

    private static double[] Gray(RgbImage i)
    {
        var g = new double[i.Width * i.Height];
        for (int p = 0; p < g.Length; p++)
        {
            g[p] = (0.299 * i.Data[p * 3]) + (0.587 * i.Data[(p * 3) + 1]) + (0.114 * i.Data[(p * 3) + 2]);
        }

        return g;
    }

    /// <summary>Mean SSIM over non-overlapping (h/16 x w/16) blocks of the BT.601 luma, as compute_ssim.</summary>
    public static double Ssim(RgbImage a, RgbImage b)
    {
        double[] g1 = Gray(a), g2 = Gray(b);
        const double c1 = (0.01 * 255.0) * (0.01 * 255.0);
        const double c2 = (0.03 * 255.0) * (0.03 * 255.0);
        int h = a.Height, w = a.Width;
        int bh = Math.Max(1, h / 16), bw = Math.Max(1, w / 16);
        double total = 0;
        int count = 0;
        for (int y = 0; y <= h - bh; y += bh)
        {
            for (int x = 0; x <= w - bw; x += bw)
            {
                double s1 = 0, s2 = 0;
                for (int j = 0; j < bh; j++)
                {
                    for (int i = 0; i < bw; i++)
                    {
                        s1 += g1[((y + j) * w) + x + i];
                        s2 += g2[((y + j) * w) + x + i];
                    }
                }

                int n = bh * bw;
                double m1 = s1 / n, m2 = s2 / n, v1 = 0, v2 = 0, cov = 0;
                for (int j = 0; j < bh; j++)
                {
                    for (int i = 0; i < bw; i++)
                    {
                        double d1 = g1[((y + j) * w) + x + i] - m1, d2 = g2[((y + j) * w) + x + i] - m2;
                        v1 += d1 * d1;
                        v2 += d2 * d2;
                        cov += d1 * d2;
                    }
                }

                v1 /= n;
                v2 /= n;
                cov /= n;
                double denom = ((m1 * m1) + (m2 * m2) + c1) * (v1 + v2 + c2);
                if (denom > 0)
                {
                    total += ((2 * m1 * m2) + c1) * ((2 * cov) + c2) / denom;
                    count++;
                }
            }
        }

        return count > 0 ? total / count : 1.0;
    }

    public static double Psnr(RgbImage a, RgbImage b)
    {
        double sum = 0;
        for (int i = 0; i < a.Data.Length; i++)
        {
            double d = a.Data[i] - b.Data[i];
            sum += d * d;
        }

        double mse = sum / a.Data.Length;
        return mse <= 1e-10 ? double.PositiveInfinity : 20.0 * Math.Log10(255.0 / Math.Sqrt(mse));
    }

    public static double RmsePct(RgbImage a, RgbImage b)
    {
        var diff = new double[a.Data.Length];
        for (int i = 0; i < diff.Length; i++)
        {
            diff[i] = Math.Abs(a.Data[i] - b.Data[i]);
        }

        return Rms(diff) / 255.0 * 100.0;
    }

    private static (double[] Y, double[] Cb, double[] Cr) YCbCr(RgbImage i)
    {
        int n = i.Width * i.Height;
        double[] y = new double[n], cb = new double[n], cr = new double[n];
        for (int p = 0; p < n; p++)
        {
            double r = i.Data[p * 3], g = i.Data[(p * 3) + 1], b = i.Data[(p * 3) + 2];
            y[p] = (0.299 * r) + (0.587 * g) + (0.114 * b);
            cb[p] = (-0.168736 * r) - (0.331264 * g) + (0.5 * b) + 128.0;
            cr[p] = (0.5 * r) - (0.418688 * g) - (0.081312 * b) + 128.0;
        }

        return (y, cb, cr);
    }

    /// <summary>numpy.gradient on one channel: central differences inside, one-sided on the border.</summary>
    private static double[] GradientMagnitude(double[] f, int h, int w)
    {
        var m = new double[f.Length];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                double gy = y == 0 ? f[w + x] - f[x]
                    : y == h - 1 ? f[(y * w) + x] - f[((y - 1) * w) + x]
                    : (f[((y + 1) * w) + x] - f[((y - 1) * w) + x]) / 2.0;
                double gx = x == 0 ? f[(y * w) + 1] - f[y * w]
                    : x == w - 1 ? f[(y * w) + x] - f[(y * w) + x - 1]
                    : (f[(y * w) + x + 1] - f[(y * w) + x - 1]) / 2.0;
                m[(y * w) + x] = Math.Sqrt((gy * gy) + (gx * gx));
            }
        }

        return m;
    }

    private static double CorrelationPct(double[] a, double[] b)
    {
        double ma = a.Average(), mb = b.Average(), saa = 0, sbb = 0, sab = 0;
        for (int i = 0; i < a.Length; i++)
        {
            double da = a[i] - ma, db = b[i] - mb;
            saa += da * da;
            sbb += db * db;
            sab += da * db;
        }

        double denom = Math.Sqrt(saa * sbb);
        return denom > 0 ? sab / denom * 100.0 : 100.0;
    }

    private static IReadOnlyList<Hotspot> Hotspots(RgbImage a, RgbImage b, int divisions = 8, int topK = 4)
    {
        int h = a.Height, w = a.Width;
        int ch = Math.Max(1, h / divisions), cw = Math.Max(1, w / divisions);
        var (ya, cba, cra) = YCbCr(a);
        var (yb, cbb, crb) = YCbCr(b);
        var cells = new List<(int X0, int Y0, int X1, int Y1, double Err, double Lum, double Chrom)>();
        for (int r = 0; r < divisions; r++)
        {
            for (int c = 0; c < divisions; c++)
            {
                int y0 = r * ch, y1 = Math.Min(h, (r + 1) * ch), x0 = c * cw, x1 = Math.Min(w, (c + 1) * cw);
                if (y0 >= y1 || x0 >= x1)
                {
                    continue;
                }

                double pe = 0, le = 0, ce = 0;
                for (int y = y0; y < y1; y++)
                {
                    for (int x = x0; x < x1; x++)
                    {
                        int p = (y * w) + x;
                        double s = 0;
                        for (int k = 0; k < 3; k++)
                        {
                            double d = a.Data[(p * 3) + k] - b.Data[(p * 3) + k];
                            s += d * d;
                        }

                        pe += Math.Sqrt(s / 3.0);
                        le += Math.Abs(ya[p] - yb[p]);
                        double dcb = cba[p] - cbb[p], dcr = cra[p] - crb[p];
                        ce += Math.Sqrt(((dcb * dcb) + (dcr * dcr)) / 2.0);
                    }
                }

                double n = (y1 - y0) * (x1 - x0);
                cells.Add((x0, y0, x1, y1, pe / n / 255.0 * 100.0, le / n / 255.0 * 100.0, ce / n / 255.0 * 100.0));
            }
        }

        // Python's list.sort(reverse=True) is stable; OrderByDescending is too.
        var sorted = cells.OrderByDescending(c => c.Err).ToList();
        var result = new List<Hotspot>();
        for (int rank = 1; rank <= Math.Min(topK, sorted.Count); rank++)
        {
            var cell = sorted[rank - 1];
            if (cell.Err < 0.2)
            {
                break;
            }

            string mode = cell.Lum > 1.4 * cell.Chrom ? "GEOMETRY_EDGE"
                : cell.Chrom > 1.4 * cell.Lum ? "CHROMATIC_COLOR" : "MIXED";
            result.Add(new Hotspot(rank, cell.X0, cell.Y0, cell.X1, cell.Y1, Math.Round(cell.Err, 2), mode));
        }

        return result;
    }

    /// <summary>compute_drift plus analyze_decomposed_drift on two images already on the normalised canvas.</summary>
    public static DriftNumbers Drift(RgbImage reference, RgbImage render)
    {
        double rmse = RmsePct(reference, render);
        double ssim = Ssim(reference, render);
        double psnr = Psnr(reference, render);

        var (yr, cbr, crr) = YCbCr(reference);
        var (yn, cbn, crn) = YCbCr(render);
        int n = yr.Length;
        double[] lum = new double[n], chrom = new double[n];
        for (int p = 0; p < n; p++)
        {
            lum[p] = Math.Abs(yr[p] - yn[p]);
            double dcb = cbr[p] - cbn[p], dcr = crr[p] - crn[p];
            chrom[p] = Math.Sqrt((dcb * dcb) + (dcr * dcr));
        }

        double lumRmse = Rms(lum) / 255.0 * 100.0;
        double chromRmse = Rms(chrom) / (255.0 * Math.Sqrt(2.0)) * 100.0;
        double ratio = chromRmse / Math.Max(0.01, lumRmse);

        double[] gr = GradientMagnitude(yr, reference.Height, reference.Width);
        double[] gn = GradientMagnitude(yn, reference.Height, reference.Width);
        double edge = CorrelationPct(gr, gn);

        double bias = 0;
        double sum = 0;
        int count = 0;
        for (int p = 0; p < n; p++)
        {
            if (gr[p] > 20.0 || gn[p] > 20.0)
            {
                sum += (yn[p] - yr[p]) / 255.0;
                count++;
            }
        }

        if (count > 0)
        {
            bias = Math.Min(1.0, Math.Max(-1.0, sum / count * 10.0));
        }

        return new DriftNumbers(rmse, ssim, psnr, lumRmse, chromRmse, ratio, edge, bias, Hotspots(reference, render));
    }

    /// <summary>score_candidate: half the full frame, half the mean of the per-feature crops.</summary>
    public static Score ScoreCandidate(RgbImage reference, RgbImage render, IReadOnlyList<FeatureBox> boxes)
    {
        double full = RmsePct(reference, render);
        var per = new Dictionary<string, double>();
        foreach (var box in boxes)
        {
            per[box.Key] = RmsePct(
                reference.Crop(box.X0, box.Y0, box.X1, box.Y1),
                render.Crop(box.X0, box.Y0, box.X1, box.Y1));
        }

        double composite = per.Count > 0 ? (FrameWeight * full) + ((1.0 - FrameWeight) * per.Values.Average()) : full;
        return new Score(composite, full, per);
    }

    public sealed record Verdict(
        bool Promotable,
        double CompositeDelta,
        double SsimDelta,
        double WorstFeatureDelta,
        string? WorstFeatureKey)
    {
        /// <summary>Smallest distance (percentage points, or SSIM units) to any gate edge; positive means clear of it.</summary>
        public double MarginComposite => CompositeDelta - TieThresholdPct;

        public double MarginSsim => SsimDelta;

        public double MarginFeature => MaxFeatureRegressionPct - WorstFeatureDelta;
    }

    /// <summary>evaluate_promotion over already-computed scores.</summary>
    public static Verdict EvaluatePromotion(Score incumbent, double incumbentSsim, Score candidate, double candidateSsim)
    {
        double compositeDelta = incumbent.CompositePct - candidate.CompositePct;
        double ssimDelta = candidateSsim - incumbentSsim;
        double worst = 0;
        string? worstKey = null;
        bool missing = false;
        foreach (var (key, inc) in incumbent.PerFeaturePct)
        {
            if (!candidate.PerFeaturePct.TryGetValue(key, out double cand))
            {
                missing = true;
                continue;
            }

            double delta = cand - inc;
            if (delta > worst)
            {
                worst = delta;
                worstKey = key;
            }
        }

        bool promotable = compositeDelta >= TieThresholdPct && ssimDelta >= 0 && worst <= MaxFeatureRegressionPct && !missing;
        return new Verdict(promotable, compositeDelta, ssimDelta, worst, worstKey);
    }
}
