using System.Globalization;
using DotMake.CommandLine;
using Rig.Core;
using Rig.Image;

namespace Rig.Cli.Image;

/// <summary>
/// <c>rig image contact-sheet</c>: a reference-versus-candidates comparison sheet as one PNG, the composition the preview gallery uses.
/// </summary>
/// <param name="host">Where the assets are and which renderer draws the candidates.</param>
/// <param name="output">Where the result goes.</param>
/// <param name="error">Where failures go.</param>
[CliCommand(Name = "contact-sheet", Description = "Compose a reference-vs-candidates comparison sheet as a single PNG.")]
internal sealed class ContactSheetCommand(ImageHost host, TextWriter output, TextWriter error)
{
    /// <summary>The instance DotMake binds onto; rig always builds the wired one instead.</summary>
    internal ContactSheetCommand()
        : this(null!, TextWriter.Null, TextWriter.Null)
    {
    }

    /// <summary>The target project brand.</summary>
    [CliOption(Name = "--project", Aliases = ["-p"], Required = false, Description = "Target project brand ('rtkelly-labs' or 'parquet-source-generator').")]
    public string Project { get; set; } = ImageProjects.Default;

    /// <summary>One feature region.</summary>
    [CliOption(Name = "--feature", Aliases = ["-f"], Required = false, Description = "Feature region to crop. Omitted, uses the whole logo.")]
    public string? Feature { get; set; }

    /// <summary>Where to write the sheet.</summary>
    [CliOption(Name = "--output", Aliases = ["-o"], Required = false, Description = "Where to write the sheet PNG.")]
    public string OutputPath { get; set; } = "contact_sheet.png";

    /// <summary>The reference image.</summary>
    [CliOption(Name = "--reference", Aliases = ["-r"], Required = false, Description = "Canonical reference image. Defaults to the project reference.")]
    public string? Reference { get; set; }

    /// <summary>Which variants to include.</summary>
    [CliOption(Name = "--variants", Required = false, Description = "Comma-separated variant keys. Omitted, uses all of them.")]
    public string? Variants { get; set; }

    /// <summary>The panel size.</summary>
    [CliOption(Name = "--cell", Required = false, Description = "Pixel size of each panel.")]
    public int Cell { get; set; } = 200;

    /// <summary>A browser executable to use instead of Playwright's.</summary>
    [CliOption(Name = "--chromium", Required = false, Description = "A Chromium executable to use instead of the one Playwright installed.")]
    public string? Chromium { get; set; }

    /// <summary>Composes the sheet and returns the exit code.</summary>
    public Task<int> RunAsync(CliContext context) => ImageGuard.RunAsync(error, async () =>
    {
        var cancellationToken = context.CancellationToken;
        var project = ImageProjects.Resolve(host.RepoRoot, Project);
        var referencePath = ImageInputs.Require(Reference ?? project.Reference, "a reference image");
        FeatureBox? box = Feature is null ? null : ImageInputs.Region(project, Feature);
        var keys = ImageInputs.VariantKeys(project, Variants);
        var scratch = Path.Join(host.Scratch, "contact-sheet");
        Directory.CreateDirectory(scratch);
        var reference = await ImageInputs.LoadAsync(referencePath, RasterPipeline.NormalisedSize, cancellationToken).ConfigureAwait(false);
        var panels = new List<SheetPanel> { new(box is null ? reference : ImageInputs.Crop(reference, box), "reference", Highlight: true) };
        var renderer = await host.LaunchAuthoritative(Chromium).ConfigureAwait(false);
        await using (renderer.ConfigureAwait(false))
        {
            foreach (var key in keys)
            {
                var svg = Path.Join(scratch, $"sheet_{key}.svg");
                await File.WriteAllTextAsync(svg, project.GenerateSvg(key), new System.Text.UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
                var image = await ImageInputs.RenderAsync(renderer, svg, RasterPipeline.NormalisedSize, cancellationToken).ConfigureAwait(false);
                panels.Add(new SheetPanel(box is null ? image : ImageInputs.Crop(image, box), key));
                await output.WriteLineAsync($"  {key}").ConfigureAwait(false);
            }
        }

        await ImageInputs.WritePngAsync(OutputPath, Sheets.ContactSheet(panels, Cell, panels.Count), cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"\nContact sheet ({Feature ?? "full logo"}, {panels.Count} panels) written to: {Path.GetFullPath(OutputPath)}")).ConfigureAwait(false);
        return (int)ExitCode.Clean;
    });
}
