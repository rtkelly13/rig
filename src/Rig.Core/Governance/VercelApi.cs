using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Rig.Core.Processes;

namespace Rig.Core.Governance;

/// <summary>A Vercel API call that failed: no token, a transport error, a non-2xx status or a body that is not JSON.</summary>
public sealed class VercelApiException : Exception
{
    /// <summary>A failure with no message.</summary>
    public VercelApiException()
    {
    }

    /// <summary>A failure described by <paramref name="message"/>.</summary>
    public VercelApiException(string message)
        : base(message)
    {
    }

    /// <summary>A failure described by <paramref name="message"/>, caused by <paramref name="innerException"/>.</summary>
    public VercelApiException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The Vercel REST seam: one authenticated GET, its JSON body back.</summary>
public interface IVercelApi
{
    /// <summary>GETs <paramref name="path"/> with <paramref name="query"/>; throws <see cref="VercelApiException"/> when it fails.</summary>
    Task<JsonNode?> GetAsync(string path, IReadOnlyList<KeyValuePair<string, string>> query, CancellationToken cancellationToken = default);
}

/// <summary>
/// The real Vercel API over <paramref name="http"/>. The token is resolved on the first call, so a command that never
/// reaches Vercel never reads the CLI's credentials. Ported from <c>vercel_gating._get</c>.
/// </summary>
/// <param name="http">The client; its timeout bounds each call.</param>
/// <param name="token">Resolves the bearer token, or throws <see cref="VercelApiException"/>.</param>
public sealed class VercelApi(HttpClient http, Func<CancellationToken, Task<string>> token) : IVercelApi
{
    /// <summary>Where the API lives.</summary>
    public const string Root = "https://api.vercel.com";

    private string? resolved;

    /// <inheritdoc />
    public async Task<JsonNode?> GetAsync(string path, IReadOnlyList<KeyValuePair<string, string>> query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        resolved ??= await token(cancellationToken).ConfigureAwait(false);
        var search = string.Join("&", query.Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"{Root}{path}{(search.Length > 0 ? "?" + search : string.Empty)}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", resolved);
        request.Headers.UserAgent.ParseAdd("shared-utilities-repo-governance");
        try
        {
            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new VercelApiException(string.Create(CultureInfo.InvariantCulture, $"Vercel {path} answered HTTP {(int)response.StatusCode}"));
            }

            return JsonNode.Parse(body);
        }
        catch (HttpRequestException e)
        {
            throw new VercelApiException($"Vercel {path} failed: {e.Message}", e);
        }
        catch (TaskCanceledException e) when (!cancellationToken.IsCancellationRequested)
        {
            throw new VercelApiException($"Vercel {path} timed out", e);
        }
        catch (JsonException e)
        {
            throw new VercelApiException($"Vercel {path} returned a body that is not JSON", e);
        }
    }
}

/// <summary>The Vercel CLI's auth file: its token and, for a rotating OAuth token, when that expires.</summary>
/// <param name="Token">The token, or null when the file has none.</param>
/// <param name="ExpiresAt">Its expiry, in epoch seconds or milliseconds; null when it does not expire.</param>
public sealed record VercelCliAuth(string? Token, double? ExpiresAt);

/// <summary>
/// The token the Vercel API is called with: <c>VERCEL_TOKEN</c>, else the Vercel CLI's own, refreshed through the CLI
/// when it has expired. Never refreshes the OAuth grant itself and never writes the CLI's state. Ported from
/// <c>vercel_gating.vercel_token</c>.
/// </summary>
/// <param name="environment">Reads an environment variable.</param>
/// <param name="home">The home folder the CLI's auth file lives under.</param>
/// <param name="proc">Runs <c>vercel whoami</c>, which rotates an expired token.</param>
/// <param name="time">The clock expiry is judged by.</param>
public sealed class VercelToken(Func<string, string?> environment, string home, IProc proc, TimeProvider time)
{
    // Where the Vercel CLI persists its OAuth token, in preference order. The CLI is the credential; requiring a
    // second, hand-copied token is how config drifts stale.
    private static readonly string[][] CliAuthFiles =
    [
        ["Library", "Application Support", "com.vercel.cli", "auth.json"],
        [".config", "com.vercel.cli", "auth.json"],
    ];

    /// <summary>
    /// Whether <paramref name="entry"/>'s token is usable at <paramref name="nowSeconds"/>: no expiry, or one more than
    /// a minute away. The CLI has written the expiry in seconds and in milliseconds; past 1e12 it can only be the latter.
    /// </summary>
    public static bool IsLive(VercelCliAuth entry, double nowSeconds)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.ExpiresAt is not { } expiresAt || expiresAt <= 0)
        {
            return true;
        }

        var expiresSeconds = expiresAt > 1e12 ? expiresAt / 1000 : expiresAt;
        return expiresSeconds > nowSeconds + 60;
    }

    /// <summary>The auth file at <paramref name="path"/>, or null when it is unreadable or not a JSON object.</summary>
    public static VercelCliAuth? Read(string path)
    {
        try
        {
            if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject file)
            {
                return null;
            }

            var token = file["token"] is JsonValue t && t.GetValueKind() == JsonValueKind.String ? t.GetValue<string>() : null;
            var expires = file["expiresAt"] is JsonValue e && e.GetValueKind() == JsonValueKind.Number ? e.GetValue<double>() : (double?)null;
            return new(token, expires);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>The token to call Vercel with; throws <see cref="VercelApiException"/> when there is none.</summary>
    public async Task<string> ResolveAsync(CancellationToken cancellationToken = default)
    {
        if (environment("VERCEL_TOKEN") is { Length: > 0 } fromEnvironment)
        {
            return fromEnvironment;
        }

        var nowSeconds = time.GetUtcNow().ToUnixTimeMilliseconds() / 1000.0;
        foreach (var file in CliAuthFiles.Select(parts => Path.Join([home, .. parts])))
        {
            var entry = Read(file) ?? new(null, null);
            if (entry.Token is null)
            {
                continue;
            }

            if (!IsLive(entry, nowSeconds))
            {
                if (await RefreshAsync(file, cancellationToken).ConfigureAwait(false) is { } refreshed)
                {
                    return refreshed;
                }

                continue;
            }

            return entry.Token;
        }

        throw new VercelApiException("no usable Vercel token: `vercel login` or set VERCEL_TOKEN");
    }

    // Lets the CLI rotate its own token, then re-reads it; null when the CLI is missing or wrote no token.
    private async Task<string?> RefreshAsync(string file, CancellationToken cancellationToken)
    {
        var ran = await proc.RunAsync(new ProcRequest(["vercel", "whoami"]) { Timeout = TimeSpan.FromSeconds(60) }, cancellationToken).ConfigureAwait(false);
        return ran.Code == Ran.NotFound ? null : Read(file)?.Token is { Length: > 0 } token ? token : null;
    }
}
