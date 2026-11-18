using DotMake.CommandLine;
using Rig.Core;

namespace Rig.Cli.Image;

/// <summary>
/// <c>rig image extract-feature</c>: crops one named feature region out of a raster or an SVG for fast independent analysis. An SVG is rendered
/// first, by the authoritative renderer.
/// </summary>
/// <param name="host">Where the assets are and which renderer draws an SVG.</param>
/// <param name="output">Where the result goes.</param>
/// <param name="error">Where failures go.</param>
[CliCommand(Name = "extract-feature", Description = "Extract an isolated feature-region crop from an image for fast independent analysis.")]
internal sealed class ExtractFeatureCommand(ImageHost host, TextWriter output, TextWriter error)
{
    /// <summary>The instance DotMake binds onto; rig always builds the wired one instead.</summary>
    internal ExtractFeatureCommand()
        : this(null!, TextWriter.Null, TextWriter.Null)
    {
    }

    /// <summary>The image to crop.</summary>
    [CliOption(Name = "--image", Aliases = ["-i"], Required = true, Description = "Path to a reference or rendered image, or an SVG.")]
    public string Image { get; set; } = string.Empty;

    /// <summary>The feature region.</summary>
    [CliOption(Name = "--feature", Aliases = ["-f"], Required = true, Description = "Pre-defined feature region.")]
    public string Feature { get; set; } = string.Empty;

    /// <summary>Where to write the crop.</summary>
    [CliOption(Name = "--output", Aliases = ["-o"], Required = true, Description = "Output PNG path for the extracted crop.")]
    public string OutputPath { get; set; } = string.Empty;

    /// <summary>The target project brand.</summary>
    [CliOption(Name = "--project", Aliases = ["-p"], Required = false, Description = "Target project brand ('rtkelly-labs' or 'parquet-source-generator').")]
    public string Project { get; set; } = ImageProjects.Default;

    /// <summary>Crops and returns the exit code.</summary>
    public Task<int> RunAsync(CliContext context) => ImageGuard.RunAsync(error, async () =>
    {
        var cancellationToken = context.CancellationToken;
        var source = ImageInputs.Require(Image, "an image file");
        var project = ImageProjects.Resolve(host.RepoRoot, Project);
        var box = ImageInputs.Region(project, Feature);
        var image = source.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)
            ? await ImageInputs.RenderOnceAsync(host, null, source, Rig.Image.RasterPipeline.NormalisedSize, cancellationToken).ConfigureAwait(false)
            : await ImageInputs.LoadAsync(source, Rig.Image.RasterPipeline.NormalisedSize, cancellationToken).ConfigureAwait(false);
        await ImageInputs.WritePngAsync(OutputPath, ImageInputs.Crop(image, box), cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync($"Extracted feature region '{Feature}' to: {Path.GetFullPath(OutputPath)}").ConfigureAwait(false);
        return (int)ExitCode.Clean;
    });
}
