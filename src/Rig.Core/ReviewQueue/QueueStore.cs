using System.Globalization;
using Microsoft.Data.Sqlite;
using Rig.Core.Storage;

namespace Rig.Core.ReviewQueue;

/// <summary>Snapshots and append-only history in the existing rig database.</summary>
/// <param name="path">Database file, never a live machine default inside the adapter.</param>
/// <param name="clock">Clock for observed and local events.</param>
public sealed class QueueStore(string path, TimeProvider? clock = null)
{
    private static readonly string[] Kinds = ["pr", "issue"];
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>Read all items, including closed, archived and deferred history, in one consistent snapshot.</summary>
    public IReadOnlyList<QueueEntry> Read()
    {
        using var connection = RigDatabase.Open(path, _clock);
        using var transaction = connection.BeginTransaction(deferred: true);
        var result = new QueueWriter(connection, transaction, _clock).Read();
        transaction.Commit();
        return result;
    }

    /// <summary>Run an update under the database's writer lock; any exception rolls back the entire update.</summary>
    public void Update(Action<QueueWriter> update)
    {
        using var connection = RigDatabase.Open(path, _clock);
        using var transaction = connection.BeginTransaction(deferred: false);
        update(new QueueWriter(connection, transaction, _clock));
        transaction.Commit();
    }

    /// <summary>PRs precede the issue queue; inactive items retain their database history.</summary>
    public static IReadOnlyList<QueueEntry> Active(IEnumerable<QueueEntry> entries)
    {
        var active = entries.Where(entry => entry.Active).ToDictionary(entry => entry.Item.Reference, StringComparer.Ordinal);
        return [.. Kinds.SelectMany(kind => QueueOrder.Sort(active.Values.Where(entry => entry.Item.Kind == kind).Select(entry => entry.Item))
            .Select(item => active[item.Reference]))];
    }
}

/// <summary>A transaction-scoped triage writer; its owning store commits or rolls back after the callback.</summary>
public sealed class QueueWriter
{
    private readonly SqliteConnection _connection;
    private readonly SqliteTransaction _transaction;
    private readonly TimeProvider _clock;

    internal QueueWriter(SqliteConnection connection, SqliteTransaction transaction, TimeProvider clock)
    {
        _connection = connection;
        _transaction = transaction;
        _clock = clock;
    }

    /// <summary>Read every snapshot and its insertion-ordered events inside this transaction.</summary>
    public IReadOnlyList<QueueEntry> Read()
    {
        var items = new List<QueueItem>();
        using (var command = Command("SELECT payload FROM triage_items ORDER BY repo, number"))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                items.Add(QueueJson.DecodeItem(reader.GetString(0)));
            }
        }

        return [.. items.Select(item => new QueueEntry(item, Events(item)))];
    }

    /// <summary>Record an observation only when its snapshot changes; never deletes history.</summary>
    public void Save(IEnumerable<QueueItem> items)
    {
        foreach (var item in items)
        {
            var payload = QueueJson.EncodeItem(item);
            if (Payload(item.Repo, item.Number) is { } stored && QueueJson.SameSnapshot(stored, payload))
            {
                continue;
            }

            using var command = Command("INSERT INTO triage_items(repo, number, payload) VALUES ($repo, $number, $payload) "
                + "ON CONFLICT(repo, number) DO UPDATE SET payload = excluded.payload",
                ("$repo", item.Repo), ("$number", item.Number), ("$payload", payload));
            command.ExecuteNonQuery();
            AddEvent(item.Repo, item.Number, Now(), "observed", item.Head, payload, "rig", null);
        }
    }

    /// <summary>Reject stale heads and reviews of closed items before appending a local decision.</summary>
    public void Interact(QueueInteraction request)
    {
        request.Validate();
        var payload = Payload(request.Repo, request.Number) ?? throw new ArgumentException("Item is not in the triage database; refresh first");
        var item = QueueJson.DecodeItem(payload);
        if (request.Head != item.Head)
        {
            throw new ArgumentException("The PR head changed; refresh and review the new commit");
        }

        if (request.Action is "reviewed" or "changes_requested" && item.State != "OPEN")
        {
            throw new ArgumentException("This item is already merged or closed");
        }

        AddEvent(request.Repo, request.Number, Now(), request.Action, request.Head, request.Note, "local", null);
    }

    /// <summary>Deduplicate remote discussion versions across polls.</summary>
    public void ImportActivity(QueueItem item, IEnumerable<QueueActivity> activity)
    {
        foreach (var e in activity)
        {
            AddEvent(item.Repo, item.Number, e.At, e.Action, e.Head, e.Note, e.Actor, e.Key);
        }
    }

    private List<QueueEvent> Events(QueueItem item)
    {
        using var command = Command("SELECT id, at, action, head, note, actor FROM triage_events WHERE repo = $repo AND number = $number ORDER BY id",
            ("$repo", item.Repo), ("$number", item.Number));
        using var reader = command.ExecuteReader();
        var events = new List<QueueEvent>();
        while (reader.Read())
        {
            events.Add(new QueueEvent(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5)));
        }

        return events;
    }

    private string? Payload(string repo, int number)
    {
        using var command = Command("SELECT payload FROM triage_items WHERE repo = $repo AND number = $number", ("$repo", repo), ("$number", number));
        return command.ExecuteScalar() as string;
    }

    private string Now() => _clock.GetUtcNow().ToString("yyyy-MM-dd'T'HH:mm:ss.ffffffzzz", CultureInfo.InvariantCulture);

    private void AddEvent(string repo, int number, string at, string action, string head, string note, string actor, string? key)
    {
        using var command = Command("INSERT OR IGNORE INTO triage_events(repo, number, at, action, head, note, actor, source_key) "
            + "VALUES ($repo, $number, $at, $action, $head, $note, $actor, $key)",
            ("$repo", repo), ("$number", number), ("$at", at), ("$action", action), ("$head", head), ("$note", note), ("$actor", actor), ("$key", key));
        command.ExecuteNonQuery();
    }

    private SqliteCommand Command(string sql, params (string Key, object? Value)[] parameters)
    {
        var command = _connection.CreateCommand();
        command.Transaction = _transaction;
        // All call sites are private, fixed SQL statements; observed values are bound below.
#pragma warning disable CA2100
        command.CommandText = sql;
#pragma warning restore CA2100
        foreach (var (key, value) in parameters)
        {
            command.Parameters.AddWithValue(key, value ?? DBNull.Value);
        }

        return command;
    }
}
