using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Rig.Core.Devenv;
using Rig.Core.Tests.Otel;

namespace Rig.Core.Tests.Devenv;

/// <summary>Answers the plugin registry, the releases API and asset downloads from memory; nothing reaches the network.</summary>
internal sealed class PluginServer : IDisposable
{
    private readonly StubHandler _handler;
    private readonly HttpClient _http;

    public PluginServer()
    {
        _handler = new StubHandler(request => Task.FromResult(Answer(request)));
        _http = new HttpClient(_handler, disposeHandler: false);
    }

    /// <summary>Bodies by URL; a URL not here answers 404.</summary>
    public Dictionary<string, string> Bodies { get; } = new(StringComparer.Ordinal);

    /// <summary>Every URL asked for, in order.</summary>
    public List<string> Asked { get; } = [];

    public ObsidianPlugins Plugins => new(_http);

    /// <summary>Lists <paramref name="ids"/> in the registry, each with a release <paramref name="tag"/> carrying all three assets.</summary>
    public PluginServer WithPlugins(string tag, params string[] ids)
    {
        Bodies[ObsidianPlugins.RegistryUrl] = new JsonArray([.. ids.Select(id => (JsonNode)new JsonObject { ["id"] = id, ["repo"] = $"owner/{id}", ["name"] = $"Plugin {id}" })]).ToJsonString();
        foreach (var id in ids)
        {
            var assets = new JsonArray();
            foreach (var asset in new[] { "manifest.json", "main.js", "styles.css" })
            {
                var url = $"https://github.com/owner/{id}/releases/download/{tag}/{asset}";
                assets.Add(new JsonObject { ["name"] = asset, ["browser_download_url"] = url });
                Bodies[url] = asset == "manifest.json" ? $"{{\"version\": \"{tag}\"}}" : $"// {id} {asset}";
            }

            Bodies[Release(id)] = new JsonObject { ["tag_name"] = tag, ["assets"] = assets }.ToJsonString();
        }

        return this;
    }

    public static string Release(string id) => $"https://api.github.com/repos/owner/{id}/releases/latest";

    public void Dispose()
    {
        _http.Dispose();
        _handler.Dispose();
    }

    private HttpResponseMessage Answer(HttpRequestMessage request)
    {
        var url = request.RequestUri!.ToString();
        Asked.Add(url);
        Assert.Equal("devenv-obsidian-plugins", request.Headers.UserAgent.ToString());
        return Bodies.TryGetValue(url, out var body)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8) }
            : new HttpResponseMessage(HttpStatusCode.NotFound);
    }
}
