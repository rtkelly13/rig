using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Rig.Web;

/// <summary>The React app's document, its static files and the brand images, served from the checkout.</summary>
/// <remarks>
/// | route | answer |
/// |---|---|
/// | the app's paths (<see cref="AppPaths"/>, <see cref="AppPrefixes"/>) | <c>index.html</c> from the templates folder, <c>no-store</c> |
/// | <c>GET /static/triage.html</c> | 302 to <c>/triage/inbox</c>, the old triage page's new home |
/// | <c>GET /static/{file}</c> | a file under the templates folder |
/// | <c>GET /media/assets/{file}</c> | a brand image under the assets folder, for the estate cards |
///
/// Files are sent as Python's <c>StudioServer</c> sent them: the content type its <c>mimetypes</c> gave for each extension
/// the checkout serves (<see cref="ContentTypes"/>), and <c>no-cache, no-store, must-revalidate</c>. A path that is
/// missing, or that resolves outside its folder, is a 404. <c>/c4/static/</c> is the C4 viewer's and is not served here.
/// </remarks>
internal static class FrontendRoutes
{
    /// <summary>The paths that are the app's document exactly.</summary>
    public static readonly IReadOnlyList<string> AppPaths = ["/", "/index.html", "/estate", "/studio", "/optical", "/triage", "/ops"];

    /// <summary>The prefixes the app's client-side router owns.</summary>
    public static readonly IReadOnlyList<string> AppPrefixes = ["/triage", "/repos", "/estate", "/ops"];

    /// <summary>What Python's <c>mimetypes.guess_type</c> answered, by extension; anything else is a byte stream.</summary>
    public static readonly IReadOnlyDictionary<string, string> ContentTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [".css"] = "text/css",
        [".csv"] = "text/csv",
        [".gif"] = "image/gif",
        [".htm"] = "text/html",
        [".html"] = "text/html",
        [".ico"] = "image/vnd.microsoft.icon",
        [".jpeg"] = "image/jpeg",
        [".jpg"] = "image/jpeg",
        [".js"] = "text/javascript",
        [".json"] = "application/json",
        [".md"] = "text/markdown",
        [".mjs"] = "text/javascript",
        [".pdf"] = "application/pdf",
        [".png"] = "image/png",
        [".svg"] = "image/svg+xml",
        [".txt"] = "text/plain",
        [".webp"] = "image/webp",
        [".woff"] = "font/woff",
        [".woff2"] = "font/woff2",
    };

    private const string StaticPrefix = "/static/";
    private const string AssetsPrefix = "/media/assets/";

    /// <summary>Maps the routes onto <paramref name="routes"/>.</summary>
    public static void Map(IEndpointRouteBuilder routes, WebHostSettings settings)
    {
        foreach (var path in AppPaths)
        {
            routes.MapGet(path, () => Document(settings.TemplatesDir));
        }

        foreach (var prefix in AppPrefixes)
        {
            routes.MapGet(prefix + "/{**rest}", () => Document(settings.TemplatesDir));
        }

        routes.MapGet("/static/triage.html", (HttpContext context) =>
        {
            context.Response.Headers.ContentLength = 0;
            return Results.Redirect("/triage/inbox");
        });
        routes.MapGet("/static/{**file}", (HttpContext context) =>
            File(settings.TemplatesDir, Rest(context, StaticPrefix), "Static file"));
        routes.MapGet("/media/assets/{**file}", (HttpContext context) =>
            File(settings.AssetsRoot, Uri.UnescapeDataString(Rest(context, AssetsPrefix)), "Asset"));
    }

    /// <summary>
    /// The request target after <paramref name="prefix"/>, undecoded, as Python's handler read it: a static file's name
    /// was used as sent, and a brand image's was decoded once. <see cref="Under"/> refuses whatever leaves the folder.
    /// </summary>
    internal static string Rest(HttpContext context, string prefix)
    {
        var raw = context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpRequestFeature>()?.RawTarget;
        var path = raw is { Length: > 0 } && raw.StartsWith(prefix, StringComparison.Ordinal)
            ? raw.Split('?', 2)[0]
            : context.Request.Path.Value ?? prefix;
        return path.StartsWith(prefix, StringComparison.Ordinal) ? path[prefix.Length..] : string.Empty;
    }

    /// <summary>
    /// The file <paramref name="relative"/> names under <paramref name="root"/>, or null when it is missing or resolves
    /// outside it, through <c>..</c> or a link.
    /// </summary>
    public static string? Under(string root, string relative)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(relative);
        if (relative.Contains('\0', StringComparison.Ordinal) || Path.IsPathRooted(relative))
        {
            return null;
        }

        var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(Path.Join(folder, relative));
        if (!target.StartsWith(folder, StringComparison.Ordinal) || !System.IO.File.Exists(target))
        {
            return null;
        }

        // Python resolved links before checking, so a link inside the folder, to a file or to a directory on the way,
        // may not lead out of it.
        var realFolder = Real(folder) is { } real ? Path.TrimEndingDirectorySeparator(real) + Path.DirectorySeparatorChar : null;
        return realFolder is not null && Real(target) is { } realTarget && realTarget.StartsWith(realFolder, StringComparison.Ordinal)
            ? target
            : null;
    }

    /// <summary><paramref name="path"/> with a link at any of its components followed, or null for a cycle.</summary>
    private static string? Real(string path, int depth = 0)
    {
        if (depth > 32)
        {
            return null;
        }

        var full = Path.GetFullPath(path);
        var current = Path.GetPathRoot(full) ?? string.Empty;
        foreach (var part in full[current.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Join(current, part);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (info.LinkTarget is { } linked)
            {
                if (Real(Path.GetFullPath(linked, Path.GetDirectoryName(current) ?? current), depth + 1) is not { } resolved)
                {
                    return null;
                }

                current = resolved;
            }
        }

        return current;
    }

    private static IResult Document(string templates)
    {
        var document = Path.Join(templates, "index.html");
        if (!System.IO.File.Exists(document))
        {
            return NotFound("Application document not found");
        }

        return Results.Bytes(System.IO.File.ReadAllBytes(document), "text/html; charset=utf-8", lastModified: null)
            .WithHeader("Cache-Control", "no-store");
    }

    private static IResult File(string root, string relative, string what)
    {
        if (Under(root, relative) is not { } file)
        {
            return NotFound($"{what} not found: {relative}");
        }

        var type = ContentTypes.GetValueOrDefault(Path.GetExtension(file), "application/octet-stream");
        return Results.Bytes(System.IO.File.ReadAllBytes(file), type).WithHeader("Cache-Control", "no-cache, no-store, must-revalidate");
    }

    private static IResult NotFound(string message) =>
        Results.Text(System.Text.Json.JsonSerializer.Serialize(new { error = message }), WebJson.ContentType, statusCode: StatusCodes.Status404NotFound);
}

/// <summary>An answer with one more header set.</summary>
internal static class HeaderResult
{
    /// <summary><paramref name="inner"/>, sending <paramref name="name"/>: <paramref name="value"/> as well.</summary>
    public static IResult WithHeader(this IResult inner, string name, string value) => new Headed(inner, name, value);

    private sealed class Headed(IResult inner, string name, string value) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            ArgumentNullException.ThrowIfNull(httpContext);
            httpContext.Response.Headers[name] = value;
            return inner.ExecuteAsync(httpContext);
        }
    }
}
