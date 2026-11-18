using System.Net;
using System.Text;
using System.Text.Json;
using Rig.Image;
using Rig.Web.Tests.Fakes;

namespace Rig.Web.Tests;

/// <summary>The Asset Studio's API on the web host: catalogs, drift diffs, snapshot capture and the variation media.</summary>
public sealed class StudioRoutesTests : IAsyncLifetime
{
    private readonly EstateTree _tree = new();
    private RigWebHost? _host;
    private HttpClient _client = null!;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(Path.Join(_tree.Assets, "demo", "reference"));
        await File.WriteAllBytesAsync(Path.Join(_tree.Assets, "demo", "reference", "01-ref.png"), RasterPipeline.EncodePng(Solid(40)), TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(Path.Join(_tree.Assets, "demo", "reference", "02-cand.png"), RasterPipeline.EncodePng(Solid(90)), TestContext.Current.CancellationToken);
        _host = await RigWebHost.CreateAsync(_tree.Settings());
        await _host.StartAsync(TestContext.Current.CancellationToken);
        _client = new HttpClient { BaseAddress = _host.Address };
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        if (_host is not null)
        {
            await _host.DisposeAsync();
        }

        _tree.Dispose();
    }

    [Fact]
    public async Task Projects_lists_the_catalog_with_media_urls()
    {
        using var response = await _client.GetAsync(new Uri("/api/projects", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var demo = json.RootElement.GetProperty("projects").EnumerateArray().Single(project => project.GetProperty("name").GetString() == "demo");
        Assert.Equal("/media/assets/demo/reference/01-ref.png", demo.GetProperty("references")[0].GetProperty("url").GetString());
    }

    [Theory]
    [InlineData("/media/assets/demo/reference/01-ref.png", "/media/assets/demo/reference/02-cand.png", HttpStatusCode.OK)]
    [InlineData("/media/assets/demo/reference/01-ref.png", "/media/assets/demo/reference/absent.png", HttpStatusCode.NotFound)]
    [InlineData("/media/assets/demo/reference/01-ref.png", "/media/assets/../../outside.txt", HttpStatusCode.NotFound)]
    public async Task Diff_resolves_media_urls_under_the_asset_root_only(string reference, string candidate, HttpStatusCode expected)
    {
        using var response = await _client.GetAsync(new Uri($"/api/diff?ref={Uri.EscapeDataString(reference)}&cand={Uri.EscapeDataString(candidate)}", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task Diff_without_both_images_is_a_bad_request()
    {
        using var response = await _client.GetAsync(new Uri("/api/diff?ref=x", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_snapshot_is_captured_under_the_variations_folder_and_served_back_uncached()
    {
        using var content = new StringContent("""{"project": "demo", "run_id": "run-1", "step_name": "first", "svg_content": "<svg viewBox='0 0 1 1'/>"}""", Encoding.UTF8, "application/json");

        using var posted = await _client.PostAsync(new Uri("/api/snapshot", UriKind.Relative), content, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, posted.StatusCode);
        var svg = Directory.GetFiles(Path.Join(_tree.Root, "temp", "variations", "demo", "run-1"), "*.svg").Single();
        using var served = await _client.GetAsync(new Uri("/media/variations/demo/run-1/" + Path.GetFileName(svg), UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, served.StatusCode);
        Assert.Equal("image/svg+xml", served.Content.Headers.ContentType?.ToString());
        using var missing = await _client.GetAsync(new Uri("/media/variations/demo/run-1/absent.svg", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task A_snapshot_without_a_run_is_a_bad_request()
    {
        using var content = new StringContent("""{"project": "demo"}""", Encoding.UTF8, "application/json");

        using var response = await _client.PostAsync(new Uri("/api/snapshot", UriKind.Relative), content, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public void The_variations_root_defaults_beside_the_working_directory_and_can_be_set()
    {
        var settings = _tree.Settings();

        Assert.Equal(Path.Join(settings.WorkingDirectory, "temp", "variations"), settings.EffectiveVariationsRoot);
        Assert.Equal("/elsewhere", (settings with { VariationsRoot = "/elsewhere" }).EffectiveVariationsRoot);
    }

    private static Raster Solid(byte value)
    {
        var pixels = new byte[8 * 8 * 4];
        Array.Fill(pixels, value);
        return new Raster(8, 8, pixels);
    }
}
