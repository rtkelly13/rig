namespace Rig.Core.GitHub;

/// <summary>Pure data specification of a GitHub GraphQL request.</summary>
/// <param name="Query">The GraphQL query or mutation document.</param>
/// <param name="Variables">The GraphQL variables dictionary.</param>
/// <param name="OperationName">The optional operation name.</param>
/// <param name="Description">Human-readable description for error reporting.</param>
public sealed record GraphQLRequestSpec(
    string Query,
    IReadOnlyDictionary<string, object?>? Variables = null,
    string? OperationName = null,
    string? Description = null)
{
    /// <summary>The GraphQL variables dictionary.</summary>
    public IReadOnlyDictionary<string, object?> Variables { get; init; } = Variables ?? new Dictionary<string, object?>(StringComparer.Ordinal);
}
