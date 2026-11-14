using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Rig.Core.ReviewQueue;
using Rig.Core.Storage;
using Rig.Core.Tests.Otel;

namespace Rig.Core.Tests.ReviewQueue;

public sealed class QueueStoreTests : IDisposable
{
    private readonly TempDir _temp = new();
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 11, 13, 9, 30, 5, TimeSpan.Zero));
    private string Db => Path.Join(_temp.Path, "nested", "rig.db");
    private QueueStore Store => new(Db, _clock);

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Reads_a_python_database_without_changing_snapshots_history_or_unrelated_data()
    {
        using var oracle = PythonDatabase();
        var entries = Store.Read();
        var recorded = oracle.RootElement.GetProperty("entry");
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(recorded.GetRawText()), JsonSerializer.SerializeToNode(entries[0], QueueJson.Options)));
        Assert.Null(entries[1].Item.Body);
        Assert.Null(entries[1].Item.Facts);
        Assert.Equal(oracle.RootElement.GetProperty("payload").GetString(), QueueJson.EncodeItem(entries[0].Item));
        Store.Update(writer => writer.Save([entries[0].Item]));
        Assert.Equal(3, Store.Read()[0].Events.Count);
        Assert.Equal(4L, Scalar("SELECT COUNT(*) FROM schema_migrations"));
        Assert.Equal(1L, Scalar("SELECT enabled FROM feature_flags WHERE name='legacy-flag'"));
        Assert.Equal("legacy-job", Scalar("SELECT job_name FROM job_runs"));
    }

    [Fact]
    public void History_survives_updates_deferrals_discussion_edits_and_merge()
    {
        var item = Item(1);
        Store.Update(writer =>
        {
            writer.Save([item]);
            writer.Interact(new QueueInteraction(item.Repo, 1, "reviewed", item.Head, "Looks good"));
            writer.Interact(new QueueInteraction(item.Repo, 1, "deferred", item.Head, "Later"));
            writer.Save([item with { Head = new string('b', 40) }]);
        });
        Assert.Empty(QueueStore.Active(Store.Read()));
        var error = Assert.Throws<ArgumentException>(() => Store.Update(writer => writer.Interact(new QueueInteraction(item.Repo, 1, "reviewed", item.Head))));
        Assert.Contains("head changed", error.Message, StringComparison.Ordinal);
        Store.Update(writer => writer.Interact(new QueueInteraction(item.Repo, 1, "resumed", new string('b', 40))));
        Assert.Single(QueueStore.Active(Store.Read()));
        var discussion = new QueueActivity("comment/1", "2026-01-02T00:00:00Z", "reviewer", "github:approved", item.Head, "LGTM");
        Store.Update(writer =>
        {
            writer.ImportActivity(item, [discussion, discussion]);
            writer.Save([item with { Head = new string('b', 40), State = "MERGED" }]);
            writer.Save([item with { Head = new string('b', 40), State = "MERGED" }]);
        });
        var saved = Assert.Single(Store.Read());
        Assert.Empty(QueueStore.Active([saved]));
        Assert.Equal(7, saved.Events.Count);
        Assert.Equal("Looks good", saved.Events[1].Note);
        Assert.Single(saved.Events, e => e.Actor == "reviewer");
        Assert.Equal("2026-11-13T09:30:05.000000+00:00", saved.Events[0].At);
        Store.Update(writer => writer.ImportActivity(item, [discussion with { Key = "comment/1/edited", Note = "Edited" }]));
        Assert.Equal("Edited", Store.Read()[0].Events[^1].Note);
    }

    [Fact]
    public void Failed_updates_roll_back_both_snapshot_and_event_writes()
    {
        Store.Update(writer => writer.Save([Item(1)]));
        Assert.Throws<InvalidOperationException>(() => Store.Update(writer =>
        {
            writer.Save([Item(1) with { Title = "uncommitted" }, Item(2)]);
            throw new InvalidOperationException("failed refresh");
        }));
        var entry = Assert.Single(Store.Read());
        Assert.Equal("PR", entry.Item.Title);
        Assert.Single(entry.Events);
    }

    [Theory]
    [InlineData("reviewed")]
    [InlineData("changes_requested")]
    public void Closed_items_refuse_review_decisions_but_retain_local_notes(string action)
    {
        var item = Item(1) with { State = "CLOSED" };
        Store.Update(writer => writer.Save([item]));
        Assert.Throws<ArgumentException>(() => Store.Update(writer => writer.Interact(new QueueInteraction(item.Repo, 1, action, item.Head))));
        Store.Update(writer => writer.Interact(new QueueInteraction(item.Repo, 1, "note", item.Head, "Still useful")));
        Assert.Equal(2, Store.Read()[0].Events.Count);
    }

    [Fact]
    public void Unknown_items_and_invalid_interactions_never_append_events()
    {
        Assert.Throws<ArgumentException>(() => Store.Update(writer => writer.Interact(new QueueInteraction("owner/repo", 1, "note", ""))));
        Assert.Throws<ArgumentException>(() => new QueueInteraction("owner/repo", 0, "note", "").Validate());
        Assert.Throws<ArgumentException>(() => new QueueInteraction("owner/repo", 1, "unsupported", "").Validate());
        Assert.Throws<ArgumentException>(() => new QueueInteraction("owner/repo", 1, "note", "", new string('x', 10001)).Validate());
        new QueueInteraction("owner/repo", 1, "note", "", string.Concat(Enumerable.Repeat("😀", 10000))).Validate();
        Assert.Empty(Store.Read());
    }

    [Fact]
    public void Local_archive_survives_bot_activity_but_not_human_activity_or_a_new_head()
    {
        var item = Item(1);
        Store.Update(writer =>
        {
            writer.Save([item]);
            writer.Interact(new QueueInteraction(item.Repo, 1, "archived", item.Head));
        });
        Assert.True(Store.Read()[0].Archived);
        Assert.Empty(QueueStore.Active(Store.Read()));
        var activity = new QueueActivity("bot/1", "2099-01-01T00:00:00Z", "ci[bot]", "github:commented", "", "CI");
        Store.Update(writer => writer.ImportActivity(item, [activity]));
        Assert.True(Store.Read()[0].Archived);
        Store.Update(writer => writer.Save([item with { Head = new string('b', 40) }]));
        Assert.False(Store.Read()[0].Archived);
        Store.Update(writer => writer.Interact(new QueueInteraction(item.Repo, 1, "archived", new string('b', 40))));
        Assert.True(Store.Read()[0].Archived);
        Store.Update(writer => writer.ImportActivity(item, [activity with { Key = "human/1", Actor = "reviewer" }]));
        Assert.False(Store.Read()[0].Archived);
        Assert.Single(QueueStore.Active(Store.Read()));
    }

    [Fact]
    public void Unarchive_and_resume_are_last_decisions_and_prs_precede_issues()
    {
        var item = Item(3);
        Store.Update(writer =>
        {
            writer.Save([item, Item(2) with { Kind = "issue", Priority = 0 }, Item(1), Item(4) with { InScope = false }]);
            writer.Interact(new QueueInteraction(item.Repo, 3, "archived", item.Head));
            writer.Interact(new QueueInteraction(item.Repo, 3, "unarchived", item.Head));
            writer.Interact(new QueueInteraction(item.Repo, 3, "deferred", item.Head));
            writer.Interact(new QueueInteraction(item.Repo, 3, "resumed", item.Head));
        });
        Assert.Equal([1, 3, 2], QueueStore.Active(Store.Read()).Select(entry => entry.Item.Number));
        Assert.Equal(4, Store.Read().Count);
    }

    [Fact]
    public void Migration_name_conflicts_refuse_the_database_without_rewriting_history()
    {
        using var oracle = PythonDatabase();
        Execute("UPDATE schema_migrations SET name='002_conflicting_branch' WHERE version=2");
        var error = Assert.Throws<InvalidDataException>(() => Store.Read());
        Assert.Contains("002_triage", error.Message, StringComparison.Ordinal);
        Assert.Equal("002_conflicting_branch", Scalar("SELECT name FROM schema_migrations WHERE version=2"));
        Assert.Equal(3L, Scalar("SELECT COUNT(*) FROM triage_events"));
    }

    [Fact]
    public void Json_requires_snapshot_fields_but_retains_backward_compatible_defaults()
    {
        Assert.Throws<JsonException>(() => QueueJson.DecodeItem("{}"));
        Assert.Throws<JsonException>(() => QueueJson.DecodeItem("null"));
        var payload = QueueJson.EncodeItem(Item(1));
        Assert.Throws<JsonException>(() => QueueJson.DecodeItem(payload.Replace("\"kind\": \"pr\"", "\"kind\": \"other\"", StringComparison.Ordinal)));
        Assert.Throws<JsonException>(() => QueueJson.DecodeItem(payload.Replace("\"repo\": \"owner/repo\"", "\"repo\": null", StringComparison.Ordinal)));
    }

    [Fact]
    public void A_future_database_is_not_downgraded_and_foreign_keys_remain_enforced()
    {
        Store.Read();
        Execute("INSERT INTO schema_migrations VALUES (99, '099_future', '2026-01-01T00:00:00Z'); PRAGMA user_version=99;");
        Assert.Empty(Store.Read());
        Assert.Equal(99L, Scalar("PRAGMA user_version"));
        var activity = new QueueActivity("unknown", "2026-01-01T00:00:00Z", "me", "github:commented", "", "");
        Assert.Throws<SqliteException>(() => Store.Update(writer => writer.ImportActivity(Item(1), [activity])));
    }

    private JsonDocument PythonDatabase()
    {
        var oracle = JsonDocument.Parse(File.ReadAllText(Path.Join(AppContext.BaseDirectory, "ReviewQueue", "queue-store-oracle.json")));
        Directory.CreateDirectory(Path.GetDirectoryName(Db)!);
        // iterdump emits tables alphabetically, including the child events before their parent items.
        Execute("PRAGMA foreign_keys=OFF;\n" + oracle.RootElement.GetProperty("sql").GetString()!);
        return oracle;
    }

    private object? Scalar(string sql)
    {
        using var connection = new SqliteConnection($"Data Source={Db};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
#pragma warning disable CA2100 // Test-owned SQL literals only; the helper never receives production input.
        command.CommandText = sql;
#pragma warning restore CA2100
        return command.ExecuteScalar();
    }

    private void Execute(string sql)
    {
        using var connection = new SqliteConnection($"Data Source={Db};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
#pragma warning disable CA2100 // Test-owned SQL literals and the recorded Python database fixture only.
        command.CommandText = sql;
#pragma warning restore CA2100
        command.ExecuteNonQuery();
    }

    private static QueueItem Item(int number) => new("owner/repo", number, "pr", "PR", "OPEN", "2026-01-01T00:00:00Z", "2026-01-02T00:00:00Z", new string('a', 40));
}
