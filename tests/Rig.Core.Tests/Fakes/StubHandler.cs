namespace Rig.Core.Tests.Otel;

/// <summary>An HTTP handler that answers every request with <paramref name="send"/>.</summary>
public sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
}
