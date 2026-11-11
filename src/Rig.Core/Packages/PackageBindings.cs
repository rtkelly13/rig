using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

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

            var url = manifest?["repository"] switch
            {
                JsonObject repository => Text(repository["url"]),
                var other => Text(other),
            };
            return new(url, Text(manifest?["version"]));
        }

        var found = RepositoryUrlPattern().Match(text);
        var version = VersionPattern().Match(text);
        return new(found.Success ? found.Groups[1].Value : null, version.Success ? version.Groups[1].Value : null);
    }

    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    [GeneratedRegex(@"<RepositoryUrl>\s*([^<]+?)\s*</RepositoryUrl>", RegexOptions.CultureInvariant)]
    private static partial Regex RepositoryUrlPattern();

    [GeneratedRegex(@"<(?:Version|VersionPrefix)>\s*([^<$]+?)\s*</(?:Version|VersionPrefix)>", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();
}

/// <summary>
/// The packages declared in <c>config/managed-projects.json</c>: every enabled project's <c>packages</c> entries
/// (ADR 0020: the CLI validates the file, it does not edit it).
/// </summary>
public static class PackageBindings
{
    /// <summary>Every enabled project's packages in <paramref name="path"/>.</summary>
    /// <exception cref="InvalidDataException">The file, a project or a package entry is malformed.</exception>
    public static IReadOnlyList<PackageBinding> Load(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(File.ReadAllText(path));
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"{path}: {e.Message}", e);
        }

        if (root is not JsonObject document)
        {
            throw new InvalidDataException($"{path}: expected an object");
        }

        var bindings = new List<PackageBinding>();
        foreach (var (project, index) in Items(document["projects"], $"{path}: projects").Select((p, i) => (p, i)))
        {
            var where = $"{path}: projects[{index}]";
            var entry = project as JsonObject ?? throw new InvalidDataException($"{where}: expected an object");
            var name = Required(entry, "name", where);
            var repo = Required(entry, "repo", where);
            var enabled = entry["enabled"] switch
            {
                null => true,
                JsonValue value when value.TryGetValue<bool>(out var flag) => flag,
                _ => throw new InvalidDataException($"{where}.enabled: expected true or false"),
            };
            var specs = Items(entry["packages"], $"{where}.packages").Select((spec, i) => Spec(spec, $"{where}.packages[{i}]")).ToList();
            if (enabled)
            {
                bindings.AddRange(specs.Select(spec => new PackageBinding(name, repo, spec)));
            }
        }

        return bindings;
    }

    private static PackageSpec Spec(JsonNode? node, string where)
    {
        var entry = node as JsonObject ?? throw new InvalidDataException($"{where}: expected an object");
        var registry = Required(entry, "registry", where);
        if (registry is not (PackageSpec.Npm or PackageSpec.NuGet))
        {
            throw new InvalidDataException($"{where}.registry: expected 'npm' or 'nuget', not '{registry}'");
        }

        var auth = Optional(entry, "auth", where) ?? PackageSpec.Oidc;
        if (auth is not (PackageSpec.Oidc or PackageSpec.Token))
        {
            throw new InvalidDataException($"{where}.auth: expected 'oidc' or 'token', not '{auth}'");
        }

        return new(registry, Required(entry, "name", where), Required(entry, "manifest", where), Required(entry, "workflow", where), auth, Optional(entry, "environment", where))
        {
            NuGetOwner = Optional(entry, "nuget_owner", where),
            NuGetUser = Optional(entry, "nuget_user", where),
            Owners = Strings(entry["owners"], $"{where}.owners"),
            Scopes = Strings(entry["scopes"], $"{where}.scopes"),
        };
    }

    private static JsonArray Items(JsonNode? node, string where) => node switch
    {
        null => [],
        JsonArray items => items,
        _ => throw new InvalidDataException($"{where}: expected a list"),
    };

    private static string[] Strings(JsonNode? node, string where) =>
        [.. Items(node, where).Select(item => item is JsonValue value && value.TryGetValue<string>(out var text) ? text : throw new InvalidDataException($"{where}: expected strings"))];

    private static string Required(JsonObject entry, string field, string where) =>
        Optional(entry, field, where) ?? throw new InvalidDataException($"{where}.{field}: required");

    private static string? Optional(JsonObject entry, string field, string where) => entry[field] switch
    {
        null => null,
        JsonValue value when value.TryGetValue<string>(out var text) => text,
        _ => throw new InvalidDataException($"{where}.{field}: expected a string"),
    };
}
