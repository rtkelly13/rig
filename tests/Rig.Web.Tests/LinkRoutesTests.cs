using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Rig.Core.Chrome;
using Rig.Web.Tests.Fakes;

namespace Rig.Web.Tests;

/// <summary><c>POST /api/open</c> hands dashboard links to the router, and only web pages from the dashboard.</summary>
public sealed class LinkRoutesTests : IAsyncDisposable
{
    private readonly EstateTree _tree = new();
    private readonly List<Uri> _opened = [];
    private RigWebHost? _host;
    private HttpClient? _client;

    public async ValueTask DisposeAsync()
    {
        _client?.Dispose();
        if (_host is not null)
        {
            await _host.DisposeAsync();
        }

        _tree.Dispose();
    }

    [Fact]
    public async Task A_web_page_goes_to_the_router()
    {
        var client = await Start(page =>
        {
            _opened.Add(page);
            return new LinkOutcome(true, "opened in Rig links");
        });

        using var response = await Post(client, "{\"url\": \"https://github.com/rtkelly13/rig/pull/1\"}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;
        Assert.True((bool?)json["landed"]);
        Assert.Equal("opened in Rig links", (string?)json["detail"]);
        Assert.Equal([new Uri("https://github.com/rtkelly13/rig/pull/1")], _opened);
    }

    [Theory]
    [InlineData("{\"url\": \"file:///C:/secrets.txt\"}", "application/json")]
    [InlineData("{\"url\": \"javascript:alert(1)\"}", "application/json")]
    [InlineData("{\"url\": \"/relative\"}", "application/json")]
    [InlineData("{}", "application/json")]
    [InlineData("{\"url\": \"https://example.com\"}", "text/plain")]
    public async Task Anything_but_a_web_page_from_the_dashboard_is_refused(string body, string contentType)
    {
        var client = await Start(page =>
        {
            _opened.Add(page);
            return new LinkOutcome(true, "opened");
        });

        using var response = await Post(client, body, contentType);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_opened);
    }

    [Fact]
    public async Task Without_a_router_the_page_opens_the_link_itself()
    {
        var client = await Start(null);

        using var response = await Post(client, "{\"url\": \"https://example.com\"}");

        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
    }

    private async Task<HttpClient> Start(Func<Uri, LinkOutcome>? open)
    {
        _host = await RigWebHost.CreateAsync(_tree.Settings(), openLink: open, cancellationToken: TestContext.Current.CancellationToken);
        await _host.StartAsync(TestContext.Current.CancellationToken);
        _client = new HttpClient { BaseAddress = _host.Address };
        return _client;
    }

    private static async Task<HttpResponseMessage> Post(HttpClient client, string body, string contentType = "application/json")
    {
        using var content = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        return await client.PostAsync(new Uri(LinkRoutes.OpenPath, UriKind.Relative), content, TestContext.Current.CancellationToken);
    }
}
