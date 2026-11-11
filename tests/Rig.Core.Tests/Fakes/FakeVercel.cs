using System.Text.Json.Nodes;
using Rig.Core.Governance;

namespace Rig.Core.Tests.Fakes;

/// <summary>
/// A Vercel API that answers by path, and optionally by query parameters: the latest matching answer wins, and an
/// unanswered call fails as an HTTP 404 would. Every call is recorded.
/// </summary>
public sealed class FakeVercel : IVercelApi
{
    private readonly List<(string Path, KeyValuePair<string, string>[] Where, string Json)> _answers = [];

    /// <summary>Every call, as its path and query.</summary>
    public List<(string Path, IReadOnlyList<KeyValuePair<string, string>> Query)> Calls { get; } = [];

    /// <summary>Answers <paramref name="path"/> with <paramref name="json"/> when the query holds every pair in <paramref name="where"/>.</summary>
    public FakeVercel On(string path, string json, params (string Key, string Value)[] where)
    {
        _answers.Add((path, [.. where.Select(pair => new KeyValuePair<string, string>(pair.Key, pair.Value))], json));
        return this;
    }

    /// <inheritdoc />
    public Task<JsonNode?> GetAsync(string path, IReadOnlyList<KeyValuePair<string, string>> query, CancellationToken cancellationToken = default)
    {
        Calls.Add((path, query));
        for (var i = _answers.Count - 1; i >= 0; i--)
        {
            var (answered, where, json) = _answers[i];
            if (answered == path && where.All(query.Contains))
            {
                return Task.FromResult(JsonNode.Parse(json));
            }
        }

        throw new VercelApiException($"Vercel {path} answered HTTP 404");
    }
}
