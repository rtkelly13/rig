using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace Rig.Web;

/// <summary>
/// JSON as the Python server wrote it: snake_case keys and nulls kept. The React app in <c>web/</c> declares the same
/// shapes in TypeScript, so a renamed key here is a broken page there.
/// </summary>
internal static class WebJson
{
    /// <summary>The serializer options every route uses.</summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
    };

    /// <summary>The media type of every JSON answer.</summary>
    public const string ContentType = "application/json; charset=utf-8";

    /// <summary>
    /// <paramref name="value"/> as JSON, readable from any origin as the host's own endpoints always were
    /// (<c>Access-Control-Allow-Origin: *</c>); the mounted feature routes answer without it.
    /// </summary>
    public static IResult Open<T>(HttpContext context, T value)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Response.Headers.AccessControlAllowOrigin = "*";
        return Results.Text(JsonSerializer.Serialize(value, Options), ContentType);
    }
}
