using System.Globalization;
using DotMake.CommandLine;
using Rig.Core;
using Rig.Image;

namespace Rig.Cli.Image;

/// <summary>
/// <c>rig image export-avatar</c>: writes every production cut a project's <c>production-cuts.csv</c> declares, and nothing else. SVG cuts are copies,
/// raster cuts are drawn by the authoritative renderer, and the cuts marked <c>favicon</c> are drawn from the builder's favicon variant.
/// </summary>
/// <param name="host">Where the assets are and which renderer draws the cuts.</param>
/// <param name="output">Where the result goes.</param>
/// <param name="error">Where failures go.</param>
[CliCommand(Name = "export-avatar", Description = "Export every production cut declared in the project's production-cuts.csv.")]
internal sealed class ExportAvatarCommand(ImageHost host, TextWriter output, TextWriter error)
{
    /// <summary>The instance DotMake binds onto; rig always builds the wired one instead.</summary>
    internal ExportAvatarCommand()
        : this(null!, TextWriter.Null, TextWriter.Null)
    {
    }

    /// <summary>The target project brand.</summary>
    [CliOption(Name = "--project", Aliases = ["-p"], Required = false, Description = "Target project brand ('rtkelly-labs' or 'parquet-source-generator').")]
    public string Project { get; set; } = ImageProjects.Default;

    /// <summary>Where the cuts go.</summary>
    [CliOption(Name = "--output-dir", Aliases = ["-o"], Required = false, Description = "Directory where the cuts are saved. Defaults to assets/<project>/production-cuts/.")]
    public string? OutputDir { get; set; }

    /// <summary>A browser executable to use instead of Playwright's.</summary>
    [CliOption(Name = "--chromium", Required = false, Description = "A Chromium executable to use instead of the one Playwright installed.")]
    public string? Chromium { get; set; }

    /// <summary>Exports and returns the exit code.</summary>
    public Task<int> RunAsync(CliContext context) => ImageGuard.RunAsync(error, async () =>
    {
        var cancellationToken = context.CancellationToken;
        var project = ImageProjects.Resolve(host.RepoRoot, Project);
        var cutsFolder = Path.Join(host.RepoRoot, "assets", project.Name, "production-cuts");
        var cuts = ProductionCuts.Read(Path.Join(cutsFolder, ProductionCuts.CsvName));
        var target = Path.GetFullPath(OutputDir ?? cutsFolder);
        var master = ImageInputs.Require(project.Svg, "the project master SVG");
        var scratch = Path.Join(host.Scratch, "export-avatar");
        Directory.CreateDirectory(scratch);
        var favicon = Path.Join(scratch, $"{project.Name}-favicon.svg");
        await File.WriteAllTextAsync(favicon, project.GenerateSvg(ProductionCuts.FaviconVariant), new System.Text.UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        var renderer = await host.LaunchAuthoritative(Chromium).ConfigureAwait(false);
        await using (renderer.ConfigureAwait(false))
        {
            var written = await ProductionCuts.ExportAsync(cuts, target, master, favicon, renderer, cancellationToken).ConfigureAwait(false);
            await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"Exported {written.Count} {project.Name} cuts to: {target}")).ConfigureAwait(false);
        }

        return (int)ExitCode.Clean;
    });
}
