using System.Text.Json;
using Rig.Core.ReviewQueue;

namespace Rig.Web.Ops;

/// <summary>One inbox entry as the SPA receives it.</summary>
/// <param name="Item">Its latest snapshot.</param>
/// <param name="Events">Its append-only history.</param>
/// <param name="Agent">The wrap-up verdict of the worktree on its branch, or null.</param>
public sealed record TriageEntry(QueueItem Item, IReadOnlyList<QueueEvent> Events, AgentSignal? Agent);

/// <summary>A selected item to observe once, as the SPA's poll asks.</summary>
/// <param name="Repo">Repository slug.</param>
/// <param name="Number">PR or issue number.</param>
public sealed record TriageWatch(string Repo, int Number);

/// <summary>
/// The persistent triage queue behind <c>/api/triage</c>: local snapshots and history from SQLite, with the agent's
/// wrap-up verdict attached.
/// </summary>
public sealed class TriageInbox : IDisposable
{
    private readonly QueueStore _store;
    private readonly QueueService _service;
    private readonly AgentSignals _signals;

    /// <summary>Builds the queue over its store, its GitHub source and the wrap-up scanner.</summary>
    /// <param name="dbPath">The shared rig SQLite database.</param>
    /// <param name="source">Observed GitHub state.</param>
    /// <param name="queuePath">The local, regenerable Markdown projection.</param>
    /// <param name="signals">The wrap-up verdicts to attach; it is disposed with the queue.</param>
    public TriageInbox(
        string dbPath,
        IQueueSource source,
        string queuePath,
        AgentSignals signals)
    {
        ArgumentException.ThrowIfNullOrEmpty(dbPath);
        ArgumentException.ThrowIfNullOrEmpty(queuePath);
        QueuePath = queuePath;
        _store = new QueueStore(dbPath);
        _service = new QueueService(_store, source, queuePath);
        _signals = signals;
    }

    /// <summary>The Markdown projection the queue is exported to.</summary>
    public string QueuePath { get; }

    /// <summary>Every tracked item, in the versioned priority/readiness/size/age order.</summary>
    public async Task<IReadOnlyList<TriageEntry>> EntriesAsync(CancellationToken cancellationToken = default)
    {
        var found = await SignalsAsync(cancellationToken).ConfigureAwait(false);
        return Rows(_store.Read(), found);
    }

    /// <summary>Refreshes every managed repository, then answers the queue again.</summary>
    /// <param name="registry">The managed-projects registry.</param>
    /// <param name="cancellationToken">Cancels the GitHub reads.</param>
    public async Task<IReadOnlyList<TriageEntry>> RefreshAsync(string registry, CancellationToken cancellationToken = default)
    {
        var entries = await _service.RefreshAsync(registry, cancellationToken).ConfigureAwait(false);
        return Rows(entries, await SignalsAsync(cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Observes one selected item once, then answers the queue again.</summary>
    /// <param name="repo">Repository slug.</param>
    /// <param name="number">PR or issue number.</param>
    /// <param name="cancellationToken">Cancels the GitHub reads.</param>
    public async Task<IReadOnlyList<TriageEntry>> WatchAsync(string repo, int number, CancellationToken cancellationToken = default)
    {
        _ = await _service.WatchAsync(repo, number, cancellationToken).ConfigureAwait(false);
        return await EntriesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Records a local decision, then answers the queue again.</summary>
    /// <param name="request">The decision.</param>
    /// <param name="cancellationToken">Cancels the observation a review makes first.</param>
    public async Task<IReadOnlyList<TriageEntry>> RecordAsync(QueueInteraction request, CancellationToken cancellationToken = default)
    {
        await _service.RecordAsync(request, cancellationToken).ConfigureAwait(false);
        return await EntriesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _service.Dispose();
        _signals.Dispose();
    }

    // A failed scan leaves every verdict null rather than failing the inbox.
    private async Task<IReadOnlyDictionary<AgentKey, AgentSignal>> SignalsAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _signals.AllAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return new Dictionary<AgentKey, AgentSignal>();
        }
    }

    private static IReadOnlyList<TriageEntry> Rows(
        IReadOnlyList<QueueEntry> entries,
        IReadOnlyDictionary<AgentKey, AgentSignal> signals)
    {
        var byReference = entries.ToDictionary(entry => entry.Item.Reference, StringComparer.Ordinal);
        return
        [
            .. QueueOrder.Sort(entries.Select(entry => entry.Item)).Select(item => Row(byReference[item.Reference], signals)),
        ];
    }

    private static TriageEntry Row(QueueEntry entry, IReadOnlyDictionary<AgentKey, AgentSignal> signals)
    {
        var item = entry.Item;
        var branch = item is { Kind: "pr", Facts: { CrossRepository: false } facts } ? facts.Head : string.Empty;
        return new TriageEntry(item, entry.Events, AgentSignals.ForPr(signals, item.Repo, branch));
    }

    /// <summary>Decodes a watch request, refusing an item number that cannot name one.</summary>
    public static TriageWatch Watch(string body, JsonSerializerOptions options)
    {
        var request = JsonSerializer.Deserialize<TriageWatch>(body, options) ?? throw new ArgumentException("Invalid triage watch request");
        return request.Number > 0 ? request : throw new ArgumentException("Invalid triage watch request");
    }
}
