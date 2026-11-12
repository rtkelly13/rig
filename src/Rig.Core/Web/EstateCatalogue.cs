using System.Text.Json;
using System.Text.Json.Nodes;
using Rig.Core.Workspace;

namespace Rig.Core.Web;

/// <summary>One managed project as the estate cards show it (<c>GET /api/estate</c>).</summary>
/// <param name="Name">The clone's folder name.</param>
/// <param name="Repo">Its remote.</param>
/// <param name="Description">What it is.</param>
/// <param name="Domain">Its domain.</param>
/// <param name="Stack">Its tech stack.</param>
/// <param name="Enabled">False for a retired project.</param>
/// <param name="LogoUrl">Its logo under <c>/media/assets/</c>, or null.</param>
/// <param name="BannerUrl">Its banner under <c>/media/assets/</c>, or null.</param>
/// <param name="OrcaGroup">The Orca group it is shown in, or null.</param>
/// <param name="Color">Its Orca badge colour from <c>config/orca.json</c>, or null.</param>
/// <param name="Icon">Its Orca badge icon from <c>config/orca.json</c>, or null.</param>
public sealed record EstateProject(
    string Name,
    string Repo,
    string Description,
    string Domain,
    string Stack,
    bool Enabled,
    string? LogoUrl,
    string? BannerUrl,
    string? OrcaGroup,
    string? Color,
    string? Icon);

/// <summary>The estate catalogue: every managed and retired project with its brand images and Orca badge.</summary>
/// <param name="Projects">The projects in registry order, retired ones last.</param>
public sealed record EstateCatalogue(IReadOnlyList<EstateProject> Projects)
{
    /// <summary>
    /// Builds the catalogue from the registry at <paramref name="registryFile"/>. The Orca badges come from the
    /// <c>orca.json</c> beside it, else <c>config/orca.json</c> under <paramref name="workingDirectory"/>; a missing or
    /// malformed badge file means no badges.
    /// </summary>
    public static EstateCatalogue Load(string registryFile, string workingDirectory, string assetsRoot)
    {
        var badges = Badges(OrcaConfig(registryFile, workingDirectory));
        return new(
            [
                .. ManagedProjects.Load(registryFile).Select(spec =>
                {
                    var assets = BrandAssets.Find(assetsRoot, spec.Name);
                    var badge = badges.GetValueOrDefault(spec.Name);
                    return new EstateProject(
                        spec.Name,
                        spec.Repo,
                        spec.Description,
                        spec.Domain,
                        spec.Stack,
                        spec.Enabled,
                        assets.LogoUrl,
                        assets.BannerUrl,
                        spec.OrcaGroup,
                        badge.Color,
                        badge.Icon);
                }),
            ]);
    }

    private static string OrcaConfig(string registryFile, string workingDirectory)
    {
        var beside = Path.Join(Path.GetDirectoryName(registryFile), "orca.json");
        return File.Exists(beside) ? beside : Path.Join(workingDirectory, "config", "orca.json");
    }

    // Each repo's badge. As in Python, any entry or field of the wrong shape, or a file that will not read, gives no badges at all.
    private static Dictionary<string, (string? Color, string? Icon)> Badges(string path)
    {
        var found = new Dictionary<string, (string? Color, string? Icon)>(StringComparer.Ordinal);
        JsonNode? root;
        try
        {
            root = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return found;
        }

        if (root is not JsonObject config || config["repos"] is not JsonObject byName)
        {
            return found;
        }

        foreach (var (name, node) in byName)
        {
            if (node is not JsonObject badge || !Optional(badge, "color", out var color) || !Optional(badge, "icon", out var icon))
            {
                return new(StringComparer.Ordinal);
            }

            found[name] = (color, icon);
        }

        return found;
    }

    private static bool Optional(JsonObject badge, string key, out string? value)
    {
        value = null;
        if (!badge.TryGetPropertyValue(key, out var node) || node is null)
        {
            return true;
        }

        if (node.GetValueKind() != JsonValueKind.String)
        {
            return false;
        }

        value = node.GetValue<string>();
        return true;
    }
}
