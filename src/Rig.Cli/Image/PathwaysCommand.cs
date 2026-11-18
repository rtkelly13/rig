using System.Globalization;
using System.Text;
using DotMake.CommandLine;
using Rig.Core;
using Rig.Image;
using Rig.Image.Pathways;

namespace Rig.Cli.Image;

/// <summary>
/// <c>rig image pathways</c>: builds the mark by every pathway and compares them on edge quality and, when Chromium is available, drift. Exit codes: 0 reported,
/// 2 an input could not be handled. Nothing is promoted.
/// </summary>
/// <param name="host">Where assets are, which renderer scores and which tracer runs VTracer.</param>
/// <param name="output">Where the table goes.</param>
/// <param name="error">Where failures go.</param>
[CliCommand(Name = "pathways", Description = "Build the mark by every available pathway and compare them on edge quality and drift.")]
internal sealed class PathwaysCommand(ImageHost host, TextWriter output, TextWriter error)
{
    /// <summary>The instance DotMake binds onto; rig always builds the wired one instead.</summary>
    internal PathwaysCommand()
        : this(null!, TextWriter.Null, TextWriter.Null)
    {
    }

    /// <summary>The target project brand.</summary>
    [CliOption(Name = "--project", Aliases = ["-p"], Required = false, Description = "Target project brand.")]
    public string Project { get; set; } = ImageProjects.Default;

    /// <summary>Pathway keys to build.</summary>
    [CliOption(Name = "--only", Required = false, Description = "Comma-separated pathway keys. Defaults to all.")]
    public string? Only { get; set; }

    /// <summary>The canonical edge angles.</summary>
    [CliOption(Name = "--axes", Required = false, Description = "Canonical edge angles, comma-separated.")]
    public string Axes { get; set; } = "0,30,60,90,120,150";

    /// <summary>Where to write each pathway's SVG.</summary>
    [CliOption(Name = "--out-dir", Required = false, Description = "Write each pathway's SVG here for inspection.")]
    public string? OutDir { get; set; }

    /// <summary>A browser executable to use instead of Playwright's Chromium.</summary>
    [CliOption(Name = "--chromium", Required = false, Description = "Score with this Chromium executable instead of the one Playwright installed.")]
    public string? Chromium { get; set; }

    /// <summary>Builds, prints and returns the exit code.</summary>
    public async Task<int> RunAsync(CliContext context)
    {
        var cancellationToken = context.CancellationToken;
        ImageProject project;
        IReadOnlyList<double> axes;
        IReadOnlyList<string> keys;
        try
        {
            project = ImageProjects.Resolve(host.RepoRoot, Project);
            axes = ImageOptions.ParseAxes(Axes);
            keys = Only is null ? [.. PathwayCatalog.All.Keys] : [.. Only.Split(',').Select(k => k.Trim())];
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            await error.WriteLineAsync($"Error: {exception.Message}").ConfigureAwait(false);
            return (int)ExitCode.Error;
        }

        var unknown = keys.Where(k => !PathwayCatalog.All.ContainsKey(k)).ToArray();
        if (unknown.Length > 0)
        {
            await error.WriteLineAsync($"Error: expected pathways from {string.Join(", ", PathwayCatalog.All.Keys)}, found {string.Join(", ", unknown)}").ConfigureAwait(false);
            return (int)ExitCode.Error;
        }

        if (!File.Exists(project.Svg))
        {
            await error.WriteLineAsync($"Error: expected the master SVG at {project.Svg}, found none").ConfigureAwait(false);
            return (int)ExitCode.Error;
        }

        ISvgRenderer? renderer = null;
        try
        {
            try
            {
                renderer = await host.LaunchAuthoritative(Chromium).ConfigureAwait(false);
            }
            catch (RendererUnavailableException exception)
            {
                await output.WriteLineAsync($"Chromium unavailable ({exception.Message}): reporting edge quality only; drift columns omitted.").ConfigureAwait(false);
            }

            return await BuildAllAsync(project, axes, keys, renderer, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException or VtracerChecksumException or HttpRequestException or FormatException or ParameterOverrideException or PlatformNotSupportedException || (exception is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            await error.WriteLineAsync($"Error: {exception.Message}").ConfigureAwait(false);
            return (int)ExitCode.Error;
        }
        finally
        {
            if (renderer is not null)
            {
                await renderer.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task<int> BuildAllAsync(ImageProject project, IReadOnlyList<double> axes, IReadOnlyList<string> keys, ISvgRenderer? renderer, CancellationToken cancellationToken)
    {
        var scorer = renderer is null ? null : new ImageScorer(renderer, project, Path.Join(host.Scratch, "pathways"));
        var master = await File.ReadAllTextAsync(project.Svg, cancellationToken).ConfigureAwait(false);
        var context = new PathwayContext(master, project.Reference)
        {
            Axes = axes,
            Tracer = host.Tracer,
            Score = scorer is null ? null : async (svg, token) => (await scorer.ScoreAsync(svg, "fit", token).ConfigureAwait(false)).Score.CompositePct,
        };

        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"PATHWAYS: {project.Name}");
        text.Append(CultureInfo.InvariantCulture, $"{"Pathway",-24} {"Conformance",12} {"Edges",7} {"Defects",8}");
        text.AppendLine(renderer is null ? string.Empty : string.Create(CultureInfo.InvariantCulture, $" {"SSIM",8} {"Composite",10}"));
        foreach (var key in keys)
        {
            var pathway = PathwayCatalog.All[key];
            if (pathway.NeedsScoring && renderer is null)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"Skipping '{key}': needs a renderer.");
                continue;
            }

            var svg = await pathway.Generate(context, cancellationToken).ConfigureAwait(false);
            var report = EdgeRegularity.Analyze(svg, axes);
            var defects = report.Count(DefectKind.MicroSegment) + report.Count(DefectKind.OffAxis) + report.Count(DefectKind.Staircase);
            text.Append(CultureInfo.InvariantCulture, $"{pathway.Name,-24} {report.AxisConformancePct,11:0.0}% {report.TotalEdges,7} {defects,8}");
            if (scorer is not null)
            {
                var scored = await scorer.ScoreAsync(svg, $"pathway-{key}", cancellationToken).ConfigureAwait(false);
                text.Append(CultureInfo.InvariantCulture, $" {scored.Drift.Ssim,8:0.0000} {scored.Score.CompositePct,9:0.000}%");
            }

            text.AppendLine();
            if (OutDir is not null)
            {
                Directory.CreateDirectory(OutDir);
                await File.WriteAllTextAsync(Path.Join(OutDir, $"{project.Name}-{key}.svg"), svg, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            }
        }

        if (OutDir is not null)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"Candidates written to {Path.GetFullPath(OutDir)}");
        }

        text.AppendLine("\nNothing promoted. Higher conformance is cleaner geometry; higher SSIM is closer to the reference.");
        await output.WriteAsync(text.ToString(), CancellationToken.None).ConfigureAwait(false);
        return (int)ExitCode.Clean;
    }
}
