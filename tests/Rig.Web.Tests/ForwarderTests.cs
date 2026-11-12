using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Rig.Web.Tests.Fakes;

namespace Rig.Web.Tests;

/// <summary>Paths the host does not own go to the Python routes as the browser sent them, and come back as they left.</summary>
public sealed class ForwarderTests : IAsyncLifetime
{
    private readonly EstateTree _tree = new();
    private readonly Upstream _upstream = new();
    private readonly HttpClient _forwarding;
    private readonly SocketsHttpHandler _noRedirects = new() { AllowAutoRedirect = false };
    private RigWebHost? _host;
    private HttpClient _client = null!;

    public ForwarderTests() => _forwarding = new HttpClient(_upstream);

    public async ValueTask InitializeAsync()
    {
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

    [Fact]
    public async Task A_get_keeps_its_raw_target_host_and_origin()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/api/triage?x=1&x=%2F", UriKind.Relative));
        request.Headers.Add("Origin", "http://example.test");

        using var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);

        var (sent, body) = Assert.Single(_upstream.Seen);
        Assert.Equal("upstream", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("/api/triage?x=1&x=%2F", sent.RequestUri!.PathAndQuery);
        Assert.Equal($"127.0.0.1:{_host!.Address.Port}", sent.Headers.Host);
        Assert.Equal("http://example.test", Assert.Single(sent.Headers.GetValues("Origin")));
        Assert.Null(body);
    }

    [Fact]
    public async Task A_post_carries_its_body_and_content_type()
    {
        using var content = new StringContent("""{"paths":[]}""", Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        using var response = await _client.PostAsync(new Uri("/api/ops/estate/trim/apply", UriKind.Relative), content, TestContext.Current.CancellationToken);

        var (sent, body) = Assert.Single(_upstream.Seen);
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Equal("""{"paths":[]}""", body);
        Assert.Equal("application/json", sent.Content!.Headers.ContentType?.ToString());
        Assert.Equal(12, sent.Content.Headers.ContentLength);
    }

    [Fact]
    public async Task A_redirect_comes_back_unfollowed_with_its_headers()
    {
        _upstream.Answer = _ =>
        {
            var answer = new HttpResponseMessage(HttpStatusCode.Found) { Content = new ByteArrayContent([]) };
            answer.Headers.Location = new Uri("/triage/inbox", UriKind.Relative);
            answer.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
            return answer;
        };

        using var response = await _client.GetAsync(new Uri("/static/triage.html", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal("/triage/inbox", response.Headers.Location?.OriginalString);
        Assert.True(response.Headers.CacheControl?.NoStore);
    }

    [Fact]
    public async Task An_error_status_and_body_pass_through()
    {
        _upstream.Answer = _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent("""{"error": "down"}""", Encoding.UTF8, "application/json"),
        };

        using var response = await _client.GetAsync(new Uri("/api/ops/decisions", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("down", (string?)JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!["error"]);
    }

    [Fact]
    public async Task Unreachable_python_routes_are_a_502()
    {
        _upstream.Answer = _ => throw new HttpRequestException("connection refused");

        using var response = await _client.GetAsync(new Uri("/api/triage", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal(
            "The Python web routes did not answer: connection refused",
            (string?)JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!["error"]);
    }

    [Theory]
    [InlineData("//example.com/x?y=1", "/example.com/x?y=1")]
    [InlineData("http://example.com/x?y=1", "/x?y=1")]
    public async Task A_target_naming_another_host_still_goes_to_the_python_routes(string target, string path)
    {
        var answer = await RawGetAsync(target, target.StartsWith('/') ? _host!.Address.Authority : "example.com");

        Assert.StartsWith("HTTP/1.1 200", answer, StringComparison.Ordinal);
        var (sent, _) = Assert.Single(_upstream.Seen);
        Assert.Equal(Upstream.Address.Authority, sent.RequestUri!.Authority);
        Assert.Equal(path, sent.RequestUri.PathAndQuery);
    }

    [Fact]
    public async Task Headers_a_connection_header_names_go_no_further_either_way()
    {
        _upstream.Answer = _ =>
        {
            var answer = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("upstream") };
            answer.Headers.Connection.Add("X-Upstream-Hop");
            answer.Headers.Add("X-Upstream-Hop", "1");
            answer.Headers.Add("X-Kept", "1");
            return answer;
        };
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/api/triage", UriKind.Relative));
        request.Headers.Connection.Add("X-Browser-Hop");
        request.Headers.Add("X-Browser-Hop", "1");

        using var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);

        var (sent, _) = Assert.Single(_upstream.Seen);
        Assert.False(sent.Headers.Contains("X-Browser-Hop"));
        Assert.False(response.Headers.Contains("X-Upstream-Hop"));
        Assert.True(response.Headers.Contains("X-Kept"));
    }

    [Fact]
    public async Task The_host_answers_its_own_routes_without_asking_upstream()
    {
        using var response = await _client.GetAsync(new Uri("/api/status", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(_upstream.Seen);
    }

    // HttpClient normalises the request target, so a hostile one goes over a bare socket, written as a client could.
    private async Task<string> RawGetAsync(string target, string host)
    {
        var address = _host!.Address;
        using var socket = new System.Net.Sockets.TcpClient();
        await socket.ConnectAsync(address.Host, address.Port, TestContext.Current.CancellationToken);
        var stream = socket.GetStream();
        var request = $"GET {target} HTTP/1.1\r\nHost: {host}\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request), TestContext.Current.CancellationToken);
        using var reader = new StreamReader(stream, Encoding.ASCII);
        return await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
    }
}
