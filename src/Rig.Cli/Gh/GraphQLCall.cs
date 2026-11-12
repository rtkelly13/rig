using System.Text.Json;
using Rig.Cli.Gh.Threads;
using Rig.Core.GitHub;
using Rig.Core.Processes;

namespace Rig.Cli.Gh;

/// <summary>One GraphQL call through <see cref="IGitHubApi"/>, decoded into <c>data</c> or thrown as a <see cref="GitHubException"/>.</summary>
internal static class GraphQLCall
{
    /// <summary>How GraphQL answers are read: camelCase names, and a missing required member fails the decode.</summary>
    public static readonly JsonSerializerOptions Wire = GitHubSerializerDefaults.Options;

    /// <summary>
    /// Sends <paramref name="query"/> and returns its <c>data</c>. GraphQL errors, a failed <c>gh</c> and an answer that
    /// does not decode all throw, each message starting with <paramref name="what"/>.
    /// </summary>
    public static async Task<TData> DataAsync<TData>(
        IGitHubApi api, string what, string query, IReadOnlyDictionary<string, object?> variables, CancellationToken cancellationToken)
        where TData : class
    {
        try
        {
            return await api.GraphQL(query)
                .Describing(what)
                .WithVariables(variables)
                .ExecuteAsync<TData>(Wire, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (GitHubApiException ex) when (ex is not GitHubException)
        {
            throw new GitHubException(ex.Message, ex);
        }
    }
}
