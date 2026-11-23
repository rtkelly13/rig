using Rig.Core.GitHub;

namespace Rig.Cli.Gh;

/// <summary>A GraphQL call failed or answered with something this module cannot read.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1032", Justification = "Always carries the message the command prints.")]
internal sealed class GitHubException : GitHubApiException
{
    public GitHubException(string message)
        : base(message)
    {
    }

    public GitHubException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
