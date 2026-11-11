using System.Net;
using System.Text;

namespace Rig.Core.Tests.Fakes;

/// <summary>An HTTP handler that answers by exact URL: a known one gets its JSON, any other a 404. Every URL is recorded.</summary>
public sealed class FakeHttp : HttpMessageHandler
{
    /// <summary>The JSON body served for each URL.</summary>
    public Dictionary<string, string> Routes { get; } = new(StringComparer.Ordinal);

    /// <summary>Every requested URL, in order.</summary>
    public List<string> Requested { get; } = [];

    /// <summary>A client over this handler; the handler outlives it.</summary>
    public HttpClient Client() => new(this, disposeHandler: false);

    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var url = request.RequestUri!.OriginalString;
        Requested.Add(url);
        return Task.FromResult(
            Routes.TryGetValue(url, out var json)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}
