using Rig.Core.Text;
using Rig.Core.Processes;
using System.Text.Json.Nodes;
using Rig.Core.WorkItems;

namespace Rig.Core.Tests.WorkItems;

/// <summary>The git facts a test chooses, with every call recorded.</summary>
internal sealed class FakeWorkGitFacts : IWorkGitFacts
{
    public IReadOnlyList<RepoFacts> Repos { get; init; } = [];

    public IReadOnlyDictionary<string, string> Slugs { get; init; } = new Dictionary<string, string>();

    public int EstateCalls { get; private set; }

    public int ProbeCalls { get; private set; }

    public List<string> SlugRepos { get; } = [];

    public string? Root { get; private set; }

    public bool Refresh { get; private set; }

    public Task<IReadOnlyList<RepoFacts>> ReadEstateAsync(string root, bool refresh, CancellationToken cancellationToken = default)
    {
        EstateCalls++;
        Root = root;
        Refresh = refresh;
        return Task.FromResult(Repos);
    }

    public Task<IReadOnlyList<RepoFacts>> ProbeCheckoutsAsync(IReadOnlyList<RepoFacts> repos, CancellationToken cancellationToken = default)
    {
        ProbeCalls++;
        return Task.FromResult<IReadOnlyList<RepoFacts>>(
            [.. repos.Select(repo => repo with { Checkouts = [.. repo.Checkouts.Select(checkout => checkout with { Dirty = true })] })]);
    }

    public Task<IReadOnlyDictionary<string, string>> ReadSlugsAsync(IReadOnlyList<string> repos, CancellationToken cancellationToken = default)
    {
        SlugRepos.AddRange(repos);
        return Task.FromResult(Slugs);
    }
}

/// <summary>The PR answers a test chooses, with every request recorded.</summary>
internal sealed class FakeWorkPrSource : IWorkPrSource
{
    public PrResults Results { get; init; } = PrResults.None();

    public List<PrRequest> Requests { get; } = [];

    public bool Refresh { get; private set; }

    public DateTimeOffset Now { get; private set; }

    public Task<PrResults> FetchAsync(
        IReadOnlyList<PrRequest> requests,
        bool refresh,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        Requests.AddRange(requests);
        Refresh = refresh;
        Now = now;
        return Task.FromResult(Results);
    }
}

/// <summary>The sessions and read window a test chooses.</summary>
internal sealed class FakeWorkSessions(IReadOnlyList<SessionFacts>? sessions = null) : IWorkSessionSource
{
    public IReadOnlyList<SessionFacts> Sessions { get; } = sessions ?? [];

    public DateTimeOffset? Since { get; private set; }

    public Task<SessionScan> ReadAsync(DateTimeOffset since, CancellationToken cancellationToken = default)
    {
        Since = since;
        return Task.FromResult(new SessionScan(Sessions, Sessions.Count));
    }
}

/// <summary>The directories a test says exist, and the outside folders it can attribute.</summary>
internal sealed class FakeWorkFolders : IWorkFolderProbe
{
    public HashSet<string> Existing { get; init; } = new(StringComparer.Ordinal);

    public Dictionary<string, Attribution?> Resolved { get; init; } = new(StringComparer.Ordinal);

    public List<string> ExistsCalls { get; } = [];

    public List<string> ResolveCalls { get; } = [];

    public bool Exists(string path)
    {
        ExistsCalls.Add(path);
        return Existing.Contains(path);
    }

    public Attribution? Resolve(string path)
    {
        ResolveCalls.Add(path);
        return Resolved.GetValueOrDefault(path);
    }
}

/// <summary>An <see cref="IGitHubApi"/> that answers GraphQL calls from a delegate, recording every request.</summary>
internal sealed class FakePrApi(Func<string, Ran> answer) : IGitHubApi
{
    public List<ApiRequest> Calls { get; } = [];

    public List<JsonObject> Bodies { get; } = [];

    private readonly object _gate = new();

    public Task<Ran> CallAsync(ApiRequest request, CancellationToken cancellationToken = default)
    {
        var body = JsonNode.Parse(request.Body!)!.AsObject();
        lock (_gate)
        {
            Calls.Add(request);
            Bodies.Add(body);
        }

        return Task.FromResult(answer(body["query"]!.GetValue<string>()));
    }
}
