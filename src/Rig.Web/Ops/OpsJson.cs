using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Rig.Core.GitHub;
using Rig.Core.Otel;

namespace Rig.Web.Ops;

/// <summary>An ops request the route answers with its own status and message, rather than by propagating.</summary>
public sealed class OpsRequestException : Exception
{
    /// <summary>A bad request with no message.</summary>
    public OpsRequestException()
        : this(StatusCodes.Status400BadRequest, string.Empty)
    {
    }

    /// <summary>A bad request with <paramref name="message"/>.</summary>
    /// <param name="message">The <c>error</c> the envelope carries.</param>
    public OpsRequestException(string message)
        : this(StatusCodes.Status400BadRequest, message)
    {
    }

    /// <summary>A bad request wrapping <paramref name="inner"/>.</summary>
    /// <param name="message">The <c>error</c> the envelope carries.</param>
    /// <param name="inner">What caused it.</param>
    public OpsRequestException(string message, Exception inner)
        : base(message, inner)
    {
        Status = StatusCodes.Status400BadRequest;
    }

    /// <summary>An answer with <paramref name="status"/> and <paramref name="message"/>.</summary>
    /// <param name="status">The HTTP status to answer with.</param>
    /// <param name="message">The <c>error</c> the envelope carries.</param>
    public OpsRequestException(int status, string message)
        : base(message)
    {
        Status = status;
    }

    /// <summary>The HTTP status to answer with.</summary>
    public int Status { get; }
}

/// <summary>
/// The ops routes' JSON and request rules: snake_case keys,
/// nulls kept, compact bodies, <c>no-store</c>, and a write accepted only from this dashboard on a loopback address.
/// </summary>
public static class OpsJson
{
    /// <summary>The largest write body a route reads.</summary>
    public const int MaxBodyBytes = 65536;

    /// <summary>The serializer every ops answer uses.</summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false,
    };

    /// <summary>Reads a browser write: every required field must be present, as pydantic's validation demanded.</summary>
    public static readonly JsonSerializerOptions Boundary = new(Options)
    {
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
    };

    private static readonly string[] LoopbackHosts = ["127.0.0.1", "localhost", "::1"];

    /// <summary><paramref name="value"/> as an ops answer, which browsers and proxies must not cache.</summary>
    public static IResult Ok<T>(HttpContext context, T value)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Response.Headers.CacheControl = "no-store";
        return Results.Text(Serialize(value), WebJson.ContentType);
    }

    /// <summary>The shared error envelope, <c>{"error": "..."}</c>, which browsers and proxies must not cache.</summary>
    public static IResult Error(int status, string message)
    {
        var body = JsonSerializer.Serialize(new ErrorBody(message), Options);
        return Results.Text(body, WebJson.ContentType, statusCode: status).WithHeader("Cache-Control", "no-store");
    }

    /// <summary><paramref name="value"/> as compact JSON, the way pydantic's <c>model_dump_json</c> wrote it.</summary>
    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    /// <summary>
    /// Runs an ops answer, mapping its failures to the envelope: bad input is a 400, a store or service that is down a
    /// 503, and anything else is a bug that propagates.
    /// </summary>
    public static async Task<IResult> Run(Func<Task<IResult>> produce)
    {
        try
        {
            return await produce().ConfigureAwait(false);
        }
        catch (OpsRequestException failure)
        {
            return Error(failure.Status, failure.Message);
        }
        catch (Exception error) when (error is ArgumentException or JsonException or FormatException or InvalidDataException)
        {
            return Error(StatusCodes.Status400BadRequest, error.Message);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SqliteException or GhException
            or ClickHouseException or HttpRequestException or Win32Exception or TimeoutException)
        {
            return Error(StatusCodes.Status503ServiceUnavailable, error.Message);
        }
    }

    /// <summary>
    /// The body of a browser write, refusing anything that did not come from the dashboard on this machine. All failures
    /// carry their status through <see cref="OpsRequestException"/> so <see cref="Run"/> answers them as a 400.
    /// </summary>
    public static async Task<string> BodyAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        // The body of an acceptable size is read before any header check: answering with it unread resets the socket.
        var length = context.Request.ContentLength;
        if (length is not (> 0 and <= MaxBodyBytes))
        {
            throw new OpsRequestException(StatusCodes.Status400BadRequest, "Invalid request size");
        }

        using var reader = new StreamReader(context.Request.Body, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: true);
        var body = await reader.ReadToEndAsync(context.RequestAborted).ConfigureAwait(false);
        var host = context.Request.Host;
        if (!LoopbackHosts.Contains(host.Host.Trim('[', ']'), StringComparer.OrdinalIgnoreCase))
        {
            throw new OpsRequestException(StatusCodes.Status400BadRequest, "Writes are only accepted on a loopback address");
        }

        var origin = context.Request.Headers.Origin.ToString();
        if (origin.Length > 0 && !string.Equals(origin, $"http://{host.Value}", StringComparison.Ordinal))
        {
            throw new OpsRequestException(StatusCodes.Status400BadRequest, "Cross-origin writes are not allowed");
        }

        if (!string.Equals(context.Request.ContentType, "application/json", StringComparison.Ordinal))
        {
            throw new OpsRequestException(StatusCodes.Status400BadRequest, "Expected application/json");
        }

        return body;
    }

    /// <summary>A bounded integer query parameter; a malformed or out-of-range value is a 400, not a silent clamp.</summary>
    /// <param name="context">The request.</param>
    /// <param name="name">The parameter's name.</param>
    /// <param name="fallback">The value used when the parameter is absent or empty.</param>
    /// <param name="low">The smallest accepted value.</param>
    /// <param name="high">The largest accepted value.</param>
    public static int IntParam(HttpContext context, string name, int fallback, int low, int high)
    {
        ArgumentNullException.ThrowIfNull(context);
        var raw = context.Request.Query[name].LastOrDefault();
        if (string.IsNullOrEmpty(raw))
        {
            return fallback;
        }

        if (!int.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value))
        {
            throw new OpsRequestException(StatusCodes.Status400BadRequest, $"`{name}` must be an integer");
        }

        return value >= low && value <= high
            ? value
            : throw new OpsRequestException(StatusCodes.Status400BadRequest, $"`{name}` must be between {low} and {high}");
    }

    /// <summary>The last non-empty value of a query parameter, or null.</summary>
    public static string? Param(HttpContext context, string name)
    {
        ArgumentNullException.ThrowIfNull(context);
        var raw = context.Request.Query[name].LastOrDefault();
        return string.IsNullOrEmpty(raw) ? null : raw;
    }

    /// <summary>Whether a query flag is set (<c>1</c>, <c>true</c> or <c>yes</c>).</summary>
    public static bool Flag(HttpContext context, string name) =>
        Param(context, name) is "1" or "true" or "yes";

    private sealed record ErrorBody(string Error);
}
