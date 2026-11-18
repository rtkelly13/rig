using System.Globalization;
using System.Text;
using DotMake.CommandLine;
using Rig.Core;
using Rig.Image;
using Rig.Image.Pathways;

namespace Rig.Cli.Image;

/// <summary>
/// <c>rig image vectorize</c>: traces a raster to SVG with the pinned VTracer and scores the result against the input.
/// </summary>
/// <param name="host">Which tracer and renderer to use.</param>
/// <param name="output">Where the report goes.</param>
/// <param name="error">Where failures go.</param>
[CliCommand(Name = "vectorize", Description = "Deterministically vectorize a raster PNG/JPG to SVG with the pinned VisionCortex VTracer.")]
internal sealed class VectorizeCommand(ImageHost host, TextWriter output, TextWriter error)
{
    /// <summary>The instance DotMake binds onto; rig always builds the wired one instead.</summary>
    internal VectorizeCommand()
        : this(null!, TextWriter.Null, TextWriter.Null)
    {
    }

    /// <summary>The raster to trace.</summary>
    [CliOption(Name = "--image", Aliases = ["-i"], Required = true, Description = "Path to the input raster image (PNG/JPG).")]
    public string Image { get; set; } = string.Empty;

    /// <summary>Where to write the SVG.</summary>
    [CliOption(Name = "--output", Aliases = ["-o"], Required = false, Description = "Path to the output SVG file.")]
    public string OutputPath { get; set; } = "vectorized.svg";

    /// <summary>The curve mode.</summary>
    [CliOption(Name = "--mode", Aliases = ["-m"], Required = false, Description = "'polygon' (isometric/sharp edges), 'spline' (curves) or 'none'.")]
    public string Mode { get; set; } = "polygon";

    /// <summary>Colour precision.</summary>
    [CliOption(Name = "--color-precision", Aliases = ["-c"], Required = false, Description = "Color precision bits (1-8).")]
    public int ColorPrecision { get; set; } = 5;

    /// <summary>Layer difference.</summary>
    [CliOption(Name = "--layer-difference", Aliases = ["-l"], Required = false, Description = "Color difference threshold between layers.")]
    public int LayerDifference { get; set; } = 20;

    /// <summary>Speckle filter.</summary>
    [CliOption(Name = "--filter-speckle", Required = false, Description = "Discard noise speckles below this pixel area.")]
    public int FilterSpeckle { get; set; } = 4;

    /// <summary>Corner threshold.</summary>
    [CliOption(Name = "--corner-threshold", Required = false, Description = "Corner smoothness threshold in degrees.")]
    public int CornerThreshold { get; set; } = 60;

    /// <summary>Segment length.</summary>
    [CliOption(Name = "--length-threshold", Required = false, Description = "Minimum segment length threshold.")]
    public double LengthThreshold { get; set; } = 4.0;

    /// <summary>Path precision.</summary>
    [CliOption(Name = "--path-precision", Required = false, Description = "Coordinate decimal precision (1-3).")]
    public int PathPrecision { get; set; } = 2;

    /// <summary>Layering.</summary>
    [CliOption(Name = "--hierarchical", Required = false, Description = "'stacked' (layered) or 'cutout' (non-overlapping).")]
    public string Hierarchical { get; set; } = "stacked";

    /// <summary>Skip scoring.</summary>
    [CliOption(Name = "--no-compare", Required = false, Description = "Skip rendering and scoring the output SVG against the input image.")]
    public bool NoCompare { get; set; }

    /// <summary>Where to write the comparison sheet.</summary>
    [CliOption(Name = "--output-sheet", Required = false, Description = "Write a 3-panel comparison sheet [Reference | Render | Diff].")]
    public string? OutputSheet { get; set; }

    /// <summary>A browser executable to use instead of Playwright's.</summary>
    [CliOption(Name = "--chromium", Required = false, Description = "A Chromium executable to use instead of the one Playwright installed.")]
    public string? Chromium { get; set; }

    /// <summary>Traces, scores and returns the exit code.</summary>
    public Task<int> RunAsync(CliContext context) => ImageGuard.RunAsync(error, async () =>
    {
        var cancellationToken = context.CancellationToken;
        var source = ImageInputs.Require(Image, "an image file");
        if (host.Tracer is not { } tracer)
        {
            throw new InvalidOperationException("expected a VTracer to be wired in, found none");
        }

        var config = new VtracerConfig
        {
            Mode = Mode,
            Hierarchical = Hierarchical,
            FilterSpeckle = FilterSpeckle,
            ColorPrecision = ColorPrecision,
            LayerDifference = LayerDifference,
            CornerThreshold = CornerThreshold,
            LengthThreshold = LengthThreshold,
            PathPrecision = PathPrecision,
        };
        await output.WriteLineAsync($"Vectorizing {Path.GetFileName(source)} in mode='{Mode}', CP={ColorPrecision}, LD={LayerDifference}...").ConfigureAwait(false);
        var svg = await tracer.TraceAsync(source, config, cancellationToken).ConfigureAwait(false);
        var target = Path.GetFullPath(OutputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await File.WriteAllTextAsync(target, svg, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        var sizeKb = new FileInfo(target).Length / 1024.0;
        await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"Vectorized SVG written to: {target} ({sizeKb:0.0} KB)")).ConfigureAwait(false);
        if (NoCompare)
        {
            return (int)ExitCode.Clean;
        }

        var reference = await ImageInputs.LoadAsync(source, RasterPipeline.NormalisedSize, cancellationToken).ConfigureAwait(false);
        var render = await ImageInputs.RenderOnceAsync(host, Chromium, target, RasterPipeline.NormalisedSize, cancellationToken).ConfigureAwait(false);
        var drift = DriftMetrics.Drift(reference, render);
        var text = new StringBuilder(ImageReports.Summary("Vectorization Scoring", drift));
        text.AppendLine(string.Create(CultureInfo.InvariantCulture, $"Deterministic SVG Size: {sizeKb:0.0} KB"));
        await output.WriteAsync(text.ToString()).ConfigureAwait(false);
        if (OutputSheet is not null)
        {
            var sheet = Sheets.ComparisonSheet(reference, render, DriftReport.Heatmap(reference, render), drift.RmsePct, drift.Ssim);
            await ImageInputs.WritePngAsync(OutputSheet, sheet, cancellationToken).ConfigureAwait(false);
            await output.WriteLineAsync($"Comparison sheet saved to: {Path.GetFullPath(OutputSheet)}").ConfigureAwait(false);
        }

        return (int)ExitCode.Clean;
    });
}
