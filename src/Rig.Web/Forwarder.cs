using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace Rig.Web;

/// <summary>
/// Hands every request the C# host does not own yet to the Python <c>StudioServer</c> it runs beside it: the HTTP form of
/// ADR 0022's strangler, where the C# front door forwards what it has not ported. Each ported route group takes its
/// paths away from here; when the last one is ported this class and the Python server are deleted.
/// </summary>
/// <remarks>
/// The request goes over as it came: its raw target, method, body and headers, <c>Host</c> and <c>Origin</c> included,
/// so the Python write guards see what the browser sent. The answer comes back as it left, redirects unfollowed.
/// </remarks>
internal sealed class Forwarder(HttpClient client, Func<Uri?> upstream)
{
    // Hop-by-hop headers (RFC 9110 section 7.6.1) belong to one connection and are never forwarded.
    private static readonly HashSet<string> HopByHop = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection", "Keep-Alive", "Proxy-Connection", "Proxy-Authenticate", "Proxy-Authorization", "TE", "Trailer",
        "Transfer-Encoding", "Upgrade",
    };

    /// <summary>Forwards <paramref name="context"/>'s request upstream and copies the answer back.</summary>
    public async Task ForwardAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var target = context.Features.Get<IHttpRequestFeature>()?.RawTarget is { Length: > 0 } raw
            ? raw
            : context.Request.Path.ToUriComponent() + context.Request.QueryString.ToUriComponent();
        if (upstream() is not { } baseAddress)
        {
            await Fail(context, StatusCodes.Status404NotFound, $"Endpoint not found: {context.Request.Path}").ConfigureAwait(false);
            return;
        }

        // A safe request that never got an answer is sent once more (RFC 9110 section 9.2.2): on Windows the Python server
        // now and then aborts a fresh connection before it answers, and nothing was done upstream that a retry repeats.
        var retries = Safe(context.Request) ? 1 : 0;
        HttpResponseMessage response;
        while (true)
        {
            using var request = Build(context, baseAddress, target);
            try
            {
                response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted).ConfigureAwait(false);
                break;
            }
            catch (HttpRequestException) when (retries-- > 0)
            {
            }
            catch (HttpRequestException e)
            {
                await Fail(context, StatusCodes.Status502BadGateway, $"The Python web routes did not answer: {Reason(e)}").ConfigureAwait(false);
                return;
            }
        }

        using (response)
        {
            context.Response.StatusCode = (int)response.StatusCode;
            var dropped = Listed(response.Headers.Connection);
            foreach (var (name, values) in response.Headers.Concat(response.Content.Headers))
            {
                if (!HopByHop.Contains(name) && !dropped.Contains(name))
                {
                    context.Response.Headers[name] = values.ToArray();
                }
            }

            await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted).ConfigureAwait(false);
        }
    }

    private static HttpRequestMessage Build(HttpContext context, Uri baseAddress, string target)
    {
        var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), Destination(baseAddress, target));
        if (HasBody(context.Request))
        {
            request.Content = new StreamContent(context.Request.Body);
        }

        var listed = Listed(context.Request.Headers.Connection);
        foreach (var (name, values) in context.Request.Headers)
        {
            if (HopByHop.Contains(name) || listed.Contains(name) || name.Equals("Host", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!request.Headers.TryAddWithoutValidation(name, values.ToArray()))
            {
                _ = request.Content?.Headers.TryAddWithoutValidation(name, values.ToArray());
            }
        }

        request.Headers.Host = context.Request.Host.Value;

        // Python's http.server answers HTTP/1.0 and closes the connection after every answer, but HttpClient pools it
        // anyway. A request sent on it as Python closes it is reset (on Windows, most often), so each one asks for its own.
        request.Headers.ConnectionClose = true;
        return request;
    }

    private static bool HasBody(HttpRequest request) => request.ContentLength > 0 || request.Headers.TransferEncoding.Count > 0;

    // A body is read once, so only a bodiless request of a safe method can go again.
    private static bool Safe(HttpRequest request) =>
        !HasBody(request) && (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method) || HttpMethods.IsOptions(request.Method));

    /// <summary>
    /// The upstream URL for <paramref name="target"/>: only its path and query travel, onto the upstream's own scheme and
    /// authority, so an absolute-form (<c>http://elsewhere/x</c>) or <c>//elsewhere/x</c> target cannot send the request
    /// anywhere but the Python routes.
    /// </summary>
    internal static Uri Destination(Uri upstream, string target)
    {
        var pathAndQuery = !target.StartsWith('/') && Uri.TryCreate(target, UriKind.Absolute, out var absolute) ? absolute.PathAndQuery : target;
        return new Uri(upstream.GetLeftPart(UriPartial.Authority) + "/" + pathAndQuery.TrimStart('/'));
    }

    // The headers a Connection header names are hop-by-hop too (RFC 9110 section 7.6.1).
    private static HashSet<string> Listed(IEnumerable<string?> connection) =>
        new(
            connection.SelectMany(v => (v ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)),
            StringComparer.OrdinalIgnoreCase);

    // HttpClient's own message is often only "An error occurred while sending the request"; the cause is inside it.
    internal static string Reason(HttpRequestException error) =>
        error.InnerException is { } inner ? $"{error.Message} ({inner.Message})" : error.Message;

    private static Task Fail(HttpContext context, int status, string message)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = WebJson.ContentType;
        return context.Response.WriteAsync(JsonSerializer.Serialize(new { error = message }), context.RequestAborted);
    }
}
