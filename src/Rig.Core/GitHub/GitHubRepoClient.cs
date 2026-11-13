using Rig.Core.Processes;

namespace Rig.Core.GitHub;

/// <summary>A scoped client for executing REST and GraphQL operations on a specific repository.</summary>
/// <param name="api">The underlying GitHub API seam.</param>
/// <param name="repo">The repository slug.</param>
public sealed class GitHubRepoClient(IGitHubApi api, RepoSlug repo)
{
    /// <summary>The repository slug.</summary>
    public RepoSlug Repo { get; } = repo ?? throw new ArgumentNullException(nameof(repo));

    /// <summary>The underlying GitHub API seam.</summary>
    public IGitHubApi Api { get; } = api ?? throw new ArgumentNullException(nameof(api));

    /// <summary>Creates a GET request for a repository subroute (e.g. <c>rules/branches/main</c> or <c>labels</c>).</summary>
    public GitHubRestRequest Get(string subroute) =>
        Api.Rest("GET", $"repos/{Repo}/{Normalize(subroute)}");

    /// <summary>Creates a POST request for a repository subroute (e.g. <c>labels</c>).</summary>
    public GitHubRestRequest Post(string subroute) =>
        Api.Rest("POST", $"repos/{Repo}/{Normalize(subroute)}");

    /// <summary>Creates a PATCH request for a repository subroute.</summary>
    public GitHubRestRequest Patch(string subroute) =>
        Api.Rest("PATCH", $"repos/{Repo}/{Normalize(subroute)}");

    /// <summary>Creates a PUT request for a repository subroute.</summary>
    public GitHubRestRequest Put(string subroute) =>
        Api.Rest("PUT", $"repos/{Repo}/{Normalize(subroute)}");

    /// <summary>Creates a DELETE request for a repository subroute.</summary>
    public GitHubRestRequest Delete(string subroute) =>
        Api.Rest("DELETE", $"repos/{Repo}/{Normalize(subroute)}");

    /// <summary>
    /// Creates a GraphQL query pre-populated with <c>owner</c> and <c>name</c> variables matching this repo.
    /// </summary>
    public GitHubGraphQLRequest GraphQL(string query) =>
        Api.GraphQL(query)
            .WithVariable("owner", Repo.Owner)
            .WithVariable("name", Repo.Name);

    private static string Normalize(string subroute)
    {
        ArgumentNullException.ThrowIfNull(subroute);
        return subroute.TrimStart('/');
    }
}
