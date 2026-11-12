using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Rig.Core.Processes;

namespace Rig.Core.GitHub;

/// <summary>A fluent request builder for GitHub REST API calls.</summary>
/// <param name="Api">The underlying GitHub API seam.</param>
/// <param name="Method">The HTTP method (GET, POST, PATCH, PUT, DELETE).</param>
/// <param name="Route">The API route without query parameters (or base route).</param>
public sealed record GitHubRestRequest(IGitHubApi Api, string Method, string Route)
{
    /// <summary>Extra request headers, each as <c>Name: value</c>.</summary>
    public IReadOnlyList<string> Headers { get; init; } = [];

    /// <summary>Query parameters to append to the route.</summary>
    public IReadOnlyDictionary<string, string> QueryParameters { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>The raw request body, if any.</summary>
    public string? Body { get; init; }

    /// <summary>A human-readable description of the operation for error reporting.</summary>
    public string? Description { get; init; }

    /// <summary>Adds a header formatted as <paramref name="name"/>: <paramref name="value"/>.</summary>
    public GitHubRestRequest WithHeader(string name, string value)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(value);
        return this with { Headers = [.. Headers, $"{name}: {value}"] };
    }

    /// <summary>Adds a preformatted header string (<c>Name: value</c>).</summary>
    public GitHubRestRequest WithHeader(string header)
    {
        ArgumentNullException.ThrowIfNull(header);
        return this with { Headers = [.. Headers, header] };
    }

    /// <summary>Adds multiple preformatted headers.</summary>
    public GitHubRestRequest WithHeaders(IEnumerable<string> headers)
    {
        ArgumentNullException.ThrowIfNull(headers);
        return this with { Headers = [.. Headers, .. headers] };
    }

    /// <summary>Adds a query parameter. A null value is ignored.</summary>
    public GitHubRestRequest WithQuery(string key, object? value)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (value is null)
        {
            return this;
        }

        var formatted = value switch
        {
            bool b => b ? "true" : "false",
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty,
        };

        var updated = new Dictionary<string, string>(QueryParameters, StringComparer.Ordinal)
        {
            [key] = formatted,
        };
        return this with { QueryParameters = updated };
    }

    /// <summary>Adds multiple query parameters.</summary>
    public GitHubRestRequest WithQueryParameters(IReadOnlyDictionary<string, object?> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var updated = new Dictionary<string, string>(QueryParameters, StringComparer.Ordinal);
        foreach (var (key, value) in parameters)
        {
            if (value is null)
            {
                continue;
            }

            var formatted = value switch
            {
                bool b => b ? "true" : "false",
                IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
                _ => value.ToString() ?? string.Empty,
            };
            updated[key] = formatted;
        }

        return this with { QueryParameters = updated };
    }

    /// <summary>Sets a raw string request body.</summary>
    public GitHubRestRequest WithBody(string body) => this with { Body = body };

    /// <summary>Serializes <paramref name="payload"/> to JSON and sets it as the request body.</summary>
    public GitHubRestRequest WithJsonBody<T>(T payload, JsonSerializerOptions? options = null) =>
        this with { Body = JsonSerializer.Serialize(payload, options ?? GitHubSerializerDefaults.Options) };

    /// <summary>Sets a human-readable description of the operation for error reporting.</summary>
    public GitHubRestRequest Describing(string description) => this with { Description = description };

    /// <summary>Builds the full route including encoded query parameters.</summary>
    public string BuildRoute()
    {
        if (QueryParameters.Count == 0)
        {
            return Route;
        }

        var query = string.Join('&', QueryParameters.Select(static kv =>
            $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));

        return Route.Contains('?', StringComparison.Ordinal)
            ? $"{Route}&{query}"
            : $"{Route}?{query}";
    }

    /// <summary>Converts this fluent request into an <see cref="ApiRequest"/>.</summary>
    public ApiRequest ToApiRequest() => new(Method, BuildRoute(), Body)
    {
        Headers = Headers,
    };

    /// <summary>Executes the REST request without asserting success, returning the raw <see cref="Ran"/> result.</summary>
    public Task<Ran> SendAsync(CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(Api);
        return Api.CallAsync(ToApiRequest(), cancellationToken);
    }

    /// <summary>Executes the REST request and throws <see cref="GitHubApiException"/> if the process or HTTP call failed.</summary>
    public async Task<Ran> EnsureSuccessAsync(CancellationToken cancellationToken = default)
    {
        var ran = await SendAsync(cancellationToken).ConfigureAwait(false);
        if (!ran.Ok)
        {
            throw CreateException(ran);
        }

        return ran;
    }

    /// <summary>Executes the REST request, asserts success, and returns the response body as plain text.</summary>
    public async Task<string> ReadTextAsync(CancellationToken cancellationToken = default)
    {
        var ran = await EnsureSuccessAsync(cancellationToken).ConfigureAwait(false);
        return ran.Out;
    }

    /// <summary>Executes the REST request, asserts success, and deserializes the response JSON into <typeparamref name="T"/>.</summary>
    public async Task<T> ReadJsonAsync<T>(JsonSerializerOptions? options = null, CancellationToken cancellationToken = default)
    {
        var ran = await EnsureSuccessAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return JsonSerializer.Deserialize<T>(ran.Out, options ?? GitHubSerializerDefaults.Options)
                ?? throw new GitHubApiException(Description ?? $"{Method} {Route}: unreadable response");
        }
        catch (JsonException ex)
        {
            throw new GitHubApiException(Description ?? $"{Method} {Route}: unreadable JSON response", ex);
        }
    }

    /// <summary>Executes the REST request. Returns null if HTTP 404 (Not Found); deserializes JSON if successful; throws on other errors.</summary>
    public async Task<T?> ReadOptionalJsonAsync<T>(JsonSerializerOptions? options = null, CancellationToken cancellationToken = default)
        where T : class
    {
        var ran = await SendAsync(cancellationToken).ConfigureAwait(false);
        if (IsNotFound(ran))
        {
            return null;
        }

        if (!ran.Ok)
        {
            throw CreateException(ran);
        }

        try
        {
            return JsonSerializer.Deserialize<T>(ran.Out, options ?? GitHubSerializerDefaults.Options);
        }
        catch (JsonException ex)
        {
            throw new GitHubApiException(Description ?? $"{Method} {Route}: unreadable JSON response", ex);
        }
    }

    /// <summary>Executes the REST request, asserts success, and parses the response as a <see cref="JsonNode"/>.</summary>
    public async Task<JsonNode> ReadJsonNodeAsync(CancellationToken cancellationToken = default)
    {
        var ran = await EnsureSuccessAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return JsonNode.Parse(ran.Out) ?? throw new GitHubApiException(Description ?? $"{Method} {Route}: unreadable JSON");
        }
        catch (JsonException ex)
        {
            throw new GitHubApiException(Description ?? $"{Method} {Route}: unreadable JSON", ex);
        }
    }

    /// <summary>Executes the REST request. Returns null if HTTP 404; parses as <see cref="JsonNode"/> if successful; throws on other errors.</summary>
    public async Task<JsonNode?> ReadOptionalJsonNodeAsync(CancellationToken cancellationToken = default)
    {
        var ran = await SendAsync(cancellationToken).ConfigureAwait(false);
        if (IsNotFound(ran))
        {
            return null;
        }

        if (!ran.Ok)
        {
            throw CreateException(ran);
        }

        try
        {
            return JsonNode.Parse(ran.Out);
        }
        catch (JsonException ex)
        {
            throw new GitHubApiException(Description ?? $"{Method} {Route}: unreadable JSON", ex);
        }
    }

    /// <summary>Determines whether <paramref name="ran"/> represents a 404 Not Found response.</summary>
    public static bool IsNotFound(Ran ran)
    {
        ArgumentNullException.ThrowIfNull(ran);
        return ran.Err.Contains("HTTP 404", StringComparison.Ordinal)
            || ran.Err.Contains("Not Found", StringComparison.Ordinal)
            || ran.Out.Contains("Not Found", StringComparison.Ordinal);
    }

    private GitHubApiException CreateException(Ran ran)
    {
        var err = ran.Err.Trim();
        var message = Description is { Length: > 0 } desc
            ? $"{desc}: {(err.Length > 0 ? err : $"gh exited {ran.Code}")}"
            : $"{Method} {Route}: {(err.Length > 0 ? err : $"gh exited {ran.Code}")}";

        int? status = null;
        var idx = ran.Err.IndexOf("HTTP ", StringComparison.Ordinal);
        if (idx >= 0 && idx + 8 <= ran.Err.Length && int.TryParse(ran.Err.AsSpan(idx + 5, 3), CultureInfo.InvariantCulture, out var parsed))
        {
            status = parsed;
        }

        return new GitHubApiException(message)
        {
            ExitCode = ran.Code,
            StatusCode = status,
            Route = Route,
            ErrorOutput = err,
        };
    }
}
