using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Rig.Core.C4;

namespace Rig.Web.C4;

/// <summary>The C4 viewer: its page and static files, and the bundle as JSON.</summary>
/// <remarks>
/// | route | answer |
/// |---|---|
/// | <c>GET /c4</c>, <c>/c4/</c> | the viewer page |
/// | <c>GET /c4/static/{file}</c> | a file under the viewer's static folder |
/// | <c>GET /api/c4/bundle</c> | the concept list, zoom tree and findings (<see cref="BundleView"/>) |
/// | <c>GET /api/c4/concept?id=</c> | one concept (<see cref="ConceptView"/>) |
/// | <c>GET /api/c4/expand?id=&amp;alias=</c> | a parent diagram with one zoomed element expanded (<see cref="ExpandView"/>) |
///
/// <c>?bundle=</c> picks one of <see cref="C4Viewer.Bundles"/>; the first is the default. <c>RIG_C4_BUNDLE</c> adds a
/// bundle at the front. The bundle is re-read on every request, so edits show on the next click. Every answer is
/// <c>no-cache</c>; every failure is <c>{"error": ...}</c>.
/// </remarks>
internal static class C4Routes
{
    /// <summary>The environment variable that points the viewer at another bundle.</summary>
    public const string BundleVariable = "RIG_C4_BUNDLE";

    private const string StaticPrefix = "/c4/static/";
    private const string JsonType = "application/json; charset=utf-8";

    /// <summary>Maps the routes onto <paramref name="routes"/>.</summary>
    public static void Map(IEndpointRouteBuilder routes, WebHostSettings settings)
    {
        ArgumentNullException.ThrowIfNull(routes);
        // A route template ignores a trailing slash, so this answers `/c4/` too; a second route for it would be ambiguous.
        routes.MapGet("/c4", () => Static(settings.C4StaticDir, "index.html"));
        routes.MapGet(StaticPrefix + "{**file}", (HttpContext context) =>
            Static(settings.C4StaticDir, Uri.UnescapeDataString(FrontendRoutes.Rest(context, StaticPrefix))));
        routes.MapGet("/api/c4/{**rest}", (HttpContext context) => Api(context, settings));
    }

    private static Answer Api(HttpContext context, WebHostSettings settings)
    {
        var path = context.Request.Path.Value ?? string.Empty;
        var query = context.Request.Query;
        var known = C4Viewer.Bundles(settings.C4SampleBundle, Environment.GetEnvironmentVariable(BundleVariable));
        var name = First(query["bundle"]);
        if (C4Viewer.BundleRoot(known, name) is not { } root)
        {
            return Error(name.Length > 0 ? $"no bundle `{name}`" : $"no C4 bundle: set {BundleVariable}", StatusCodes.Status404NotFound);
        }

        var bundle = OkfBundle.Load(root);
        var id = First(query["id"]);
        switch (path)
        {
            case "/api/c4/bundle":
                return Json(C4Viewer.View(bundle, known));
            case "/api/c4/concept":
                return bundle.Get(id) is { } concept
                    ? Json(C4Viewer.View(bundle, concept))
                    : Error($"no concept `{id}`", StatusCodes.Status404NotFound);
            case "/api/c4/expand":
                var alias = First(query["alias"]);
                try
                {
                    return Json(new ExpandView(id, alias, C4Composer.Expand(bundle, id, alias)));
                }
                catch (ExpandException error)
                {
                    return Error(error.Message, StatusCodes.Status400BadRequest);
                }

            default:
                return Error($"unknown endpoint {path}", StatusCodes.Status404NotFound);
        }
    }

    // A blank value is the same as absent.
    private static string First(Microsoft.Extensions.Primitives.StringValues values) => values.FirstOrDefault() ?? string.Empty;

    private static Answer Static(string root, string name)
    {
        if (root.Length == 0 || FrontendRoutes.Under(root, name) is not { } file)
        {
            return Error($"not found: {name}", StatusCodes.Status404NotFound);
        }

        var extension = Path.GetExtension(file);
        var type = FrontendRoutes.ContentTypes.GetValueOrDefault(extension, "application/octet-stream");
        if (type.StartsWith("text/", StringComparison.Ordinal))
        {
            type += "; charset=utf-8";
        }

        return Send(StatusCodes.Status200OK, type, File.ReadAllBytes(file));
    }

    private static Answer Json<T>(T body) => Send(StatusCodes.Status200OK, JsonType, JsonSerializer.SerializeToUtf8Bytes(body, C4Viewer.Json));

    private static Answer Error(string message, int status) => Send(status, JsonType, JsonSerializer.SerializeToUtf8Bytes(new ErrorView(message), C4Viewer.Json));

    private static Answer Send(int status, string type, byte[] body) =>
        new Answer(status, type, body);

    private sealed class Answer(int status, string type, byte[] body) : IResult
    {
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            ArgumentNullException.ThrowIfNull(httpContext);
            var response = httpContext.Response;
            response.StatusCode = status;
            response.ContentType = type;
            response.ContentLength = body.Length;
            response.Headers.CacheControl = "no-cache";
            await response.Body.WriteAsync(body, httpContext.RequestAborted).ConfigureAwait(false);
        }
    }
}
