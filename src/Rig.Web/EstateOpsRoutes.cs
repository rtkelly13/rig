using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Rig.Web.Ops;

namespace Rig.Web;

/// <summary>The estate's ops routes: the decision feed, disk headroom and trimming idle worktrees.</summary>
/// <remarks>
/// | route | answer |
/// |---|---|
/// | <c>GET /api/ops/decisions</c> | <c>DecisionFeedView</c>: the feed the <c>estate-decisions</c> job writes |
/// | <c>GET /api/ops/estate/disk</c> | <see cref="DiskUsageView"/> of the drive holding the code root |
/// | <c>GET /api/ops/estate/trim?refresh=true</c> | <see cref="TrimScan"/>: the last finished scan; <c>refresh</c> starts a new one |
/// | <c>POST /api/ops/estate/trim/apply</c> | <c>list[TrimResult]</c> for <c>{"paths": [...]}</c> |
///
/// The decision feed is only read here: a missing file is normal before the job's first run, so it is an empty list
/// with a note, not an error. A trim scan takes a while, so it runs in the background and a GET never waits.
/// </remarks>
internal static class EstateOpsRoutes
{
    private const string Decisions = "/api/ops/decisions";
    private const string Estate = "/api/ops/estate";
    private static readonly string[] AnyMethod = ["GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS"];

    /// <summary>Maps the estate routes onto <paramref name="routes"/>.</summary>
    /// <param name="routes">Where the endpoints go.</param>
    /// <param name="ops">The estate answers.</param>
    public static void Map(IEndpointRouteBuilder routes, EstateOps ops)
    {
        routes.MapGet(Decisions, Handler(context => OpsJson.Run(() => Task.FromResult(OpsJson.Ok(context, ops.Decisions())))));
        routes.MapGet($"{Estate}/disk", Handler(context => OpsJson.Run(() => Task.FromResult(OpsJson.Ok(context, ops.Disk())))));
        routes.MapGet($"{Estate}/trim", Handler(context =>
            OpsJson.Run(() => Task.FromResult(OpsJson.Ok(context, ops.Trim(OpsJson.Flag(context, "refresh")))))));
        routes.MapPost($"{Estate}/trim/apply", Handler(context => OpsJson.Run(async () =>
        {
            var request = JsonSerializer.Deserialize<EstateApply>(await OpsJson.BodyAsync(context).ConfigureAwait(false), OpsJson.Boundary)
                ?? throw new ArgumentException("Invalid trim request");
            return OpsJson.Ok(context, await ops.ApplyAsync(request.Paths, context.RequestAborted).ConfigureAwait(false));
        })));
        routes.MapMethods($"{Estate}/{{**rest}}", AnyMethod, Handler(context =>
            OpsJson.Run(() => Task.FromResult(OpsJson.Error(
                StatusCodes.Status404NotFound,
                $"No {context.Request.Method} {context.Request.Path.Value}")))));
    }

    // The typed delegate selects the route-handler overload, not RequestDelegate, so its IResult is written.
    private static Delegate Handler(Func<HttpContext, Task<IResult>> handler) => handler;

    /// <summary>The paths a trim apply was asked to remove.</summary>
    /// <param name="Paths">The paths; only ones the last finished scan offered are removed.</param>
    public sealed record EstateApply(IReadOnlyList<string> Paths);
}
