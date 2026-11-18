using System.Globalization;
using System.Text;
using Rig.Image.Pathways;

namespace Rig.Image.Gallery;

/// <summary>A named feature crop box.</summary>
/// <param name="Key">The region key, as in <c>feature-regions.json</c>.</param>
/// <param name="Label">What the gallery calls it.</param>
/// <param name="Box">The crop box on the 500 px canvas.</param>
internal sealed record GalleryRegion(string Key, string Label, FeatureBox Box);

/// <summary>Everything a gallery page is made from.</summary>
/// <param name="ProjectName">The heading.</param>
/// <param name="ReferencePath">The approved reference raster.</param>
/// <param name="TargetSvg">The committed master, named in the report; the gallery never writes it.</param>
/// <param name="Regions">The feature regions, in display order.</param>
/// <param name="Variants">The builder's declared variants, in display order.</param>
/// <param name="DefaultVariant">The builder's default variant key.</param>
/// <param name="Generate">The SVG text of a variant key.</param>
internal sealed record GalleryInput(
    string ProjectName,
    string ReferencePath,
    string TargetSvg,
    IReadOnlyList<GalleryRegion> Regions,
    IReadOnlyDictionary<string, Variant> Variants,
    string DefaultVariant,
    Func<string, string> Generate);

/// <summary>A built page and the progress lines the original printed while building it.</summary>
/// <param name="Html">The self-contained page.</param>
/// <param name="Lines">What to tell the person.</param>
internal sealed record GalleryRun(string Html, IReadOnlyList<string> Lines);

/// <summary>
/// The preview galleries of the original <c>build-preview</c>: a selection page with no scores, the scored wave page, and the single-target page.
/// Every candidate is rendered by the authoritative renderer and scored against the reference; the gallery never writes the master.
/// </summary>
internal static class GalleryBuilder
{
    /// <summary>Composite spread, in percentage points, below which candidates are not ranked.</summary>
    public const double TieThresholdPct = 0.5;

    private static readonly int[] ChoiceSizes = [128, 64, 32];

    /// <summary>The score badge's component class: under 20 good, under 28 warn, else bad.</summary>
    public static string BadgeClass(double compositePct)
    {
        if (compositePct < 20.0)
        {
            return "badge-good";
        }

        return compositePct < 28.0 ? "badge-warn" : "badge-bad";
    }

    /// <summary>The selection gallery: no scores, no ranking, one adopt command each.</summary>
    public static async Task<GalleryRun> ChooseAsync(GalleryInput input, ISvgRenderer renderer, string scratch, CancellationToken cancellationToken)
    {
        var lines = new List<string> { $"Building choice gallery ({input.Variants.Count} candidates)" };
        var reference = await LoadReferenceAsync(input, cancellationToken).ConfigureAwait(false);
        var socket = input.Regions.Count > 0 ? input.Regions[0] : null;
        var panels = new List<SheetPanel>();
        if (socket is not null)
        {
            panels.Add(new SheetPanel(Crop(reference.Image, socket.Box), "reference", Highlight: true));
        }

        var cards = new StringBuilder();
        foreach (var (key, variant) in input.Variants)
        {
            var render = await RenderAsync(input, renderer, scratch, $"choice_{key}", key, cancellationToken).ConfigureAwait(false);
            var current = key == input.DefaultVariant;
            cards.Append(GalleryTemplates.Render("choose-card.html", new Dictionary<string, string>
            {
                ["current_class"] = current ? " border-2 border-intent-success" : string.Empty,
                ["ribbon_html"] = current ? GalleryTemplates.Render("current-ribbon.html", new Dictionary<string, string>()) : string.Empty,
                ["hero"] = DataUri(render.Png),
                ["name"] = Name(variant, key),
                ["description"] = variant.Description,
                ["sizes_html"] = Chips(render.Image, key),
                ["key"] = key,
            }));
            if (socket is not null)
            {
                panels.Add(new SheetPanel(Crop(render.Image, socket.Box), Name(variant, key).Split(':', 2)[0]));
            }

            lines.Add($"  {Name(variant, key)}");
        }

        var sheet = panels.Count > 0 ? DataUri(Sheets.EncodePng(Sheets.ContactSheet(panels, cell: 170, columns: panels.Count))) : string.Empty;
        var content = GalleryTemplates.Render("choose-page.html", new Dictionary<string, string>
        {
            ["reference_hero"] = reference.Uri,
            ["reference_name"] = Path.GetFileName(input.ReferencePath),
            ["reference_sizes_html"] = Chips(reference.Image, "reference"),
            ["candidates_html"] = cards.ToString(),
            ["socket_sheet"] = sheet,
        });
        return new GalleryRun(Shell(input, "Pick a Base", "Choose the base to build the next round on", GalleryTemplates.Render("choose-note.html", new Dictionary<string, string>()), content), lines);
    }

    /// <summary>The scored wave gallery: every variant ranked by composite drift, or the builder default when the spread is noise.</summary>
    public static async Task<GalleryRun> WaveAsync(GalleryInput input, ISvgRenderer renderer, string scratch, CancellationToken cancellationToken)
    {
        var lines = new List<string> { $"Candidate wave generation ({input.Variants.Count} candidates)" };
        var reference = await LoadReferenceAsync(input, cancellationToken).ConfigureAwait(false);
        var scored = new List<ScoredCandidate>();
        var index = 0;
        foreach (var (key, variant) in input.Variants)
        {
            index++;
            var render = await RenderAsync(input, renderer, scratch, $"candidate_{key}", key, cancellationToken).ConfigureAwait(false);
            var score = DriftMetrics.ScoreCandidate(reference.Image, render.Image, Boxes(input));
            scored.Add(new ScoredCandidate(key, variant.Name.Length > 0 ? variant.Name : $"Candidate {index}", variant.Description, score, DataUri(render.Png), CropUris(render.Image, input)));
            lines.Add($"  {scored[^1].Name}: composite {Fixed(score.CompositePct)}% (frame {Fixed(score.FullFramePct)}%)");
        }

        scored.Sort((a, b) =>
        {
            var byScore = a.Score.CompositePct.CompareTo(b.Score.CompositePct);
            return byScore != 0 ? byScore : string.CompareOrdinal(a.Key, b.Key);
        });
        var spread = scored[^1].Score.CompositePct - scored[0].Score.CompositePct;
        var tied = spread < TieThresholdPct;
        var chosen = tied ? scored.Find(c => c.Key == input.DefaultVariant) ?? scored[0] : scored[0];
        if (tied)
        {
            lines.Add($"Candidates are statistically indistinguishable (composite spread {Fixed(spread)}pp < {TieThresholdPct.ToString("0.0", CultureInfo.InvariantCulture)}pp threshold).");
            lines.Add($"Using builder default: {chosen.Name} ({Fixed(chosen.Score.CompositePct)}% composite)");
        }
        else
        {
            lines.Add($"Lowest drift: {chosen.Name} ({Fixed(chosen.Score.CompositePct)}% composite, {Fixed(spread)}pp ahead of last)");
        }

        lines.Add($"{Path.GetFileName(input.TargetSvg)} left untouched. Use `rig image build --variant {chosen.Key}` to adopt this one.");
        var cards = new StringBuilder();
        foreach (var candidate in scored)
        {
            var isChosen = candidate.Key == chosen.Key;
            var ribbon = isChosen
                ? GalleryTemplates.Render("chosen-ribbon.html", new Dictionary<string, string> { ["text"] = tied ? "⚖ Default (tied)" : "\U0001F3C6 Best Match" })
                : string.Empty;
            cards.Append(ScoredCard(candidate, isChosen, ribbon, candidate.Name));
        }

        var content = GalleryTemplates.Render("wave-page.html", new Dictionary<string, string>
        {
            ["tie_note_html"] = tied
                ? GalleryTemplates.Render("tie-note.html", new Dictionary<string, string> { ["spread"] = Fixed(spread), ["tie_threshold"] = TieThresholdPct.ToString("0.0", CultureInfo.InvariantCulture) })
                : string.Empty,
            ["reference_name"] = Path.GetFileName(input.ReferencePath),
            ["reference_hero"] = reference.Uri,
            ["candidates_html"] = cards.ToString(),
            ["matrix_html"] = Matrix(input, ReferenceCrops(input, reference.Image), scored),
        });
        var note = GalleryTemplates.Render("wave-note.html", new Dictionary<string, string>
        {
            ["frame_weight"] = ((int)(DriftMetrics.FrameWeight * 100)).ToString(CultureInfo.InvariantCulture),
            ["feature_weight"] = ((int)((1.0 - DriftMetrics.FrameWeight) * 100)).ToString(CultureInfo.InvariantCulture),
        });
        return new GalleryRun(Shell(input, "Multi-Candidate Wave Preview", "Multi-Candidate Wave Preview Gallery &mdash; Candidate Selection &amp; Feature Matrix", note, content), lines);
    }

    /// <summary>The single-target page: the default variant against the reference, region by region.</summary>
    public static async Task<GalleryRun> SingleAsync(GalleryInput input, ISvgRenderer renderer, string scratch, CancellationToken cancellationToken)
    {
        var reference = await LoadReferenceAsync(input, cancellationToken).ConfigureAwait(false);
        var key = input.DefaultVariant;
        var render = await RenderAsync(input, renderer, scratch, "rendered_svg", key, cancellationToken).ConfigureAwait(false);
        var score = DriftMetrics.ScoreCandidate(reference.Image, render.Image, Boxes(input));
        var variant = input.Variants.GetValueOrDefault(key) ?? new Variant(string.Empty, string.Empty);
        var candidate = new ScoredCandidate(key, variant.Name.Length > 0 ? variant.Name : Path.GetFileName(input.TargetSvg), variant.Description, score, DataUri(render.Png), CropUris(render.Image, input));
        var content = GalleryTemplates.Render("single-page.html", new Dictionary<string, string>
        {
            ["reference_name"] = Path.GetFileName(input.ReferencePath),
            ["reference_hero"] = reference.Uri,
            ["candidate_html"] = ScoredCard(candidate, true, string.Empty, "Target SVG"),
            ["matrix_html"] = Matrix(input, ReferenceCrops(input, reference.Image), [candidate]),
        });
        var note = GalleryTemplates.Render("single-note.html", new Dictionary<string, string> { ["composite"] = Fixed(score.CompositePct), ["full_frame"] = Fixed(score.FullFramePct) });
        var lines = new List<string> { $"  composite {Fixed(score.CompositePct)}% (frame {Fixed(score.FullFramePct)}%)" };
        return new GalleryRun(Shell(input, "Logo Macro Element Analysis", "Deterministic Logo Reconciliation &mdash; Reference vs Target SVG Vector", note, content), lines);
    }

    private static string Shell(GalleryInput input, string title, string subtitleHtml, string noteHtml, string contentHtml) =>
        GalleryTemplates.Render("shell.html", new Dictionary<string, string>
        {
            ["title"] = $"{input.ProjectName} {title}",
            ["css_html"] = GalleryTemplates.Css,
            ["project_name"] = input.ProjectName,
            ["subtitle_html"] = subtitleHtml,
            ["metric_note_html"] = noteHtml,
            ["content_html"] = contentHtml,
        });

    private static string ScoredCard(ScoredCandidate candidate, bool chosen, string ribbonHtml, string alt) =>
        GalleryTemplates.Render("scored-card.html", new Dictionary<string, string>
        {
            ["chosen_class"] = chosen ? " border-2 border-accent-primary" : string.Empty,
            ["ribbon_html"] = ribbonHtml,
            ["name"] = candidate.Name,
            ["description"] = candidate.Description,
            ["hero"] = candidate.Hero,
            ["alt"] = alt,
            ["badge_class"] = BadgeClass(candidate.Score.CompositePct),
            ["composite"] = Fixed(candidate.Score.CompositePct),
            ["full_frame"] = Fixed(candidate.Score.FullFramePct),
        });

    // One row per region: the canonical reference first, then every candidate cropped from the same box.
    private static string Matrix(GalleryInput input, Dictionary<string, string> referenceCrops, IReadOnlyList<ScoredCandidate> candidates)
    {
        var rows = new StringBuilder();
        foreach (var region in input.Regions)
        {
            var tiles = new StringBuilder();
            foreach (var candidate in candidates)
            {
                tiles.Append(GalleryTemplates.Render("feature-tile.html", new Dictionary<string, string>
                {
                    ["crop"] = candidate.Crops[region.Key],
                    ["alt"] = $"{candidate.Key} {region.Key}",
                    ["short_name"] = candidate.Name.Split(':', 2)[0],
                    ["error"] = candidate.Score.PerFeaturePct[region.Key].ToString("0.0", CultureInfo.InvariantCulture),
                }));
            }

            rows.Append(GalleryTemplates.Render("feature-row.html", new Dictionary<string, string>
            {
                ["label"] = region.Label,
                ["reference_crop"] = referenceCrops[region.Key],
                ["region"] = region.Key,
                ["tiles_html"] = tiles.ToString(),
            }));
        }

        return GalleryTemplates.Render("feature-matrix.html", new Dictionary<string, string> { ["rows_html"] = rows.ToString() });
    }

    private static string Chips(RgbImage image, string alt)
    {
        var chips = new StringBuilder();
        foreach (var size in ChoiceSizes)
        {
            chips.Append(GalleryTemplates.Render("size-chip.html", new Dictionary<string, string>
            {
                ["uri"] = DataUri(Sheets.EncodePng(PillowResize.Resize(image, size, size, ResizeFilter.Lanczos))),
                ["alt"] = $"{alt} at {size}px",
                ["size"] = size.ToString(CultureInfo.InvariantCulture),
            }));
        }

        return chips.ToString();
    }

    private static Dictionary<string, string> ReferenceCrops(GalleryInput input, RgbImage reference) =>
        input.Regions.ToDictionary(region => region.Key, region => DataUri(Sheets.EncodePng(Crop(reference, region.Box))), StringComparer.Ordinal);

    private static Dictionary<string, string> CropUris(RgbImage render, GalleryInput input) => ReferenceCrops(input, render);

    private static List<FeatureBox> Boxes(GalleryInput input) => [.. input.Regions.Select(region => region.Box)];

    private static RgbImage Crop(RgbImage image, FeatureBox box) => image.Crop(box.X0, box.Y0, box.X1, box.Y1);

    private static string Name(Variant variant, string key) => variant.Name.Length > 0 ? variant.Name : key;

    private static string Fixed(double value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    private static string DataUri(byte[] png) => "data:image/png;base64," + Convert.ToBase64String(png);

    private static async Task<(RgbImage Image, string Uri)> LoadReferenceAsync(GalleryInput input, CancellationToken cancellationToken)
    {
        var bytes = await File.ReadAllBytesAsync(input.ReferencePath, cancellationToken).ConfigureAwait(false);
        var mime = input.ReferencePath.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || input.ReferencePath.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ? "image/jpeg" : "image/png";
        return (RasterPipeline.LoadNormalised(bytes), $"data:{mime};base64,{Convert.ToBase64String(bytes)}");
    }

    private static async Task<(RgbImage Image, byte[] Png)> RenderAsync(GalleryInput input, ISvgRenderer renderer, string scratch, string label, string variant, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(scratch);
        var path = Path.Join(scratch, $"{label}.svg");
        await File.WriteAllTextAsync(path, input.Generate(variant), new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        var raster = await renderer.RenderAsync(path, RasterPipeline.NormalisedSize, cancellationToken).ConfigureAwait(false);
        return (RasterPipeline.Normalise(raster), RasterPipeline.EncodePng(raster));
    }

    private sealed record ScoredCandidate(string Key, string Name, string Description, Score Score, string Hero, IReadOnlyDictionary<string, string> Crops);
}
