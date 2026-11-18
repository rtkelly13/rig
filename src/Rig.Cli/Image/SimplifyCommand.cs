using System.Globalization;
using System.Text;
using DotMake.CommandLine;
using Rig.Core;
using Rig.Image;

namespace Rig.Cli.Image;

/// <summary>
/// <c>rig image simplify</c> (Chromium unless <c>--fast</c>; ADR 0024): multi-pass SVG simplification (geometric decimation, scour-style scrubbing, coordinate
/// quantisation) gated on visual drift against the unsimplified render. Exit codes: 0 gate met, 1 gate not met, 2 an
/// input, renderer or document could not be handled.
/// </summary>
/// <param name="host">Where scratch space is and which renderers to use.</param>
/// <param name="output">Where the table goes.</param>
/// <param name="error">Where failures go.</param>
[CliCommand(Name = "simplify", Description = "Simplify an SVG (geometric decimation, scrubbing, quantisation) and keep only a result that stays within the visual drift budget.")]
internal sealed class SimplifyCommand(ImageHost host, TextWriter output, TextWriter error)
{
    /// <summary>The instance DotMake binds onto; rig always builds the wired one instead.</summary>
    internal SimplifyCommand()
        : this(null!, TextWriter.Null, TextWriter.Null)
    {
    }

    /// <summary>The SVG to simplify.</summary>
    [CliArgument(Name = "svg", Description = "Path to the SVG file to simplify.")]
    public string Svg { get; set; } = string.Empty;

    /// <summary>Where to write the result.</summary>
    [CliOption(Name = "--output", Aliases = ["-o"], Required = false, Description = "Where to write the simplified SVG (default: <name>.optimized.svg beside the input).")]
    public string? Output { get; set; }

    /// <summary>The geometric tolerance, in the SVG's own units.</summary>
    [CliOption(Name = "--tolerance", Aliases = ["-t"], Required = false, Description = "Geometric error tolerance for line and Bezier decimation.")]
    public double Tolerance { get; set; } = 0.5;

    /// <summary>The lowest acceptable SSIM against the unsimplified render.</summary>
    [CliOption(Name = "--min-ssim", Required = false, Description = "Minimum SSIM against the unsimplified render.")]
    public double MinSsim { get; set; } = 0.995;

    /// <summary>The highest acceptable RMSE against the unsimplified render.</summary>
    [CliOption(Name = "--max-rmse", Required = false, Description = "Maximum RMSE percentage against the unsimplified render.")]
    public double MaxRmse { get; set; } = 1.0;

    /// <summary>Overwrite the input.</summary>
    [CliOption(Name = "--in-place", Aliases = ["-i"], Required = false, Description = "Overwrite the input with the simplified SVG.")]
    public bool InPlace { get; set; }

    /// <summary>A browser executable to use instead of Playwright's Chromium.</summary>
    [CliOption(Name = "--chromium", Required = false, Description = "Render with this Chromium executable instead of the one Playwright installed.")]
    public string? Chromium { get; set; }

    /// <summary>Renders with Svg.Skia, which is not authoritative, so the result cannot overwrite the input.</summary>
    [CliOption(Name = "--fast", Required = false, Description = "Render with Svg.Skia instead of Chromium, for previews; cannot be combined with --in-place or --chromium.")]
    public bool Fast { get; set; }

    /// <summary>Also publish the result as a step of a <c>simplification-run</c> in the variations tree.</summary>
    [CliOption(Name = "--snapshot", Aliases = ["-s"], Required = false, Description = "Publish the optimisation candidate into the variations tree.")]
    public bool Snapshot { get; set; }

    /// <summary>The project the snapshot belongs to.</summary>
    [CliOption(Name = "--project", Aliases = ["-p"], Required = false, Description = "Project name for the snapshot (defaults to the SVG's parent folder name).")]
    public string? Project { get; set; }

    /// <summary>The snapshot root.</summary>
    [CliOption(Name = "--variations-dir", Required = false, Description = "Git-ignored snapshot root.")]
    public string VariationsDir { get; set; } = Path.Join("temp", "variations");

    /// <summary>Simplifies, prints and returns the exit code.</summary>
    public async Task<int> RunAsync(CliContext context)
    {
        var cancellationToken = context.CancellationToken;
        if (Fast && (InPlace || Chromium is not null))
        {
            await error.WriteLineAsync($"Error: --fast renders with Svg.Skia, which is not authoritative (ADR 0024); it cannot be combined with {(InPlace ? "--in-place" : "--chromium")}. Drop --fast to render with Chromium.").ConfigureAwait(false);
            return (int)ExitCode.Error;
        }

        var input = Path.GetFullPath(Svg);
        if (!File.Exists(input))
        {
            await error.WriteLineAsync($"Error: expected an SVG file at {input}, found none").ConfigureAwait(false);
            return (int)ExitCode.Error;
        }

        var target = InPlace ? input : Output is not null ? Path.GetFullPath(Output) : Path.Join(Path.GetDirectoryName(input), $"{Path.GetFileNameWithoutExtension(input)}.optimized.svg");
        GatedOutput gated;
        try
        {
            var renderer = Fast ? host.CreateFast() : await host.LaunchAuthoritative(Chromium).ConfigureAwait(false);
            await using (renderer.ConfigureAwait(false))
            {
                gated = await new DriftGate(renderer, host.Scratch).OptimizeAsync(input, target, Tolerance, MinSsim, MaxRmse, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (RendererUnavailableException exception)
        {
            await error.WriteLineAsync($"Error: {exception.Message}").ConfigureAwait(false);
            return (int)ExitCode.Error;
        }
        catch (DriftGateNotMetException exception)
        {
            await error.WriteLineAsync($"Drift gate not met: {exception.Message}").ConfigureAwait(false);
            return (int)ExitCode.Drift;
        }
        catch (InvalidOperationException exception)
        {
            await error.WriteLineAsync($"Error: could not simplify {input}: {exception.Message}").ConfigureAwait(false);
            return (int)ExitCode.Error;
        }
        catch (FormatException exception)
        {
            await error.WriteLineAsync($"Error: could not simplify {input}: {exception.Message}").ConfigureAwait(false);
            return (int)ExitCode.Error;
        }
        catch (InvalidDataException exception)
        {
            await error.WriteLineAsync($"Error: {exception.Message}").ConfigureAwait(false);
            return (int)ExitCode.Error;
        }

        await output.WriteAsync(Format(gated)).ConfigureAwait(false);
        if (Snapshot && gated.Report.GatePassed)
        {
            await output.WriteLineAsync(await PublishSnapshotAsync(input, gated, cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
        }

        return gated.Report.GatePassed ? (int)ExitCode.Clean : (int)ExitCode.Drift;
    }

    private async Task<string> PublishSnapshotAsync(string input, GatedOutput gated, CancellationToken cancellationToken)
    {
        var report = gated.Report;
        var project = Project ?? Path.GetFileName(Path.GetDirectoryName(input)) ?? string.Empty;
        var svg = await File.ReadAllTextAsync(gated.Path, cancellationToken).ConfigureAwait(false);
        var step = new SnapshotManager(Path.GetFullPath(VariationsDir)).CaptureStep(
            project,
            "simplification-run",
            1,
            "svg-optimized",
            svg,
            null,
            new Dictionary<string, object> { ["ssim"] = report.Ssim, ["psnr"] = report.PsnrDb, ["rmse"] = report.RmsePct, ["cand_size"] = report.FinalSizeBytes },
            string.Create(CultureInfo.InvariantCulture, $"Automated multi-pass simplification (tolerance={report.Tolerance:0.00}px, SSIM={report.Ssim:0.0000}, -{report.SizeReductionPct}% bytes)"));
        return $"Snapshot published for project [{project}]: {Path.GetFileName(step.SvgPath)}";
    }

    private string Format(GatedOutput gated)
    {
        var report = gated.Report;
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"{"metric",-18} {"original",12} {"optimized",12} {"change",9}");
        text.AppendLine(CultureInfo.InvariantCulture, $"{"File size",-18} {report.OriginalSizeBytes,10:N0} B {report.FinalSizeBytes,10:N0} B {"-" + report.SizeReductionPct.ToString("0.0", CultureInfo.InvariantCulture) + "%",9}");
        text.AppendLine(CultureInfo.InvariantCulture, $"{"Path node count",-18} {report.OriginalNodeCount,12:N0} {report.FinalNodeCount,12:N0} {"-" + report.NodeReductionPct.ToString("0.0", CultureInfo.InvariantCulture) + "%",9}");
        text.AppendLine(CultureInfo.InvariantCulture, $"{"Visual SSIM",-18} {"-",12} {report.Ssim,12:0.0000} {(report.Ssim >= MinSsim ? "PASS" : "WARN"),9}");
        text.AppendLine(CultureInfo.InvariantCulture, $"{"Visual PSNR",-18} {"-",12} {report.PsnrDb.ToString("0.00", CultureInfo.InvariantCulture) + " dB",12} {"-",9}");
        text.AppendLine(CultureInfo.InvariantCulture, $"{"Visual RMSE",-18} {"-",12} {report.RmsePct.ToString("0.000", CultureInfo.InvariantCulture) + "%",12} {(report.RmsePct <= MaxRmse ? "PASS" : "WARN"),9}");
        text.AppendLine(CultureInfo.InvariantCulture, $"{"Active tolerance",-18} {Tolerance,12:0.####} {report.Tolerance,12:0.0000} {"-",9}");
        if (report.GatePassed)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"Wrote optimized SVG to: {gated.Path}");
        }
        else
        {
            text.AppendLine(
                CultureInfo.InvariantCulture,
                $"Drift gate not met after backoff: SSIM {report.Ssim} (need >= {MinSsim}), RMSE {report.RmsePct}% (need <= {MaxRmse}%). Candidate written to {gated.Path} for inspection, but it is not fit to promote.");
        }

        return text.ToString();
    }
}
