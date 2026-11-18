using DotMake.CommandLine;
using Rig.Core;
using Rig.Image;

namespace Rig.Cli.Image;

/// <summary>
/// <c>rig image diagnose</c>: decomposes a render's drift into structural and chromatic channels with actionable guidance.
/// </summary>
/// <param name="host">Where the assets are and which renderer draws an SVG.</param>
/// <param name="output">Where the report goes.</param>
/// <param name="error">Where failures go.</param>
[CliCommand(Name = "diagnose", Description = "Decompose visual drift into structural/geometric vs chromatic/color channels with actionable guidance.")]
internal sealed class DiagnoseCommand(ImageHost host, TextWriter output, TextWriter error)
{
    /// <summary>The instance DotMake binds onto; rig always builds the wired one instead.</summary>
    internal DiagnoseCommand()
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

    /// <summary>The canvas size.</summary>
    [CliOption(Name = "--size", Required = false, Description = "Canvas size for rendering and evaluation.")]
    public int Size { get; set; } = 1000;

    /// <summary>A browser executable to use instead of Playwright's.</summary>
    [CliOption(Name = "--chromium", Required = false, Description = "A Chromium executable to use instead of the one Playwright installed.")]
    public string? Chromium { get; set; }

    /// <summary>Diagnoses and returns the exit code.</summary>
    public Task<int> RunAsync(CliContext context) => ImageGuard.RunAsync(error, async () =>
    {
        var cancellationToken = context.CancellationToken;
        if (Size < 5)
        {
            throw new ArgumentException($"expected --size of at least 5, found {Size}");
        }

        var project = ImageProjects.Resolve(host.RepoRoot, Project);
        var referencePath = ImageInputs.Require(Reference ?? project.Reference, "a reference image");
        var reference = await ImageInputs.LoadAsync(referencePath, Size, cancellationToken).ConfigureAwait(false);
        var render = Render is not null
            ? await ImageInputs.LoadAsync(ImageInputs.Require(Render, "a render image"), Size, cancellationToken).ConfigureAwait(false)
            : await ImageInputs.RenderOnceAsync(host, Chromium, ImageInputs.Require(Svg ?? project.Svg, "an SVG file"), Size, cancellationToken).ConfigureAwait(false);
        await output.WriteAsync(ImageReports.Diagnostics(DriftMetrics.Drift(reference, render))).ConfigureAwait(false);
        return (int)ExitCode.Clean;
    });
}
