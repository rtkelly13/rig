using System.Globalization;
using System.Text;

namespace Rig.Image;

/// <summary>One production cut a project's <c>production-cuts.csv</c> declares.</summary>
/// <param name="Filename">The file written into the cuts folder.</param>
/// <param name="CutType">The CSV's cut type; <c>favicon*</c> types come from the favicon variant.</param>
/// <param name="Width">Pixel width.</param>
/// <param name="Height">Pixel height.</param>
/// <param name="Format">Lower-case format: <c>svg</c>, <c>png</c> or <c>ico</c>.</param>
/// <param name="Purpose">What the cut is for; empty when the CSV has no purpose column.</param>
/// <param name="Description">A sentence describing the cut; empty when the CSV has none.</param>
internal sealed record CutSpec(string Filename, string CutType, int Width, int Height, string Format, string Purpose = "", string Description = "")
{
    /// <summary>Whether the cut is drawn from the favicon variant, which stays legible at small sizes.</summary>
    public bool FromFaviconVariant => CutType.StartsWith(ProductionCuts.FaviconVariant, StringComparison.Ordinal);
}

/// <summary>
/// Exports a project's production cuts exactly as its <c>production-cuts.csv</c> declares them (AGENTS.md rule 4): the CSV is the only source
/// of what is written, one file per row at the row's size and format.
/// </summary>
internal static class ProductionCuts
{
    /// <summary>The CSV's file name, inside a project's <c>production-cuts/</c> folder.</summary>
    public const string CsvName = "production-cuts.csv";

    /// <summary>The builder variant the <c>favicon*</c> cut types come from.</summary>
    public const string FaviconVariant = "favicon";

    private static readonly string[] Columns = ["filename", "cut_type", "width", "height", "format"];

    /// <summary>Every row of the CSV at <paramref name="csvPath"/>, in file order; rows with a blank filename are skipped.</summary>
    public static IReadOnlyList<CutSpec> Read(string csvPath)
    {
        if (!File.Exists(csvPath))
        {
            throw new FileNotFoundException($"expected the production cuts CSV at {csvPath}, found none", csvPath);
        }

        return Parse(csvPath, File.ReadAllText(csvPath, Encoding.UTF8));
    }

    /// <summary>Parses CSV text; <paramref name="source"/> names it in errors.</summary>
    public static IReadOnlyList<CutSpec> Parse(string source, string text)
    {
        var records = Records(text);
        if (records.Count == 0)
        {
            throw new InvalidDataException($"{source}: expected a header row with {string.Join(", ", Columns)}, found an empty file");
        }

        var header = records[0].Select(name => name.Trim()).ToList();
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var column in Columns)
        {
            var at = header.IndexOf(column);
            if (at < 0)
            {
                throw new InvalidDataException($"{source}: expected a '{column}' column in the header, found {string.Join(", ", header)}");
            }

            index[column] = at;
        }

        foreach (var optional in new[] { "purpose", "description" })
        {
            var at = header.IndexOf(optional);
            if (at >= 0)
            {
                index[optional] = at;
            }
        }

        var cuts = new List<CutSpec>();
        for (var line = 1; line < records.Count; line++)
        {
            var record = records[line];
            var filename = Field(record, index["filename"]).Trim();
            if (filename.Length == 0)
            {
                continue;
            }

            cuts.Add(new CutSpec(
                filename,
                Field(record, index["cut_type"]).Trim(),
                Dimension(source, line + 1, "width", Field(record, index["width"])),
                Dimension(source, line + 1, "height", Field(record, index["height"])),
                Field(record, index["format"]).Trim().ToLowerInvariant(),
                Optional(record, index, "purpose"),
                Optional(record, index, "description")));
        }

        return cuts;
    }

    /// <summary>Whether the cut is one a renderer produces rather than a copy of the SVG.</summary>
    public static bool IsRaster(CutSpec cut) => cut.Format is "png" or "ico";

    /// <summary>
    /// Writes each declared cut into <paramref name="outputDir"/> and returns the written paths in CSV order. <paramref name="masterSvg"/> and
    /// <paramref name="faviconSvg"/> are SVG files; <paramref name="renderer"/> draws the raster cuts.
    /// </summary>
    public static async Task<IReadOnlyList<string>> ExportAsync(IReadOnlyList<CutSpec> cuts, string outputDir, string masterSvg, string faviconSvg, ISvgRenderer renderer, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(outputDir);
        var written = new List<string>();
        foreach (var cut in cuts)
        {
            var source = cut.FromFaviconVariant ? faviconSvg : masterSvg;
            var target = Path.Join(outputDir, cut.Filename);
            switch (cut.Format)
            {
                case "svg":
                    File.Copy(source, target, overwrite: true);
                    break;
                case "png":
                    await File.WriteAllBytesAsync(target, RasterPipeline.EncodePng(await Render(renderer, source, cut, cancellationToken).ConfigureAwait(false)), cancellationToken).ConfigureAwait(false);
                    break;
                case "ico":
                    await File.WriteAllBytesAsync(target, IcoFile.FromFavicon(await Render(renderer, source, cut, cancellationToken).ConfigureAwait(false)), cancellationToken).ConfigureAwait(false);
                    break;
                default:
                    throw new InvalidDataException($"{cut.Filename}: expected a cut format of svg, png or ico, found '{cut.Format}'");
            }

            written.Add(target);
        }

        return written;
    }

    private static async Task<Raster> Render(ISvgRenderer renderer, string svg, CutSpec cut, CancellationToken cancellationToken)
    {
        if (cut.Width != cut.Height)
        {
            throw new InvalidDataException($"{cut.Filename}: expected a square cut, found {cut.Width}x{cut.Height}");
        }

        return await renderer.RenderAsync(svg, cut.Width, cancellationToken).ConfigureAwait(false);
    }

    private static string Optional(List<string> record, Dictionary<string, int> index, string column) => index.TryGetValue(column, out var at) ? Field(record, at).Trim() : string.Empty;

    private static string Field(List<string> record, int index) => index < record.Count ? record[index] : string.Empty;

    private static int Dimension(string source, int line, string column, string value)
    {
        if (!int.TryParse(value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed <= 0)
        {
            throw new InvalidDataException($"{source}: line {line}: expected {column} to be a positive whole number, found '{value}'");
        }

        return parsed;
    }

    // RFC 4180: quoted fields may hold commas, doubled quotes and newlines. Blank lines are dropped.
    private static List<List<string>> Records(string text)
    {
        var records = new List<List<string>>();
        var record = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    field.Append(c);
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    quoted = true;
                    break;
                case ',':
                    record.Add(field.ToString());
                    field.Clear();
                    break;
                case '\r':
                    break;
                case '\n':
                    EndRecord(records, record, field);
                    record = [];
                    break;
                default:
                    field.Append(c);
                    break;
            }
        }

        EndRecord(records, record, field);
        return records;
    }

    private static void EndRecord(List<List<string>> records, List<string> record, StringBuilder field)
    {
        record.Add(field.ToString());
        field.Clear();
        if (record.Count > 1 || record[0].Length > 0)
        {
            records.Add(record);
        }
    }
}
