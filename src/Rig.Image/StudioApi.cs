using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Rig.Image;

/// <summary>An Asset Studio API answer: a status and a JSON body.</summary>
/// <param name="Status">The HTTP status.</param>
/// <param name="Body">The JSON text.</param>
internal sealed record StudioResponse(int Status, string Body);

/// <summary>
/// The Asset Studio's JSON API (the original <c>serve</c>): project catalogs, drift diffs against companion rasters and snapshot capture. The web host
/// maps routes onto these; nothing here renders SVG, so no browser is needed.
/// </summary>
internal sealed class StudioApi(string assetsRoot, string variationsRoot)
{
    /// <summary>Where the brand assets are served.</summary>
    public const string AssetsPrefix = "/media/assets/";

    /// <summary>Where variation snapshots are served.</summary>
    public const string VariationsPrefix = "/media/variations/";

    private static readonly JsonWriterOptions Options = new() { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static readonly JsonSerializerOptions MetricsOptions = new() { NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals };

    private readonly string _assets = Path.GetFullPath(assetsRoot);
    private readonly string _variations = Path.GetFullPath(variationsRoot);

    /// <summary><c>GET /api/projects</c>: every managed project's catalog with media URLs.</summary>
    public StudioResponse Projects()
    {
        var catalog = new AssetCatalog(_assets, _variations);
        return Write(200, writer =>
        {
            writer.WriteStartObject();
            writer.WriteStartArray("projects");
            foreach (var project in catalog.IndexAll())
            {
                WriteProject(writer, catalog, project);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        });
    }

    /// <summary><c>GET /api/diff</c>: the drift of a candidate image against a reference. <paramref name="resolve"/> turns each media URL or path the caller sent into a file, or null when there is none.</summary>
    public static StudioResponse Diff(string? reference, string? candidate, Func<string, string?> resolve)
    {
        if (string.IsNullOrEmpty(reference) || string.IsNullOrEmpty(candidate))
        {
            return Error(400, "Missing 'ref' or 'cand' query parameter");
        }

        var referencePath = resolve(reference);
        var candidatePath = resolve(candidate);
        if (referencePath is null || candidatePath is null)
        {
            return Write(404, writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("error", "Could not resolve ref or cand image path");
                writer.WriteString("ref", reference);
                writer.WriteString("cand", candidate);
                writer.WriteEndObject();
            });
        }

        try
        {
            var referenceRaster = CompanionRaster(referencePath, fallbacks: false);
            var candidateRaster = CompanionRaster(candidatePath, fallbacks: true);
            var referenceImage = RasterPipeline.LoadNormalised(File.ReadAllBytes(referenceRaster));
            var candidateImage = RasterPipeline.LoadNormalised(File.ReadAllBytes(candidateRaster));
            var drift = DriftMetrics.Drift(referenceImage, candidateImage);
            var heatmap = Convert.ToBase64String(Sheets.EncodePng(DriftReport.Heatmap(referenceImage, candidateImage)));
            return Write(200, writer =>
            {
                writer.WriteStartObject();
                writer.WriteNumber("rmse_pct", Round(drift.RmsePct, 2));
                writer.WriteNumber("ssim", Round(drift.Ssim, 4));
                writer.WriteNumber("psnr_db", double.IsInfinity(drift.PsnrDb) ? 999.0 : Round(drift.PsnrDb, 2));
                writer.WriteString("diff_heatmap_data", "data:image/png;base64," + heatmap);
                writer.WriteNumber("ref_size_bytes", new FileInfo(referencePath).Length);
                writer.WriteNumber("cand_size_bytes", new FileInfo(candidatePath).Length);
                writer.WriteStartObject("decomposed");
                writer.WriteNumber("luminance_rmse_pct", Round(drift.LuminanceRmsePct, 2));
                writer.WriteNumber("chrominance_rmse_pct", Round(drift.ChrominanceRmsePct, 2));
                writer.WriteNumber("color_to_geometry_ratio", Round(drift.ColorToGeometryRatio, 2));
                writer.WriteNumber("edge_correlation", Round(drift.EdgeCorrelation, 1));
                writer.WriteNumber("boundary_dilation_bias", Round(drift.BoundaryDilationBias, 3));
                writer.WriteStartArray("hotspots");
                foreach (var hotspot in drift.Hotspots)
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("rank", hotspot.Rank);
                    writer.WriteNumber("x0", hotspot.X0);
                    writer.WriteNumber("y0", hotspot.Y0);
                    writer.WriteNumber("x1", hotspot.X1);
                    writer.WriteNumber("y1", hotspot.Y1);
                    writer.WriteNumber("error_pct", Round(hotspot.ErrorPct, 2));
                    writer.WriteString("dominant_mode", hotspot.Mode);
                    writer.WriteString("summary", DriftReport.HotspotSummary(hotspot.Mode));
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteStartArray("recommendations");
                foreach (var recommendation in DriftReport.Recommendations(drift))
                {
                    writer.WriteStringValue(recommendation);
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
                writer.WriteEndObject();
            });
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException)
        {
            return Error(500, $"Error computing drift: {exception.Message}");
        }
    }

    /// <summary><c>POST /api/snapshot</c>: records a step under the variations tree. The body is JSON with <c>project</c>, <c>run_id</c>, and optionally <c>step_idx</c>, <c>step_name</c>, <c>svg_content</c>, <c>metrics</c> and <c>notes</c>.</summary>
    public StudioResponse Snapshot(string body)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException exception)
        {
            return Error(400, $"Invalid snapshot request: expected a JSON object, found {exception.Message}");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Error(400, $"Invalid snapshot request: expected a JSON object, found {root.ValueKind}");
            }

            string? project;
            string? runId;
            string stepName;
            string notes;
            string? svg;
            int stepIdx;
            try
            {
                project = Text(root, "project", null);
                runId = Text(root, "run_id", null);
                stepName = Text(root, "step_name", "checkpoint")!;
                notes = Text(root, "notes", string.Empty)!;
                svg = Text(root, "svg_content", null);
                stepIdx = root.TryGetProperty("step_idx", out var idx) ? (idx.ValueKind == JsonValueKind.Number && idx.TryGetInt32(out var parsed) ? parsed : throw new FormatException($"expected step_idx to be an integer, found {idx}")) : 1;
            }
            catch (FormatException exception)
            {
                return Error(400, $"Invalid snapshot request: {exception.Message}");
            }

            if (string.IsNullOrEmpty(project) || string.IsNullOrEmpty(runId))
            {
                return Error(400, "Missing project or run_id");
            }

            var metrics = new Dictionary<string, object>();
            if (root.TryGetProperty("metrics", out var recorded) && recorded.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in recorded.EnumerateObject())
                {
                    metrics[property.Name] = property.Value.Clone();
                }
            }

            try
            {
                var step = new SnapshotManager(_variations).CaptureStep(project, runId, stepIdx, stepName, svg, null, metrics, notes);
                return Write(200, writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteString("status", "ok");
                    writer.WriteStartObject("step");
                    writer.WriteNumber("step_idx", step.StepIdx);
                    writer.WriteString("step_name", step.StepName);
                    writer.WriteString("timestamp", step.Timestamp);
                    writer.WriteString("svg_path", step.SvgPath);
                    writer.WriteString("metadata_path", step.MetadataPath);
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                });
            }
            catch (IOException exception)
            {
                return Error(500, $"Failed to capture snapshot: {exception.Message}");
            }
        }
    }

    private static string? Text(JsonElement root, string name, string? fallback)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return fallback;
        }

        return value.ValueKind == JsonValueKind.String ? value.GetString() : throw new FormatException($"expected {name} to be a string, found {value}");
    }

    private static double Round(double value, int digits) => Math.Round(value, digits, MidpointRounding.ToEven);

    private static StudioResponse Error(int status, string message) => Write(status, writer =>
    {
        writer.WriteStartObject();
        writer.WriteString("error", message);
        writer.WriteEndObject();
    });

    private static StudioResponse Write(int status, Action<Utf8JsonWriter> body)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, Options))
        {
            body(writer);
        }

        return new StudioResponse(status, Encoding.UTF8.GetString(stream.ToArray()));
    }

    private static void WriteProject(Utf8JsonWriter writer, AssetCatalog catalog, ProjectCatalog project)
    {
        writer.WriteStartObject();
        writer.WriteString("name", project.Name);
        WriteUrl(writer, "master_svg", project.MasterSvg);
        WriteUrl(writer, "master_png", project.MasterPng);
        writer.WriteStartArray("references");
        foreach (var reference in project.References)
        {
            writer.WriteStartObject();
            writer.WriteNumber("sequence_number", reference.SequenceNumber);
            writer.WriteString("filename", reference.Filename);
            writer.WriteString("description", reference.Description);
            writer.WriteString("url", $"{AssetsPrefix}{project.Name}/reference/{reference.Filename}");
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteStartArray("production_cuts");
        foreach (var cut in project.Cuts)
        {
            var path = catalog.CutPath(project.Name, cut);
            writer.WriteStartObject();
            writer.WriteString("cut_type", cut.CutType);
            writer.WriteString("filename", cut.Filename);
            writer.WriteNumber("size_bytes", File.Exists(path) ? new FileInfo(path).Length : 0);
            writer.WriteNumber("width", cut.Width);
            writer.WriteNumber("height", cut.Height);
            writer.WriteString("format", cut.Format);
            writer.WriteString("purpose", cut.Purpose);
            writer.WriteString("description", cut.Description);
            writer.WriteString("url", $"{AssetsPrefix}{project.Name}/production-cuts/{cut.Filename}");
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteStartArray("variation_runs");
        foreach (var run in project.Runs)
        {
            writer.WriteStartObject();
            writer.WriteString("run_id", run.RunId);
            writer.WriteString("project", run.Project);
            writer.WriteStartArray("steps");
            foreach (var step in run.Steps)
            {
                writer.WriteStartObject();
                writer.WriteNumber("step_idx", step.StepIdx);
                writer.WriteString("step_name", step.StepName);
                writer.WriteString("timestamp", step.Timestamp);
                writer.WriteString("svg_url", step.SvgPath is null ? null : $"{VariationsPrefix}{project.Name}/{run.RunId}/{Path.GetFileName(step.SvgPath)}");
                writer.WriteString("png_url", step.PngPath is null ? null : $"{VariationsPrefix}{project.Name}/{run.RunId}/{Path.GetFileName(step.PngPath)}");
                writer.WritePropertyName("metrics");
                JsonSerializer.Serialize(writer, step.Metrics, MetricsOptions);
                writer.WriteString("notes", step.Notes);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteUrl(Utf8JsonWriter writer, string name, string? relative) => writer.WriteString(name, relative is null ? null : AssetsPrefix + relative);

    // An SVG is compared through its companion PNG; a candidate without one falls back to a 1000 or 2000 px cut beside it.
    private static string CompanionRaster(string path, bool fallbacks)
    {
        if (!path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
        {
            return path;
        }

        var companion = Path.ChangeExtension(path, ".png");
        if (File.Exists(companion))
        {
            return companion;
        }

        var folder = Path.GetDirectoryName(path)!;
        var cut = fallbacks ? Directory.EnumerateFiles(folder, "*1000x1000.png").Order(StringComparer.Ordinal).FirstOrDefault() ?? Directory.EnumerateFiles(folder, "*2000x2000.png").Order(StringComparer.Ordinal).FirstOrDefault() : null;
        return cut ?? throw new InvalidDataException($"expected a PNG beside {path} to compare, found none; this API does not render SVG");
    }
}
