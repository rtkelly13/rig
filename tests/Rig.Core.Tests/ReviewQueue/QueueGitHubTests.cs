using System.Text.Json;
using System.Text.Json.Nodes;
using Rig.Core.GitHub;
using Rig.Core.ReviewQueue;
using Rig.Core.Tests.Fakes;

namespace Rig.Core.Tests.ReviewQueue;

public sealed class QueueGitHubTests : IDisposable
{
    private readonly TempDir _temp = new();
    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task Selected_snapshots_match_recorded_including_unknown_partial_and_native_stack_metadata()
    {
        using var oracle = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Join(AppContext.BaseDirectory, "ReviewQueue", "queue-github-oracle.json"), TestContext.Current.CancellationToken));
        foreach (var example in oracle.RootElement.EnumerateArray())
        {
            var kind = example.GetProperty("kind").GetString()!;
            var input = example.GetProperty("input");
            var github = new FakeGitHub().On("POST", "graphql", Lookup(input.GetRawText()));
            var before = Item(input.GetProperty("number").GetInt32()) with { Kind = kind, InScope = false };
            var actual = await new QueueGitHub(github).ObserveAsync(before, TestContext.Current.CancellationToken);
            Assert.False(actual.InScope);
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(example.GetProperty("expected").GetRawText()),
                JsonSerializer.SerializeToNode(actual with { InScope = true }, QueueJson.Options)), example.GetProperty("name").GetString());
            using var request = JsonDocument.Parse(Assert.Single(github.Calls).Body!);
            Assert.Equal(before.Number, request.RootElement.GetProperty("variables").GetProperty("number").GetInt32());
            if (kind == "pr")
            {
                Assert.Contains("contexts(first:100)", request.RootElement.GetProperty("query").GetString(), StringComparison.Ordinal);
            }
        }
    }

    [Theory]
    [InlineData("pr", "pullRequests")]
    [InlineData("issue", "issues")]
    public async Task Fetch_follows_every_cursor_and_sends_values_as_variables(string kind, string connection)
    {
        const string cursor = "a cursor with \"quotes\" and \\ escapes";
        var github = new FakeGitHub().OnSequence("POST", "graphql", Page("[" + Node(1) + "]", true, cursor), Page("[" + Node(2) + "]"));
        var items = await new QueueGitHub(github).FetchAsync("Owner/Repo", kind, TestContext.Current.CancellationToken);
        Assert.Equal([1, 2], items.Select(item => item.Number));
        Assert.All(items, item => Assert.Equal("owner/repo", item.Repo));
        Assert.Equal(2, github.Calls.Count);
        using var request = JsonDocument.Parse(github.Calls[1].Body!);
        var variables = request.RootElement.GetProperty("variables");
        Assert.Equal(cursor, variables.GetProperty("after").GetString());
        Assert.Equal("owner", variables.GetProperty("owner").GetString());
        var query = request.RootElement.GetProperty("query").GetString()!;
        Assert.Contains($"items:{connection}", query, StringComparison.Ordinal);
        Assert.DoesNotContain(cursor, query, StringComparison.Ordinal);
        Assert.DoesNotContain("contexts(first:100)", query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Later_page_failures_never_return_a_partial_queue()
    {
        var github = new FakeGitHub().OnSequence("POST", "graphql", Page("[" + Node(1) + "]", true, "next")).Fail("POST", "graphql", 403);
        var error = await Assert.ThrowsAsync<GhException>(() => new QueueGitHub(github).FetchAsync("owner/repo", "pr", TestContext.Current.CancellationToken));
        Assert.Contains("403", error.Message, StringComparison.Ordinal);
        Assert.Equal(2, github.Calls.Count);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"data\":{\"repository\":null}}")]
    [InlineData("{\"data\":{\"repository\":{\"items\":{\"nodes\":null,\"pageInfo\":{\"hasNextPage\":false}}}}}")]
    [InlineData("{\"data\":{\"repository\":{\"items\":{\"nodes\":[],\"pageInfo\":{}}}}}")]
    [InlineData("{\"data\":{\"repository\":{\"items\":{\"pageInfo\":{\"hasNextPage\":false}}}}}")]
    public async Task Missing_or_malformed_bulk_fields_fail_closed(string response)
    {
        var github = new FakeGitHub().On("POST", "graphql", response);
        await Assert.ThrowsAsync<FormatException>(() => new QueueGitHub(github).FetchAsync("owner/repo", "pr", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Graphql_errors_null_nodes_and_broken_cursors_are_refused()
    {
        var errors = new FakeGitHub().On("POST", "graphql", "{\"errors\":[{\"message\":\"denied\"}],\"data\":{\"repository\":{}}}");
        await Assert.ThrowsAsync<GhException>(() => new QueueGitHub(errors).FetchAsync("owner/repo", "pr", TestContext.Current.CancellationToken));
        var nullNode = new FakeGitHub().On("POST", "graphql", Page("[null]"));
        await Assert.ThrowsAsync<FormatException>(() => new QueueGitHub(nullNode).FetchAsync("owner/repo", "pr", TestContext.Current.CancellationToken));
        var missing = new FakeGitHub().On("POST", "graphql", Page("[]", true));
        await Assert.ThrowsAsync<FormatException>(() => new QueueGitHub(missing).FetchAsync("owner/repo", "pr", TestContext.Current.CancellationToken));
        var repeated = new FakeGitHub().On("POST", "graphql", Page("[]", true, "same"));
        await Assert.ThrowsAsync<FormatException>(() => new QueueGitHub(repeated).FetchAsync("owner/repo", "pr", TestContext.Current.CancellationToken));
        Assert.Equal(2, repeated.Calls.Count);
    }

    [Fact]
    public async Task A_pr_keeps_additions_and_deletions_apart_and_an_omitted_split_stays_unknown()
    {
        const string node = """{"number":1,"title":"PR","state":"OPEN","createdAt":"2026-01-01T00:00:00Z","updatedAt":"2026-01-02T00:00:00Z","additions":12,"deletions":3}""";
        var observed = await new QueueGitHub(new FakeGitHub().On("POST", "graphql", Lookup(node))).ObserveAsync(Item(), TestContext.Current.CancellationToken);
        Assert.Equal((12, 3, 15), (observed.Additions, observed.Deletions, observed.Lines));
        Assert.Contains("\"additions\":12", QueueJson.EncodeItem(observed), StringComparison.Ordinal);

        var unknown = await new QueueGitHub(new FakeGitHub().On("POST", "graphql", Lookup(Node(1)))).ObserveAsync(Item(), TestContext.Current.CancellationToken);
        Assert.Equal((null, null), (unknown.Additions, unknown.Deletions));
        Assert.DoesNotContain("additions", QueueJson.EncodeItem(unknown), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("{\"number\":1,\"title\":null}")]
    public async Task Missing_selected_items_or_required_fields_are_errors(string node)
    {
        var github = new FakeGitHub().On("POST", "graphql", Lookup(node));
        await Assert.ThrowsAsync<FormatException>(() => new QueueGitHub(github).ObserveAsync(Item(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Discussion_pages_preserve_edit_keys_actors_reviews_and_pending_review_privacy()
    {
        var first = JsonSerializer.Serialize(Enumerable.Range(1, 100).Select(id => new { id, user = new { login = "reviewer" }, body = "hello", updated_at = "2026-01-02T00:00:00Z" }));
        const string second = """
            [{"id":2147483648,"user":null,"body":null,"submitted_at":"2026-01-03T00:00:00Z","state":"APPROVED","commit_id":"abc"},
             {"id":999,"user":null}]
            """;
        var github = new FakeGitHub()
            .On("GET", "repos/owner/repo/issues/1/comments?per_page=100&page=1", first)
            .On("GET", "repos/owner/repo/issues/1/comments?per_page=100&page=2", second)
            .On("GET", "repos/owner/repo/pulls/1/reviews?per_page=100&page=1", "[]")
            .On("GET", "repos/owner/repo/pulls/1/comments?per_page=100&page=1", "[]");
        var events = await new QueueGitHub(github).ActivityAsync(Item(), TestContext.Current.CancellationToken);
        Assert.Equal(101, events.Count);
        const string key = "owner/repo/issues/1/comments/1/2026-01-02T00:00:00Z/COMMENTED/2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824";
        Assert.Equal("reviewer", Assert.Single(events, e => e.Key == key).Actor);
        Assert.Equal("deleted-user", events[^1].Actor);
        Assert.Equal("github:approved", events[^1].Action);
        Assert.Equal("abc", events[^1].Head);
        Assert.Equal("", events[^1].Note);
        Assert.Equal(4, github.Calls.Count);
        var issue = await new QueueGitHub(github).ActivityAsync(Item() with { Kind = "issue" }, TestContext.Current.CancellationToken);
        Assert.Equal(events, issue);
        Assert.Equal(6, github.Calls.Count);
    }

    [Fact]
    public async Task Discussion_errors_fail_instead_of_losing_activity()
    {
        var github = new FakeGitHub().On("GET", "repos/owner/repo/issues/1/comments?per_page=100&page=1", "[{}]");
        await Assert.ThrowsAsync<FormatException>(() => new QueueGitHub(github).ActivityAsync(Item(), TestContext.Current.CancellationToken));
        github.On("GET", "repos/owner/repo/issues/1/comments?per_page=100&page=1", "[{\"id\":1}]");
        await Assert.ThrowsAsync<FormatException>(() => new QueueGitHub(github).ActivityAsync(Item(), TestContext.Current.CancellationToken));
        github.Fail("GET", "repos/owner/repo/issues/1/comments?per_page=100&page=1", 500);
        await Assert.ThrowsAsync<GhException>(() => new QueueGitHub(github).ActivityAsync(Item(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Registry_filters_deduplicates_and_orders_managed_repositories()
    {
        var path = _temp.File("managed.json");
        File.WriteAllText(path, """
            {"projects":[{"repo":"git@github.com:Owner/Repo.git"},{"repo":"https://github.com/owner/repo.git"},
            {"repo":"owner/another"},{"repo":"invalid but disabled","enabled":false},{"repo":"invalid but archived","archived":true}]}
            """);
        Assert.Equal(["owner/another", "owner/repo"], new QueueGitHub(new FakeGitHub()).Repositories(path));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"projects\":[]}")]
    [InlineData("{\"projects\":[null]}")]
    [InlineData("{\"projects\":[{\"repo\":\"not-a-github-remote\"}]}")]
    [InlineData("{\"projects\":[{\"repo\":null}]}")]
    public void Invalid_registries_are_not_empty_successes(string text)
    {
        var path = _temp.File("managed.json");
        File.WriteAllText(path, text);
        var error = Record.Exception(() => new QueueGitHub(new FakeGitHub()).Repositories(path));
        Assert.True(error is JsonException or FormatException);
    }

    [Fact]
    public async Task Invalid_kinds_and_cancellation_do_not_call_github()
    {
        var github = new FakeGitHub();
        var source = new QueueGitHub(github);
        await Assert.ThrowsAsync<ArgumentException>(() => source.FetchAsync("owner/repo", "unknown", TestContext.Current.CancellationToken));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.FetchAsync("owner/repo", "pr", cancelled.Token));
        Assert.Empty(github.Calls);
    }

    private static string Lookup(string node) => JsonSerializer.Serialize(new { data = new { repository = new { item = JsonNode.Parse(node) } } });
    private static string Page(string nodes = "[]", bool more = false, string? cursor = null) =>
        JsonSerializer.Serialize(new { data = new { repository = new { items = new { nodes = JsonNode.Parse(nodes), pageInfo = new { hasNextPage = more, endCursor = cursor } } } } });
    private static string Node(int number) => JsonSerializer.Serialize(new { number, title = "PR", state = "OPEN", createdAt = "2026-01-01T00:00:00Z", updatedAt = "2026-01-02T00:00:00Z" });
    private static QueueItem Item(int number = 1) => new("owner/repo", number, "pr", "PR", "OPEN", "2026-01-01T00:00:00Z", "2026-01-02T00:00:00Z");
}
