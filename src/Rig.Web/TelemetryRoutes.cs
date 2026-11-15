using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Rig.Web.Ops;

namespace Rig.Web;

/// <summary>Telemetry for the ops pages: which trace store answers, which explorers are up, and recent traces.</summary>
/// <remarks>
/// | route | answer |
/// |---|---|
/// | <c>GET /api/ops/telemetry/status</c> | <c>TelemetryView</c> |
/// | <c>GET /api/ops/telemetry/traces?days=1..30&amp;limit=1..500&amp;service=</c> | <c>TraceListView</c>, newest first |
///
/// Traces come from the on-demand ClickHouse when it answers and from the recorded NDJSON files otherwise. A
/// ClickHouse that is down is not an error: status reports it and the list falls back, saying so in <c>note</c>.
/// </remarks>
internal static class TelemetryRoutes
{
    private const string Prefix = "/api/ops/telemetry";
    private static readonly string[] AnyMethod = ["GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS"];

    /// <summary>Maps the telemetry routes onto <paramref name="routes"/>.</summary>
    /// <param name="routes">Where the endpoints go.</param>
    /// <param name="telemetry">The trace store and explorers.</param>
    public static void Map(IEndpointRouteBuilder routes, Telemetry telemetry)
    {
        routes.MapGet($"{Prefix}/status", Handler(context =>
            OpsJson.Run(async () => OpsJson.Ok(context, await telemetry.StatusAsync().ConfigureAwait(false)))));
        routes.MapGet($"{Prefix}/traces", Handler(context => OpsJson.Run(async () =>
        {
            var query = new TraceQuery(
                OpsJson.IntParam(context, "days", fallback: 1, low: 1, high: 30),
                OpsJson.IntParam(context, "limit", fallback: 100, low: 1, high: 500),
                OpsJson.Param(context, "service"));
            return OpsJson.Ok(context, await telemetry.RecentAsync(query).ConfigureAwait(false));
        })));
        routes.MapMethods($"{Prefix}/{{**rest}}", AnyMethod, Handler(context =>
            OpsJson.Run(() => Task.FromResult(OpsJson.Error(
                StatusCodes.Status404NotFound,
                $"No telemetry endpoint at {context.Request.Path.Value}")))));
    }

    // The typed delegate selects the route-handler overload, not RequestDelegate, so its IResult is written.
    private static Delegate Handler(Func<HttpContext, Task<IResult>> handler) => handler;
}
