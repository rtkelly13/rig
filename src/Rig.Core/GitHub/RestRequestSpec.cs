namespace Rig.Core.GitHub;

/// <summary>Pure data specification of a GitHub REST API request.</summary>
/// <param name="Method">The HTTP method (GET, POST, PATCH, PUT, DELETE).</param>
/// <param name="Route">The route, optionally before query string concatenation.</param>
/// <param name="Body">The request body string, or null.</param>
/// <param name="Headers">Extra request headers formatted as <c>Name: value</c>.</param>
/// <param name="QueryParameters">Query parameters to append to the route.</param>
/// <param name="Description">Human-readable description for error reporting.</param>
public sealed record RestRequestSpec(
    string Method,
    string Route,
    string? Body = null,
    IReadOnlyList<string>? Headers = null,
    IReadOnlyDictionary<string, string>? QueryParameters = null,
    string? Description = null)
{
    /// <summary>Extra request headers formatted as <c>Name: value</c>.</summary>
    public IReadOnlyList<string> Headers { get; init; } = Headers ?? [];

    /// <summary>Query parameters to append to the route.</summary>
    public IReadOnlyDictionary<string, string> QueryParameters { get; init; } = QueryParameters ?? new Dictionary<string, string>(StringComparer.Ordinal);
}
