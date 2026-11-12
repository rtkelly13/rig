using System.Text.Json;
using Rig.Core.Processes;

namespace Rig.Core.GitHub;

/// <summary>A fluent request builder for GitHub GraphQL API calls.</summary>
/// <param name="Api">The underlying GitHub API seam.</param>
/// <param name="Query">The GraphQL query or mutation document.</param>
public sealed record GitHubGraphQLRequest(IGitHubApi Api, string Query)
{
    /// <summary>The GraphQL variables dictionary.</summary>
    public IReadOnlyDictionary<string, object?> Variables { get; init; } = new Dictionary<string, object?>(StringComparer.Ordinal);

    /// <summary>A human-readable description of the operation for error reporting.</summary>
    public string? Description { get; init; }

    /// <summary>The optional operation name.</summary>
    public string? OperationName { get; init; }

    /// <summary>Adds or replaces a variable.</summary>
    public GitHubGraphQLRequest WithVariable(string name, object? value)
    {
        ArgumentNullException.ThrowIfNull(name);
        var updated = new Dictionary<string, object?>(Variables, StringComparer.Ordinal)
        {
            [name] = value,
        };
        return this with { Variables = updated };
    }

    /// <summary>Adds or replaces multiple variables.</summary>
    public GitHubGraphQLRequest WithVariables(IReadOnlyDictionary<string, object?> variables)
    {
        ArgumentNullException.ThrowIfNull(variables);
        var updated = new Dictionary<string, object?>(Variables, StringComparer.Ordinal);
        foreach (var (key, value) in variables)
        {
            updated[key] = value;
        }

        return this with { Variables = updated };
    }

    /// <summary>Sets the operation name.</summary>
    public GitHubGraphQLRequest WithOperationName(string operationName) => this with { OperationName = operationName };

    /// <summary>Sets a human-readable description of the operation for error reporting.</summary>
    public GitHubGraphQLRequest Describing(string description) => this with { Description = description };

    /// <summary>Converts this request into an <see cref="ApiRequest"/>.</summary>
    public ApiRequest ToApiRequest()
    {
        var present = Variables.Where(static pair => pair.Value is not null).ToDictionary(static pair => pair.Key, static pair => pair.Value);
        var body = new Dictionary<string, object?>
        {
            ["query"] = Query,
            ["variables"] = present,
        };

        if (!string.IsNullOrEmpty(OperationName))
        {
            body["operationName"] = OperationName;
        }

        return new ApiRequest("POST", "graphql", JsonSerializer.Serialize(body));
    }

    /// <summary>Executes the GraphQL call returning the raw <see cref="Ran"/> result without decoding.</summary>
    public Task<Ran> ExecuteRawAsync(CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(Api);
        return Api.CallAsync(ToApiRequest(), cancellationToken);
    }

    /// <summary>
    /// Executes the GraphQL query or mutation, decoding its <c>data</c> payload.
    /// Throws <see cref="GitHubApiException"/> on GraphQL errors, command execution failures, or decoding errors.
    /// </summary>
    public async Task<TData> ExecuteAsync<TData>(JsonSerializerOptions? options = null, CancellationToken cancellationToken = default)
        where TData : class
    {
        var ran = await ExecuteRawAsync(cancellationToken).ConfigureAwait(false);
        var what = Description ?? "GraphQL query";
        var response = string.IsNullOrWhiteSpace(ran.Out) ? null : Decode<TData>(ran.Out, options ?? GitHubSerializerDefaults.Options);
        var messages = response?.Errors?.Select(static error => error.Message).Where(static message => !string.IsNullOrEmpty(message)).ToList() ?? [];
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

        return response?.Data ?? throw new GitHubApiException($"{what}: unreadable response from GitHub")
        {
            ExitCode = ran.Code,
            ErrorOutput = ran.Err,
        };
    }

    private static Response<TData>? Decode<TData>(string json, JsonSerializerOptions options)
        where TData : class
    {
        try
        {
            return JsonSerializer.Deserialize<Response<TData>>(json, options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes", Justification = "Instantiated by System.Text.Json.")]
    private sealed record WireError(string Message = "");

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes", Justification = "Instantiated by System.Text.Json.")]
    private sealed record Response<TData>(TData? Data = null, List<WireError>? Errors = null)
        where TData : class;
}
