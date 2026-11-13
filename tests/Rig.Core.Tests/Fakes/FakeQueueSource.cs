using Rig.Core.ReviewQueue;

namespace Rig.Core.Tests.Fakes;

/// <summary>Stateful GitHub observations for triage's service contract.</summary>
public sealed class FakeQueueSource : IQueueSource
{
    public List<string> Repos { get; } = ["owner/repo"];
    public List<QueueItem> Items { get; } = [];
    public Dictionary<string, QueueItem> Observed { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, IReadOnlyList<QueueActivity>> Discussions { get; } = new(StringComparer.Ordinal);
    public List<string> Calls { get; } = [];
    public string? FailAt { get; set; }

    public IReadOnlyList<string> Repositories(string registry)
    {
        Call($"repositories:{registry}");
        return Repos;
    }

    public Task<IReadOnlyList<QueueItem>> FetchAsync(string repo, string kind, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Call($"fetch:{repo}:{kind}");
        return Task.FromResult<IReadOnlyList<QueueItem>>([.. Items.Where(item => item.Repo == repo && item.Kind == kind && item.State == "OPEN")]);
    }

    public Task<QueueItem> ObserveAsync(QueueItem item, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Call($"observe:{item.Reference}");
        return Task.FromResult(Observed.GetValueOrDefault(item.Reference, item));
    }

    public Task<IReadOnlyList<QueueActivity>> ActivityAsync(QueueItem item, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Call($"activity:{item.Reference}");
        return Task.FromResult(Discussions.GetValueOrDefault(item.Reference, []));
    }

    private void Call(string operation)
    {
        Calls.Add(operation);
        if (operation == FailAt)
        {
            throw new InvalidDataException("GitHub unavailable");
        }
    }
}
