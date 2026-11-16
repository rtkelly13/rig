using System.Text.Json;
using System.Text.Json.Nodes;
using Rig.Core.Processes;
using Rig.Core.WorkItems;

namespace Rig.Core.Tests.WorkItems;

public sealed class PrsTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    private readonly TempDir _temp = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void BuildQuery_aliases_each_branch_and_escapes_json()
    {
        var query = WorkPrSource.BuildQuery("o/n", ["a", "we\"ird"]);

        Assert.Contains("repository(owner: \"o\", name: \"n\")", query, StringComparison.Ordinal);
        Assert.Contains(
            "b0: pullRequests(headRefName: \"a\", first: 1, orderBy: {field: CREATED_AT, direction: DESC}) { nodes { number state isDraft url updatedAt } }",
            query,
            StringComparison.Ordinal);
        Assert.Contains("b1: pullRequests(headRefName: \"we\\\"ird\"", query, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildQuery_escapes_non_ascii_as_python_json_dumps_does()
    {
        Assert.Contains("headRefName: \"caf\\u00e9\"", WorkPrSource.BuildQuery("o/n", ["café"]), StringComparison.Ordinal);
        Assert.Contains("repository(owner: \"o\", name: \"\")", WorkPrSource.BuildQuery("o", ["a"]), StringComparison.Ordinal);
    }

    [Fact]
    public void ParseResponse_maps_states_and_keeps_the_first_node()
    {
        var stdout = Response(new Dictionary<string, string>
        {
            ["b0"] = """[{"number":1,"state":"OPEN","isDraft":true,"url":"u1","updatedAt":"2026-10-01T00:00:00Z"}]""",
            ["b1"] = """[{"number":2,"state":"MERGED","isDraft":false,"url":"u2"}]""",
            ["b2"] = "[]",
            ["b3"] = "null",
            ["b4"] = """[{"number":4,"state":"CLOSED","url":"u4"}]""",
        });

        var parsed = WorkPrSource.ParseResponse(stdout, ["a", "b", "c", "d", "e"]);

        Assert.NotNull(parsed);
        Assert.Equal(new PrInfo(1, PrStatus.Draft, "u1", new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero)), parsed["a"]);
        Assert.Equal(new PrInfo(2, PrStatus.Merged, "u2"), parsed["b"]);
        Assert.Null(parsed["c"]);
        Assert.Null(parsed["d"]);
        Assert.Equal(new PrInfo(4, PrStatus.Closed, "u4"), parsed["e"]);
    }

    [Fact]
    public void ParseResponse_returns_null_for_an_unusable_reply()
    {
        Assert.Null(WorkPrSource.ParseResponse("not json", ["a"]));
        Assert.Null(WorkPrSource.ParseResponse("""{"data":null}""", ["a"]));
        Assert.Null(WorkPrSource.ParseResponse("""{"data":{"repository":null}}""", ["a"]));
        Assert.Null(WorkPrSource.ParseResponse("""{"data":{"repository":{"b0":"wrong"}}}""", ["a"]));
        Assert.Null(WorkPrSource.ParseResponse("""{"data":{"repository":{"b0":{"nodes":null}}}}""", ["a"]));
        Assert.Null(WorkPrSource.ParseResponse("""{"data":{"repository":{"b0":{"nodes":[{"state":"OPEN"}]}}}}""", ["a"]));
        Assert.Null(WorkPrSource.ParseResponse("""{"data":{"repository":{"b0":{"nodes":["wrong"]}}}}""", ["a"]));
        Assert.Null(WorkPrSource.ParseResponse("""{"data":{"repository":[]}}""", ["a"]));
    }

    [Fact]
    public void ParseResponse_handles_an_unknown_state_and_a_missing_first_node()
    {
        Assert.Null(WorkPrSource.ParseResponse(
            """{"data":{"repository":{"b0":{"nodes":[{"number":1,"state":"DRAFT"}]}}}}""",
            ["a"])!["a"]);
        Assert.Null(WorkPrSource.ParseResponse(
            """{"data":{"repository":{"b0":{"nodes":[null]}}}}""",
            ["a"])!["a"]);
        Assert.Null(WorkPrSource.ParseResponse("""{"data":{"repository":{"b0":{}}}}""", ["a"])!["a"]);
        Assert.Null(WorkPrSource.ParseResponse("""{"data":{"repository":{}}}""", ["a"])!["a"]);
    }

    [Fact]
    public async Task Fetch_answers_from_the_network_then_from_the_cache()
    {
        var api = new FakePrApi(_ => new Ran(0, Response(new Dictionary<string, string> { ["b0"] = """[{"number":5,"state":"CLOSED","url":"u"}]""" }), string.Empty));
        var source = Source(api);
        var request = new PrRequest("/code/acme/app", "o/n", ["feat"]);

        var first = await source.FetchAsync([request], refresh: false, Now, Token);
        var second = await source.FetchAsync([request], refresh: false, Now + TimeSpan.FromMinutes(1), Token);

        var expected = new Dictionary<BranchKey, PrInfo> { [new BranchKey("/code/acme/app", "feat")] = new(5, PrStatus.Closed, "u") };
        Assert.Equal(expected, first.Prs);
        Assert.Equal(PrLookup.Ok, first.Lookups["/code/acme/app"]);
        Assert.Equal(1, first.Fetched);
        Assert.Empty(first.Warnings);
        Assert.Equal(expected, second.Prs);
        Assert.Equal(1, second.Cached);
        Assert.Equal(0, second.Fetched);
        Assert.Single(api.Calls);
    }

    [Fact]
    public async Task Fetch_refreshes_past_the_cache_and_when_asked()
    {
        var api = new FakePrApi(_ => new Ran(0, Response(new Dictionary<string, string> { ["b0"] = """[{"number":5,"state":"OPEN","url":"u"}]""" }), string.Empty));
        var source = Source(api);
        var request = new PrRequest("/code/acme/app", "o/n", ["feat"]);

        _ = await source.FetchAsync([request], refresh: false, Now, Token);
        _ = await source.FetchAsync([request], refresh: false, Now + WorkPrSource.DefaultTtl, Token);
        _ = await source.FetchAsync([request], refresh: true, Now, Token);

        Assert.Equal(3, api.Calls.Count);
    }

    [Fact]
    public async Task Fetch_reuses_a_cached_repo_only_when_it_covers_every_branch()
    {
        var api = new FakePrApi(_ => new Ran(0, Response(new Dictionary<string, string>
        {
            ["b0"] = """[{"number":5,"state":"OPEN","url":"u"}]""",
            ["b1"] = "[]",
        }), string.Empty));
        var source = Source(api);

        _ = await source.FetchAsync([new PrRequest("/code/acme/app", "o/n", ["a"])], refresh: false, Now, Token);
        var second = await source.FetchAsync([new PrRequest("/code/acme/app", "o/n", ["a", "b"])], refresh: false, Now, Token);

        Assert.Equal(2, api.Calls.Count);
        Assert.Equal(1, second.Fetched);
    }

    [Fact]
    public async Task Fetch_degrades_when_gh_fails_or_the_origin_is_not_github()
    {
        var api = new FakePrApi(_ => new Ran(1, string.Empty, "auth required"));
        var source = Source(api);

        var results = await source.FetchAsync(
            [
                new PrRequest("/code/acme/app", "o/n", ["feat"]),
                new PrRequest("/code/other/lib", string.Empty, ["x"]),
            ],
            refresh: false,
            Now,
            Token);

        Assert.Empty(results.Prs);
        Assert.Equal(PrLookup.Unavailable, results.Lookups["/code/acme/app"]);
        Assert.Equal(PrLookup.Unavailable, results.Lookups["/code/other/lib"]);
        Assert.Equal(
            [
                "lib: origin is not a GitHub remote",
                "o/n: gh lookup failed (offline, unauthenticated or no access)",
            ],
            results.Warnings);
        Assert.Equal(0, results.Fetched);
    }

    [Fact]
    public async Task Fetch_queries_each_repo_and_keys_answers_by_repo_and_branch()
    {
        var api = new FakePrApi(query => query.Contains("name: \"app\"", StringComparison.Ordinal)
            ? new Ran(0, Response(new Dictionary<string, string> { ["b0"] = """[{"number":1,"state":"MERGED","url":"u1"}]""" }), string.Empty)
            : new Ran(0, Response(new Dictionary<string, string> { ["b0"] = """[{"number":2,"state":"OPEN","url":"u2"}]""" }), string.Empty));
        var source = Source(api);

        var results = await source.FetchAsync(
            [
                new PrRequest("/code/acme/app", "o/app", ["feat"]),
                new PrRequest("/code/other/lib", "o/lib", ["feat"]),
            ],
            refresh: false,
            Now,
            Token);

        Assert.Equal(new PrInfo(1, PrStatus.Merged, "u1"), results.Prs[new BranchKey("/code/acme/app", "feat")]);
        Assert.Equal(new PrInfo(2, PrStatus.Open, "u2"), results.Prs[new BranchKey("/code/other/lib", "feat")]);
        Assert.Equal(2, results.Fetched);
        Assert.Equal(2, api.Bodies.Count);
        Assert.All(api.Bodies, body => Assert.NotNull(body["variables"]));
    }

    [Fact]
    public async Task Fetch_ignores_a_corrupt_or_expired_cache()
    {
        var cache = Path.Join(_temp.Path, "prs.json");
        await File.WriteAllTextAsync(cache, "not json", Token);
        var api = new FakePrApi(_ => new Ran(0, Response(new Dictionary<string, string> { ["b0"] = """[{"number":5,"state":"OPEN","url":"u"}]""" }), string.Empty));
        var source = new WorkPrSource(api, cache);
        var request = new PrRequest("/code/acme/app", "o/n", ["feat"]);

        _ = await source.FetchAsync([request], refresh: false, Now, Token);
        await File.WriteAllTextAsync(cache, """{"o/n":{"fetched_at":"2026-10-10T12:00:00+00:00","branches":null}}""", Token);
        _ = await source.FetchAsync([request], refresh: false, Now, Token);

        Assert.Equal(2, api.Calls.Count);
    }

    [Fact]
    public async Task Fetch_writes_the_python_cache_shape_and_reads_it_back()
    {
        var cache = Path.Join(_temp.Path, "prs.json");
        var api = new FakePrApi(_ => new Ran(0, Response(new Dictionary<string, string>
        {
            ["b0"] = """[{"number":5,"state":"OPEN","isDraft":true,"url":"u","updatedAt":"2026-10-01T00:00:00Z"}]""",
            ["b1"] = "[]",
        }), string.Empty));
        var source = new WorkPrSource(api, cache);

        _ = await source.FetchAsync([new PrRequest("/code/acme/app", "o/n", ["a", "b"])], refresh: false, Now, Token);

        var text = await File.ReadAllTextAsync(cache, Token);
        var written = JsonNode.Parse(text)!.AsObject()["o/n"]!.AsObject();
        Assert.Equal("2026-10-10T12:00:00+00:00", written["fetched_at"]!.GetValue<string>());
        Assert.True(written["branches"]?["a"] is not null, text);
        Assert.Equal(5, written["branches"]!["a"]!["number"]!.GetValue<int>());
        Assert.Equal("draft", written["branches"]!["a"]!["status"]!.GetValue<string>());
        Assert.Null(written["branches"]!["b"]);
        Assert.Equal(2, written["branches"]!.AsObject().Count);
    }

    [Fact]
    public async Task Fetch_without_a_cache_file_still_answers()
    {
        var api = new FakePrApi(_ => new Ran(0, Response(new Dictionary<string, string> { ["b0"] = """[{"number":5,"state":"OPEN","url":"u"}]""" }), string.Empty));
        var source = new WorkPrSource(api);

        var results = await source.FetchAsync([new PrRequest("/code/acme/app", "o/n", ["feat"])], refresh: false, Now, Token);

        Assert.Single(results.Prs);
    }

    [Fact]
    public void CacheFileUnder_uses_the_home_folder()
    {
        Assert.Equal(
            Path.Join("/home", WorkPrSource.CacheFileName),
            WorkPrSource.CacheFileUnder("/home"));
    }

    [Fact]
    public void JsonQuote_escapes_backslashes_quotes_and_control_characters()
    {
        Assert.Equal(
            "\"a\\\\b\\\"c\\b\\f\\n\\r\\t\\u0001\\u00e9\"",
            WorkPrSource.JsonQuote("a\\b\"c\b\f\n\r\t\u0001é"));
    }

    [Fact]
    public async Task Fetch_reads_every_status_and_a_null_branch_from_a_valid_cache()
    {
        var cache = Path.Join(_temp.Path, "cached.json");
        await File.WriteAllTextAsync(
            cache,
            """
            {"o/n":{"fetched_at":"2026-10-10T12:00:00+00:00","branches":{
              "a":{"number":1,"status":"open","url":"u1","updated_at":null},
              "b":{"number":2,"status":"draft","url":"u2","updated_at":"2026-10-01T00:00:00+00:00"},
              "c":{"number":3,"status":"merged","url":"u3"},
              "d":{"number":4,"status":"closed","url":"u4"},
              "e":null}}}
            """,
            Token);
        var api = new FakePrApi(_ => throw new InvalidOperationException("the cache should answer"));
        var source = new WorkPrSource(api, cache);

        var results = await source.FetchAsync(
            [new PrRequest("/code/acme/app", "o/n", ["a", "b", "c", "d", "e"])],
            refresh: false,
            Now,
            Token);

        Assert.Empty(api.Calls);
        Assert.Equal(1, results.Cached);
        Assert.Equal(0, results.Fetched);
        Assert.Equal(PrStatus.Open, results.Prs[new BranchKey("/code/acme/app", "a")].Status);
        Assert.Equal(PrStatus.Draft, results.Prs[new BranchKey("/code/acme/app", "b")].Status);
        Assert.Equal(PrStatus.Merged, results.Prs[new BranchKey("/code/acme/app", "c")].Status);
        Assert.Equal(PrStatus.Closed, results.Prs[new BranchKey("/code/acme/app", "d")].Status);
        Assert.False(results.Prs.ContainsKey(new BranchKey("/code/acme/app", "e")));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("""{"o/n":{}}""")]
    [InlineData("""{"o/n":{"fetched_at":"not a time","branches":{}}}""")]
    [InlineData("""{"o/n":{"fetched_at":"2026-10-10T12:00:00+00:00","branches":{"a":"wrong"}}}""")]
    [InlineData("""{"o/n":{"fetched_at":"2026-10-10T12:00:00+00:00","branches":{"a":{"number":1,"status":"weird","url":"u"}}}}""")]
    public async Task Fetch_treats_a_misshapen_cache_as_absent(string cacheText)
    {
        var cache = Path.Join(_temp.Path, Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(cache, cacheText, Token);
        var api = new FakePrApi(_ => new Ran(0, Response(new Dictionary<string, string>
        {
            ["b0"] = """[{"number":5,"state":"OPEN","url":"u"}]""",
        }), string.Empty));
        var source = new WorkPrSource(api, cache);

        var results = await source.FetchAsync([new PrRequest("/code/acme/app", "o/n", ["a"])], refresh: false, Now, Token);

        Assert.Equal(1, results.Fetched);
        Assert.Single(api.Calls);
    }

    [Fact]
    public async Task Fetch_survives_an_unwritable_cache()
    {
        var cache = _temp.Folder("cache-is-a-directory");
        var api = new FakePrApi(_ => new Ran(0, Response(new Dictionary<string, string>
        {
            ["b0"] = """[{"number":5,"state":"OPEN","url":"u"}]""",
        }), string.Empty));
        var source = new WorkPrSource(api, cache);

        var results = await source.FetchAsync([new PrRequest("/code/acme/app", "o/n", ["a"])], refresh: false, Now, Token);

        Assert.Single(results.Prs);
    }

    private WorkPrSource Source(FakePrApi api) => new(api, Path.Join(_temp.Path, Guid.NewGuid().ToString("N") + ".json"));

    private static string Response(IReadOnlyDictionary<string, string> nodesByAlias)
    {
        var repository = new JsonObject();
        foreach (var (alias, nodes) in nodesByAlias)
        {
            repository[alias] = nodes == "null" ? null : new JsonObject { ["nodes"] = JsonNode.Parse(nodes) };
        }

        return new JsonObject { ["data"] = new JsonObject { ["repository"] = repository } }.ToJsonString();
    }
}
