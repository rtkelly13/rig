namespace Rig.Core.ReviewQueue;

/// <summary>The queue after a refresh, and the managed repositories GitHub could not resolve.</summary>
/// <param name="Entries">Every tracked item, in the versioned order.</param>
/// <param name="Skipped">Repositories GitHub answered as not found; their items left scope and keep their history.</param>
public sealed record QueueRefresh(IReadOnlyList<QueueEntry> Entries, IReadOnlyList<string> Skipped);
