using System.Text;
using DotMake.CommandLine;
using Rig.Core;
using Rig.Image;

namespace Rig.Cli.Image;

/// <summary>
/// <c>rig image compare</c>: pixel drift, SSIM and PSNR of a render against the reference, with the worst regions, per-feature drift, an optional
/// diff heatmap and comparison sheet, and the decomposed diagnostics. An SVG is drawn by the authoritative renderer.
/// </summary>
/// <param name="host">Where the assets are and which renderer draws an SVG.</param>
/// <param name="output">Where the report goes.</param>
/// <param name="error">Where failures go.</param>
[CliCommand(Name = "compare", Description = "Run pixel-by-pixel drift, SSIM, and PSNR comparison against the reference.")]
internal sealed class CompareCommand(ImageHost host, TextWriter output, TextWriter error)
{
    /// <summary>The instance DotMake binds onto; rig always builds the wired one instead.</summary>
    internal CompareCommand()
        : this(null!, TextWriter.Null, TextWriter.Null)
    {
    }

    /// <summary>The target project brand.</summary>
    [CliOption(Name = "--project", Aliases = ["-p"], Required = false, Description = "Target project brand ('rtkelly-labs' or 'parquet-source-generator').")]
    public string Project { get; set; } = ImageProjects.Default;

    /// <summary>The reference image.</summary>
    [CliOption(Name = "--reference", Aliases = ["-r"], Required = false, Description = "Path to the reference image (JPG/PNG). Defaults to the project reference.")]
    public string? Reference { get; set; }

    /// <summary>The SVG to render.</summary>
    [CliOption(Name = "--svg", Aliases = ["-s"], Required = false, Description = "Path to the target SVG. Defaults to the project SVG.")]
    public string? Svg { get; set; }

    /// <summary>A pre-rendered image.</summary>
    [CliOption(Name = "--render", Required = false, Description = "Path to a pre-rendered image (alternative to --svg).")]
    public string? Render { get; set; }

    /// <summary>One feature region.</summary>
    [CliOption(Name = "--feature", Aliases = ["-f"], Required = false, Description = "Optional feature region name.")]
    public string? Feature { get; set; }

    /// <summary>Where to write the diff heatmap.</summary>
    [CliOption(Name = "--output-diff", Aliases = ["-d"], Required = false, Description = "Write a diff heatmap PNG here (off by default).")]
    public string? OutputDiff { get; set; }

    /// <summary>Where to write the comparison sheet.</summary>
    [CliOption(Name = "--output-sheet", Required = false, Description = "Write a 3-panel comparison sheet [Reference | Render | Diff].")]
    public string? OutputSheet { get; set; }

    /// <summary>Decompose the drift.</summary>
    [CliOption(Name = "--decompose", Aliases = ["-D"], Required = false, Description = "Decompose drift into structural/geometric vs chromatic/color channels with guidance.")]
    public bool Decompose { get; set; }

    /// <summary>A browser executable to use instead of Playwright's.</summary>
    [CliOption(Name = "--chromium", Required = false, Description = "A Chromium executable to use instead of the one Playwright installed.")]
    public string? Chromium { get; set; }

    /// <summary>Compares and returns the exit code.</summary>
    public Task<int> RunAsync(CliContext context) => ImageGuard.RunAsync(error, async () =>
    {
        var cancellationToken = context.CancellationToken;
        var project = ImageProjects.Resolve(host.RepoRoot, Project);
        var referencePath = ImageInputs.Require(Reference ?? project.Reference, "a reference image");
        FeatureBox? box = Feature is null ? null : ImageInputs.Region(project, Feature);
        var reference = await ImageInputs.LoadAsync(referencePath, RasterPipeline.NormalisedSize, cancellationToken).ConfigureAwait(false);
        var render = Render is not null
            ? await ImageInputs.LoadAsync(ImageInputs.Require(Render, "a render image"), RasterPipeline.NormalisedSize, cancellationToken).ConfigureAwait(false)
            : await ImageInputs.RenderOnceAsync(host, Chromium, ImageInputs.Require(Svg ?? project.Svg, "an SVG file"), RasterPipeline.NormalisedSize, cancellationToken).ConfigureAwait(false);

        var shownReference = box is null ? reference : ImageInputs.Crop(reference, box);
        var shownRender = box is null ? render : ImageInputs.Crop(render, box);
        var drift = DriftMetrics.Drift(shownReference, shownRender);
        var suffix = Feature is null ? string.Empty : $" (Isolated Feature: {Feature})";
        var text = new StringBuilder();
        text.Append(ImageReports.Summary($"Drift Summary - {project.Name}", drift, suffix));
        text.Append(ImageReports.WorstCells(drift, shownReference.Width, shownReference.Height, suffix));
        if (Feature is null)
        {
            text.AppendLine("Per-Feature Drift");
            foreach (var region in project.Regions)
            {
                var pct = DriftMetrics.RmsePct(ImageInputs.Crop(reference, region), ImageInputs.Crop(render, region));
                text.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"  {region.Key,-20} {pct:0.00}%");
            }
        }

        await output.WriteAsync(text.ToString()).ConfigureAwait(false);
        if (Decompose)
        {
            await output.WriteAsync(ImageReports.Diagnostics(drift)).ConfigureAwait(false);
        }

        if (OutputDiff is null && OutputSheet is null)
        {
            return (int)ExitCode.Clean;
        }

        var heatmap = DriftReport.Heatmap(shownReference, shownRender);
        if (OutputDiff is not null)
        {
            await ImageInputs.WritePngAsync(OutputDiff, heatmap, cancellationToken).ConfigureAwait(false);
            await output.WriteLineAsync($"\nDiff heatmap saved to: {Path.GetFullPath(OutputDiff)}").ConfigureAwait(false);
        }

        if (OutputSheet is not null)
        {
            var sheet = Sheets.ComparisonSheet(shownReference, shownRender, heatmap, drift.RmsePct, drift.Ssim);
            await ImageInputs.WritePngAsync(OutputSheet, sheet, cancellationToken).ConfigureAwait(false);
            await output.WriteLineAsync($"Comparison sheet saved to: {Path.GetFullPath(OutputSheet)}").ConfigureAwait(false);
        }

        return (int)ExitCode.Clean;
    });
}
