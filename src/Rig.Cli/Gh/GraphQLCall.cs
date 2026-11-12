using System.Text.Json;
using Rig.Cli.Gh.Threads;
using Rig.Core.Processes;

namespace Rig.Cli.Gh;

/// <summary>One GraphQL call through <see cref="IGitHubApi"/>, decoded into <c>data</c> or thrown as a <see cref="GitHubException"/>.</summary>
internal static class GraphQLCall
{
    /// <summary>How GraphQL answers are read: camelCase names, and a missing required member fails the decode.</summary>
    public static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web)
    {
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
    };

    /// <summary>
    /// Sends <paramref name="query"/> and returns its <c>data</c>. GraphQL errors, a failed <c>gh</c> and an answer that
    /// does not decode all throw, each message starting with <paramref name="what"/>.
    /// </summary>
    public static async Task<TData> DataAsync<TData>(
        IGitHubApi api, string what, string query, IReadOnlyDictionary<string, object?> variables, CancellationToken cancellationToken)
        where TData : class
    {
        var ran = await api.CallAsync(ApiRequest.GraphQL(query, variables), cancellationToken).ConfigureAwait(false);
        var response = string.IsNullOrWhiteSpace(ran.Out) ? null : Decode<TData>(ran.Out);
        var messages = response?.Errors?.Select(error => error.Message).Where(message => !string.IsNullOrEmpty(message)).ToList() ?? [];
        if (messages.Count > 0)
        {
            throw new GitHubException($"{what}: {string.Join("; ", messages)}");
        }

        if (!ran.Ok)
        {
            var err = ran.Err.Trim();
            throw new GitHubException($"{what}: {(err.Length > 0 ? err : $"gh exited {ran.Code}")}");
        }

        return response?.Data ?? throw new GitHubException($"{what}: unreadable response from GitHub");
    }

    private static Response<TData>? Decode<TData>(string json)
        where TData : class
    {
        try
        {
            return JsonSerializer.Deserialize<Response<TData>>(json, Wire);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record WireError(string Message = "");

    private sealed record Response<TData>(TData? Data = null, List<WireError>? Errors = null)
        where TData : class;
}
