using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Rig.Core.Connectivity;

namespace Rig.Web;

/// <summary>The estate graph the estate page draws, read-only, from a short-lived local snapshot.</summary>
/// <remarks>
/// | route | answer |
/// |---|---|
/// | <c>GET /api/estate/graph</c> | <see cref="ConnectivityGraph"/>, the snapshot when it is under a minute old |
/// | <c>GET /api/estate/graph/refresh</c> | <see cref="ConnectivityGraph"/>, inventoried again |
///
/// Both answer <c>Cache-Control: no-store</c>, and 503 with <c>{"error": ...}</c> when the registry or a snapshot cannot
/// be read or written.
/// </remarks>
internal static class ConnectivityRoutes
{
    /// <summary>Maps the graph routes onto <paramref name="routes"/>, reading the checkouts under <paramref name="estateDir"/>.</summary>
    public static void Map(IEndpointRouteBuilder routes, WebHostSettings settings, EstateGraph graph, string estateDir)
    {
        routes.MapGet("/api/estate/graph", (Delegate)((HttpContext context) => AnswerAsync(context, settings, graph, estateDir, refresh: false)));
        routes.MapGet("/api/estate/graph/refresh", (Delegate)((HttpContext context) => AnswerAsync(context, settings, graph, estateDir, refresh: true)));
    }

    private static async Task<IResult> AnswerAsync(HttpContext context, WebHostSettings settings, EstateGraph graph, string estateDir, bool refresh)
    {
        context.Response.Headers.CacheControl = "no-store";
        try
        {
            var value = await graph.LoadAsync(settings.RegistryFile, estateDir, refresh, context.RequestAborted).ConfigureAwait(false);
            return Results.Text(JsonSerializer.Serialize(value, WebJson.Options), WebJson.ContentType);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            return Results.Text(JsonSerializer.Serialize(new { error = e.Message }, WebJson.Options), WebJson.ContentType, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }
}
