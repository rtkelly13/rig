using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Rig.Core.Storage;
using Rig.Core.Web;

namespace Rig.Web;

/// <summary>The host's own endpoints: which environment this is, live-reload polling, the estate catalogue and database backup.</summary>
/// <remarks>
/// | route | answer |
/// |---|---|
/// | <c>GET /api/status</c> | <see cref="StatusView"/> |
/// | <c>GET /api/dev/mtime</c> | <see cref="MtimeView"/>, polled by the legacy studio's live reload |
/// | <c>GET /api/estate</c> | <see cref="EstateCatalogue"/>, the estate cards |
/// | <c>POST /api/db/backup</c> | <see cref="BackupView"/>, an immediate copy of the shared database |
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
        routes.MapPost("/api/db/backup", (HttpContext context) =>
        {
            try
            {
                var directory = Path.Join(Path.GetDirectoryName(Path.GetFullPath(settings.DbPath))!, "backups");
                var file = RigDatabase.Backup(settings.DbPath, directory, TimeProvider.System);
                return WebJson.Open(context, new BackupView("ok", file));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
            {
                context.Response.Headers.AccessControlAllowOrigin = "*";
                return Results.Text(
                    System.Text.Json.JsonSerializer.Serialize(new BackupError($"Database backup failed: {e.Message}"), WebJson.Options),
                    WebJson.ContentType,
                    statusCode: StatusCodes.Status500InternalServerError);
            }
        });
    }
}

/// <summary>The answer to <c>POST /api/db/backup</c>.</summary>
/// <param name="Status"><c>ok</c> when a copy was written.</param>
/// <param name="BackupFile">The created backup's path.</param>
internal sealed record BackupView(string Status, string BackupFile);

/// <summary>The answer to a failed <c>POST /api/db/backup</c>.</summary>
/// <param name="Error">Why the copy could not be written.</param>
internal sealed record BackupError(string Error);

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
