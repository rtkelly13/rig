using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Rig.Core.Chrome;
using OpsJson = Rig.Web.Ops.OpsJson;

namespace Rig.Web;

/// <summary>Request payload to open a page.</summary>
/// <param name="Url">An absolute http or https address.</param>
public sealed record OpenLinkRequest([property: JsonPropertyName("url")] Uri? Url = null);

/// <summary>
/// <c>POST /api/open</c>: opens a page from the dashboard in the "Rig links" Chrome window. The dashboard runs as an
/// <c>--app</c> window, so a plain link would open in whichever Chrome window was used last.
/// </summary>
/// <remarks>
/// The body is read as every dashboard write is (<see cref="OpsJson.BodyAsync"/>): loopback, same origin and
/// <c>application/json</c> only, so another site cannot open pages through it. Without a router (not Windows, or
/// no desktop plan) the route answers 501 and the dashboard opens the link itself.
/// </remarks>
public static class LinkRoutes
{
    /// <summary>The route.</summary>
    public const string OpenPath = "/api/open";

    /// <summary>Maps the route onto <paramref name="routes"/>.</summary>
    /// <param name="routes">Where the endpoint goes.</param>
    /// <param name="open">Opens a page in the companion window; null when this machine cannot.</param>
    public static void Map(IEndpointRouteBuilder routes, Func<Uri, LinkOutcome>? open)
    {
        ArgumentNullException.ThrowIfNull(routes);
        routes.MapPost(OpenPath, (Func<HttpContext, Task<IResult>>)(context => OpsJson.Run(async () =>
        {
            var request = OpsJson.Read<OpenLinkRequest>(await OpsJson.BodyAsync(context).ConfigureAwait(false));
            if (request.Url is not { } page || !LinkRouter.IsWebPage(page))
            {
                return OpsJson.Error(StatusCodes.Status400BadRequest, "`url` must be an absolute http or https address");
            }

            if (open is null)
            {
                return OpsJson.Error(StatusCodes.Status501NotImplemented, "This machine cannot route links; open it in the browser");
            }

            var outcome = await Task.Run(() => open(page)).ConfigureAwait(false);
            return OpsJson.Ok(context, new { landed = outcome.Landed, detail = outcome.Detail });
        })));
    }
}
