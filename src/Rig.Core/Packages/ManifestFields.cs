using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Rig.Core.Config;
using Rig.Core.Text;

namespace Rig.Core.Packages;

/// <summary>A manifest's repository URL and version, either of which may be absent.</summary>
/// <param name="RepositoryUrl">The repository it names.</param>
/// <param name="Version">The version it declares.</param>
public sealed partial record ManifestFields(string? RepositoryUrl, string? Version)
{
    /// <summary>
    /// The fields of a <paramref name="registry"/> manifest: <c>package.json</c>'s <c>repository</c> (a string or an
    /// object's <c>url</c>) and <c>version</c> for npm, else MSBuild's <c>RepositoryUrl</c> and <c>Version</c> or
    /// <c>VersionPrefix</c>.
    /// </summary>
    public static ManifestFields Read(string registry, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (registry == PackageSpec.Npm)
        {
            JsonObject? manifest;
            try
            {
                manifest = JsonNode.Parse(text) as JsonObject;
            }
            catch (JsonException)
            {
                manifest = null;
            }

            var repository = JsonShape.Field(manifest, "repository");
            var url = JsonShape.AsObject(repository) is { } declaration
                ? JsonShape.FieldText(declaration, "url")
                : JsonShape.AsText(repository);
            return new(url, JsonShape.FieldText(manifest, "version"));
        }

        var found = RepositoryUrlPattern().Match(text);
        var version = VersionPattern().Match(text);
        return new(found.Success ? found.Groups[1].Value : null, version.Success ? version.Groups[1].Value : null);
    }

    [GeneratedRegex(@"<RepositoryUrl>\s*([^<]+?)\s*</RepositoryUrl>", RegexOptions.CultureInvariant)]
    private static partial Regex RepositoryUrlPattern();

    [GeneratedRegex(@"<(?:Version|VersionPrefix)>\s*([^<$]+?)\s*</(?:Version|VersionPrefix)>", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();
}
