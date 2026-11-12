using System.Text.Json;
using Rig.Core.Processes;

namespace Rig.Core.GitHub;

/// <summary>A thin fluent wrapper over <see cref="GraphQLRequestSpec"/> and <see cref="GitHubFunctions"/>.</summary>
/// <param name="Api">The underlying GitHub API seam.</param>
/// <param name="Spec">The request data specification.</param>
public sealed record GitHubGraphQLRequest(IGitHubApi Api, GraphQLRequestSpec Spec)
{
    /// <summary>Creates a fluent GraphQL request wrapper for <paramref name="query"/>.</summary>
    public GitHubGraphQLRequest(IGitHubApi api, string query)
        : this(api, new GraphQLRequestSpec(query))
    {
    }

    /// <summary>The GraphQL query document.</summary>
    public string Query => Spec.Query;

    /// <summary>The GraphQL variables dictionary.</summary>
    public IReadOnlyDictionary<string, object?> Variables => Spec.Variables;

    /// <summary>Human-readable description for error reporting.</summary>
    public string? Description => Spec.Description;

    /// <summary>The optional operation name.</summary>
    public string? OperationName => Spec.OperationName;

    /// <summary>Adds or replaces a variable.</summary>
    public GitHubGraphQLRequest WithVariable(string name, object? value)
    {
        ArgumentNullException.ThrowIfNull(name);
        var updated = new Dictionary<string, object?>(Spec.Variables, StringComparer.Ordinal)
        {
            [name] = value,
        };
        return this with { Spec = Spec with { Variables = updated } };
    }

    /// <summary>Adds or replaces multiple variables.</summary>
    public GitHubGraphQLRequest WithVariables(IReadOnlyDictionary<string, object?> variables)
    {
        ArgumentNullException.ThrowIfNull(variables);
        var updated = new Dictionary<string, object?>(Spec.Variables, StringComparer.Ordinal);
        foreach (var (key, value) in variables)
        {
            updated[key] = value;
        }

        return this with { Spec = Spec with { Variables = updated } };
    }

    /// <summary>Sets the operation name.</summary>
    public GitHubGraphQLRequest WithOperationName(string operationName) =>
        this with { Spec = Spec with { OperationName = operationName } };

    /// <summary>Sets a human-readable description of the operation for error reporting.</summary>
    public GitHubGraphQLRequest Describing(string description) =>
        this with { Spec = Spec with { Description = description } };

    /// <summary>Converts this request into an <see cref="ApiRequest"/>.</summary>
    public ApiRequest ToApiRequest() => GitHubFunctions.ToApiRequest(Spec);

    /// <summary>Executes the GraphQL call returning the raw <see cref="Ran"/> result without decoding.</summary>
    public Task<Ran> ExecuteRawAsync(CancellationToken cancellationToken = default) =>
        GitHubFunctions.ExecuteGraphQLRawAsync(Api, Spec, cancellationToken);

    /// <summary>
    /// Executes the GraphQL query or mutation, decoding its <c>data</c> payload.
    /// Throws <see cref="GitHubApiException"/> on GraphQL errors, command execution failures, or decoding errors.
    /// </summary>
    public Task<TData> ExecuteAsync<TData>(JsonSerializerOptions? options = null, CancellationToken cancellationToken = default)
        where TData : class =>
        GitHubFunctions.ExecuteGraphQLAsync<TData>(Api, Spec, options, cancellationToken);
}
