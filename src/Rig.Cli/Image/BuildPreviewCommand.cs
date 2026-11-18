using System.Text;
using DotMake.CommandLine;
using Rig.Core;
using Rig.Image;
using Rig.Image.Gallery;

namespace Rig.Cli.Image;

/// <summary>
/// <c>rig image build-preview</c>: renders every candidate with the authoritative renderer, scores it against the reference and writes the gallery
/// page, optionally with a screenshot. The committed master is never written.
/// </summary>
/// <param name="host">Where the assets are and which renderer draws the candidates.</param>
/// <param name="output">Where the progress goes.</param>
/// <param name="error">Where failures go.</param>
[CliCommand(Name = "build-preview", Description = "Render every candidate, score it against the reference, and build the preview gallery.")]
internal sealed class BuildPreviewCommand(ImageHost host, TextWriter output, TextWriter error)
{
    /// <summary>The instance DotMake binds onto; rig always builds the wired one instead.</summary>
    internal BuildPreviewCommand()
        : this(null!, TextWriter.Null, TextWriter.Null)
    {
    }

    /// <summary>The target project brand.</summary>
    [CliOption(Name = "--project", Aliases = ["-p"], Required = false, Description = "Target project brand ('rtkelly-labs' or 'parquet-source-generator').")]
    public string Project { get; set; } = ImageProjects.Default;

    /// <summary>The master named in the single-target page.</summary>
    [CliOption(Name = "--svg", Aliases = ["-s"], Required = false, Description = "The target SVG the single-target page scores. Defaults to the project master.")]
    public string? Svg { get; set; }

    /// <summary>The reference image.</summary>
    [CliOption(Name = "--reference", Aliases = ["-r"], Required = false, Description = "The canonical reference image to diff against. Defaults to the project reference.")]
    public string? Reference { get; set; }

    /// <summary>Where the page goes.</summary>
    [CliOption(Name = "--output-dir", Aliases = ["-o"], Required = false, Description = "Directory where preview.html and screenshot.png are written. Defaults to temp/logo-preview.")]
    public string? OutputDir { get; set; }

    /// <summary>Build the single-target page.</summary>
    [CliOption(Name = "--single", Required = false, Description = "Build the single-target page instead of the multi-candidate wave gallery.")]
    public bool Single { get; set; }

    /// <summary>Skip the screenshot.</summary>
    [CliOption(Name = "--no-screenshot", Required = false, Description = "Do not capture screenshot.png of the rendered page.")]
    public bool NoScreenshot { get; set; }

    /// <summary>Which variants to include.</summary>
    [CliOption(Name = "--variants", Required = false, Description = "Comma-separated variant keys to include. Omitted, uses all of them.")]
    public string? Variants { get; set; }

    /// <summary>Which page to build.</summary>
    [CliOption(Name = "--mode", Aliases = ["-m"], Required = false, Description = "'choose' for a selection gallery (no scores), 'drift' for the scored wave gallery.")]
    public string Mode { get; set; } = "drift";

    /// <summary>A browser executable to use instead of Playwright's.</summary>
    [CliOption(Name = "--chromium", Required = false, Description = "A Chromium executable to use instead of the one Playwright installed.")]
    public string? Chromium { get; set; }

    /// <summary>Builds the page and returns the exit code.</summary>
    public Task<int> RunAsync(CliContext context) => ImageGuard.RunAsync(error, async () =>
    {
        var cancellationToken = context.CancellationToken;
        if (Mode is not ("choose" or "drift"))
        {
            throw new ArgumentException($"expected --mode choose or drift, found '{Mode}'");
        }

        var project = ImageProjects.Resolve(host.RepoRoot, Project);
        var referencePath = ImageInputs.Require(Reference ?? project.Reference, "a reference image");
        var keys = ImageInputs.VariantKeys(project, Variants);
        var variants = keys.ToDictionary(key => key, key => project.Variants[key], StringComparer.Ordinal);
        var input = new GalleryInput(project.Name, referencePath, Path.GetFullPath(Svg ?? project.Svg), project.LabelledRegions, variants, project.DefaultVariant, project.GenerateSvg);
        var folder = Path.GetFullPath(OutputDir ?? Path.Join(host.RepoRoot, "temp", "logo-preview"));
        Directory.CreateDirectory(folder);
        var scratch = Path.Join(host.Scratch, "preview");
        var renderer = await host.LaunchAuthoritative(Chromium).ConfigureAwait(false);
        await using (renderer.ConfigureAwait(false))
        {
            var run = Mode == "choose"
                ? await GalleryBuilder.ChooseAsync(input, renderer, scratch, cancellationToken).ConfigureAwait(false)
                : Single || variants.Count == 0
                    ? await GalleryBuilder.SingleAsync(input, renderer, scratch, cancellationToken).ConfigureAwait(false)
                    : await GalleryBuilder.WaveAsync(input, renderer, scratch, cancellationToken).ConfigureAwait(false);
            foreach (var line in run.Lines)
            {
                await output.WriteLineAsync(line).ConfigureAwait(false);
            }

            var page = Path.Join(folder, "preview.html");
            await File.WriteAllTextAsync(page, run.Html, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            await output.WriteLineAsync($"preview.html written to: {page}").ConfigureAwait(false);
            if (!NoScreenshot)
            {
                if (renderer is not ChromiumRenderer browser)
                {
                    throw new InvalidOperationException("expected the authoritative renderer to be a browser for the screenshot, found another renderer; pass --no-screenshot");
                }

                var shot = Path.Join(folder, "screenshot.png");
                await browser.ScreenshotPageAsync(page, shot, 1280, 960, cancellationToken).ConfigureAwait(false);
                await output.WriteLineAsync($"screenshot.png written to: {shot}").ConfigureAwait(false);
            }
        }

        await output.WriteLineAsync("\nPreview complete; all outputs are deterministic.").ConfigureAwait(false);
        return (int)ExitCode.Clean;
    });
}
