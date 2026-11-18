using Rig.Image;

namespace Rig.Cli.Image;

/// <summary>A brand project the explore commands work on, with the files they read.</summary>
/// <param name="Name">The canonical project key.</param>
/// <param name="Svg">The committed master SVG.</param>
/// <param name="Reference">The approved reference raster.</param>
internal sealed record ImageProject(string Name, string Svg, string Reference)
{
    /// <summary>The feature boxes this project's drift score weights.</summary>
    public IReadOnlyList<FeatureBox> Regions => FeatureRegions.For(Name);
}

/// <summary>Resolves project keys and aliases the way image_drift's <c>resolve_project</c> does.</summary>
internal static class ImageProjects
{
    /// <summary>The project used when none is named.</summary>
    public const string Default = "rtkelly-labs";

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal)
    {
        ["rtkelly-labs"] = "rtkelly-labs",
        ["labs"] = "rtkelly-labs",
        ["parquet-source-generator"] = "parquet-source-generator",
        ["parquet"] = "parquet-source-generator",
        ["diamond"] = "parquet-source-generator",
    };

    /// <summary>The project for <paramref name="name"/> under <paramref name="repoRoot"/>; unknown names throw with the choices listed.</summary>
    public static ImageProject Resolve(string repoRoot, string? name)
    {
        var raw = (name ?? Default).Trim().ToLowerInvariant();
        var canonical = Aliases.GetValueOrDefault(raw, raw);
        return canonical switch
        {
            "rtkelly-labs" => For(repoRoot, canonical, "rtkelly-labs.svg", ["02-smooth-crossover-baseline.jpg", "rtkelly-labs.png"]),
            "parquet-source-generator" => For(repoRoot, canonical, "parquet-diamond.svg", ["02-simplified-cad-baseline.jpg", "parquet-diamond.png"]),
            _ => throw new ArgumentException($"expected one of parquet-source-generator, rtkelly-labs, found '{name}'", nameof(name)),
        };
    }

    private static ImageProject For(string repoRoot, string name, string svg, string[] candidates)
    {
        var assets = Path.Join(repoRoot, "assets", name);
        return new ImageProject(name, Path.Join(assets, svg), Reference(assets, candidates));
    }

    private static string Reference(string assets, string[] candidates)
    {
        var folder = Path.Join(assets, "reference");
        var primary = ManifestPrimary(Path.Join(folder, "manifest.json"));
        foreach (var name in primary is null ? candidates : [primary, .. candidates])
        {
            foreach (var directory in new[] { folder, assets })
            {
                var path = Path.Join(directory, name);
                if (File.Exists(path))
                {
                    return path;
                }
            }
        }

        return Path.Join(folder, candidates[0]);
    }

    private static string? ManifestPrimary(string manifest)
    {
        if (!File.Exists(manifest))
        {
            return null;
        }

        var document = StrictJson.Parse(manifest, File.ReadAllText(manifest));
        var primary = document.PropertyNames().Contains("primary", StringComparer.Ordinal) ? document.Property("primary").AsString().Trim() : string.Empty;
        return primary.Length == 0 ? null : primary;
    }
}
