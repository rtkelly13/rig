namespace Rig.Core.GitHub;

/// <summary>An error occurred when communicating with GitHub REST or GraphQL APIs.</summary>
public class GitHubApiException : Exception
{
    /// <summary>Creates a new exception with no message.</summary>
    public GitHubApiException()
    {
    }

    /// <summary>Creates a new exception with <paramref name="message"/>.</summary>
    public GitHubApiException(string message)
        : base(message)
    {
    }

    /// <summary>Creates a new exception with <paramref name="message"/> and <paramref name="innerException"/>.</summary>
    public GitHubApiException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>The process exit code if invoked via CLI/process runner, or null.</summary>
    public int? ExitCode { get; init; }

    /// <summary>The parsed HTTP status code (e.g. 404, 422, 500) if identifiable, or null.</summary>
    public int? StatusCode { get; init; }

    /// <summary>The route or operation being executed.</summary>
    public string? Route { get; init; }

    /// <summary>The raw error output from stderr or the response body.</summary>
    public string? ErrorOutput { get; init; }

    /// <summary>GraphQL error messages, if any.</summary>
    public IReadOnlyList<string> Errors { get; init; } = [];
}
