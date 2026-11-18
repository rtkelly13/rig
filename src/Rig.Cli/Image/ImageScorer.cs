using Rig.Image;

namespace Rig.Cli.Image;

/// <summary>An SVG's score and drift against a project's reference.</summary>
/// <param name="Score">The composite score the promotion gate uses.</param>
/// <param name="Drift">The whole-frame drift numbers.</param>
internal sealed record SvgScore(Score Score, DriftNumbers Drift);

/// <summary>Renders SVGs and scores them against the project reference in one pass, so every candidate and the incumbent are comparable; the reference is decoded once.</summary>
/// <param name="renderer">Renders each SVG.</param>
/// <param name="project">Supplies the reference and the feature boxes.</param>
/// <param name="scratch">A folder for the SVG files the renderer reads.</param>
internal sealed class ImageScorer(ISvgRenderer renderer, ImageProject project, string scratch)
{
    private RgbImage? _reference;

    /// <summary>Scores <paramref name="svgText"/>; <paramref name="label"/> names its scratch file.</summary>
    public async Task<SvgScore> ScoreAsync(string svgText, string label, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(scratch);
        var path = Path.Join(scratch, $"{label}.svg");
        await File.WriteAllTextAsync(path, svgText, new System.Text.UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        var reference = _reference ??= RasterPipeline.LoadNormalised(await File.ReadAllBytesAsync(project.Reference, cancellationToken).ConfigureAwait(false));
        var render = RasterPipeline.Normalise(await renderer.RenderAsync(path, RasterPipeline.NormalisedSize, cancellationToken).ConfigureAwait(false));
        return new SvgScore(DriftMetrics.ScoreCandidate(reference, render, project.Regions), DriftMetrics.Drift(reference, render));
    }
}

/// <summary>Command-line parsing shared by the explore commands.</summary>
internal static class ImageOptions
{
    /// <summary>Parses <c>--axes</c>: comma-separated angles; anything unparseable or empty is an error naming the value.</summary>
    public static IReadOnlyList<double> ParseAxes(string axes)
    {
        var values = new List<double>();
        foreach (var part in axes.Split(',').Select(a => a.Trim()).Where(a => a.Length > 0))
        {
            if (!double.TryParse(part, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var angle))
            {
                throw new FormatException($"expected --axes to be comma-separated numbers, found '{part}' in '{axes}'");
            }

            values.Add(angle);
        }

        return values.Count == 0 ? throw new FormatException("expected --axes to name at least one angle, found none") : values;
    }
}
