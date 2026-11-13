namespace Rig.Core.ReviewQueue;

/// <summary>Observed GitHub state for triage, separated from local queue storage and decisions.</summary>
public interface IQueueSource
{
    /// <summary>Enabled, nonarchived managed repositories; malformed or empty registries fail.</summary>
    IReadOnlyList<string> Repositories(string registry);

    /// <summary>All open items of one kind, following every GitHub page.</summary>
    Task<IReadOnlyList<QueueItem>> FetchAsync(string repo, string kind, CancellationToken cancellationToken);

    /// <summary>A fresh, detailed snapshot, retaining the item's local scope flag.</summary>
    Task<QueueItem> ObserveAsync(QueueItem item, CancellationToken cancellationToken);

    /// <summary>Discussion versions, including reviews and inline comments.</summary>
    Task<IReadOnlyList<QueueActivity>> ActivityAsync(QueueItem item, CancellationToken cancellationToken);
}
