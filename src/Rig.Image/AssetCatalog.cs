using System.Globalization;
using System.Text.RegularExpressions;

namespace Rig.Image;

/// <summary>A reference image a project is scored against.</summary>
internal sealed record ReferenceAsset(int SequenceNumber, string Filename, string Description, bool IsPrimary);

/// <summary>A project's assets as the Asset Studio shows them: master, references and production cuts, the cuts exactly as its CSV declares them.</summary>
internal sealed record ProjectCatalog(string Name, string? MasterSvg, string? MasterPng, IReadOnlyList<ReferenceAsset> References, IReadOnlyList<CutSpec> Cuts, IReadOnlyList<VariationRun> Runs);

/// <summary>Indexes the assets folder and the git-ignored variations tree into project catalogs (image_drift's AssetIndexer).</summary>
internal sealed partial class AssetCatalog(string assetsRoot, string variationsRoot)
{
    private static readonly Dictionary<string, int> CutOrder = new(StringComparer.Ordinal)
    {
        ["cut_2000"] = 1,
        ["master_png"] = 2,
        ["cut_1000"] = 2,
        ["master_svg"] = 3,
        ["svg"] = 3,
        ["avatar"] = 4,
        ["apple_touch"] = 5,
        ["favicon_svg"] = 6,
        ["favicon"] = 7,
        ["reference_raster"] = 8,
        ["raster"] = 9,
    };

    /// <summary>The managed projects: folders under the assets root, minus dot, underscore, <c>testing</c> and <c>temp</c> folders.</summary>
    public IReadOnlyList<string> Projects()
    {
        if (!Directory.Exists(assetsRoot))
        {
            return [];
        }

        return [.. Directory.EnumerateDirectories(assetsRoot)
            .Select(Path.GetFileName)
            .OfType<string>()
            .Where(name => !name.StartsWith('.') && !name.StartsWith('_') && name is not ("testing" or "temp"))
            .Order(StringComparer.Ordinal)];
    }

    /// <summary>One project's catalog; production cuts come only from its <c>production-cuts.csv</c> (AGENTS.md rule 4), so a project without one has none.</summary>
    public ProjectCatalog Index(string project)
    {
        var folder = Path.Join(assetsRoot, project);
        var cutsFolder = Path.Join(folder, "production-cuts");
        var csv = Path.Join(cutsFolder, ProductionCuts.CsvName);
        var cuts = File.Exists(csv)
            ? ProductionCuts.Read(csv).OrderBy(cut => CutOrder.GetValueOrDefault(cut.CutType, 99)).ThenBy(cut => cut.Filename, StringComparer.Ordinal).ToList()
            : [];
        var references = Directory.Exists(Path.Join(folder, "reference")) ? References(Path.Join(folder, "reference")) : [];
        var manager = new SnapshotManager(variationsRoot);
        var runs = manager.ListRuns(project).Select(run => manager.GetRun(project, run)).OfType<VariationRun>().ToList();
        return new ProjectCatalog(
            project,
            RelativeOrNull(cutsFolder, cuts.FirstOrDefault(cut => cut.CutType == "master_svg")),
            RelativeOrNull(cutsFolder, cuts.FirstOrDefault(cut => cut.CutType is "master_png" or "cut_1000")),
            references,
            cuts,
            runs);
    }

    /// <summary>The catalogs of every managed project.</summary>
    public IReadOnlyList<ProjectCatalog> IndexAll() => [.. Projects().Select(Index)];

    /// <summary>The path of the file a cut names, under the assets root.</summary>
    public string CutPath(string project, CutSpec cut) => Path.Join(assetsRoot, project, "production-cuts", cut.Filename);

    private string? RelativeOrNull(string cutsFolder, CutSpec? cut) => cut is null ? null : Path.GetRelativePath(assetsRoot, Path.Join(cutsFolder, cut.Filename)).Replace(Path.DirectorySeparatorChar, '/');

    private static List<ReferenceAsset> References(string folder)
    {
        var references = new List<ReferenceAsset>();
        var manifest = Path.Join(folder, "manifest.json");
        if (File.Exists(manifest))
        {
            references.AddRange(ReadManifest(manifest));
        }

        var known = references.Select(reference => reference.Filename).ToHashSet(StringComparer.Ordinal);
        var counter = 1;
        foreach (var file in Directory.EnumerateFiles(folder).Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileName(file);
            if (name.StartsWith('.') || Path.GetExtension(name).ToLowerInvariant() is ".md" or ".json" || known.Contains(name))
            {
                continue;
            }

            var prefix = SequencePrefix().Match(name);
            var sequence = prefix.Success ? int.Parse(prefix.Groups[1].Value, CultureInfo.InvariantCulture) : 90 + counter++;
            references.Add(new ReferenceAsset(sequence, name, Path.GetFileNameWithoutExtension(name), false));
        }

        return [.. references.OrderBy(reference => reference.SequenceNumber)];
    }

    private static List<ReferenceAsset> ReadManifest(string manifest)
    {
        var document = StrictJson.Parse(manifest, File.ReadAllText(manifest));
        var names = document.PropertyNames();
        var primary = names.Contains("primary", StringComparer.Ordinal) ? document.Property("primary").AsString().Trim() : string.Empty;
        var references = new List<ReferenceAsset>();
        if (!names.Contains("references", StringComparer.Ordinal))
        {
            return references;
        }

        foreach (var item in document.Property("references").Items())
        {
            var fields = item.PropertyNames();
            var filename = fields.Contains("filename", StringComparer.Ordinal) ? item.Property("filename").AsString().Trim() : string.Empty;
            if (filename.Length == 0)
            {
                continue;
            }

            var role = fields.Contains("role", StringComparer.Ordinal) ? item.Property("role").AsString() : "historical";
            references.Add(new ReferenceAsset(
                fields.Contains("sequence_number", StringComparer.Ordinal) ? item.Property("sequence_number").AsInteger() : 99,
                filename,
                fields.Contains("description", StringComparer.Ordinal) ? item.Property("description").AsString() : string.Empty,
                filename == primary || role == "primary"));
        }

        return references;
    }

    [GeneratedRegex(@"^(\d+)-", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex SequencePrefix();
}
