using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Rig.Core.Processes;

namespace Rig.Core.GitHub;

/// <summary>Pure functions and operations for interacting with GitHub REST and GraphQL APIs.</summary>
public static class GitHubFunctions
{
    /// <summary>Builds a full route including URL-encoded query parameters.</summary>
    public static string BuildRoute(string route, IReadOnlyDictionary<string, string>? queryParameters)
    {
        ArgumentNullException.ThrowIfNull(route);
        if (queryParameters is null || queryParameters.Count == 0)
        {
            return route;
        }

        var query = string.Join('&', queryParameters.Select(static kv =>
            $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));

        return route.Contains('?', StringComparison.Ordinal)
            ? $"{route}&{query}"
            : $"{route}?{query}";
    }

    /// <summary>Converts a <see cref="RestRequestSpec"/> into an <see cref="ApiRequest"/>.</summary>
    public static ApiRequest ToApiRequest(RestRequestSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        return new ApiRequest(spec.Method, BuildRoute(spec.Route, spec.QueryParameters), spec.Body)
        {
            Headers = spec.Headers,
        };
    }

    /// <summary>Converts a <see cref="GraphQLRequestSpec"/> into an <see cref="ApiRequest"/>.</summary>
    public static ApiRequest ToApiRequest(GraphQLRequestSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        return GraphQLRequest(spec.Query, spec.Variables, spec.OperationName);
    }

    /// <summary>Constructs an <see cref="ApiRequest"/> for a GraphQL query or mutation with variables.</summary>
    public static ApiRequest GraphQLRequest(
        string query,
        IReadOnlyDictionary<string, object?>? variables = null,
        string? operationName = null)
    {
        ArgumentNullException.ThrowIfNull(query);
        var present = variables?
            .Where(static pair => pair.Value is not null)
            .ToDictionary(static pair => pair.Key, static pair => pair.Value)
            ?? [];

        var body = new Dictionary<string, object?>
        {
            ["query"] = query,
            ["variables"] = present,
        };

        if (!string.IsNullOrEmpty(operationName))
        {
            body["operationName"] = operationName;
        }

        return new ApiRequest("POST", "graphql", JsonSerializer.Serialize(body, GitHubSerializerDefaults.Options));
    }

    /// <summary>Executes a REST request via <paramref name="api"/> without asserting success.</summary>
    public static Task<Ran> SendAsync(IGitHubApi api, RestRequestSpec spec, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(spec);
        return api.CallAsync(ToApiRequest(spec), cancellationToken);
    }

    /// <summary>Executes an <see cref="ApiRequest"/> via <paramref name="api"/> without asserting success.</summary>
    public static Task<Ran> SendAsync(IGitHubApi api, ApiRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(request);
        return api.CallAsync(request, cancellationToken);
    }

    /// <summary>Executes a REST request and throws <see cref="GitHubApiException"/> if the process or HTTP call failed.</summary>
    public static async Task<Ran> EnsureSuccessAsync(IGitHubApi api, RestRequestSpec spec, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var ran = await SendAsync(api, spec, cancellationToken).ConfigureAwait(false);
        if (!ran.Ok)
        {
            throw CreateException(ran, spec.Description, spec.Method, spec.Route);
        }

        return ran;
    }

    /// <summary>Executes an <see cref="ApiRequest"/> and throws <see cref="GitHubApiException"/> if the call failed.</summary>
    public static async Task<Ran> EnsureSuccessAsync(
        IGitHubApi api,
        ApiRequest request,
        string? description = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var ran = await SendAsync(api, request, cancellationToken).ConfigureAwait(false);
        if (!ran.Ok)
        {
            throw CreateException(ran, description, request.Method, request.Route);
        }

        return ran;
    }

    /// <summary>Executes a REST request, asserts success, and returns the response body as plain text.</summary>
    public static async Task<string> ReadTextAsync(IGitHubApi api, RestRequestSpec spec, CancellationToken cancellationToken = default)
    {
        var ran = await EnsureSuccessAsync(api, spec, cancellationToken).ConfigureAwait(false);
        return ran.Out;
    }

    /// <summary>Executes a REST request, asserts success, and deserializes the response JSON into <typeparamref name="T"/>.</summary>
    public static async Task<T> ReadJsonAsync<T>(
        IGitHubApi api,
        RestRequestSpec spec,
        JsonSerializerOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var ran = await EnsureSuccessAsync(api, spec, cancellationToken).ConfigureAwait(false);
        try
        {
            return JsonSerializer.Deserialize<T>(ran.Out, options ?? GitHubSerializerDefaults.Options)
                ?? throw new GitHubApiException(spec.Description ?? $"{spec.Method} {spec.Route}: unreadable response");
        }
        catch (JsonException ex)
        {
            throw new GitHubApiException(spec.Description ?? $"{spec.Method} {spec.Route}: unreadable JSON response", ex);
        }
    }

    /// <summary>Executes a REST request. Returns null if HTTP 404 (Not Found); deserializes JSON if successful; throws on other errors.</summary>
    public static async Task<T?> ReadOptionalJsonAsync<T>(
        IGitHubApi api,
        RestRequestSpec spec,
        JsonSerializerOptions? options = null,
        CancellationToken cancellationToken = default)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(spec);
        var ran = await SendAsync(api, spec, cancellationToken).ConfigureAwait(false);
        if (IsNotFound(ran))
        {
            return null;
        }

        if (!ran.Ok)
        {
            throw CreateException(ran, spec.Description, spec.Method, spec.Route);
        }

        try
        {
            return JsonSerializer.Deserialize<T>(ran.Out, options ?? GitHubSerializerDefaults.Options);
        }
        catch (JsonException ex)
        {
            throw new GitHubApiException(spec.Description ?? $"{spec.Method} {spec.Route}: unreadable JSON response", ex);
        }
    }

    /// <summary>Executes a REST request, asserts success, and parses the response as a <see cref="JsonNode"/>.</summary>
    public static async Task<JsonNode> ReadJsonNodeAsync(
        IGitHubApi api,
        RestRequestSpec spec,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var ran = await EnsureSuccessAsync(api, spec, cancellationToken).ConfigureAwait(false);
        try
        {
            return JsonNode.Parse(ran.Out) ?? throw new GitHubApiException(spec.Description ?? $"{spec.Method} {spec.Route}: unreadable JSON");
        }
        catch (JsonException ex)
        {
            throw new GitHubApiException(spec.Description ?? $"{spec.Method} {spec.Route}: unreadable JSON", ex);
        }
    }

    /// <summary>Executes a REST request. Returns null if HTTP 404; parses as <see cref="JsonNode"/> if successful; throws on other errors.</summary>
    public static async Task<JsonNode?> ReadOptionalJsonNodeAsync(
        IGitHubApi api,
        RestRequestSpec spec,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var ran = await SendAsync(api, spec, cancellationToken).ConfigureAwait(false);
        if (IsNotFound(ran))
        {
            return null;
        }

        if (!ran.Ok)
        {
            throw CreateException(ran, spec.Description, spec.Method, spec.Route);
        }

        try
        {
            return JsonNode.Parse(ran.Out);
        }
        catch (JsonException ex)
        {
            throw new GitHubApiException(spec.Description ?? $"{spec.Method} {spec.Route}: unreadable JSON", ex);
        }
    }

    /// <summary>Executes a GraphQL call returning the raw <see cref="Ran"/> result without decoding.</summary>
    public static Task<Ran> ExecuteGraphQLRawAsync(
        IGitHubApi api,
        GraphQLRequestSpec spec,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(spec);
        return api.CallAsync(ToApiRequest(spec), cancellationToken);
    }

    /// <summary>
    /// Executes a GraphQL query or mutation, decoding its <c>data</c> payload.
    /// Throws <see cref="GitHubApiException"/> on GraphQL errors, command execution failures, or decoding errors.
    /// </summary>
    public static async Task<TData> ExecuteGraphQLAsync<TData>(
        IGitHubApi api,
        GraphQLRequestSpec spec,
        JsonSerializerOptions? options = null,
        CancellationToken cancellationToken = default)
        where TData : class
    {
        ArgumentNullException.ThrowIfNull(spec);
        var ran = await ExecuteGraphQLRawAsync(api, spec, cancellationToken).ConfigureAwait(false);
        var what = spec.Description ?? "GraphQL query";
        var envelope = string.IsNullOrWhiteSpace(ran.Out) ? null : DecodeGraphQLEnvelope(ran.Out);
        var messages = envelope?.Errors?.Select(static error => error.Message).Where(static message => !string.IsNullOrEmpty(message)).ToList() ?? [];
        if (messages.Count > 0)
        {
            throw new GitHubApiException($"{what}: {string.Join("; ", messages)}")
            {
                ExitCode = ran.Code,
                Errors = messages,
                ErrorOutput = ran.Err,
            };
        }

        if (!ran.Ok)
        {
            var err = ran.Err.Trim();
            throw new GitHubApiException($"{what}: {(err.Length > 0 ? err : $"gh exited {ran.Code}")}")
            {
                ExitCode = ran.Code,
                ErrorOutput = err,
            };
        }

        if (envelope?.Data is { ValueKind: not JsonValueKind.Null and not JsonValueKind.Undefined } dataElement)
        {
            try
            {
                var decoded = JsonSerializer.Deserialize<TData>(dataElement.GetRawText(), options ?? GitHubSerializerDefaults.Options);
                if (decoded is not null)
                {
                    return decoded;
                }
            }
            catch (JsonException ex)
            {
                throw new GitHubApiException($"{what}: unreadable response from GitHub", ex)
                {
                    ExitCode = ran.Code,
                    ErrorOutput = ran.Err,
                };
            }
        }

        throw new GitHubApiException($"{what}: unreadable response from GitHub")
        {
            ExitCode = ran.Code,
            ErrorOutput = ran.Err,
        };
    }

    /// <summary>Convenience overload to execute a GraphQL document with variables.</summary>
    public static Task<TData> ExecuteGraphQLAsync<TData>(
        IGitHubApi api,
        string query,
        IReadOnlyDictionary<string, object?>? variables = null,
        string? operationName = null,
        string? description = null,
        JsonSerializerOptions? options = null,
        CancellationToken cancellationToken = default)
        where TData : class =>
        ExecuteGraphQLAsync<TData>(
            api,
            new GraphQLRequestSpec(query, variables, operationName, description),
            options,
            cancellationToken);

    /// <summary>
    /// Executes a GraphQL query or mutation, returning null if the resource was not found (HTTP 404 or GraphQL NOT_FOUND error),
    /// or decoding its <c>data</c> payload.
    /// </summary>
    public static async Task<TData?> ExecuteGraphQLOptionalAsync<TData>(
        IGitHubApi api,
        GraphQLRequestSpec spec,
        JsonSerializerOptions? options = null,
        CancellationToken cancellationToken = default)
        where TData : class
    {
        ArgumentNullException.ThrowIfNull(spec);
        var ran = await ExecuteGraphQLRawAsync(api, spec, cancellationToken).ConfigureAwait(false);
        if (IsNotFound(ran))
        {
            return null;
        }

        var what = spec.Description ?? "GraphQL query";
        var envelope = string.IsNullOrWhiteSpace(ran.Out) ? null : DecodeGraphQLEnvelope(ran.Out);

        if (IsGraphQLNotFound(envelope?.Errors))
        {
            return null;
        }

        var messages = envelope?.Errors?.Select(static error => error.Message).Where(static message => !string.IsNullOrEmpty(message)).ToList() ?? [];
        if (messages.Count > 0)
        {
            throw new GitHubApiException($"{what}: {string.Join("; ", messages)}")
            {
                ExitCode = ran.Code,
                Errors = messages,
                ErrorOutput = ran.Err,
            };
        }

        if (!ran.Ok)
        {
            var err = ran.Err.Trim();
            throw new GitHubApiException($"{what}: {(err.Length > 0 ? err : $"gh exited {ran.Code}")}")
            {
                ExitCode = ran.Code,
                ErrorOutput = err,
            };
        }

        if (envelope is null)
        {
            throw new GitHubApiException($"{what}: unreadable response from GitHub")
            {
                ExitCode = ran.Code,
                ErrorOutput = ran.Err,
            };
        }

        if (envelope.Data is null or { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined })
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<TData>(envelope.Data.Value.GetRawText(), options ?? GitHubSerializerDefaults.Options);
        }
        catch (JsonException ex)
        {
            throw new GitHubApiException($"{what}: unreadable response from GitHub", ex)
            {
                ExitCode = ran.Code,
                ErrorOutput = ran.Err,
            };
        }
    }

    /// <summary>Convenience overload to execute a GraphQL document with variables, returning null if not found.</summary>
    public static Task<TData?> ExecuteGraphQLOptionalAsync<TData>(
        IGitHubApi api,
        string query,
        IReadOnlyDictionary<string, object?>? variables = null,
        string? operationName = null,
        string? description = null,
        JsonSerializerOptions? options = null,
        CancellationToken cancellationToken = default)
        where TData : class =>
        ExecuteGraphQLOptionalAsync<TData>(
            api,
            new GraphQLRequestSpec(query, variables, operationName, description),
            options,
            cancellationToken);

    /// <summary>Determines whether <paramref name="ran"/> represents a 404 Not Found response.</summary>
    public static bool IsNotFound(Ran ran)
    {
        ArgumentNullException.ThrowIfNull(ran);
        return !ran.Ok && ran.Err.Contains("HTTP 404", StringComparison.Ordinal);
    }

    /// <summary>Returns a trimmed error message from <paramref name="ran"/>, or exit code description if empty.</summary>
    public static string Reason(Ran ran)
    {
        ArgumentNullException.ThrowIfNull(ran);
        return ran.Err.Trim() is { Length: > 0 } err ? err : $"exit {ran.Code}";
    }

    /// <summary>Constructs a <see cref="GitHubApiException"/> extracting HTTP status codes and command details.</summary>
    public static GitHubApiException CreateException(
        Ran ran,
        string? description = null,
        string? method = null,
        string? route = null)
    {
        ArgumentNullException.ThrowIfNull(ran);
        var err = ran.Err.Trim();
        var message = description is { Length: > 0 } desc
            ? $"{desc}: {(err.Length > 0 ? err : $"gh exited {ran.Code}")}"
            : $"{method ?? "HTTP"} {route ?? "request"}: {(err.Length > 0 ? err : $"gh exited {ran.Code}")}";

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
            Route = route,
            ErrorOutput = err,
        };
    }

    private static WireGraphQLEnvelope? DecodeGraphQLEnvelope(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<WireGraphQLEnvelope>(json, GitHubSerializerDefaults.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsGraphQLNotFound(IReadOnlyList<WireError>? errors)
    {
        if (errors is null || errors.Count == 0)
        {
            return false;
        }

        return errors.All(static e =>
            string.Equals(e.Type, "NOT_FOUND", StringComparison.OrdinalIgnoreCase) ||
            e.Message.Contains("Could not resolve to a", StringComparison.OrdinalIgnoreCase));
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes", Justification = "Instantiated by System.Text.Json.")]
    private sealed record WireError(string Message = "", string? Type = null);

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes", Justification = "Instantiated by System.Text.Json.")]
    private sealed record WireGraphQLEnvelope(JsonElement? Data = null, List<WireError>? Errors = null);
}
