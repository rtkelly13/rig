using System.Text.Json;
using System.Text.Json.Nodes;
using Rig.Core.Connectivity;
using Rig.Core.Processes;

namespace Rig.Core.Tests.Connectivity;

/// <summary>
/// A throwaway estate: real git checkouts under <c>personal/</c>, each manifest staged so <c>git ls-files</c> lists it,
/// and a registry at <c>rig/config/managed-projects.json</c>, as the Python tests built it.
/// </summary>
internal sealed class EstateFixture : IDisposable
{
    private readonly TempDir _dir = new();

    public EstateFixture() => Git = new Git(new SystemProc(), _dir.Path);

    public Git Git { get; }

    public string Personal => Path.Join(_dir.Path, "personal");

    public string Registry => Path.Join(_dir.Path, "rig", "config", "managed-projects.json");

    public string Snapshot => EstateGraph.SnapshotPath(Registry);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>Checks out <paramref name="name"/> with <paramref name="files"/> (paths relative to it), staged in a fresh repo.</summary>
    public async Task<string> CheckoutAsync(string name, IReadOnlyDictionary<string, string> files)
    {
        var path = Path.Join(Personal, name);
        Directory.CreateDirectory(path);
        foreach (var (relative, text) in files)
        {
            var target = Path.Join(path, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await File.WriteAllTextAsync(target, text, Token);
        }

        Assert.True((await Git.RunAsync(path, ["init", "--quiet"], cancellationToken: Token)).Ok);
        Assert.True((await Git.RunAsync(path, ["add", "-f", "."], cancellationToken: Token)).Ok);
        return path;
    }

    /// <summary>Writes the registry's <c>projects</c>.</summary>
    public void Projects(JsonArray projects)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Registry)!);
        File.WriteAllText(Registry, new JsonObject { ["projects"] = projects }.ToJsonString());
    }

    /// <summary>A registry of plain projects <c>https://github.com/owner/{name}</c>.</summary>
    public void Owned(params string[] names) =>
        Projects([.. names.Select(name => (JsonNode)new JsonObject { ["name"] = name, ["repo"] = $"https://github.com/owner/{name}" })]);

    /// <summary>Lays out <c>graph-fixture.json</c>-shaped input: <c>projects</c> and per-checkout file maps.</summary>
    public async Task LayOutAsync(JsonObject fixture)
    {
        foreach (var (name, files) in fixture["checkouts"]!.AsObject())
        {
            await CheckoutAsync(name, files!.AsObject().ToDictionary(pair => pair.Key, pair => pair.Value!.GetValue<string>()));
        }

        Projects(fixture["projects"]!.DeepClone().AsArray());
    }

    public EstateGraph Graph(TimeProvider? time = null) => new(Git, time ?? TimeProvider.System);

    public Task<ConnectivityGraph> BuildAsync() => Graph().BuildAsync(Registry, Personal, Token);

    public static JsonNode Json(ConnectivityGraph graph) => JsonNode.Parse(JsonSerializer.Serialize(graph, EstateGraph.JsonOptions))!;

    public void Dispose() => _dir.Dispose();
}
