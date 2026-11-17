using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Rig.Core.ReviewQueue;
using Rig.Web.Ops;

namespace Rig.Web;

/// <summary>The triage inbox's routes: the persistent queue and the three writes that change it.</summary>
/// <remarks>
/// | route | answer |
/// |---|---|
/// | <c>GET /api/triage</c> | <see cref="TriageAnswer"/> |
/// | <c>POST /api/triage/refresh</c> | <see cref="TriageAnswer"/>, after refreshing every managed repository |
/// | <c>POST /api/triage/watch</c> | <see cref="TriageAnswer"/>, after observing <c>{"repo", "number"}</c> once |
/// | <c>POST /api/triage/interact</c> | <see cref="TriageAnswer"/>, after recording an interaction |
///
/// Browser writes are accepted only from this dashboard, with a JSON body.
/// </remarks>
internal static class TriageRoutes
{
    private const string Prefix = "/api/triage";
    private static readonly string[] ReadsAndWrites = ["GET", "POST"];

    /// <summary>Maps the triage routes onto <paramref name="routes"/>.</summary>
    /// <param name="routes">Where the endpoints go.</param>
    /// <param name="inbox">The persistent queue the routes answer from.</param>
    /// <param name="registry">The managed-projects registry a refresh reads.</param>
    public static void Map(IEndpointRouteBuilder routes, TriageInbox inbox, string registry)
    {
        routes.MapGet(Prefix, Handler(context => OpsJson.Run(async () =>
            OpsJson.Ok(context, Answer(await inbox.EntriesAsync(context.RequestAborted).ConfigureAwait(false), inbox)))));
        routes.MapPost($"{Prefix}/refresh", Handler(context => OpsJson.Run(async () =>
        {
            _ = await OpsJson.BodyAsync(context).ConfigureAwait(false);
            return OpsJson.Ok(context, Answer(await inbox.RefreshAsync(registry, context.RequestAborted).ConfigureAwait(false), inbox));
        })));
        routes.MapPost($"{Prefix}/watch", Handler(context => OpsJson.Run(async () =>
        {
            var watch = TriageInbox.Watch(await OpsJson.BodyAsync(context).ConfigureAwait(false), OpsJson.Boundary);
            return OpsJson.Ok(context, Answer(await inbox.WatchAsync(watch.Repo, watch.Number, context.RequestAborted).ConfigureAwait(false), inbox));
        })));
        routes.MapPost($"{Prefix}/interact", Handler(context => OpsJson.Run(async () =>
        {
            var request = JsonSerializer.Deserialize<QueueInteraction>(await OpsJson.BodyAsync(context).ConfigureAwait(false), OpsJson.Boundary)
                ?? throw new ArgumentException("Invalid triage interaction");
            request.Validate();
            return OpsJson.Ok(context, Answer(await inbox.RecordAsync(request, context.RequestAborted).ConfigureAwait(false), inbox));
        })));
        routes.MapPost(Prefix, Handler(_ => Unknown()));
        routes.MapMethods($"{Prefix}/{{**rest}}", ReadsAndWrites, Handler(_ => Unknown()));
    }

    private static Task<IResult> Unknown() => OpsJson.Run(() => Task.FromResult(OpsJson.Error(StatusCodes.Status400BadRequest, "Unknown triage operation")));

    private static TriageAnswer Answer(IReadOnlyList<TriageEntry> entries, TriageInbox inbox) => new(entries, inbox.QueuePath);

    // The typed delegate selects the route-handler overload, not RequestDelegate, so its IResult is written.
    private static Delegate Handler(Func<HttpContext, Task<IResult>> handler) => handler;
}

/// <summary>The answer to every triage route.</summary>
/// <param name="Entries">Every tracked item, in the versioned order.</param>
/// <param name="QueuePath">The Markdown projection the queue was exported to.</param>
public sealed record TriageAnswer(IReadOnlyList<TriageEntry> Entries, string QueuePath);
