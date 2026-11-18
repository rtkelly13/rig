using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Rig.Image;

/// <summary>One step of an experimental run: the files a capture wrote and what its sidecar says.</summary>
/// <param name="Project">The project the run belongs to.</param>
/// <param name="RunId">The run's identifier.</param>
/// <param name="StepIdx">The step's position in the run.</param>
/// <param name="StepName">The step's name.</param>
/// <param name="Timestamp">The capture time, <c>yyyyMMdd_HHmmss</c> UTC.</param>
/// <param name="SvgPath">The step's SVG, if one was captured.</param>
/// <param name="PngPath">The step's PNG, if one was captured.</param>
/// <param name="MetadataPath">The step's JSON sidecar.</param>
/// <param name="Metrics">The metrics the sidecar recorded.</param>
/// <param name="Notes">Free text the sidecar recorded.</param>
public sealed record SnapshotStep(
    string Project,
    string RunId,
    int StepIdx,
    string StepName,
    string Timestamp,
    string? SvgPath,
    string? PngPath,
    string? MetadataPath,
    IReadOnlyDictionary<string, object> Metrics,
    string Notes);

/// <summary>A named sequence of steps held in the git-ignored variations tree.</summary>
/// <param name="RunId">The run's identifier.</param>
/// <param name="Project">The project the run belongs to.</param>
/// <param name="Steps">The steps in index order.</param>
public sealed record VariationRun(string RunId, string Project, IReadOnlyList<SnapshotStep> Steps);

/// <summary>Writes and reads step snapshots under a git-ignored variations root (image_drift's <c>SnapshotManager</c>).</summary>
/// <param name="variationsRoot">The folder holding <c>&lt;project&gt;/&lt;run&gt;/</c> directories.</param>
public sealed partial class SnapshotManager(string variationsRoot)
{
    // A lossless PSNR is infinite; JSON has no literal for it, so it is written as the string "Infinity" rather than failing the capture.
    private static readonly JsonSerializerOptions NamedLiterals = new() { NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals };

    private static readonly JsonWriterOptions SidecarOptions = new() { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>The folder snapshots are kept in.</summary>
    public string VariationsRoot { get; } = variationsRoot;

    /// <summary>Writes a step's SVG, PNG and sidecar into the run folder, creating it.</summary>
    public SnapshotStep CaptureStep(string project, string runId, int stepIdx, string stepName, string? svgContent = null, byte[]? pngBytes = null, IReadOnlyDictionary<string, object>? metrics = null, string notes = "", string? timestamp = null)
    {
        var stamp = timestamp ?? DateTime.UtcNow.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        var runDir = Path.Join(VariationsRoot, project, runId);
        Directory.CreateDirectory(runDir);
        var prefix = string.Create(CultureInfo.InvariantCulture, $"{project}__{runId}__{stepIdx:00}_{stepName}__{stamp}");

        string? svgPath = null;
        if (svgContent is not null)
        {
            svgPath = Path.Join(runDir, prefix + ".svg");
            File.WriteAllText(svgPath, svgContent.Replace("\r\n", "\n", StringComparison.Ordinal), new UTF8Encoding(false));
        }

        string? pngPath = null;
        if (pngBytes is not null)
        {
            pngPath = Path.Join(runDir, prefix + ".png");
            File.WriteAllBytes(pngPath, pngBytes);
        }

        var recorded = metrics ?? new Dictionary<string, object>();
        var metadataPath = Path.Join(runDir, prefix + ".json");
        File.WriteAllText(metadataPath, Sidecar(project, runId, stepIdx, stepName, stamp, recorded, notes), new UTF8Encoding(false));
        return new SnapshotStep(project, runId, stepIdx, stepName, stamp, svgPath, pngPath, metadataPath, recorded, notes);
    }

    /// <summary>The run identifiers of a project, sorted, dot-folders excluded.</summary>
    public IReadOnlyList<string> ListRuns(string project)
    {
        var projectDir = Path.Join(VariationsRoot, project);
        if (!Directory.Exists(projectDir))
        {
            return [];
        }

        return [.. Directory.EnumerateDirectories(projectDir).Select(d => Path.GetFileName(d)).Where(n => !n.StartsWith('.')).Order(StringComparer.Ordinal)];
    }

    /// <summary>A run with its steps parsed from the file names and sidecars, or null when there is none.</summary>
    public VariationRun? GetRun(string project, string runId)
    {
        var runDir = Path.Join(VariationsRoot, project, runId);
        if (string.IsNullOrEmpty(runId) || runId.StartsWith('.') || !Directory.Exists(runDir))
        {
            return null;
        }

        var steps = new SortedDictionary<(int Index, string Name), SnapshotStep>();
        foreach (var file in Directory.EnumerateFiles(runDir).Order(StringComparer.Ordinal))
        {
            var match = SnapshotName().Match(Path.GetFileName(file));
            if (!match.Success)
            {
                continue;
            }

            var key = (int.Parse(match.Groups["idx"].Value, CultureInfo.InvariantCulture), match.Groups["step"].Value);
            var step = steps.GetValueOrDefault(key) ?? new SnapshotStep(match.Groups["project"].Value, match.Groups["run"].Value, key.Item1, key.Item2, match.Groups["ts"].Value, null, null, null, new Dictionary<string, object>(), string.Empty);
            steps[key] = match.Groups["ext"].Value switch
            {
                "svg" => step with { SvgPath = file },
                "png" => step with { PngPath = file },
                _ => ReadSidecar(step, file),
            };
        }

        return new VariationRun(runId, project, [.. steps.Values.OrderBy(s => s.StepIdx)]);
    }

    /// <summary>Deletes a run's folder; false when there was none.</summary>
    public bool PruneRun(string project, string runId)
    {
        var runDir = Path.Join(VariationsRoot, project, runId);
        if (!Directory.Exists(runDir))
        {
            return false;
        }

        Directory.Delete(runDir, recursive: true);
        return true;
    }

    [GeneratedRegex(@"^(?<project>[a-zA-Z0-9_\-]+)__(?<run>[a-zA-Z0-9_\-]+)__(?<idx>\d+)_(?<step>[a-zA-Z0-9_\-]+)__(?<ts>[a-zA-Z0-9_]+)\.(?<ext>svg|png|json)$", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex SnapshotName();

    private static string Sidecar(string project, string runId, int stepIdx, string stepName, string stamp, IReadOnlyDictionary<string, object> metrics, string notes)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, SidecarOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("project", project);
            writer.WriteString("run_id", runId);
            writer.WriteNumber("step_idx", stepIdx);
            writer.WriteString("step_name", stepName);
            writer.WriteString("timestamp", stamp);
            writer.WritePropertyName("metrics");
            JsonSerializer.Serialize(writer, metrics, NamedLiterals);
            writer.WriteString("notes", notes);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static SnapshotStep ReadSidecar(SnapshotStep step, string file)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return step with { MetadataPath = file };
            }

            var metricsElement = root.TryGetProperty("metrics", out var m) && m.ValueKind == JsonValueKind.Object ? m : root;
            var metrics = metricsElement.EnumerateObject().ToDictionary(p => p.Name, p => (object)p.Value.Clone());
            var notes = root.TryGetProperty("notes", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? string.Empty : string.Empty;
            return step with { MetadataPath = file, Metrics = metrics, Notes = notes };
        }
        catch (JsonException)
        {
            return step with { MetadataPath = file };
        }
        catch (IOException)
        {
            return step with { MetadataPath = file };
        }
    }
}
