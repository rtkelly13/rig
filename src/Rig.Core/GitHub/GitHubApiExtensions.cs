using Rig.Core.Processes;

namespace Rig.Core.GitHub;

/// <summary>Fluent extension methods on <see cref="IGitHubApi"/>.</summary>
public static class GitHubApiExtensions
{
    /// <summary>Creates a REST request with method <paramref name="method"/> and route <paramref name="route"/>.</summary>
    public static GitHubRestRequest Rest(this IGitHubApi api, string method, string route)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(route);
        return new GitHubRestRequest(api, method, route);
    }

    /// <summary>Creates a GET request for <paramref name="route"/>.</summary>
    public static GitHubRestRequest Get(this IGitHubApi api, string route) =>
        api.Rest("GET", route);

    /// <summary>Creates a POST request for <paramref name="route"/>.</summary>
    public static GitHubRestRequest Post(this IGitHubApi api, string route) =>
        api.Rest("POST", route);

    /// <summary>Creates a PATCH request for <paramref name="route"/>.</summary>
    public static GitHubRestRequest Patch(this IGitHubApi api, string route) =>
        api.Rest("PATCH", route);

    /// <summary>Creates a PUT request for <paramref name="route"/>.</summary>
    public static GitHubRestRequest Put(this IGitHubApi api, string route) =>
        api.Rest("PUT", route);

    /// <summary>Creates a DELETE request for <paramref name="route"/>.</summary>
    public static GitHubRestRequest Delete(this IGitHubApi api, string route) =>
        api.Rest("DELETE", route);

    /// <summary>Creates a GraphQL request with query document <paramref name="query"/>.</summary>
    public static GitHubGraphQLRequest GraphQL(this IGitHubApi api, string query)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(query);
        return new GitHubGraphQLRequest(api, query);
    }

    /// <summary>Creates a scoped repository client for <paramref name="repo"/>.</summary>
    public static GitHubRepoClient ForRepo(this IGitHubApi api, RepoSlug repo)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(repo);
        return new GitHubRepoClient(api, repo);
    }

    /// <summary>Creates a scoped repository client for <paramref name="owner"/>/<paramref name="name"/>.</summary>
    public static GitHubRepoClient ForRepo(this IGitHubApi api, string owner, string name)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(name);
        return new GitHubRepoClient(api, new RepoSlug(owner, name));
    }
}
