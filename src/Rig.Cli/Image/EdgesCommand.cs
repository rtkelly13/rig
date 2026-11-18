using System.Globalization;
using System.Text;
using DotMake.CommandLine;
using Rig.Core;
using Rig.Image;
using Rig.Image.Pathways;

namespace Rig.Cli.Image;

/// <summary>
/// <c>rig image edges</c>: how well a mark's edges conform to its canonical axes, and optionally a regularized copy. Exit codes: 0 reported, 1 conformance below
/// <c>--min-conformance</c>, 2 an input could not be handled. Nothing is promoted.
/// </summary>
/// <param name="host">Where assets are and which renderer scores a fix.</param>
/// <param name="output">Where the report goes.</param>
/// <param name="error">Where failures go.</param>
[CliCommand(Name = "edges", Description = "Report how well a mark's edges conform to its canonical axes; --fix writes a regularized copy and scores it.")]
internal sealed class EdgesCommand(ImageHost host, TextWriter output, TextWriter error)
{
    /// <summary>The instance DotMake binds onto; rig always builds the wired one instead.</summary>
    internal EdgesCommand()
        : this(null!, TextWriter.Null, TextWriter.Null)
    {
    }

    /// <summary>The target project brand.</summary>
    [CliOption(Name = "--project", Aliases = ["-p"], Required = false, Description = "Target project brand.")]
    public string Project { get; set; } = ImageProjects.Default;

    /// <summary>An SVG to analyse instead of the project master.</summary>
    [CliOption(Name = "--svg", Aliases = ["-s"], Required = false, Description = "SVG to analyse. Defaults to the project master.")]
    public string? Svg { get; set; }

    /// <summary>The canonical edge angles.</summary>
    [CliOption(Name = "--axes", Required = false, Description = "Comma-separated canonical edge angles. Default is the pointy-top hexagonal lattice.")]
    public string Axes { get; set; } = "0,30,60,90,120,150";

    /// <summary>How far off an axis still counts as jitter.</summary>
    [CliOption(Name = "--angle-tol", Required = false, Description = "Max degrees off-axis still treated as jitter to snap.")]
    public double AngleTol { get; set; } = 6.0;

    /// <summary>The shortest segment that is not a stepping artefact.</summary>
    [CliOption(Name = "--min-segment", Required = false, Description = "Segments shorter than this are stepping artefacts.")]
    public double MinSegment { get; set; } = 5.0;

    /// <summary>How many defects to list.</summary>
    [CliOption(Name = "--limit", Required = false, Description = "How many individual defects to list.")]
    public int Limit { get; set; } = 10;

    /// <summary>The conformance floor, for use as a CI ratchet.</summary>
    [CliOption(Name = "--min-conformance", Required = false, Description = "Exit non-zero if axis conformance falls below this percentage.")]
    public double? MinConformance { get; set; }

    /// <summary>Where to write a regularized copy.</summary>
    [CliOption(Name = "--fix", Required = false, Description = "Write a regularized copy to this path and score it.")]
    public string? Fix { get; set; }

    /// <summary>Analyses, prints and returns the exit code.</summary>
    public async Task<int> RunAsync(CliContext context)
    {
        var cancellationToken = context.CancellationToken;
        ImageProject project;
        IReadOnlyList<double> axes;
        try
        {
            project = ImageProjects.Resolve(host.RepoRoot, Project);
            axes = ImageOptions.ParseAxes(Axes);
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            await error.WriteLineAsync($"Error: {exception.Message}").ConfigureAwait(false);
            return (int)ExitCode.Error;
        }

        var target = Svg is null ? project.Svg : Path.GetFullPath(Svg);
        if (!File.Exists(target))
        {
            await error.WriteLineAsync($"Error: expected an SVG file at {target}, found none").ConfigureAwait(false);
            return (int)ExitCode.Error;
        }

        var source = await File.ReadAllTextAsync(target, cancellationToken).ConfigureAwait(false);
        var report = EdgeRegularity.Analyze(source, axes, angleTol: AngleTol, minSegment: MinSegment);
        await output.WriteAsync(Table(target, axes, report)).ConfigureAwait(false);

        if (MinConformance is { } floor && report.AxisConformancePct < floor)
        {
            await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"\nAxis conformance {report.AxisConformancePct:0.00}% is below the required {floor:0.00}%.")).ConfigureAwait(false);
            return (int)ExitCode.Drift;
        }

        if (Fix is null)
        {
            return (int)ExitCode.Clean;
        }

        var fixedText = EdgeRegularity.RegularizeSvg(source, axes, AngleTol, MinSegment);
        var destination = Path.GetFullPath(Fix);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await File.WriteAllTextAsync(destination, fixedText, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        var after = EdgeRegularity.Analyze(fixedText, axes, angleTol: AngleTol, minSegment: MinSegment);
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"\nWrote regularized SVG to {destination}");
        text.AppendLine(CultureInfo.InvariantCulture, $"  Axis conformance: {report.AxisConformancePct:0.0}% -> {after.AxisConformancePct:0.0}%");
        text.AppendLine(CultureInfo.InvariantCulture, $"  Micro segments:   {report.Count(DefectKind.MicroSegment)} -> {after.Count(DefectKind.MicroSegment)}");
        text.AppendLine(CultureInfo.InvariantCulture, $"  Off-axis edges:   {report.Count(DefectKind.OffAxis)} -> {after.Count(DefectKind.OffAxis)}");
        await output.WriteAsync(text.ToString()).ConfigureAwait(false);
        await ScoreFixAsync(project, source, fixedText, cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync("\nNothing was promoted. Review, then use `promote` if you want this to become the master.").ConfigureAwait(false);
        return (int)ExitCode.Clean;
    }

    private async Task ScoreFixAsync(ImageProject project, string before, string after, CancellationToken cancellationToken)
    {
        try
        {
            var renderer = await host.LaunchAuthoritative(null).ConfigureAwait(false);
            await using (renderer.ConfigureAwait(false))
            {
                var scorer = new ImageScorer(renderer, project, Path.Join(host.Scratch, "edges"));
                var first = await scorer.ScoreAsync(before, "before", cancellationToken).ConfigureAwait(false);
                var second = await scorer.ScoreAsync(after, "after", cancellationToken).ConfigureAwait(false);
                await output.WriteLineAsync(
                    string.Create(CultureInfo.InvariantCulture, $"  Drift vs reference: SSIM {first.Drift.Ssim:0.0000} -> {second.Drift.Ssim:0.0000}, composite {first.Score.CompositePct:0.000}% -> {second.Score.CompositePct:0.000}%"), CancellationToken.None).ConfigureAwait(false);
                if (second.Drift.Ssim < first.Drift.Ssim)
                {
                    await output.WriteLineAsync("  Fidelity fell. The reference raster's own edges are not exactly on-axis, so geometric correctness and reference fidelity pull apart here. Judge by eye, not by SSIM.", CancellationToken.None).ConfigureAwait(false);
                }
            }
        }
        catch (RendererUnavailableException exception)
        {
            await output.WriteLineAsync($"  Drift not scored: {exception.Message}", CancellationToken.None).ConfigureAwait(false);
        }
    }

    private string Table(string target, IReadOnlyList<double> axes, EdgeReport report)
    {
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"EDGE_REGULARITY: {Path.GetFileName(target)}");
        text.AppendLine(CultureInfo.InvariantCulture, $"{"Axis conformance",-18} {report.AxisConformancePct,8:0.0}%  edge length lying on {string.Join('/', axes.Select(a => a.ToString("0.######", CultureInfo.InvariantCulture) + "deg"))}");
        text.AppendLine(CultureInfo.InvariantCulture, $"{"Total edges",-18} {report.TotalEdges,9}  polygon segments examined");
        text.AppendLine(CultureInfo.InvariantCulture, $"{"Micro segments",-18} {report.Count(DefectKind.MicroSegment),9}  shorter than {MinSegment:0.######}px, tracer stepping");
        text.AppendLine(CultureInfo.InvariantCulture, $"{"Off-axis edges",-18} {report.Count(DefectKind.OffAxis),9}  within {AngleTol:0.######} degrees of an axis but not on it");
        text.AppendLine(CultureInfo.InvariantCulture, $"{"Staircase runs",-18} {report.Count(DefectKind.Staircase),9}  short alternating jogs along one line");
        if (report.Defects.Count > 0 && Limit > 0)
        {
            text.AppendLine("\nWorst offenders (by deviation, then length):");
            foreach (var defect in report.Defects.OrderBy(d => -Math.Abs(d.Deviation)).ThenBy(d => -d.Length).Take(Limit))
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"  - {defect.Describe()}");
            }
        }

        return text.ToString();
    }
}
