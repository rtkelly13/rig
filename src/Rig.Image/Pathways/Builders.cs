using System.Collections.Frozen;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Rig.Image.Pathways;

/// <summary>A committed variant file, its digest and where it came from (<c>builder_assets/rtkelly-labs/manifest.json</c>).</summary>
/// <param name="File">The file name beside the manifest.</param>
/// <param name="Sha256">The file's SHA-256, lower-case hex.</param>
/// <param name="Gated">Whether the reference drift gate covers it.</param>
/// <param name="Provenance">Where it came from.</param>
/// <param name="PromotedFrom">The exploration candidate it was promoted from, if any.</param>
public sealed record VariantEntry(string File, string Sha256, bool Gated, string Provenance, PromotionRecord? PromotedFrom);

/// <summary>The exploration candidate a variant was promoted from.</summary>
/// <param name="RunId">The exploration run.</param>
/// <param name="Candidate">The candidate's number in the run.</param>
/// <param name="Notes">Free text.</param>
public sealed record PromotionRecord(string RunId, int Candidate, string Notes);

/// <summary>The labs variant manifest.</summary>
/// <param name="Project">The project name.</param>
/// <param name="Variants">Each variant's entry, in file order.</param>
public sealed record VariantManifest(string Project, IReadOnlyDictionary<string, VariantEntry> Variants);

/// <summary>The hand-tuned master and favicon SVGs of a project, embedded from <c>builder-assets/</c>.</summary>
internal static class BuilderAssets
{
    /// <summary>Reads one embedded asset as text.</summary>
    public static string Read(string project, string file)
    {
        var name = $"Rig.Image.builder_assets.{project}.{file}";
        using var stream = typeof(BuilderAssets).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"{name} is not embedded in {typeof(BuilderAssets).Assembly.GetName().Name}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

/// <summary>The rtkelly-labs governed master (the original <c>labs_builder</c>): a trace, so the SVG itself is the artefact.</summary>
public static class LabsBuilder
{
    /// <summary>The variant used when none is named.</summary>
    public const string DefaultVariant = "master";

    private const string Project = "rtkelly-labs";

    /// <summary>The declared variants.</summary>
    public static IReadOnlyDictionary<string, Variant> Variants { get; } = new Dictionary<string, Variant>
    {
        ["master"] = new Variant(
            "Candidate 1 Master",
            "Resolution-independent vector logo for rtkelly-labs: isometric facets, native multi-stop linear gradients, and zero staircase aliasing."),
        ["favicon"] = new Variant(
            "Favicon Cut",
            "High-contrast cut for 16-32px display. Same loop and beam geometry as the master, flattened to two tones with the drop shadow removed."),
    }.ToFrozenDictionary();

    /// <summary>The manifest: per-variant file, digest, gating and provenance.</summary>
    public static VariantManifest Manifest()
    {
        const string Source = "rtkelly-labs/manifest.json";
        var document = StrictJson.Parse(Source, BuilderAssets.Read(Project, "manifest.json"));
        var variants = document.Property("variants");
        return new VariantManifest(
            document.Property("project").AsString(),
            variants.PropertyNames().ToDictionary(name => name, name => ReadEntry(variants.Property(name))));
    }

    /// <summary>The SVG text of a variant; null takes the default.</summary>
    public static string GenerateSvg(string? variant = DefaultVariant)
    {
        var key = VariantResolution.Resolve(variant, [.. Variants.Keys], DefaultVariant);
        return BuilderAssets.Read(Project, Manifest().Variants[key].File);
    }

    /// <summary>The manifest as committed: two-space indented, unset fields omitted, trailing newline (matches the original <c>to_json</c>).</summary>
    public static string ManifestJson(VariantManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            writer.WriteString("project", manifest.Project);
            writer.WriteStartObject("variants");
            foreach (var (name, entry) in manifest.Variants)
            {
                writer.WriteStartObject(name);
                writer.WriteString("file", entry.File);
                writer.WriteString("sha256", entry.Sha256);
                writer.WriteBoolean("gated", entry.Gated);
                writer.WriteString("provenance", entry.Provenance);
                if (entry.PromotedFrom is { } promoted)
                {
                    writer.WriteStartObject("promoted_from");
                    writer.WriteString("run_id", promoted.RunId);
                    writer.WriteNumber("candidate", promoted.Candidate);
                    writer.WriteString("notes", promoted.Notes);
                    writer.WriteEndObject();
                }

                writer.WriteEndObject();
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray()).ReplaceLineEndings("\n") + "\n";
    }

    private static PromotionRecord ReadPromotion(StrictJson promoted) =>
        new(promoted.Property("run_id").AsString(), promoted.Property("candidate").AsInteger(), promoted.Property("notes").AsString());

    private static VariantEntry ReadEntry(StrictJson entry)
    {
        var hasPromotion = entry.PropertyNames().Contains("promoted_from");
        return new VariantEntry(
            entry.Property("file").AsString(),
            entry.Property("sha256").AsString(),
            entry.Property("gated").AsBoolean(),
            entry.Property("provenance").AsString(),
            hasPromotion ? ReadPromotion(entry.Property("promoted_from")) : null);
    }
}

/// <summary>The Parquet.SourceGenerator diamond mark (the original <c>diamond_parquet_builder</c>): one hand-tuned SVG per variant.</summary>
public static class DiamondParquetBuilder
{
    /// <summary>The variant used when none is named.</summary>
    public const string DefaultVariant = "master";

    private const string Project = "parquet-source-generator";

    /// <summary>The declared variants.</summary>
    public static IReadOnlyDictionary<string, Variant> Variants { get; } = new Dictionary<string, Variant>
    {
        ["master"] = new Variant(
            "Minimalist Diamond Parquet Master",
            "Resolution-independent vector mark for Parquet.SourceGenerator featuring an interlocking 45-degree diamond parquet tile card on dark navy-charcoal with a centered 3-way git fork graph carved into wood seams with glowing nodes."),
        ["favicon"] = new Variant("Favicon Cut", "High-contrast diamond parquet cut optimized for 16-32px display."),
    }.ToFrozenDictionary();

    /// <summary>The SVG text of a variant; null takes the default.</summary>
    public static string GenerateSvg(string? variant = DefaultVariant)
    {
        var key = VariantResolution.Resolve(variant, [.. Variants.Keys], DefaultVariant);
        return BuilderAssets.Read(Project, $"{key}.svg");
    }
}
