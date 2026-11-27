namespace Rig.Core.ReviewQueue;

/// <summary>Serialized triage operations, shared by the CLI and web UI.</summary>
/// <param name="store">Local snapshots and history.</param>
/// <param name="source">Observed GitHub state.</param>
/// <param name="output">The local, regenerable Markdown queue.</param>
public sealed class QueueService(QueueStore store, IQueueSource source, string output) : IDisposable
{
    private readonly SemaphoreSlim _serial = new(1, 1);

    /// <inheritdoc/>
    public void Dispose() => _serial.Dispose();

    /// <summary>
    /// Fetch every observation before committing; a failed GitHub read leaves snapshots and Markdown alone. A repository
    /// GitHub answers as not found is skipped and reported instead, and its items leave scope with their history kept.
    /// When none resolve, the login is more likely wrong than every repository gone, so that fails too.
    /// </summary>
    public async Task<QueueRefresh> RefreshAsync(string registry, CancellationToken cancellationToken = default)
    {
        await _serial.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Initialize the database before observing, as the previous command did.
            store.Read();
            var managed = source.Repositories(registry);
            var fetched = new List<(string Repo, IReadOnlyList<QueueItem>? Items)>();
            foreach (var repo in managed)
            {
                fetched.Add((repo, await FetchAllAsync(repo, cancellationToken).ConfigureAwait(false)));
            }

            var skipped = fetched.Where(each => each.Items is null).Select(each => each.Repo).ToList();
            var items = fetched.SelectMany(each => each.Items ?? []).ToList();

            if (skipped.Count == managed.Count)
            {
                throw new InvalidDataException($"No managed repository resolves on GitHub; check the gh login: {string.Join(", ", skipped)}");
            }

            var repos = managed.Except(skipped, StringComparer.Ordinal).ToList();

            var found = items.Select(item => item.Reference).ToHashSet(StringComparer.Ordinal);
            var previous = store.Read();
            foreach (var entry in previous)
            {
                if (!repos.Contains(entry.Item.Repo, StringComparer.Ordinal) && entry.Item.InScope)
                {
                    items.Add(entry.Item with { InScope = false });
                }
                else if (repos.Contains(entry.Item.Repo, StringComparer.Ordinal) && entry.Item.State == "OPEN" && !found.Contains(entry.Item.Reference))
                {
                    items.Add(await source.ObserveAsync(entry.Item, cancellationToken).ConfigureAwait(false));
                }
            }

            var retained = previous.Where(entry => entry.Events.Any(e => e.Action is "archived" or "reviewed" or "changes_requested"))
                .Select(entry => entry.Item.Reference).ToHashSet(StringComparer.Ordinal);
            var discussions = new List<DiscussionImport>();
            foreach (var item in items.Where(item => item.InScope && retained.Contains(item.Reference)))
            {
                discussions.Add(new DiscussionImport(item, await source.ActivityAsync(item, cancellationToken).ConfigureAwait(false)));
            }

            cancellationToken.ThrowIfCancellationRequested();
            store.Update(writer =>
            {
                writer.Save(items);
                foreach (var discussion in discussions)
                {
                    writer.ImportActivity(discussion.Item, discussion.Activity);
                }
            });
            return new QueueRefresh(Project(), skipped);
        }
        finally
        {
            _serial.Release();
        }
    }

    /// <summary>One bounded observation of a selected item; callers own the polling lifecycle.</summary>
    public async Task<QueueItem> WatchAsync(string repo, int number, CancellationToken cancellationToken = default)
    {
        await _serial.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var previous = store.Read().FirstOrDefault(entry => entry.Item.Repo == repo && entry.Item.Number == number)?.Item
                ?? throw new ArgumentException("Unknown triage item");
            var item = await source.ObserveAsync(previous, cancellationToken).ConfigureAwait(false);
            var activity = await source.ActivityAsync(item, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            store.Update(writer =>
            {
                writer.Save([item]);
                writer.ImportActivity(item, activity);
            });
            Project();
            return item;
        }
        finally
        {
            _serial.Release();
        }
    }

    /// <summary>Reviews first observe and project the live head. A stale decision is refused after retaining that observation.</summary>
    public async Task RecordAsync(QueueInteraction request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        await _serial.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (request.Action is "reviewed" or "changes_requested")
            {
                var previous = store.Read().FirstOrDefault(entry => entry.Item.Repo == request.Repo && entry.Item.Number == request.Number)?.Item
                    ?? throw new ArgumentException("Unknown triage item");
                var observed = await source.ObserveAsync(previous, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                store.Update(writer => writer.Save([observed]));
                Project();
            }

            cancellationToken.ThrowIfCancellationRequested();
            store.Update(writer => writer.Interact(request));
            Project();
        }
        finally
        {
            _serial.Release();
        }
    }

    // Every kind of one repository, or null when GitHub says the repository does not resolve.
    private async Task<IReadOnlyList<QueueItem>?> FetchAllAsync(string repo, CancellationToken cancellationToken)
    {
        try
        {
            var pulls = await source.FetchAsync(repo, "pr", cancellationToken).ConfigureAwait(false);
            var issues = await source.FetchAsync(repo, "issue", cancellationToken).ConfigureAwait(false);
            return [.. pulls, .. issues];
        }
        catch (MissingRepositoryException)
        {
            return null;
        }
    }

    private IReadOnlyList<QueueEntry> Project()
    {
        IReadOnlyList<QueueEntry> result = [];
        store.Update(writer =>
        {
            result = writer.Read();
            QueueProjection.Export(output, result);
        });
        return result;
    }

    private sealed record DiscussionImport(QueueItem Item, IReadOnlyList<QueueActivity> Activity);
}
