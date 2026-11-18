using DotMake.CommandLine;
using Rig.Core;
using Rig.Image;

namespace Rig.Cli.Image;

/// <summary>
/// <c>rig image snapshot</c>: captures a pipeline step into the git-ignored <c>temp/variations/</c> folder the Asset Studio reads.
/// </summary>
/// <param name="host">Where the assets are.</param>
/// <param name="output">Where the result goes.</param>
/// <param name="error">Where failures go.</param>
[CliCommand(Name = "snapshot", Description = "Capture an ephemeral vector pipeline snapshot into git-ignored temp/variations/.")]
internal sealed class SnapshotCommand(ImageHost host, TextWriter output, TextWriter error)
{
    /// <summary>The instance DotMake binds onto; rig always builds the wired one instead.</summary>
    internal SnapshotCommand()
        : this(null!, TextWriter.Null, TextWriter.Null)
    {
    }

    /// <summary>The project folder name under <c>assets/</c>.</summary>
    [CliOption(Name = "--project", Aliases = ["-p"], Required = true, Description = "Project brand to snapshot (for example rtkelly-labs, parquet-source-generator).")]
    public string Project { get; set; } = string.Empty;

    /// <summary>The step's name.</summary>
    [CliOption(Name = "--name", Aliases = ["-n"], Required = true, Description = "Name of the snapshot step (for example initial-geometry).")]
    public string StepName { get; set; } = string.Empty;

    /// <summary>The run the step belongs to.</summary>
    [CliOption(Name = "--run-id", Aliases = ["-r"], Required = false, Description = "Variation run identifier grouping this sequence.")]
    public string RunId { get; set; } = "run-1";

    /// <summary>The step's index.</summary>
    [CliOption(Name = "--step-idx", Aliases = ["-s"], Required = false, Description = "Integer index of the step within the variation run.")]
    public int StepIndex { get; set; } = 1;

    /// <summary>The SVG to capture.</summary>
    [CliOption(Name = "--svg", Required = false, Description = "Path to an SVG file to snapshot. Defaults to the project master SVG.")]
    public string? Svg { get; set; }

    /// <summary>Why the step was taken.</summary>
    [CliOption(Name = "--notes", Required = false, Description = "Descriptive notes or design rationale for this step.")]
    public string Notes { get; set; } = string.Empty;

    /// <summary>Where snapshots go.</summary>
    [CliOption(Name = "--variations-dir", Required = false, Description = "Directory for ephemeral snapshots and variations (git-ignored). Defaults to temp/variations.")]
    public string? VariationsDir { get; set; }

    /// <summary>Captures and returns the exit code.</summary>
    public Task<int> RunAsync(CliContext context) => ImageGuard.RunAsync(error, async () =>
    {
        var source = FindSvg() ?? throw new FileNotFoundException($"Could not find an SVG to snapshot for project '{Project}'.");
        var content = await File.ReadAllTextAsync(source, context.CancellationToken).ConfigureAwait(false);
        var manager = new SnapshotManager(Path.GetFullPath(VariationsDir ?? Path.Join(host.RepoRoot, "temp", "variations")));
        var step = manager.CaptureStep(Project, RunId, StepIndex, StepName, svgContent: content, notes: Notes);
        await output.WriteAsync(
            $"Snapshot captured\n  Project:   {step.Project}\n  Run ID:    {step.RunId}\n  Step {step.StepIdx}:    {step.StepName}\n  Timestamp: {step.Timestamp}\n  SVG file:  {step.SvgPath}\n  Metadata:  {step.MetadataPath}\n").ConfigureAwait(false);
        return (int)ExitCode.Clean;
    });

    // --svg, then assets/<project>/<project>.svg, then the folder's first SVG.
    private string? FindSvg()
    {
        if (Svg is not null && File.Exists(Svg))
        {
            return Svg;
        }

        var folder = Path.Join(host.RepoRoot, "assets", Project);
        var named = Path.Join(folder, $"{Project}.svg");
        if (File.Exists(named))
        {
            return named;
        }

        return Directory.Exists(folder) ? Directory.EnumerateFiles(folder, "*.svg").Order(StringComparer.Ordinal).FirstOrDefault() : null;
    }
}
