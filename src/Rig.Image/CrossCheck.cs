using System.Globalization;

namespace Rig.Image;

/// <summary>
/// How far the fast renderer may drift from the authoritative one before the cross-check fails (ADR 0024, #321). Differences are
/// absolute: percentage points for the scores, raw SSIM for structure, 0 to 255 for pixels.
/// </summary>
/// <param name="MeanAbsDifference">Mean absolute channel difference over every pixel and channel.</param>
/// <param name="DifferingPixelFraction">Fraction of pixels where any channel differs.</param>
/// <param name="RmsePp">RMSE difference against the reference, in percentage points.</param>
/// <param name="Ssim">SSIM difference against the reference.</param>
/// <param name="CompositePp">Composite score difference, in percentage points.</param>
public sealed record DivergenceThresholds(double MeanAbsDifference, double DifferingPixelFraction, double RmsePp, double Ssim, double CompositePp)
{
    /// <summary>The thresholds ADR 0024 sets.</summary>
    public static DivergenceThresholds Adr { get; } = new(1.0, 0.20, 0.1, 0.001, 0.1);
}

/// <summary>One SVG rendered at one size by both renderers.</summary>
/// <param name="Id">Stable id, like <c>rtkelly-labs.master.500</c>.</param>
/// <param name="Project">The asset project the SVG belongs to.</param>
/// <param name="Svg">Repository-relative path of the SVG.</param>
/// <param name="Size">Square size in pixels.</param>
public sealed record CrossCheckFile(string Id, string Project, string Svg, int Size);

/// <summary>The two renderers' scores for one master, compared.</summary>
/// <param name="AuthoritativeRmsePct">Authoritative renderer's RMSE against the reference.</param>
/// <param name="FastRmsePct">Fast renderer's RMSE against the reference.</param>
/// <param name="AuthoritativeSsim">Authoritative renderer's SSIM against the reference.</param>
/// <param name="FastSsim">Fast renderer's SSIM against the reference.</param>
/// <param name="AuthoritativeCompositePct">Authoritative renderer's composite score.</param>
/// <param name="FastCompositePct">Fast renderer's composite score.</param>
public sealed record MetricDeltas(
    double AuthoritativeRmsePct,
    double FastRmsePct,
    double AuthoritativeSsim,
    double FastSsim,
    double AuthoritativeCompositePct,
    double FastCompositePct)
{
    /// <summary>Absolute RMSE difference in percentage points.</summary>
    public double RmsePp => Math.Abs(AuthoritativeRmsePct - FastRmsePct);

    /// <summary>Absolute SSIM difference.</summary>
    public double SsimDifference => Math.Abs(AuthoritativeSsim - FastSsim);

    /// <summary>Absolute composite difference in percentage points.</summary>
    public double CompositePp => Math.Abs(AuthoritativeCompositePct - FastCompositePct);
}

/// <summary>How the two renderers differ on one file, and which thresholds that breaks.</summary>
/// <param name="File">What was rendered.</param>
/// <param name="MeanAbsDifference">Mean absolute channel difference.</param>
/// <param name="DifferingPixelFraction">Fraction of pixels that differ in any channel.</param>
/// <param name="MaxAbsDifference">Largest single channel difference; reported, never gated.</param>
/// <param name="Metrics">Score comparison, for masters at the scoring size only.</param>
/// <param name="Breaches">One line per threshold broken.</param>
public sealed record FileDivergence(CrossCheckFile File, double MeanAbsDifference, double DifferingPixelFraction, int MaxAbsDifference, MetricDeltas? Metrics, IReadOnlyList<string> Breaches);

/// <summary>Whether the two renderers agree on a candidate's promotion verdict.</summary>
/// <param name="Project">The asset project.</param>
/// <param name="Candidate">The candidate's name.</param>
/// <param name="AuthoritativePromotable">The authoritative renderer's verdict.</param>
/// <param name="FastPromotable">The fast renderer's verdict.</param>
public sealed record VerdictComparison(string Project, string Candidate, bool AuthoritativePromotable, bool FastPromotable)
{
    /// <summary>True when the fast renderer would have decided differently.</summary>
    public bool Flipped => AuthoritativePromotable != FastPromotable;
}

/// <summary>The cross-check's result, shaped as evidence: what was compared, against which limits, and what broke them.</summary>
/// <param name="Authoritative">Name of the authoritative renderer.</param>
/// <param name="Fast">Name of the fast renderer.</param>
/// <param name="Thresholds">The limits applied.</param>
/// <param name="Files">Per-file divergence.</param>
/// <param name="Verdicts">Verdict comparison per candidate; empty when no candidates were given.</param>
/// <param name="VerdictsEvaluated">False when no candidate SVGs were supplied, so verdicts were not checked.</param>
public sealed record CrossCheckReport(
    string Authoritative,
    string Fast,
    DivergenceThresholds Thresholds,
    IReadOnlyList<FileDivergence> Files,
    IReadOnlyList<VerdictComparison> Verdicts,
    bool VerdictsEvaluated)
{
    /// <summary>True when no file broke a threshold and no verdict flipped.</summary>
    public bool Passed => Files.All(file => file.Breaches.Count == 0) && Verdicts.All(verdict => !verdict.Flipped);
}

/// <summary>Renders the project masters with two renderers and measures how far they diverge.</summary>
public static class CrossCheck
{
    private static readonly string[] Projects = ["rtkelly-labs", "parquet-source-generator"];
    private static readonly int[] MasterSizes = [500, 180, 1000, 2000];
    private static readonly int[] FaviconSizes = [500, 512, 48, 32, 16];

    /// <summary>The file set the Python oracle covers: each project's master and favicon at the sizes production cuts use.</summary>
    public static IReadOnlyList<CrossCheckFile> Files()
    {
        var files = new List<CrossCheckFile>();
        foreach (var project in Projects)
        {
            files.AddRange(MasterSizes.Select(size => new CrossCheckFile($"{project}.master.{size}", project, MasterSvg(project), size)));
            files.AddRange(FaviconSizes.Select(size => new CrossCheckFile($"{project}.favicon.{size}", project, $"assets/{project}/production-cuts/favicon.svg", size)));
        }

        return files;
    }

    /// <summary>Repository-relative path of a project's master SVG.</summary>
    public static string MasterSvg(string project) => project switch
    {
        "rtkelly-labs" => "assets/rtkelly-labs/production-cuts/rtkelly-labs.svg",
        "parquet-source-generator" => "assets/parquet-source-generator/production-cuts/parquet-diamond.svg",
        _ => throw new ArgumentOutOfRangeException(nameof(project), project, "expected rtkelly-labs or parquet-source-generator"),
    };

    /// <summary>Repository-relative path of the reference image a project is scored against.</summary>
    public static string ReferencePath(string project) => project switch
    {
        "rtkelly-labs" => "assets/rtkelly-labs/reference/rtkelly-labs.png",
        "parquet-source-generator" => "assets/parquet-source-generator/reference/parquet-diamond.png",
        _ => throw new ArgumentOutOfRangeException(nameof(project), project, "expected rtkelly-labs or parquet-source-generator"),
    };

    /// <summary>
    /// Renders every file in <paramref name="files"/> (normally <see cref="Files"/>) with both renderers and compares. When <paramref name="candidatesDir"/> is given, each
    /// <c>&lt;project&gt;.&lt;name&gt;.svg</c> in it is also scored against the master and its promotion verdict compared.
    /// </summary>
    public static async Task<CrossCheckReport> RunAsync(
        ISvgRenderer authoritative,
        ISvgRenderer fast,
        string repoRoot,
        IReadOnlyList<CrossCheckFile> files,
        string? candidatesDir,
        DivergenceThresholds thresholds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authoritative);
        ArgumentNullException.ThrowIfNull(fast);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(thresholds);
        var divergences = new List<FileDivergence>();
        foreach (var file in files)
        {
            var svg = Path.Combine(repoRoot, file.Svg);
            var slow = await authoritative.RenderAsync(svg, file.Size, cancellationToken).ConfigureAwait(false);
            var quick = await fast.RenderAsync(svg, file.Size, cancellationToken).ConfigureAwait(false);
            divergences.Add(Compare(file, slow, quick, MetricsFor(file, repoRoot, slow, quick), thresholds));
        }

        var verdicts = new List<VerdictComparison>();
        if (candidatesDir is not null)
        {
            verdicts.AddRange(await CompareVerdictsAsync(authoritative, fast, repoRoot, candidatesDir, cancellationToken).ConfigureAwait(false));
        }

        return new CrossCheckReport(authoritative.Name, fast.Name, thresholds, divergences, verdicts, candidatesDir is not null);
    }

    private static MetricDeltas? MetricsFor(CrossCheckFile file, string repoRoot, Raster authoritative, Raster fast)
    {
        if (!file.Id.Contains(".master.", StringComparison.Ordinal) || file.Size != RasterPipeline.NormalisedSize)
        {
            return null;
        }

        var reference = RasterPipeline.LoadNormalised(File.ReadAllBytes(Path.Combine(repoRoot, ReferencePath(file.Project))));
        var boxes = FeatureRegions.For(file.Project);
        var slowImage = RasterPipeline.Normalise(authoritative);
        var quickImage = RasterPipeline.Normalise(fast);
        var slowScore = DriftMetrics.ScoreCandidate(reference, slowImage, boxes);
        var quickScore = DriftMetrics.ScoreCandidate(reference, quickImage, boxes);
        return new MetricDeltas(
            slowScore.FullFramePct,
            quickScore.FullFramePct,
            DriftMetrics.Ssim(reference, slowImage),
            DriftMetrics.Ssim(reference, quickImage),
            slowScore.CompositePct,
            quickScore.CompositePct);
    }

    private static FileDivergence Compare(CrossCheckFile file, Raster authoritative, Raster fast, MetricDeltas? metrics, DivergenceThresholds thresholds)
    {
        var breaches = new List<string>();
        if (authoritative.Width != fast.Width || authoritative.Height != fast.Height)
        {
            breaches.Add($"canvas sizes differ: {authoritative.Width}x{authoritative.Height} vs {fast.Width}x{fast.Height}");
            return new FileDivergence(file, double.NaN, double.NaN, 0, metrics, breaches);
        }

        var a = authoritative.Pixels;
        var b = fast.Pixels;
        long total = 0;
        var maxAbs = 0;
        var differing = 0;
        for (var pixel = 0; pixel < a.Length; pixel += 4)
        {
            var any = false;
            for (var c = 0; c < 4; c++)
            {
                var diff = Math.Abs(a[pixel + c] - b[pixel + c]);
                total += diff;
                maxAbs = Math.Max(maxAbs, diff);
                any |= diff != 0;
            }

            if (any)
            {
                differing++;
            }
        }

        var meanAbs = (double)total / a.Length;
        var fraction = (double)differing / (a.Length / 4);
        if (meanAbs > thresholds.MeanAbsDifference)
        {
            breaches.Add($"mean abs difference {Number(meanAbs)} exceeds {Number(thresholds.MeanAbsDifference)}");
        }

        if (fraction > thresholds.DifferingPixelFraction)
        {
            breaches.Add($"differing pixels {Number(fraction * 100)}% exceeds {Number(thresholds.DifferingPixelFraction * 100)}%");
        }

        if (metrics is not null)
        {
            if (metrics.RmsePp > thresholds.RmsePp)
            {
                breaches.Add($"RMSE differs by {Number(metrics.RmsePp)}pp, limit {Number(thresholds.RmsePp)}pp");
            }

            if (metrics.SsimDifference > thresholds.Ssim)
            {
                breaches.Add($"SSIM differs by {Number(metrics.SsimDifference)}, limit {Number(thresholds.Ssim)}");
            }

            if (metrics.CompositePp > thresholds.CompositePp)
            {
                breaches.Add($"composite differs by {Number(metrics.CompositePp)}pp, limit {Number(thresholds.CompositePp)}pp");
            }
        }

        return new FileDivergence(file, meanAbs, fraction, maxAbs, metrics, breaches);
    }

    private static async Task<List<VerdictComparison>> CompareVerdictsAsync(
        ISvgRenderer authoritative,
        ISvgRenderer fast,
        string repoRoot,
        string candidatesDir,
        CancellationToken cancellationToken)
    {
        var comparisons = new List<VerdictComparison>();
        foreach (var path in Directory.EnumerateFiles(candidatesDir, "*.svg").Order(StringComparer.Ordinal))
        {
            var parts = Path.GetFileNameWithoutExtension(path).Split('.');
            if (parts.Length != 2)
            {
                throw new InvalidDataException($"{path}: expected a name like <project>.<candidate>.svg, found {Path.GetFileName(path)}");
            }

            var (project, candidate) = (parts[0], parts[1]);
            var master = Path.Combine(repoRoot, MasterSvg(project));
            var reference = RasterPipeline.LoadNormalised(await File.ReadAllBytesAsync(Path.Combine(repoRoot, ReferencePath(project)), cancellationToken).ConfigureAwait(false));
            var boxes = FeatureRegions.For(project);
            var slow = await Verdict(authoritative, master, path).ConfigureAwait(false);
            var quick = await Verdict(fast, master, path).ConfigureAwait(false);
            comparisons.Add(new VerdictComparison(project, candidate, slow, quick));

            async Task<bool> Verdict(ISvgRenderer renderer, string incumbentSvg, string candidateSvg)
            {
                var incumbent = RasterPipeline.Normalise(await renderer.RenderAsync(incumbentSvg, RasterPipeline.NormalisedSize, cancellationToken).ConfigureAwait(false));
                var challenger = RasterPipeline.Normalise(await renderer.RenderAsync(candidateSvg, RasterPipeline.NormalisedSize, cancellationToken).ConfigureAwait(false));
                return PromotionGate.Evaluate(
                    DriftMetrics.ScoreCandidate(reference, incumbent, boxes),
                    DriftMetrics.Ssim(reference, incumbent),
                    DriftMetrics.ScoreCandidate(reference, challenger, boxes),
                    DriftMetrics.Ssim(reference, challenger)).Promotable;
            }
        }

        return comparisons;
    }

    private static string Number(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);
}
