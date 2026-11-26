using System.Globalization;
using System.Text;
using DotMake.CommandLine;
using Rig.Core;
using Rig.Image;
using Rig.Image.Pathways;

namespace Rig.Cli.Image;

/// <summary>
/// <c>rig image explore</c>: proposes parametric candidates, scores each against the incumbent master in one Chromium pass and records them under the git-ignored
/// variations tree. Read-only with respect to <c>assets/</c>. Exit codes: 0 a candidate beat the incumbent, 1 none did, 2 an input or renderer could not be handled.
/// </summary>
/// <param name="host">Where assets are and which renderer scores.</param>
/// <param name="output">Where the table goes.</param>
/// <param name="error">Where failures go.</param>
[CliCommand(Name = "explore", Description = "Propose parametric candidates and score them against the incumbent master; nothing under assets/ changes.")]
internal sealed class ExploreCommand(ImageHost host, TextWriter output, TextWriter error)
{
    /// <summary>The instance DotMake binds onto; rig always builds the wired one instead.</summary>
    internal ExploreCommand()
        : this(null!, TextWriter.Null, TextWriter.Null)
    {
    }

    /// <summary>The target project brand.</summary>
    [CliOption(Name = "--project", Aliases = ["-p"], Required = false, Description = "Target project brand.")]
    public string Project { get; set; } = ImageProjects.Default;

    /// <summary>The preset to start from.</summary>
    [CliOption(Name = "--preset", Required = false, Description = "Explorer preset to start from.")]
    public string Preset { get; set; } = IsoLoopBuilder.DefaultPreset;

    /// <summary>Single-value overrides, KEY=VALUE.</summary>
    [CliOption(Name = "--param", Required = false, Description = "Override one parameter as KEY=VALUE. Repeatable.")]
    public string[] Param { get; set; } = [];

    /// <summary>Swept parameters, KEY=A,B,C.</summary>
    [CliOption(Name = "--sweep", Required = false, Description = "Sweep a parameter as KEY=A,B,C. Repeatable; combinations are crossed.")]
    public string[] Sweep { get; set; } = [];

    /// <summary>The cap on the sweep product.</summary>
    [CliOption(Name = "--max-candidates", Required = false, Description = "Cap on the sweep product.")]
    public int MaxCandidates { get; set; } = 24;

    /// <summary>The snapshot run id.</summary>
    [CliOption(Name = "--run-id", Required = false, Description = "Snapshot run id. Defaults to a timestamp.")]
    public string? RunId { get; set; }

    /// <summary>The snapshot root.</summary>
    [CliOption(Name = "--variations-dir", Required = false, Description = "Git-ignored snapshot root.")]
    public string VariationsDir { get; set; } = Path.Join("temp", "variations");

    /// <summary>A browser executable to use instead of Playwright's Chromium.</summary>
    [CliOption(Name = "--chromium", Required = false, Description = "Score with this Chromium executable instead of the one Playwright installed.")]
    public string? Chromium { get; set; }

    /// <summary>Scores, prints and returns the exit code.</summary>
    public async Task<int> RunAsync(CliContext context)
    {
        var cancellationToken = context.CancellationToken;
        ImageProject project;
        IsoLoopParams baseline;
        List<IReadOnlyDictionary<string, string>> combinations;
        try
        {
            project = ExploreOptions.LabsProject(host.RepoRoot, Project);
            baseline = ExploreOptions.StartingPoint(Preset, Param);
            combinations = ExploreOptions.Combinations(Sweep);
            if (combinations.Count > MaxCandidates)
            {
                throw new ArgumentException($"expected at most {MaxCandidates} candidates, found a sweep of {combinations.Count}");
            }

            _ = combinations.Select(c => baseline.WithOverrides(c)).ToList();
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            await error.WriteLineAsync($"Error: {exception.Message}").ConfigureAwait(false);
            return (int)ExitCode.Error;
        }

        if (!File.Exists(project.Svg))
        {
            await error.WriteLineAsync($"Error: expected the master SVG at {project.Svg}, found none").ConfigureAwait(false);
            return (int)ExitCode.Error;
        }

        var runId = RunId ?? string.Create(CultureInfo.InvariantCulture, $"explore-{DateTime.UtcNow:yyyyMMdd-HHmmss}");
        var variations = Path.GetFullPath(VariationsDir);
        try
        {
            var renderer = await host.LaunchAuthoritative(Chromium).ConfigureAwait(false);
            await using (renderer.ConfigureAwait(false))
            {
                return await ScoreAllAsync(project, baseline, combinations, runId, variations, renderer, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (RendererUnavailableException exception)
        {
            await error.WriteLineAsync($"Error: Chromium is required to score candidates ({exception.Message})").ConfigureAwait(false);
            return (int)ExitCode.Error;
        }
        catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException or FormatException or IOException)
        {
            await error.WriteLineAsync($"Error: {exception.Message}").ConfigureAwait(false);
            return (int)ExitCode.Error;
        }
    }

    private async Task<int> ScoreAllAsync(ImageProject project, IsoLoopParams baseline, List<IReadOnlyDictionary<string, string>> combinations, string runId, string variations, ISvgRenderer renderer, CancellationToken cancellationToken)
    {
        var manager = new SnapshotManager(variations);
        var scorer = new ImageScorer(renderer, project, Path.Join(host.Scratch, "explore", runId));
        var incumbentSvg = await File.ReadAllTextAsync(project.Svg, cancellationToken).ConfigureAwait(false);
        var incumbent = await scorer.ScoreAsync(incumbentSvg, "incumbent", cancellationToken).ConfigureAwait(false);
        manager.CaptureStep(project.Name, runId, 0, "incumbent", incumbentSvg, null, new Dictionary<string, object> { ["composite_pct"] = incumbent.Score.CompositePct, ["ssim"] = incumbent.Drift.Ssim }, "current governed master");

        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"Incumbent: composite {incumbent.Score.CompositePct:0.000}% / SSIM {incumbent.Drift.Ssim:0.0000}");
        text.AppendLine(CultureInfo.InvariantCulture, $"PARAMETRIC_EXPLORATION: {runId}");
        text.AppendLine(CultureInfo.InvariantCulture, $"{"#",3} {"Overrides",-40} {"Composite",10} {"SSIM",8} Verdict");
        var winners = new List<int>();
        for (var index = 1; index <= combinations.Count; index++)
        {
            var overrides = combinations[index - 1];
            var svg = IsoLoopBuilder.GenerateSvg(parameters: baseline.WithOverrides(overrides));
            var scored = await scorer.ScoreAsync(svg, $"candidate-{index}", cancellationToken).ConfigureAwait(false);
            var verdict = PromotionGate.Evaluate(incumbent.Score, incumbent.Drift.Ssim, scored.Score, scored.Drift.Ssim);
            var described = string.Join(", ", overrides.Select(o => $"{o.Key}={o.Value}"));
            manager.CaptureStep(
                project.Name,
                runId,
                index,
                $"candidate-{index}",
                svg,
                null,
                new Dictionary<string, object> { ["composite_pct"] = scored.Score.CompositePct, ["ssim"] = scored.Drift.Ssim, ["promotable"] = verdict.Promotable },
                $"preset={Preset} overrides={(overrides.Count == 0 ? "{}" : described)}");
            if (verdict.Promotable)
            {
                winners.Add(index);
            }

            text.AppendLine(CultureInfo.InvariantCulture, $"{index,3} {(described.Length == 0 ? "preset:" + Preset : described),-40} {scored.Score.CompositePct,9:0.000}% {scored.Drift.Ssim,8:0.0000} {(verdict.Promotable ? "PROMOTABLE" : "rejected")}");
        }

        text.AppendLine(CultureInfo.InvariantCulture, $"Snapshots: {Path.Join(variations, project.Name, runId)}");
        if (winners.Count == 0)
        {
            text.AppendLine("No candidate beat the incumbent. The master stands.");
            await output.WriteAsync(text.ToString()).ConfigureAwait(false);
            return (int)ExitCode.Drift;
        }

        text.AppendLine(CultureInfo.InvariantCulture, $"Promotable candidates: {string.Join(", ", winners)}. Review, then: rig image promote -p {project.Name} --run-id {runId} --candidate <N> --confirm");
        await output.WriteAsync(text.ToString()).ConfigureAwait(false);
        return (int)ExitCode.Clean;
    }
}

/// <summary>Option parsing shared by explore and promote.</summary>
internal static class ExploreOptions
{
    /// <summary>The project, which must be the labs mark: explore and promote only drive its builder.</summary>
    public static ImageProject LabsProject(string repoRoot, string name)
    {
        var project = ImageProjects.Resolve(repoRoot, name);
        return project.Name == ImageProjects.Default ? project : throw new ArgumentException($"expected '{ImageProjects.Default}', found '{project.Name}': only the labs mark supports explore and promote");
    }

    /// <summary>The preset with every <c>KEY=VALUE</c> override applied.</summary>
    public static IsoLoopParams StartingPoint(string preset, IReadOnlyList<string> overrides)
    {
        if (!IsoLoopBuilder.Presets.TryGetValue(preset, out var baseline))
        {
            throw new ArgumentException($"expected a preset from {string.Join(", ", IsoLoopBuilder.Presets.Keys.Order(StringComparer.Ordinal))}, found '{preset}'");
        }

        return overrides.Count == 0 ? baseline : baseline.WithOverrides(overrides.Select(Split).ToDictionary(p => p.Key, p => p.Value));
    }

    /// <summary>The cross product of the sweeps, one dictionary per candidate; no sweeps give one empty candidate.</summary>
    public static List<IReadOnlyDictionary<string, string>> Combinations(IReadOnlyList<string> sweeps)
    {
        var axes = new Dictionary<string, string[]>();
        foreach (var sweep in sweeps)
        {
            var (key, values) = Split(sweep);
            axes[key] = [.. values.Split(',').Select(v => v.Trim()).Where(v => v.Length > 0)];
        }

        var combinations = new List<IReadOnlyDictionary<string, string>> { new Dictionary<string, string>() };
        foreach (var (key, values) in axes)
        {
            combinations = [.. combinations.SelectMany(existing => values.Select(value => (IReadOnlyDictionary<string, string>)new Dictionary<string, string>(existing) { [key] = value }))];
        }

        return combinations;
    }

    private static KeyValuePair<string, string> Split(string item)
    {
        var at = item.IndexOf('=', StringComparison.Ordinal);
        return at < 0 ? throw new FormatException($"expected KEY=VALUE, found '{item}'") : new KeyValuePair<string, string>(item[..at], item[(at + 1)..]);
    }
}
