using System.Diagnostics.CodeAnalysis;

[assembly: SuppressMessage("Design", "CA1054", Justification = "Asset URLs are site-relative paths served by the web host; System.Uri cannot hold them unrooted.", Scope = "namespaceanddescendants", Target = "~N:Rig.Core.Web")]
[assembly: SuppressMessage("Design", "CA1056", Justification = "Asset URLs are site-relative paths served by the web host; System.Uri cannot hold them unrooted.", Scope = "namespaceanddescendants", Target = "~N:Rig.Core.Web")]

namespace Rig.Core.Web;

/// <summary>A project's logo and banner as <c>/media/assets/…</c> URLs, each null when it has none.</summary>
/// <param name="LogoUrl">The best logo candidate.</param>
/// <param name="BannerUrl">The first banner or social-card image.</param>
public sealed record ProjectBrandAssets(string? LogoUrl, string? BannerUrl);

/// <summary>Finds a managed project's brand images under the assets folder, for the estate cards.</summary>
public static class BrandAssets
{
    /// <summary>The URL prefix the web host serves the assets folder under.</summary>
    public const string UrlPrefix = "/media/assets/";

    private static readonly string[] SocialCard = ["og-", "og_", "-og.", "_og."];

    /// <summary>
    /// The project's folder under <paramref name="assetsRoot"/>: one named exactly, else one whose name matches with case,
    /// dots, dashes and underscores ignored (<c>Parquet.SourceGenerator</c> finds <c>parquet-source-generator</c>).
    /// </summary>
    public static string ProjectDir(string assetsRoot, string projectName)
    {
        ArgumentNullException.ThrowIfNull(projectName);
        var direct = Path.Join(assetsRoot, projectName);
        if (Directory.Exists(direct) || !Directory.Exists(assetsRoot))
        {
            return direct;
        }

        var target = Clean(projectName);
        return Directory.EnumerateDirectories(assetsRoot).FirstOrDefault(d => Clean(Path.GetFileName(d)) == target) ?? direct;
    }

    /// <summary>
    /// The project's logo and banner, from its folder and its <c>production-cuts/</c>. A logo is ranked: a file named
    /// <c>logo</c>, then a 1000-pixel cut, then an SVG that is not a favicon, then an icon or avatar, then any SVG.
    /// </summary>
    public static ProjectBrandAssets Find(string assetsRoot, string projectName)
    {
        var dir = ProjectDir(assetsRoot, projectName);
        if (!Directory.Exists(dir))
        {
            return new(null, null);
        }

        List<string> folders = [dir];
        var cuts = Path.Join(dir, "production-cuts");
        if (Directory.Exists(cuts))
        {
            folders.Add(cuts);
        }

        string? banner = null;
        var candidates = new List<(int Rank, string Url)>();
        foreach (var folder in folders)
        {
            foreach (var file in Directory.EnumerateFiles(folder).Order(StringComparer.Ordinal))
            {
                var name = Path.GetFileName(file);
                if (name.StartsWith('.'))
                {
                    continue;
                }

                var lower = name.ToLowerInvariant();
                // Each segment percent-encoded, so a space, # or ? in a name stays part of the path; the media route decodes it.
                var url = UrlPrefix + string.Join('/', Path.GetRelativePath(assetsRoot, file).Split('/', '\\').Select(Uri.EscapeDataString));
                var isLogo = lower.Contains("logo", StringComparison.Ordinal);
                var isBanner = lower.Contains("banner", StringComparison.Ordinal)
                    || SocialCard.Any(p => lower.Contains(p, StringComparison.Ordinal));
                if (banner is null && !isLogo && isBanner)
                {
                    banner = url;
                }

                if (Rank(lower, isLogo) is { } rank)
                {
                    candidates.Add((rank, url));
                }
            }
        }

        // OrderBy is stable, as Python's sort is: equal ranks keep their file order.
        return new(candidates.OrderBy(c => c.Rank).Select(c => c.Url).FirstOrDefault(), banner);
    }

    private static int? Rank(string lower, bool isLogo)
    {
        if (isLogo)
        {
            return 0;
        }

        if (lower.Contains("1000x1000", StringComparison.Ordinal) || lower.Contains("1000px", StringComparison.Ordinal))
        {
            return 1;
        }

        var svg = lower.EndsWith(".svg", StringComparison.Ordinal);
        if (svg && !lower.Contains("favicon", StringComparison.Ordinal))
        {
            return 2;
        }

        if (lower.Contains("icon", StringComparison.Ordinal) || lower.Contains("avatar", StringComparison.Ordinal))
        {
            return 3;
        }

        return svg ? 4 : null;
    }

    private static string Clean(string name) =>
        name.ToLowerInvariant().Replace(".", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal);
}
