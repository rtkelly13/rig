using System.Globalization;
using System.Text;
using Rig.Image.Simplifier;

namespace Rig.Image;

/// <summary>The consolidated report for a gated simplification (image_drift's <c>OptimizationReport</c>).</summary>
/// <param name="OriginalSizeBytes">UTF-8 size of the input.</param>
/// <param name="FinalSizeBytes">UTF-8 size of the kept candidate.</param>
/// <param name="SizeReductionPct">Size saved, rounded to two places.</param>
/// <param name="OriginalNodeCount">Path commands in the input.</param>
/// <param name="FinalNodeCount">Path commands in the kept candidate.</param>
/// <param name="NodeReductionPct">Nodes saved, rounded to two places.</param>
/// <param name="Tolerance">The geometric tolerance of the kept candidate.</param>
/// <param name="Ssim">SSIM of the kept candidate against the input, rounded to four places.</param>
/// <param name="PsnrDb">PSNR of the kept candidate, rounded to two places.</param>
/// <param name="RmsePct">RMSE of the kept candidate, rounded to three places.</param>
/// <param name="OutputSvg">The kept candidate.</param>
/// <param name="GatePassed">Whether a candidate met the budget within the iteration limit.</param>
public sealed record OptimizationReport(
    int OriginalSizeBytes,
    int FinalSizeBytes,
    double SizeReductionPct,
    int OriginalNodeCount,
    int FinalNodeCount,
    double NodeReductionPct,
    double Tolerance,
    double Ssim,
    double PsnrDb,
    double RmsePct,
    string OutputSvg,
    bool GatePassed);

/// <summary>Where the kept candidate was written and how it scored.</summary>
/// <param name="Path">The file written.</param>
/// <param name="Report">The report.</param>
public sealed record GatedOutput(string Path, OptimizationReport Report);

/// <summary>Simplification cannot meet its drift budget and would overwrite its input.</summary>
public sealed class DriftGateNotMetException : Exception
{
    /// <summary>Creates the exception.</summary>
    public DriftGateNotMetException()
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    public DriftGateNotMetException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and its cause.</summary>
    public DriftGateNotMetException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The closed-loop simplifier: simplify, render, score against the original, and back the tolerance off until the budget is met.</summary>
/// <param name="renderer">Renders the original and each candidate.</param>
/// <param name="scratch">A folder for the candidate files.</param>
public sealed class DriftGate(ISvgRenderer renderer, string scratch)
{
    private const int RenderSize = 1000;

    /// <summary>
    /// Optimises <paramref name="svgPath"/>. The best candidate seen (highest SSIM, the latest on a tie) is written to
    /// <paramref name="outputPath"/>, or over the input when none is given; a missed budget never overwrites the input.
    /// </summary>
    public async Task<GatedOutput> OptimizeAsync(
        string svgPath,
        string? outputPath,
        double tolerance,
        double minSsim,
        double maxRmse,
        CancellationToken cancellationToken,
        int maxIterations = 4,
        bool useScour = true,
        int? precision = null)
    {
        var originalText = await File.ReadAllTextAsync(svgPath, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        var originalSize = Encoding.UTF8.GetByteCount(originalText);
        var originalNodes = SvgSimplifier.CountPathNodes(originalText);

        Directory.CreateDirectory(scratch);
        var work = Path.Join(scratch, $"gate-{Guid.NewGuid():N}");
        Directory.CreateDirectory(work);
        var currentTolerance = tolerance;
        var bestTolerance = tolerance;
        var bestSvg = originalText;
        var bestSsim = -1.0;
        var bestRmse = 0.0;
        var bestPsnr = 0.0;
        var gatePassed = false;
        try
        {
            var baseline = await RenderAsync(svgPath, cancellationToken).ConfigureAwait(false);
            for (var iteration = 0; iteration < maxIterations; iteration++)
            {
                var candidate = SvgSimplifier.Simplify(originalText, currentTolerance, useGeometric: true, useScour, precision);
                var candidatePath = Path.Join(work, $"candidate_{iteration}.svg");
                await File.WriteAllTextAsync(candidatePath, candidate, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
                var render = await RenderAsync(candidatePath, cancellationToken).ConfigureAwait(false);
                var drift = DriftMetrics.Drift(baseline, render);

                // The highest-fidelity candidate wins, not the last: backing off does not improve fidelity monotonically.
                if (drift.Ssim >= bestSsim)
                {
                    bestSvg = candidate;
                    bestSsim = drift.Ssim;
                    bestRmse = drift.RmsePct;
                    bestPsnr = drift.PsnrDb;
                    bestTolerance = currentTolerance;
                }

                if (drift.Ssim >= minSsim && drift.RmsePct <= maxRmse)
                {
                    gatePassed = true;
                    break;
                }

                currentTolerance *= 0.5;
            }
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }

        var outFile = outputPath ?? svgPath;
        if (!gatePassed && IsSameFile(outFile, svgPath))
        {
            throw new DriftGateNotMetException(
                $"Simplification of {svgPath} did not meet the drift budget "
                + $"(ssim {PythonNumber.FormatFixed(bestSsim, 4)} < {Repr(minSsim)}, rmse {PythonNumber.FormatFixed(bestRmse, 3)}% > {Repr(maxRmse)}%). "
                + "Refusing to overwrite the input in place; pass --output to keep the candidate.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outFile))!);
        await File.WriteAllTextAsync(outFile, bestSvg, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);

        var finalSize = Encoding.UTF8.GetByteCount(bestSvg);
        var finalNodes = SvgSimplifier.CountPathNodes(bestSvg);
        var sizeReduction = originalSize > 0 ? (originalSize - finalSize) / (double)originalSize * 100.0 : 0.0;
        var nodeReduction = originalNodes > 0 ? (originalNodes - finalNodes) / (double)originalNodes * 100.0 : 0.0;
        var report = new OptimizationReport(
            originalSize,
            finalSize,
            PythonNumber.Round(sizeReduction, 2),
            originalNodes,
            finalNodes,
            PythonNumber.Round(nodeReduction, 2),
            bestTolerance,
            PythonNumber.Round(bestSsim, 4),
            PythonNumber.Round(bestPsnr, 2),
            PythonNumber.Round(bestRmse, 3),
            bestSvg,
            gatePassed);
        return new GatedOutput(outFile, report);
    }

    // A lexical compare is not enough: other casing on a case-insensitive filesystem, or a symlink, names the same file.
    // Existing paths are resolved through their links first; the comparison then follows the platform's casing.
    private static bool IsSameFile(string first, string second)
    {
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(Resolve(first), Resolve(second), comparison);
    }

    private static string Resolve(string path)
    {
        var full = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(full)!;
        var resolvedDirectory = Directory.Exists(directory)
            ? new DirectoryInfo(directory).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? directory
            : directory;
        var file = Path.Join(resolvedDirectory, Path.GetFileName(full));
        return File.Exists(file) ? new FileInfo(file).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? file : file;
    }

    // Python's repr of a float: 1.0 keeps its ".0".
    private static string Repr(double value)
    {
        var text = value.ToString("R", CultureInfo.InvariantCulture);
        return text.Contains('.', StringComparison.Ordinal) || text.Contains('E', StringComparison.Ordinal) ? text : text + ".0";
    }

    private async Task<RgbImage> RenderAsync(string path, CancellationToken cancellationToken) =>
        RasterPipeline.Normalise(await renderer.RenderAsync(path, RenderSize, cancellationToken).ConfigureAwait(false));
}
