using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using DotMake.CommandLine;
using Rig.Core;
using Rig.Image;
using Rig.Image.Pathways;

namespace Rig.Cli.Image;

/// <summary>
/// <c>rig image promote</c>: replaces the governed master with an explored candidate that still beats it. The gate is re-run live under Chromium against the current
/// master, never trusted from the run's stored report, and there is no fast renderer to decide it. Exit codes: 0 promoted, 1 the candidate does not (or no longer) win
/// or the run was a dry run, 2 an input or renderer could not be handled.
/// </summary>
/// <param name="host">Where assets are and which renderer scores.</param>
/// <param name="output">Where the verdict goes.</param>
/// <param name="error">Where failures go.</param>
[CliCommand(Name = "promote", Description = "Replace the governed master with an explored candidate that beats it, re-scored live under Chromium.")]
internal sealed class PromoteCommand(ImageHost host, TextWriter output, TextWriter error)
{
    private const string BuilderAssetsFolder = "src/Rig.Image/builder-assets/rtkelly-labs";

    /// <summary>The instance DotMake binds onto; rig always builds the wired one instead.</summary>
    internal PromoteCommand()
        : this(null!, TextWriter.Null, TextWriter.Null)
    {
    }

    /// <summary>The target project brand.</summary>
    [CliOption(Name = "--project", Aliases = ["-p"], Required = false, Description = "Target project brand.")]
    public string Project { get; set; } = ImageProjects.Default;

    /// <summary>The exploration run.</summary>
    [CliOption(Name = "--run-id", Required = false, Description = "Exploration run holding the candidate.")]
    public string RunId { get; set; } = string.Empty;

    /// <summary>The candidate's step index.</summary>
    [CliOption(Name = "--candidate", Required = false, Description = "Candidate step index within the run.")]
    public int Candidate { get; set; }

    /// <summary>Actually write.</summary>
    [CliOption(Name = "--confirm", Required = false, Description = "Actually write the new master.")]
    public bool Confirm { get; set; }

    /// <summary>The snapshot root.</summary>
    [CliOption(Name = "--variations-dir", Required = false, Description = "Git-ignored snapshot root.")]
    public string VariationsDir { get; set; } = Path.Join("temp", "variations");

    /// <summary>A browser executable to use instead of Playwright's Chromium.</summary>
    [CliOption(Name = "--chromium", Required = false, Description = "Score with this Chromium executable instead of the one Playwright installed.")]
    public string? Chromium { get; set; }

    /// <summary>Re-scores, decides and, with <c>--confirm</c>, writes.</summary>
    public async Task<int> RunAsync(CliContext context)
    {
        var cancellationToken = context.CancellationToken;
        ImageProject project;
        try
        {
            project = ExploreOptions.LabsProject(host.RepoRoot, Project);
        }
        catch (ArgumentException exception)
        {
            await error.WriteLineAsync($"Error: {exception.Message}").ConfigureAwait(false);
            return (int)ExitCode.Error;
        }

        if (RunId.Length == 0)
        {
            await error.WriteLineAsync("Error: expected --run-id, found none").ConfigureAwait(false);
            return (int)ExitCode.Error;
        }

        var variations = Path.GetFullPath(VariationsDir);
        var run = new SnapshotManager(variations).GetRun(project.Name, RunId);
        if (run is null)
        {
            await error.WriteLineAsync($"Error: expected a run '{RunId}' for project '{project.Name}', found none").ConfigureAwait(false);
            return (int)ExitCode.Error;
        }

        var step = run.Steps.FirstOrDefault(s => s.StepIdx == Candidate && s.SvgPath is not null);
        if (step?.SvgPath is null)
        {
            await error.WriteLineAsync($"Error: expected run '{RunId}' to hold candidate {Candidate} with an SVG, found none").ConfigureAwait(false);
            return (int)ExitCode.Error;
        }

        try
        {
            var renderer = await host.LaunchAuthoritative(Chromium).ConfigureAwait(false);
            await using (renderer.ConfigureAwait(false))
            {
                return await DecideAsync(project, step, variations, renderer, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (RendererUnavailableException exception)
        {
            await error.WriteLineAsync($"Error: Chromium is required to re-run the gate ({exception.Message})").ConfigureAwait(false);
            return (int)ExitCode.Error;
        }
        catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException or FormatException or IOException)
        {
            await error.WriteLineAsync($"Error: {exception.Message}").ConfigureAwait(false);
            return (int)ExitCode.Error;
        }
    }

    private async Task<int> DecideAsync(ImageProject project, SnapshotStep step, string variations, ISvgRenderer renderer, CancellationToken cancellationToken)
    {
        var candidateSvg = await File.ReadAllTextAsync(step.SvgPath!, cancellationToken).ConfigureAwait(false);
        var incumbentSvg = await File.ReadAllTextAsync(project.Svg, cancellationToken).ConfigureAwait(false);
        var scorer = new ImageScorer(renderer, project, Path.Join(host.Scratch, "promote", RunId));
        var incumbent = await scorer.ScoreAsync(incumbentSvg, "incumbent", cancellationToken).ConfigureAwait(false);
        var candidate = await scorer.ScoreAsync(candidateSvg, "candidate", cancellationToken).ConfigureAwait(false);
        var verdict = PromotionGate.Evaluate(incumbent.Score, incumbent.Drift.Ssim, candidate.Score, candidate.Drift.Ssim);

        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"Incumbent: composite {incumbent.Score.CompositePct:0.000}% / SSIM {incumbent.Drift.Ssim:0.0000}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Candidate: composite {candidate.Score.CompositePct:0.000}% / SSIM {candidate.Drift.Ssim:0.0000}");
        if (!verdict.Promotable)
        {
            text.AppendLine("Candidate does not beat the incumbent:");
            foreach (var reason in verdict.Reasons)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"  - {reason}");
            }

            await output.WriteAsync(text.ToString()).ConfigureAwait(false);
            return (int)ExitCode.Drift;
        }

        var assets = Path.Join(host.RepoRoot, BuilderAssetsFolder);
        var masterFile = Path.Join(assets, LabsBuilder.Manifest().Variants[LabsBuilder.DefaultVariant].File);
        if (!Confirm)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"Dry run. Would replace:\n  {masterFile}\n  {project.Svg}\nRe-run with --confirm to write.");
            await output.WriteAsync(text.ToString()).ConfigureAwait(false);
            return (int)ExitCode.Drift;
        }

        var backup = Path.Join(variations, project.Name, RunId, "incumbent-backup.svg");
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        var utf8 = new UTF8Encoding(false);
        await File.WriteAllTextAsync(backup, incumbentSvg.ReplaceLineEndings("\n"), utf8, cancellationToken).ConfigureAwait(false);
        var master = candidateSvg.ReplaceLineEndings("\n");
        await File.WriteAllTextAsync(masterFile, master, utf8, cancellationToken).ConfigureAwait(false);

        var manifest = LabsBuilder.Manifest();
        var promoted = manifest.Variants[LabsBuilder.DefaultVariant] with
        {
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(masterFile, cancellationToken).ConfigureAwait(false))),
            PromotedFrom = new PromotionRecord(RunId, Candidate, step.Notes),
        };
        var variants = manifest.Variants.ToDictionary(v => v.Key, v => v.Value);
        variants[LabsBuilder.DefaultVariant] = promoted;
        await File.WriteAllTextAsync(Path.Join(assets, "manifest.json"), LabsBuilder.ManifestJson(manifest with { Variants = variants }), utf8, cancellationToken).ConfigureAwait(false);

        // The committed asset is the packaged master, written from the same bytes.
        await File.WriteAllTextAsync(project.Svg, master, utf8, cancellationToken).ConfigureAwait(false);
        text.AppendLine(CultureInfo.InvariantCulture, $"Promoted. Incumbent backed up to {backup}\nReview before committing:\n  git diff -- {masterFile} {project.Svg}\nThen regenerate cuts: image-reconcile export-avatar -p {project.Name}");
        await output.WriteAsync(text.ToString()).ConfigureAwait(false);
        return (int)ExitCode.Clean;
    }
}
