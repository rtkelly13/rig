using System.Text.Json.Nodes;
using Rig.Core.Text;

namespace Rig.Core.Config;

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
        var root = JsonFile.Parse(path);

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
            var enabled = JsonShape.Field(entry, "enabled") is { } flag
                ? JsonShape.AsBool(flag) ?? throw new InvalidDataException($"{where}.enabled: expected true or false")
                : true;
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

    private static JsonArray Items(JsonNode? node, string where) =>
        node is null ? [] : JsonShape.AsArray(node) ?? throw new InvalidDataException($"{where}: expected a list");

    private static string[] Strings(JsonNode? node, string where) =>
        [.. Items(node, where).Select(item => JsonShape.AsText(item) ?? throw new InvalidDataException($"{where}: expected strings"))];

    private static string Required(JsonObject entry, string field, string where) =>
        Optional(entry, field, where) ?? throw new InvalidDataException($"{where}.{field}: required");

    private static string? Optional(JsonObject entry, string field, string where) =>
        JsonShape.Field(entry, field) is { } node
            ? JsonShape.AsText(node) ?? throw new InvalidDataException($"{where}.{field}: expected a string")
            : null;
}
