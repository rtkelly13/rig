using System.Text.Json.Nodes;

namespace Rig.Web.BrowserTests;

/// <summary>The estates the host's graph is read from: each project's manifests, path to text.</summary>
internal static class Estates
{
    /// <summary>owner/repo publishes managed-library, which owner/other uses: one explained connection.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> SharedLibrary { get; } =
        new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            ["repo"] = Package("managed-library"),
            ["other"] = Package(null, ("managed-library", "^1.0")),
        };

    /// <summary>
    /// a feeds three consumers, one of them two layers on: b and d stand in the layer between, so the direct line from
    /// a to c has to find the gap between them.
    /// </summary>
    public static IReadOnlyList<(string From, string To)> LinePairs { get; } =
        [("a", "b"), ("a", "d"), ("a", "c"), ("b", "c"), ("d", "c"), ("d", "e")];

    /// <summary>Five packages wired by <see cref="LinePairs"/>.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Lines { get; } =
        "abcde".ToDictionary(
            node => node.ToString(),
            node => (IReadOnlyDictionary<string, string>)Package(
                LinePairs.Any(pair => pair.From == node.ToString()) ? $"pkg-{node}" : null,
                [.. LinePairs.Where(pair => pair.To == node.ToString()).Select(pair => ($"pkg-{pair.From}", "^1.0"))]));

    private static Dictionary<string, string> Package(string? name, params (string Name, string Version)[] dependencies)
    {
        var manifest = new JsonObject { ["dependencies"] = new JsonObject(dependencies.Select(d => KeyValuePair.Create(d.Name, (JsonNode?)d.Version))) };
        if (name is not null)
        {
            manifest["name"] = name;
        }

        return new Dictionary<string, string> { ["package.json"] = manifest.ToJsonString() };
    }
}
