using System.Net;
using System.Text;
using Rig.Web.Tests.Fakes;

namespace Rig.Web.Tests;

/// <summary>The app's document, its static files and the brand images, answered as the Python <c>StudioServer</c> answered them.</summary>
public sealed class FrontendRoutesTests : IAsyncLifetime
{
    private const string Document = "<!DOCTYPE html><html><body id=\"rig-app\">[ RIG ]</body></html>";

    private readonly EstateTree _tree = new();
    private readonly Upstream _upstream = new() { Answer = _ => new(HttpStatusCode.NotFound) { Content = new StringContent("from python") } };
    private readonly SocketsHttpHandler _noRedirects = new() { AllowAutoRedirect = false };
    private readonly HttpClient _forwarding;
    private RigWebHost? _host;
    private HttpClient _client = null!;

    public FrontendRoutesTests() => _forwarding = new(_upstream);

    public async ValueTask InitializeAsync()
    {
        _tree.Write("templates/index.html", Document);
        _tree.Write("templates/studio.css", "body { background: #0a0a1a; }");
        _tree.Write("templates/app_assets/app.js", "console.log('ready');");
        _tree.Write("templates/triage.html", "the old page");
        _tree.Write("templates/data.bin", "bytes");
        _tree.Write("assets/blog/with space.svg", "<svg viewBox='0 0 1 1'></svg>");
        _tree.Write("outside.txt", "secret");
        _host = RigWebHost.Create(_tree.Settings(), _forwarding, () => Upstream.Address);
        await _host.StartAsync(TestContext.Current.CancellationToken);
        _client = new HttpClient(_noRedirects, disposeHandler: false) { BaseAddress = _host.Address };
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        _noRedirects.Dispose();
        _forwarding.Dispose();
        _upstream.Dispose();
        if (_host is not null)
        {
            await _host.DisposeAsync();
        }

        _tree.Dispose();
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/index.html")]
    [InlineData("/estate")]
    [InlineData("/studio")]
    [InlineData("/optical")]
    [InlineData("/triage")]
    [InlineData("/ops")]
    [InlineData("/triage/inbox/owner/repo/1")]
    [InlineData("/triage/references")]
    [InlineData("/repos/owner/repo/triage/inbox/1?layout=focus")]
    [InlineData("/estate/graph")]
    [InlineData("/ops/runs")]
    public async Task Every_app_path_is_the_one_document(string path)
    {
        using var response = await GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html; charset=utf-8", response.Content.Headers.ContentType?.ToString());
        Assert.Equal("no-store", response.Headers.NonValidated["Cache-Control"].ToString());
        Assert.Equal(Document, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Empty(_upstream.Seen);
    }

    [Fact]
    public async Task A_missing_document_is_not_found()
    {
        File.Delete(Path.Join(_tree.Templates, "index.html"));

        using var response = await GetAsync("/estate");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("""{"error":"Application document not found"}""", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task The_old_triage_page_redirects_to_the_inbox()
    {
        using var response = await GetAsync("/static/triage.html");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/triage/inbox", response.Headers.Location?.ToString());
        Assert.Equal(0, response.Content.Headers.ContentLength);
    }

    [Theory]
    [InlineData("/static/studio.css", "text/css", "#0a0a1a")]
    [InlineData("/static/app_assets/app.js", "text/javascript", "console.log")]
    [InlineData("/static/data.bin", "application/octet-stream", "bytes")]
    [InlineData("/media/assets/blog/with%20space.svg", "image/svg+xml", "<svg")]
    public async Task Files_come_from_their_folder_uncached(string path, string type, string content)
    {
        using var response = await GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(type, response.Content.Headers.ContentType?.ToString());
        Assert.Equal("no-cache, no-store, must-revalidate", response.Headers.NonValidated["Cache-Control"].ToString());
        Assert.Contains(content, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/static/nope.css", "Static file not found: nope.css")]
    [InlineData("/media/assets/nope.png", "Asset not found: nope.png")]
    [InlineData("/media/assets/..%2foutside.txt", "Asset not found: ../outside.txt")]
    [InlineData("/media/assets/blog%2f..%2f..%2foutside.txt", "Asset not found: blog/../../outside.txt")]
    [InlineData("/media/assets/%2e%2e%5coutside.txt", @"Asset not found: ..\outside.txt")]
    [InlineData("/static/..%2foutside.txt", "Static file not found: ..%2foutside.txt")]
    public async Task A_file_that_is_missing_or_outside_its_folder_is_not_found(string path, string error)
    {
        var answer = await RawGetAsync(path);

        Assert.StartsWith("HTTP/1.1 404", answer, StringComparison.Ordinal);
        Assert.EndsWith($$"""{"error":{{System.Text.Json.JsonSerializer.Serialize(error)}}}""", answer, StringComparison.Ordinal);
        Assert.Empty(_upstream.Seen);
    }

    [Theory]
    [InlineData("/static/../outside.txt")]
    [InlineData("/media/assets/../outside.txt")]
    [InlineData("/static/%2e%2e/outside.txt")]
    [InlineData("/media/assets-evil/outside.txt")]
    public async Task Dot_segments_leave_the_folder_before_routing_so_the_python_routes_answer(string path)
    {
        var answer = await RawGetAsync(path);

        Assert.StartsWith("HTTP/1.1 404", answer, StringComparison.Ordinal);
        Assert.Contains("\r\nfrom python\r\n", answer, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", answer, StringComparison.Ordinal);
    }

    [Fact]
    public void A_link_out_of_the_folder_is_refused()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "File symlinks need Developer Mode on Windows.");
        File.CreateSymbolicLink(Path.Join(_tree.Templates, "leak.txt"), Path.Join(_tree.Root, "outside.txt"));
        File.CreateSymbolicLink(Path.Join(_tree.Templates, "alias.css"), Path.Join(_tree.Templates, "studio.css"));

        Assert.Null(FrontendRoutes.Under(_tree.Templates, "leak.txt"));
        Assert.Equal(Path.Join(_tree.Templates, "alias.css"), FrontendRoutes.Under(_tree.Templates, "alias.css"));
    }

    [Fact]
    public void A_directory_link_out_of_the_folder_is_refused()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Symlinks need Developer Mode on Windows.");
        _tree.Write("elsewhere/secret.txt", "secret");
        Directory.CreateSymbolicLink(Path.Join(_tree.Templates, "linked"), Path.Join(_tree.Root, "elsewhere"));
        Directory.CreateSymbolicLink(Path.Join(_tree.Templates, "inner"), Path.Join(_tree.Templates, "app_assets"));

        Assert.Null(FrontendRoutes.Under(_tree.Templates, Path.Join("linked", "secret.txt")));
        Assert.Equal(Path.Join(_tree.Templates, "inner", "app.js"), FrontendRoutes.Under(_tree.Templates, Path.Join("inner", "app.js")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("app_assets")]
    [InlineData("a\0b")]
    public void A_folder_or_an_impossible_name_is_no_file(string relative) =>
        Assert.Null(FrontendRoutes.Under(_tree.Templates, relative));

    [Fact]
    public void A_rooted_name_is_no_file() =>
        Assert.Null(FrontendRoutes.Under(_tree.Templates, Path.Join(_tree.Root, "outside.txt")));

    private Task<HttpResponseMessage> GetAsync(string path) =>
        _client.GetAsync(new Uri(path, UriKind.Relative), TestContext.Current.CancellationToken);

    // HttpClient normalises the request target, so a hostile one goes over a bare socket, written as a client could.
    private async Task<string> RawGetAsync(string target)
    {
        var address = _host!.Address;
        using var socket = new System.Net.Sockets.TcpClient();
        await socket.ConnectAsync(address.Host, address.Port, TestContext.Current.CancellationToken);
        var stream = socket.GetStream();
        var request = $"GET {target} HTTP/1.1\r\nHost: {address.Authority}\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request), TestContext.Current.CancellationToken);
        using var reader = new StreamReader(stream, Encoding.ASCII);
        return await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
    }
}
