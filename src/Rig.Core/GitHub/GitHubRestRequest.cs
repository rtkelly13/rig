using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Rig.Core.Processes;

namespace Rig.Core.GitHub;

/// <summary>A thin fluent wrapper over <see cref="RestRequestSpec"/> and <see cref="GitHubFunctions"/>.</summary>
/// <param name="Api">The underlying GitHub API seam.</param>
/// <param name="Spec">The request data specification.</param>
public sealed record GitHubRestRequest(IGitHubApi Api, RestRequestSpec Spec)
{
    /// <summary>Creates a fluent REST request wrapper for <paramref name="method"/> and <paramref name="route"/>.</summary>
    public GitHubRestRequest(IGitHubApi api, string method, string route)
        : this(api, new RestRequestSpec(method, route))
    {
    }

    /// <summary>The HTTP method.</summary>
    public string Method => Spec.Method;

    /// <summary>The target route.</summary>
    public string Route => Spec.Route;

    /// <summary>Extra request headers.</summary>
    public IReadOnlyList<string> Headers => Spec.Headers;

    /// <summary>Query parameters to append.</summary>
    public IReadOnlyDictionary<string, string> QueryParameters => Spec.QueryParameters;

    /// <summary>The raw request body.</summary>
    public string? Body => Spec.Body;

    /// <summary>Human-readable description for error reporting.</summary>
    public string? Description => Spec.Description;

    /// <summary>Adds a header formatted as <paramref name="name"/>: <paramref name="value"/>.</summary>
    public GitHubRestRequest WithHeader(string name, string value)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(value);
        return this with { Spec = Spec with { Headers = [.. Spec.Headers, $"{name}: {value}"] } };
    }

    /// <summary>Adds a preformatted header string (<c>Name: value</c>).</summary>
    public GitHubRestRequest WithHeader(string header)
    {
        ArgumentNullException.ThrowIfNull(header);
        return this with { Spec = Spec with { Headers = [.. Spec.Headers, header] } };
    }

    /// <summary>Adds multiple preformatted headers.</summary>
    public GitHubRestRequest WithHeaders(IEnumerable<string> headers)
    {
        ArgumentNullException.ThrowIfNull(headers);
        return this with { Spec = Spec with { Headers = [.. Spec.Headers, .. headers] } };
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

        var updated = new Dictionary<string, string>(Spec.QueryParameters, StringComparer.Ordinal)
        {
            [key] = formatted,
        };
        return this with { Spec = Spec with { QueryParameters = updated } };
    }

    /// <summary>Adds multiple query parameters.</summary>
    public GitHubRestRequest WithQueryParameters(IReadOnlyDictionary<string, object?> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var updated = new Dictionary<string, string>(Spec.QueryParameters, StringComparer.Ordinal);
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

        return this with { Spec = Spec with { QueryParameters = updated } };
    }

    /// <summary>Sets a raw string request body.</summary>
    public GitHubRestRequest WithBody(string body) =>
        this with { Spec = Spec with { Body = body } };

    /// <summary>Serializes <paramref name="payload"/> to JSON and sets it as the request body.</summary>
    public GitHubRestRequest WithJsonBody<T>(T payload, JsonSerializerOptions? options = null) =>
        this with { Spec = Spec with { Body = JsonSerializer.Serialize(payload, options ?? GitHubSerializerDefaults.Options) } };

    /// <summary>Sets a human-readable description of the operation for error reporting.</summary>
    public GitHubRestRequest Describing(string description) =>
        this with { Spec = Spec with { Description = description } };

    /// <summary>Builds the full route including encoded query parameters.</summary>
    public string BuildRoute() => GitHubFunctions.BuildRoute(Spec.Route, Spec.QueryParameters);

    /// <summary>Converts this request into an <see cref="ApiRequest"/>.</summary>
    public ApiRequest ToApiRequest() => GitHubFunctions.ToApiRequest(Spec);

    /// <summary>Executes the REST request without asserting success, returning the raw <see cref="Ran"/> result.</summary>
    public Task<Ran> SendAsync(CancellationToken cancellationToken = default) =>
        GitHubFunctions.SendAsync(Api, Spec, cancellationToken);

    /// <summary>Executes the REST request and throws <see cref="GitHubApiException"/> if the call failed.</summary>
    public Task<Ran> EnsureSuccessAsync(CancellationToken cancellationToken = default) =>
        GitHubFunctions.EnsureSuccessAsync(Api, Spec, cancellationToken);

    /// <summary>Executes the REST request, asserts success, and returns the response body as plain text.</summary>
    public Task<string> ReadTextAsync(CancellationToken cancellationToken = default) =>
        GitHubFunctions.ReadTextAsync(Api, Spec, cancellationToken);

    /// <summary>Executes the REST request, asserts success, and deserializes the response JSON into <typeparamref name="T"/>.</summary>
    public Task<T> ReadJsonAsync<T>(JsonSerializerOptions? options = null, CancellationToken cancellationToken = default) =>
        GitHubFunctions.ReadJsonAsync<T>(Api, Spec, options, cancellationToken);

    /// <summary>Executes the REST request. Returns null if HTTP 404 (Not Found); deserializes JSON if successful; throws on other errors.</summary>
    public Task<T?> ReadOptionalJsonAsync<T>(JsonSerializerOptions? options = null, CancellationToken cancellationToken = default)
        where T : class =>
        GitHubFunctions.ReadOptionalJsonAsync<T>(Api, Spec, options, cancellationToken);

    /// <summary>Executes the REST request, asserts success, and parses the response as a <see cref="JsonNode"/>.</summary>
    public Task<JsonNode> ReadJsonNodeAsync(CancellationToken cancellationToken = default) =>
        GitHubFunctions.ReadJsonNodeAsync(Api, Spec, cancellationToken);

    /// <summary>Executes the REST request. Returns null if HTTP 404; parses as <see cref="JsonNode"/> if successful; throws on other errors.</summary>
    public Task<JsonNode?> ReadOptionalJsonNodeAsync(CancellationToken cancellationToken = default) =>
        GitHubFunctions.ReadOptionalJsonNodeAsync(Api, Spec, cancellationToken);

    /// <summary>Determines whether <paramref name="ran"/> represents a 404 Not Found response.</summary>
    public static bool IsNotFound(Ran ran) => GitHubFunctions.IsNotFound(ran);
}
