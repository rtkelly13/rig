using System.Net;
using System.Text.Json.Nodes;
using Rig.Web.Tests.Fakes;

namespace Rig.Web.Tests;

/// <summary>The host's own endpoints, answered as the Python <c>StudioServer</c> answered them.</summary>
public sealed class HostRoutesTests : IAsyncLifetime
{
    private readonly EstateTree _tree = new();
    private readonly HttpClient _forwarding = new(new Upstream());
    private RigWebHost? _host;
    private HttpClient _client = null!;

    public async ValueTask InitializeAsync()
    {
        _host = RigWebHost.Create(_tree.Settings(), _forwarding, () => null);
        await _host.StartAsync(TestContext.Current.CancellationToken);
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

    [Fact]
    public async Task Estate_matches_the_recorded_python_answer()
    {
        using var response = await _client.GetAsync(new Uri("/api/estate", UriKind.Relative), TestContext.Current.CancellationToken);

        var recorded = JsonNode.Parse(await File.ReadAllTextAsync(Path.Join(AppContext.BaseDirectory, "Recorded", "estate.json"), TestContext.Current.CancellationToken));
        var actual = JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(JsonNode.DeepEquals(recorded, actual), actual?.ToJsonString());
        AssertOpenJson(response);
    }

    [Fact]
    public async Task Status_names_the_environment_port_and_database()
    {
        using var response = await _client.GetAsync(new Uri("/api/status", UriKind.Relative), TestContext.Current.CancellationToken);

        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;
        Assert.Equal("PREVIEW", (string?)body["environment"]);
        Assert.Equal(_host!.Address.Port, (int?)body["port"]);
        Assert.True((bool?)body["is_preview"]);
        Assert.Equal(Path.Join(_tree.Root, "rig.db"), (string?)body["db_path"]);
        AssertOpenJson(response);
    }

    [Fact]
    public async Task Mtime_is_zero_without_frontend_files()
    {
        using var response = await _client.GetAsync(new Uri("/api/dev/mtime", UriKind.Relative), TestContext.Current.CancellationToken);

        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;
        Assert.Equal(0.0, (double?)body["mtime"]);
        Assert.Equal(0.0, (double?)body["css_mtime"]);
        Assert.True((bool?)body["is_preview"]);
        AssertOpenJson(response);
    }

    [Fact]
    public async Task Mtime_reports_the_newest_watched_file_and_the_newest_stylesheet()
    {
        _tree.Write("templates/index.html", "<html>");
        _tree.Write("templates/app_assets/app.css", "body{}");
        var html = new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero);
        var css = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        File.SetLastWriteTimeUtc(Path.Join(_tree.Templates, "index.html"), html.UtcDateTime);
        File.SetLastWriteTimeUtc(Path.Join(_tree.Templates, "app_assets", "app.css"), css.UtcDateTime);

        using var response = await _client.GetAsync(new Uri("/api/dev/mtime", UriKind.Relative), TestContext.Current.CancellationToken);

        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;
        Assert.Equal(html.ToUnixTimeSeconds(), (double?)body["mtime"]);
        Assert.Equal(css.ToUnixTimeSeconds(), (double?)body["css_mtime"]);
    }

    [Fact]
    public async Task A_path_nobody_owns_is_a_404_envelope_while_the_python_routes_are_down()
    {
        using var response = await _client.GetAsync(new Uri("/api/nothing", UriKind.Relative), TestContext.Current.CancellationToken);

        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("Endpoint not found: /api/nothing", (string?)body["error"]);
    }

    private static void AssertOpenJson(HttpResponseMessage response)
    {
        Assert.Equal("application/json; charset=utf-8", response.Content.Headers.ContentType?.ToString());
        Assert.Equal("*", Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
        Assert.Null(response.Headers.CacheControl);
    }
}
