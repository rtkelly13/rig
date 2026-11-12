using System.Net;
using System.Text.Json.Nodes;
using Rig.Web.Tests.Fakes;

namespace Rig.Web.Tests;

/// <summary>The estate graph routes: the snapshot, the rescan and the 503 the estate page shows as an error.</summary>
public sealed class ConnectivityRoutesTests : IAsyncLifetime
{
    private readonly EstateTree _tree = new();
    private readonly HttpClient _forwarding = new(new Upstream());
    private RigWebHost? _host;
    private HttpClient _client = null!;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _host = RigWebHost.Create(_tree.Settings(), _forwarding, () => null);
        await _host.StartAsync(Token);
        _client = new HttpClient { BaseAddress = _host.Address };
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        _forwarding.Dispose();
        if (_host is not null)
        {
            await _host.DisposeAsync();
        }

        _tree.Dispose();
    }

    [Theory]
    [InlineData("/api/estate/graph")]
    [InlineData("/api/estate/graph/refresh")]
    public async Task The_graph_lists_every_registry_and_retired_project_uncached(string path)
    {
        using var response = await _client.GetAsync(new Uri(path, UriKind.Relative), Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(Token))!;
        var projects = body["projects"]!.AsArray();
        Assert.Equal(["o/p", "o/f", "o/b", "o/r", "local/x", "o/old"], projects.Select(p => (string)p!["id"]!));
        Assert.All(projects, p => Assert.False((bool)p!["scanned"]!));
        Assert.Empty(body["dependencies"]!.AsArray());
        Assert.Equal(6, body["components"]!.AsArray().Count);
        Assert.NotNull((string?)body["scanned_at"]);
    }

    [Fact]
    public async Task A_malformed_registry_answers_503_with_the_error()
    {
        _tree.Write("config/managed-projects.json", """{"projects": [{"name": "a", "repo": "git@github.com:o/a.git", "depends_on": "b"}]}""");

        using var response = await _client.GetAsync(new Uri("/api/estate/graph/refresh", UriKind.Relative), Token);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(Token))!;
        Assert.EndsWith("projects[0].depends_on: expected a list", (string?)body["error"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Only_reads_are_answered_here()
    {
        using var response = await _client.PostAsync(new Uri("/api/estate/graph", UriKind.Relative), null, Token);

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }
}
