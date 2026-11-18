using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Rig.Image;

namespace Rig.Web;

/// <summary>
/// The Asset Studio's API (<c>studio.js</c>): project catalogs, drift diffs and snapshot capture over the brand assets, plus the variation
/// snapshots under <c>/media/variations/</c>. The work is <see cref="StudioApi"/>'s; these routes only carry requests to it.
/// </summary>
internal static class StudioRoutes
{
    private const string VariationsPrefix = StudioApi.VariationsPrefix;

    /// <summary>Maps the routes onto <paramref name="routes"/>.</summary>
    public static void Map(IEndpointRouteBuilder routes, WebHostSettings settings)
    {
        var api = new StudioApi(settings.AssetsRoot, settings.EffectiveVariationsRoot);
        routes.MapGet("/api/projects", () => Answer(api.Projects()));
        routes.MapGet("/api/diff", (string? @ref, string? cand) => Answer(StudioApi.Diff(@ref, cand, raw => Resolve(settings, raw))));
        routes.MapPost("/api/snapshot", async (HttpContext context) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync(context.RequestAborted).ConfigureAwait(false);
            return Answer(api.Snapshot(body));
        });
        routes.MapGet("/media/variations/{**file}", (HttpContext context) =>
            FrontendRoutes.File(settings.EffectiveVariationsRoot, Uri.UnescapeDataString(FrontendRoutes.Rest(context, VariationsPrefix)), "Variation"));
    }

    private static IResult Answer(StudioResponse response) => Results.Text(response.Body, WebJson.ContentType, statusCode: response.Status);

    // A media URL or a path under either root; whatever leaves them, or is missing, is null.
    private static string? Resolve(WebHostSettings settings, string raw)
    {
        var decoded = Uri.UnescapeDataString(raw.Trim());
        if (decoded.StartsWith(StudioApi.AssetsPrefix, StringComparison.Ordinal))
        {
            return FrontendRoutes.Under(settings.AssetsRoot, decoded[StudioApi.AssetsPrefix.Length..]);
        }

        if (decoded.StartsWith(VariationsPrefix, StringComparison.Ordinal))
        {
            return FrontendRoutes.Under(settings.EffectiveVariationsRoot, decoded[VariationsPrefix.Length..]);
        }

        return FrontendRoutes.Under(settings.AssetsRoot, decoded) ?? FrontendRoutes.Under(settings.EffectiveVariationsRoot, decoded);
    }
}
