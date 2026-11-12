using System.Net;

namespace Rig.Web.Tests.Fakes;

/// <summary>Stands in for the Python routes: records each request it is sent and answers with <see cref="Answer"/>.</summary>
internal sealed class Upstream : HttpMessageHandler
{
    public List<(HttpRequestMessage Request, string? Body)> Seen { get; } = [];

    public Func<HttpRequestMessage, HttpResponseMessage> Answer { get; set; } = _ => new(HttpStatusCode.OK) { Content = new StringContent("upstream") };

    public static Uri Address { get; } = new("http://127.0.0.1:1");

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Seen.Add((request, body));
        return Answer(request);
    }
}
