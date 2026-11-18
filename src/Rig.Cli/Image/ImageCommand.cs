using System.Globalization;
using System.Text;
using DotMake.CommandLine;
using Rig.Core;
using Rig.Image;

namespace Rig.Cli.Image;

/// <summary>Where <c>rig image</c> finds the repository's assets and how it gets a renderer.</summary>
/// <param name="RepoRoot">The rig checkout, which holds <c>assets/</c>.</param>
/// <param name="Scratch">A folder for the browser's HTML wrappers.</param>
/// <param name="LaunchAuthoritative">Starts the authoritative renderer; the argument is a browser executable to use instead of Playwright's.</param>
/// <param name="CreateFast">Creates the fast renderer.</param>
internal sealed record ImageHost(string RepoRoot, string Scratch, Func<string?, Task<ISvgRenderer>> LaunchAuthoritative, Func<ISvgRenderer> CreateFast);

/// <summary>
/// <c>rig image cross-check</c>: renders the project masters with Chromium and Svg.Skia and reports how far they diverge
/// (ADR 0024, #321). Exit codes: 0 within thresholds, 1 a threshold broken or a promotion verdict flipped, 2 a renderer
/// could not start.
/// </summary>
/// <param name="host">Where the assets are and which renderers to use.</param>
/// <param name="output">Where the table goes.</param>
/// <param name="error">Where failures go.</param>
[CliCommand(Name = "image", Description = "Image pipeline tools: render SVGs, score them against references and check the renderers agree.")]
internal sealed class ImageCommand(ImageHost host, TextWriter output, TextWriter error)
{
    /// <summary>The instance DotMake binds onto; rig always builds the wired one above instead.</summary>
    internal ImageCommand()
        : this(null!, TextWriter.Null, TextWriter.Null)
    {
    }

    /// <summary>The <c>rig image</c> command group.</summary>
    public System.CommandLine.Command Build()
    {
        var image = DeclarativeCommand.Build(this);
        image.Add(DeclarativeCommand.Build(new CrossCheckCommand(host, output, error)));
        return image;
    }

    /// <summary>A group named without one of its verbs is the parser's error.</summary>
    public int Run() => DeclarativeCommand.RequireSubcommand(error);

    /// <summary><c>rig image cross-check</c>: Chromium against Svg.Skia on the real masters.</summary>
    [CliCommand(Name = "cross-check", Description = "Render the masters with Chromium and Svg.Skia and report how far they diverge from each other.")]
    internal sealed class CrossCheckCommand(ImageHost host, TextWriter output, TextWriter error)
    {
        /// <summary>The instance DotMake binds onto; rig always builds the wired one above instead.</summary>
        internal CrossCheckCommand()
            : this(null!, TextWriter.Null, TextWriter.Null)
        {
        }

        /// <summary>A folder of candidate SVGs named <c>project.candidate.svg</c>.</summary>
        [CliOption(Name = "--candidates", Required = false, Description = "A folder of candidate SVGs named <project>.<candidate>.svg; without it promotion verdicts are not compared.")]
        public string? Candidates { get; set; }

        /// <summary>A browser executable to use instead of the one Playwright installed.</summary>
        [CliOption(Name = "--chromium", Required = false, Description = "A Chromium executable to use instead of the one Playwright installed.")]
        public string? Chromium { get; set; }

        /// <summary>Where to write the JSON report.</summary>
        [CliOption(Name = "--report", Required = false, Description = "Write the report as JSON to this file.")]
        public string? Report { get; set; }

        /// <summary>Renders, compares, prints and returns the exit code.</summary>
        public async Task<int> RunAsync(CliContext context)
        {
            var cancellationToken = context.CancellationToken;
            CrossCheckReport report;
            try
            {
                var authoritative = await host.LaunchAuthoritative(Chromium).ConfigureAwait(false);
                await using (authoritative.ConfigureAwait(false))
                {
                    var fast = host.CreateFast();
                    await using (fast.ConfigureAwait(false))
                    {
                        report = await CrossCheck.RunAsync(authoritative, fast, host.RepoRoot, CrossCheck.Files(), Candidates, DivergenceThresholds.Adr, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
            catch (RendererUnavailableException exception)
            {
                await error.WriteLineAsync($"Error: {exception.Message}").ConfigureAwait(false);
                return (int)ExitCode.Error;
            }
            catch (InvalidDataException exception)
            {
                await error.WriteLineAsync($"Error: {exception.Message}").ConfigureAwait(false);
                return (int)ExitCode.Error;
            }

            await output.WriteAsync(Format(report)).ConfigureAwait(false);
            if (Report is not null)
            {
                var path = Path.GetFullPath(Report);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllTextAsync(path, CrossCheckJson.Write(report), cancellationToken).ConfigureAwait(false);
            }

            return report.Passed ? (int)ExitCode.Clean : (int)ExitCode.Drift;
        }

        private static string Format(CrossCheckReport report)
        {
            var text = new StringBuilder();
            text.AppendLine(CultureInfo.InvariantCulture, $"Cross-check: {report.Authoritative} (authoritative) vs {report.Fast}");
            text.AppendLine(CultureInfo.InvariantCulture, $"{"file",-42} {"mean abs",9} {"differ %",9} {"max abs",8} {"rmse pp",8} {"ssim",8} {"composite pp",13}");
            foreach (var file in report.Files)
            {
                var metrics = file.Metrics;
                text.AppendLine(
                    CultureInfo.InvariantCulture,
                    $"{file.File.Id,-42} {file.MeanAbsDifference,9:0.0000} {file.DifferingPixelFraction * 100,9:0.00} {file.MaxAbsDifference,8} "
                    + $"{(metrics is null ? "-" : metrics.RmsePp.ToString("0.0000", CultureInfo.InvariantCulture)),8} "
                    + $"{(metrics is null ? "-" : metrics.SsimDifference.ToString("0.00000", CultureInfo.InvariantCulture)),8} "
                    + $"{(metrics is null ? "-" : metrics.CompositePp.ToString("0.0000", CultureInfo.InvariantCulture)),13}");
                foreach (var breach in file.Breaches)
                {
                    text.AppendLine(CultureInfo.InvariantCulture, $"  BREACH {breach}");
                }
            }

            if (!report.VerdictsEvaluated)
            {
                text.AppendLine("Promotion verdicts not evaluated: pass --candidates <folder> to compare them.");
            }

            foreach (var verdict in report.Verdicts)
            {
                text.AppendLine(
                    CultureInfo.InvariantCulture,
                    $"verdict {verdict.Project}.{verdict.Candidate}: {report.Authoritative} {(verdict.AuthoritativePromotable ? "promotes" : "rejects")}, {report.Fast} {(verdict.FastPromotable ? "promotes" : "rejects")}{(verdict.Flipped ? "  FLIPPED" : string.Empty)}");
            }

            text.AppendLine(report.Passed ? "Within thresholds." : "Thresholds broken.");
            return text.ToString();
        }
    }
}
