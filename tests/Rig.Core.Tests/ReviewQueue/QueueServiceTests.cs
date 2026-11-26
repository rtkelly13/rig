using Rig.Core.ReviewQueue;
using Rig.Core.Tests.Fakes;
using Rig.Core.Tests.Otel;

namespace Rig.Core.Tests.ReviewQueue;

public sealed class QueueServiceTests : IDisposable
{
    private readonly TempDir _temp = new();
    private readonly FakeQueueSource _source = new();
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 11, 13, 9, 30, 5, TimeSpan.Zero));
    private string Db => Path.Join(_temp.Path, "rig.db");
    private string Output => Path.Join(_temp.Path, "transient", "queue.md");
    private QueueStore Store => new(Db, _clock);
    private QueueService Service => new(Store, _source, Output);

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task A_repository_failure_preserves_all_existing_snapshots_and_markdown()
    {
        Seed();
        var before = await File.ReadAllBytesAsync(Output, TestContext.Current.CancellationToken);
        _source.Repos.Add("owner/failure");
        _source.Items.Add(Item() with { Title = "not committed" });
        _source.FailAt = "fetch:owner/failure:pr";
        using var service = Service;
        await Assert.ThrowsAsync<InvalidDataException>(() => service.RefreshAsync("managed.json", TestContext.Current.CancellationToken));
        Assert.Equal("Original", Assert.Single(Store.Read()).Item.Title);
        Assert.Single(Store.Read()[0].Events);
        Assert.Equal(before, await File.ReadAllBytesAsync(Output, TestContext.Current.CancellationToken));
        Assert.Equal(["repositories:managed.json", "fetch:owner/repo:pr", "fetch:owner/repo:issue", "fetch:owner/failure:pr"], _source.Calls);
    }

    [Fact]
    public async Task A_repository_missing_on_github_is_skipped_and_the_rest_refresh()
    {
        Seed();
        Store.Update(writer => writer.Interact(new QueueInteraction("owner/repo", 1, "note", Item().Head, "Keep this")));
        _source.Repos.Add("owner/other");
        _source.Items.Add(Item() with { Repo = "owner/other", Title = "Fresh" });
        _source.Missing.Add("owner/repo");
        using var service = Service;
        var refreshed = await service.RefreshAsync("managed.json", TestContext.Current.CancellationToken);
        Assert.Equal(["owner/repo"], refreshed.Skipped);
        var gone = Assert.Single(refreshed.Entries, entry => entry.Item.Repo == "owner/repo");
        Assert.False(gone.Item.InScope);
        Assert.Equal("Keep this", gone.Events[1].Note);
        Assert.True(Assert.Single(refreshed.Entries, entry => entry.Item.Repo == "owner/other").Item.InScope);
        Assert.DoesNotContain($"observe:{Item().Reference}", _source.Calls);
        Assert.DoesNotContain("fetch:owner/repo:issue", _source.Calls);
    }

    [Fact]
    public async Task When_no_repository_resolves_the_refresh_fails_and_changes_nothing()
    {
        Seed();
        var before = await File.ReadAllBytesAsync(Output, TestContext.Current.CancellationToken);
        _source.Missing.Add("owner/repo");
        using var service = Service;
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => service.RefreshAsync("managed.json", TestContext.Current.CancellationToken));
        Assert.Contains("gh login", error.Message, StringComparison.Ordinal);
        Assert.True(Assert.Single(Store.Read()).Item.InScope);
        Assert.Equal(before, await File.ReadAllBytesAsync(Output, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Missing_open_items_are_observed_and_kept_in_history_after_merge()
    {
        Seed();
        _source.Observed[Item().Reference] = Item() with { State = "MERGED" };
        using var service = Service;
        var entry = Assert.Single((await service.RefreshAsync("managed.json", TestContext.Current.CancellationToken)).Entries);
        Assert.Equal("MERGED", entry.Item.State);
        Assert.Equal(2, entry.Events.Count);
        Assert.DoesNotContain(Item().Reference, await File.ReadAllTextAsync(Output, TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Removing_a_repository_removes_it_from_the_queue_without_deleting_notes()
    {
        Seed();
        Store.Update(writer => writer.Interact(new QueueInteraction("owner/repo", 1, "note", Item().Head, "Keep this")));
        _source.Repos.Clear();
        _source.Repos.Add("owner/other");
        using var service = Service;
        var entry = Assert.Single((await service.RefreshAsync("managed.json", TestContext.Current.CancellationToken)).Entries);
        Assert.False(entry.Item.InScope);
        Assert.Equal("Keep this", entry.Events[1].Note);
        Assert.DoesNotContain(Item().Reference, await File.ReadAllTextAsync(Output, TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.DoesNotContain($"observe:{Item().Reference}", _source.Calls);
    }

    [Theory]
    [InlineData("archived")]
    [InlineData("reviewed")]
    [InlineData("changes_requested")]
    public async Task Refresh_imports_human_replies_for_retained_items_and_discussion_failures_commit_nothing(string action)
    {
        Seed();
        Store.Update(writer => writer.Interact(new QueueInteraction("owner/repo", 1, action, Item().Head)));
        _source.Items.Add(Item());
        _source.Discussions[Item().Reference] = [new("reply/1", "2099-01-01T00:00:00Z", "reviewer", "github:commented", "", "Please revisit")];
        using var service = Service;
        var refreshed = Assert.Single((await service.RefreshAsync("managed.json", TestContext.Current.CancellationToken)).Entries);
        Assert.False(refreshed.Archived);
        Assert.Contains(refreshed.Events, e => e.Note == "Please revisit");
        var repeated = Assert.Single((await service.RefreshAsync("managed.json", TestContext.Current.CancellationToken)).Entries);
        Assert.Equal(refreshed.Events.Count, repeated.Events.Count);
        var before = await File.ReadAllBytesAsync(Output, TestContext.Current.CancellationToken);
        _source.FailAt = $"activity:{Item().Reference}";
        _source.Items.Clear();
        _source.Items.Add(Item() with { Title = "not committed" });
        await Assert.ThrowsAsync<InvalidDataException>(() => service.RefreshAsync("managed.json", TestContext.Current.CancellationToken));
        Assert.Equal(before, await File.ReadAllBytesAsync(Output, TestContext.Current.CancellationToken));
        Assert.Equal("Original", Store.Read()[0].Item.Title);
        Assert.Equal(repeated.Events.Count, Store.Read()[0].Events.Count);
    }

    [Theory]
    [InlineData("reviewed")]
    [InlineData("changes_requested")]
    public async Task Review_decisions_keep_the_live_head_but_refuse_a_stale_decision(string action)
    {
        Seed();
        var live = Item() with { Head = new string('b', 40) };
        _source.Observed[Item().Reference] = live;
        using var service = Service;
        var error = await Assert.ThrowsAsync<ArgumentException>(() => service.RecordAsync(new QueueInteraction("owner/repo", 1, action, Item().Head), TestContext.Current.CancellationToken));
        Assert.Contains("head changed", error.Message, StringComparison.Ordinal);
        Assert.Equal(live.Head, Store.Read()[0].Item.Head);
        Assert.DoesNotContain(Store.Read()[0].Events, e => e.Action == action);
        Assert.Contains(live.Head, await File.ReadAllTextAsync(Output, TestContext.Current.CancellationToken), StringComparison.Ordinal);
        await service.RecordAsync(new QueueInteraction("owner/repo", 1, action, live.Head, "Reviewed live head"), TestContext.Current.CancellationToken);
        Assert.Equal("Reviewed live head", Store.Read()[0].Events[^1].Note);
    }

    [Fact]
    public async Task Local_notes_need_no_remote_observation_and_watching_fetches_one_item_and_its_discussion()
    {
        Seed();
        using var service = Service;
        await service.RecordAsync(new QueueInteraction("owner/repo", 1, "note", Item().Head, "Local note"), TestContext.Current.CancellationToken);
        Assert.Empty(_source.Calls);
        _source.Observed[Item().Reference] = Item() with { Title = "Fresh" };
        _source.Discussions[Item().Reference] = [new("comment/1", "2026-01-01T00:00:00Z", "me", "github:commented", "", "Discussion")];
        var observed = await service.WatchAsync("owner/repo", 1, TestContext.Current.CancellationToken);
        Assert.Equal("Fresh", observed.Title);
        Assert.Equal(["observe:owner/repo#1", "activity:owner/repo#1"], _source.Calls);
        Assert.Equal("Discussion", Store.Read()[0].Events[^1].Note);
        Assert.Contains("Fresh", await File.ReadAllTextAsync(Output, TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Failed_watch_discussions_leave_the_previous_snapshot_and_projection()
    {
        Seed();
        var before = await File.ReadAllBytesAsync(Output, TestContext.Current.CancellationToken);
        _source.Observed[Item().Reference] = Item() with { Title = "not committed" };
        _source.FailAt = "activity:owner/repo#1";
        using var service = Service;
        await Assert.ThrowsAsync<InvalidDataException>(() => service.WatchAsync("owner/repo", 1, TestContext.Current.CancellationToken));
        Assert.Equal("Original", Store.Read()[0].Item.Title);
        Assert.Equal(before, await File.ReadAllBytesAsync(Output, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Unknown_selected_items_are_refused_without_remote_calls()
    {
        using var service = Service;
        await Assert.ThrowsAsync<ArgumentException>(() => service.WatchAsync("owner/repo", 1, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => service.RecordAsync(new QueueInteraction("owner/repo", 1, "reviewed", ""), TestContext.Current.CancellationToken));
        Assert.Empty(_source.Calls);
    }

    [Fact]
    public async Task Cancellation_before_work_starts_writes_nothing()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        using var service = Service;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RefreshAsync("managed.json", cancelled.Token));
        Assert.False(File.Exists(Db));
        Assert.False(File.Exists(Output));
        Assert.Empty(_source.Calls);
    }

    private void Seed()
    {
        Store.Update(writer => writer.Save([Item()]));
        QueueProjection.Export(Output, Store.Read());
    }

    private static QueueItem Item() => new("owner/repo", 1, "pr", "Original", "OPEN", "2026-01-01T00:00:00Z", "2026-01-02T00:00:00Z", new string('a', 40), Readiness: 0);
}
