using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Rig.Core.Web;

namespace Rig.Web;

/// <summary>The host's own endpoints: which environment this is, live-reload polling and the estate catalogue.</summary>
/// <remarks>
/// | route | answer |
/// |---|---|
/// | <c>GET /api/status</c> | <see cref="StatusView"/> |
/// | <c>GET /api/dev/mtime</c> | <see cref="MtimeView"/>, polled by the legacy studio's live reload |
/// | <c>GET /api/estate</c> | <see cref="EstateCatalogue"/>, the estate cards |
/// </remarks>
internal static class HostRoutes
{
    /// <summary>Maps the host's endpoints onto <paramref name="routes"/>.</summary>
    public static void Map(IEndpointRouteBuilder routes, WebHostSettings settings, Func<int> boundPort)
    {
        routes.MapGet("/api/status", (HttpContext context) =>
            WebJson.Open(context, new StatusView(settings.Environment, boundPort(), settings.IsPreview, settings.DbPath)));
        routes.MapGet("/api/dev/mtime", (HttpContext context) =>
        {
            var changed = FrontendFiles.Changed(settings.TemplatesDir);
            return WebJson.Open(context, new MtimeView(changed.Overall, changed.Css, settings.IsPreview));
        });
        routes.MapGet("/api/estate", (HttpContext context) =>
            WebJson.Open(context, EstateCatalogue.Load(settings.RegistryFile, settings.WorkingDirectory, settings.AssetsRoot)));
    }
}

/// <summary>The answer to <c>GET /api/status</c>.</summary>
/// <param name="Environment"><c>PROD</c> or <c>PREVIEW</c>.</param>
/// <param name="Port">The port the host is bound to.</param>
/// <param name="IsPreview">Whether this is the preview environment.</param>
/// <param name="DbPath">The shared SQLite database.</param>
internal sealed record StatusView(string Environment, int Port, bool IsPreview, string DbPath);

/// <summary>The answer to <c>GET /api/dev/mtime</c>.</summary>
/// <param name="Mtime">The newest watched frontend file's modification time, in Unix seconds.</param>
/// <param name="CssMtime">The newest watched stylesheet's modification time, in Unix seconds.</param>
/// <param name="IsPreview">Whether this is the preview environment.</param>
internal sealed record MtimeView(double Mtime, double CssMtime, bool IsPreview);
