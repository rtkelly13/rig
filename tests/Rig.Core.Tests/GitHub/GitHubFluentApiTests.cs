using System.Text.Json;
using System.Text.Json.Nodes;
using Rig.Core.GitHub;
using Rig.Core.Processes;
using Rig.Core.Tests.Fakes;
using Rig.Core.Text;

namespace Rig.Core.Tests.GitHub;

public sealed class GitHubFluentApiTests
{
    private sealed record Widget(string Name, int Count);

    [Fact]
    public void RestRequest_builds_route_with_query_parameters()
    {
        var fake = new FakeGitHub();
        var req = fake.Get("repos/owner/repo/rules")
            .WithQuery("per_page", 50)
            .WithQuery("draft", false)
            .WithQuery("filter", "all & active");

        Assert.Equal("repos/owner/repo/rules?per_page=50&draft=false&filter=all%20%26%20active", req.BuildRoute());
    }

    [Fact]
    public void RestRequest_appends_query_when_route_already_contains_query()
    {
        var fake = new FakeGitHub();
        var req = fake.Get("repos/owner/repo/items?existing=1")
            .WithQuery("extra", 2);

        Assert.Equal("repos/owner/repo/items?existing=1&extra=2", req.BuildRoute());
    }

    [Fact]
    public void RestRequest_handles_headers_and_body()
    {
        var fake = new FakeGitHub();
        var req = fake.Post("repos/owner/repo/items")
            .WithHeader("Accept", "application/vnd.github.v3+json")
            .WithHeader("X-Custom: value")
            .WithJsonBody(new Widget("gadget", 42));

        var apiRequest = req.ToApiRequest();
        Assert.Equal("POST", apiRequest.Method);
        Assert.Equal("repos/owner/repo/items", apiRequest.Route);
        Assert.Equal(["Accept: application/vnd.github.v3+json", "X-Custom: value"], apiRequest.Headers);
        Assert.Contains("\"name\":\"gadget\"", apiRequest.Body, StringComparison.Ordinal);
        Assert.Contains("\"count\":42", apiRequest.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RestRequest_reads_typed_json_successfully()
    {
        var fake = new FakeGitHub().On("GET", "repos/owner/repo/widget", "{\"name\":\"gizmo\",\"count\":10}");

        var widget = await fake.Get("repos/owner/repo/widget").ReadJsonAsync<Widget>(cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(widget);
        Assert.Equal("gizmo", widget.Name);
        Assert.Equal(10, widget.Count);
    }

    [Fact]
    public async Task RestRequest_reads_optional_json_returning_null_on_404()
    {
        var fake = new FakeGitHub().Fail("GET", "repos/owner/repo/missing", 404);

        var result = await fake.Get("repos/owner/repo/missing").ReadOptionalJsonAsync<Widget>(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task RestRequest_does_not_treat_successful_body_containing_not_found_as_404()
    {
        var fake = new FakeGitHub().On("GET", "repos/owner/repo/item", "{\"name\":\"Not Found in database\",\"count\":1}");

        var result = await fake.Get("repos/owner/repo/item").ReadOptionalJsonAsync<Widget>(cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal("Not Found in database", result.Name);
        Assert.Equal(1, result.Count);
    }

    [Fact]
    public async Task RestRequest_read_json_throws_custom_exception_on_failure()
    {
        var fake = new FakeGitHub().Fail("GET", "repos/owner/repo/error", 500);

        var ex = await Assert.ThrowsAsync<GitHubApiException>(() =>
            fake.Get("repos/owner/repo/error").Describing("read widget").ReadJsonAsync<Widget>(cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("read widget", ex.Message, StringComparison.Ordinal);
        Assert.Equal(500, ex.StatusCode);
        Assert.Equal(1, ex.ExitCode);
    }

    [Fact]
    public async Task RestRequest_parses_json_node()
    {
        var fake = new FakeGitHub().On("GET", "meta", "{\"status\":\"ok\",\"version\":2}");

        var node = await fake.Get("meta").ReadJsonNodeAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(node);
        Assert.Equal("ok", node["status"]?.GetValue<string>());
        Assert.Equal(2, node["version"]?.GetValue<int>());
    }

    [Fact]
    public void RepoClient_prefixes_routes_and_populates_graphql_variables()
    {
        var fake = new FakeGitHub();
        var client = fake.ForRepo(new RepoSlug("rtkelly13", "rig"));

        var getReq = client.Get("/labels");
        Assert.Equal("repos/rtkelly13/rig/labels", getReq.Route);

        var postReq = client.Post("issues");
        Assert.Equal("repos/rtkelly13/rig/issues", postReq.Route);

        var gqlReq = client.GraphQL("query { test }");
        Assert.Equal("rtkelly13", gqlReq.Variables["owner"]);
        Assert.Equal("rig", gqlReq.Variables["name"]);
    }

    [Fact]
    public async Task GraphQLRequest_decodes_successful_response()
    {
        var query = "query Test($owner: String!) { repository(owner: $owner) { name } }";
        var fake = new FakeGitHub().On("POST", "graphql", "{\"data\":{\"name\":\"rig\"}}");

        var result = await fake.GraphQL(query)
            .WithVariable("owner", "rtkelly13")
            .ExecuteAsync<JsonNode>(cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal("rig", result["name"]?.GetValue<string>());
    }

    [Fact]
    public async Task GraphQLRequest_throws_when_errors_present()
    {
        var query = "query Test { fail }";
        var fake = new FakeGitHub().On("POST", "graphql", "{\"data\":null,\"errors\":[{\"message\":\"Field 'fail' not found\"}]}");

        var ex = await Assert.ThrowsAsync<GitHubApiException>(() =>
            fake.GraphQL(query)
                .Describing("failing test")
                .ExecuteAsync<JsonNode>(cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("failing test: Field 'fail' not found", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Field 'fail' not found", ex.Errors);
    }

    [Fact]
    public async Task GraphQLRequest_throws_original_errors_even_when_model_cannot_deserialize_null_data()
    {
        var query = "query Test { repository { widget } }";
        var fake = new FakeGitHub().On("POST", "graphql", "{\"data\":null,\"errors\":[{\"message\":\"Could not resolve to a Repository\"}]}");

        var ex = await Assert.ThrowsAsync<GitHubApiException>(() =>
            fake.GraphQL(query)
                .Describing("widget query")
                .ExecuteAsync<Widget>(cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("widget query: Could not resolve to a Repository", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Could not resolve to a Repository", ex.Errors);
    }

    [Fact]
    public async Task GraphQLRequest_ExecuteOptionalAsync_returns_null_on_not_found_error()
    {
        var query = "query Test { repository { widget } }";
        var fake = new FakeGitHub().On("POST", "graphql", "{\"data\":null,\"errors\":[{\"message\":\"Could not resolve to a Repository\",\"type\":\"NOT_FOUND\"}]}");

        var result = await fake.GraphQL(query)
            .ExecuteOptionalAsync<Widget>(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task GraphQLRequest_ExecuteOptionalAsync_returns_null_on_null_data()
    {
        var query = "query Test { repository { widget } }";
        var fake = new FakeGitHub().On("POST", "graphql", "{\"data\":null}");

        var result = await fake.GraphQL(query)
            .ExecuteOptionalAsync<Widget>(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task GraphQLRequest_ExecuteOptionalAsync_returns_data_on_success()
    {
        var query = "query Test { widget }";
        var fake = new FakeGitHub().On("POST", "graphql", "{\"data\":{\"name\":\"gadget\",\"count\":3}}");

        var result = await fake.GraphQL(query)
            .ExecuteOptionalAsync<Widget>(cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal("gadget", result.Name);
        Assert.Equal(3, result.Count);
    }

    [Fact]
    public async Task GraphQLRequest_ExecuteOptionalAsync_returns_null_on_404_process_failure()
    {
        var query = "query Test { widget }";
        var fake = new FakeGitHub().Fail("POST", "graphql", 404);

        var result = await fake.GraphQL(query)
            .ExecuteOptionalAsync<Widget>(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task GraphQLRequest_ExecuteOptionalAsync_throws_on_malformed_json()
    {
        var query = "query Test { widget }";
        var fake = new FakeGitHub().On("POST", "graphql", "not json");

        await Assert.ThrowsAsync<GitHubApiException>(() =>
            fake.GraphQL(query).ExecuteOptionalAsync<Widget>(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GitHubFunctions_reads_json_directly()
    {
        var fake = new FakeGitHub().On("GET", "repos/owner/repo/widget", "{\"name\":\"direct\",\"count\":7}");
        var spec = new RestRequestSpec("GET", "repos/owner/repo/widget");

        var widget = await GitHubFunctions.ReadJsonAsync<Widget>(fake, spec, cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(widget);
        Assert.Equal("direct", widget.Name);
        Assert.Equal(7, widget.Count);
    }

    [Fact]
    public async Task GitHubFunctions_reads_text_directly()
    {
        var fake = new FakeGitHub().On("GET", "repos/owner/repo/raw", "plain text content");
        var spec = new RestRequestSpec("GET", "repos/owner/repo/raw");

        var text = await GitHubFunctions.ReadTextAsync(fake, spec, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("plain text content", text);
    }

    [Fact]
    public async Task GitHubFunctions_reads_optional_json()
    {
        var spec = new RestRequestSpec("GET", "repos/owner/repo/widget");

        // Success
        var fakeSuccess = new FakeGitHub().On("GET", "repos/owner/repo/widget", "{\"name\":\"direct\",\"count\":7}");
        var widget = await GitHubFunctions.ReadOptionalJsonAsync<Widget>(fakeSuccess, spec, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(widget);
        Assert.Equal("direct", widget.Name);

        // 404 returns null
        var fake404 = new FakeGitHub().Fail("GET", "repos/owner/repo/widget", 404);
        var notFound = await GitHubFunctions.ReadOptionalJsonAsync<Widget>(fake404, spec, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Null(notFound);

        // 500 throws
        var fake500 = new FakeGitHub().Fail("GET", "repos/owner/repo/widget", 500);
        await Assert.ThrowsAsync<GitHubApiException>(() =>
            GitHubFunctions.ReadOptionalJsonAsync<Widget>(fake500, spec, cancellationToken: TestContext.Current.CancellationToken));

        // Malformed JSON throws
        var fakeBad = new FakeGitHub().On("GET", "repos/owner/repo/widget", "invalid-json");
        await Assert.ThrowsAsync<GitHubApiException>(() =>
            GitHubFunctions.ReadOptionalJsonAsync<Widget>(fakeBad, spec, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GitHubFunctions_reads_json_node_and_optional_json_node()
    {
        var spec = new RestRequestSpec("GET", "repos/owner/repo/data");

        // ReadJsonNodeAsync success and failure
        var fake = new FakeGitHub().On("GET", "repos/owner/repo/data", "{\"key\":\"value\"}");
        var node = await GitHubFunctions.ReadJsonNodeAsync(fake, spec, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("value", node["key"]?.GetValue<string>());

        var fakeBad = new FakeGitHub().On("GET", "repos/owner/repo/data", "not-json");
        await Assert.ThrowsAsync<GitHubApiException>(() =>
            GitHubFunctions.ReadJsonNodeAsync(fakeBad, spec, cancellationToken: TestContext.Current.CancellationToken));

        // ReadOptionalJsonNodeAsync success, 404, 500, bad json
        var optSuccess = await GitHubFunctions.ReadOptionalJsonNodeAsync(fake, spec, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(optSuccess);
        Assert.Equal("value", optSuccess["key"]?.GetValue<string>());

        var fake404 = new FakeGitHub().Fail("GET", "repos/owner/repo/data", 404);
        var opt404 = await GitHubFunctions.ReadOptionalJsonNodeAsync(fake404, spec, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Null(opt404);

        var fake500 = new FakeGitHub().Fail("GET", "repos/owner/repo/data", 500);
        await Assert.ThrowsAsync<GitHubApiException>(() =>
            GitHubFunctions.ReadOptionalJsonNodeAsync(fake500, spec, cancellationToken: TestContext.Current.CancellationToken));

        await Assert.ThrowsAsync<GitHubApiException>(() =>
            GitHubFunctions.ReadOptionalJsonNodeAsync(fakeBad, spec, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GitHubFunctions_send_async_and_ensure_success_with_api_request()
    {
        var fake = new FakeGitHub().On("POST", "repos/owner/repo/action", "{\"ok\":true}");
        var req = new ApiRequest("POST", "repos/owner/repo/action");

        var ran = await GitHubFunctions.SendAsync(fake, req, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(ran.Ok);

        var success = await GitHubFunctions.EnsureSuccessAsync(fake, req, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(success.Ok);

        var failFake = new FakeGitHub().Fail("POST", "repos/owner/repo/action", 500);
        await Assert.ThrowsAsync<GitHubApiException>(() =>
            GitHubFunctions.EnsureSuccessAsync(failFake, req, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GitHubFunctions_executes_graphql_directly()
    {
        var fake = new FakeGitHub().On("POST", "graphql", "{\"data\":{\"name\":\"repo-direct\"}}");
        var spec = new GraphQLRequestSpec("query { test }");

        var result = await GitHubFunctions.ExecuteGraphQLAsync<JsonNode>(fake, spec, cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal("repo-direct", result["name"]?.GetValue<string>());

        // Test overload with query string and variables
        var result2 = await GitHubFunctions.ExecuteGraphQLAsync<JsonNode>(fake, "query { test }", new Dictionary<string, object?> { ["var"] = 1 }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(result2);

        // Test optional overload with query string and variables
        var optResult = await GitHubFunctions.ExecuteGraphQLOptionalAsync<JsonNode>(fake, "query { test }", new Dictionary<string, object?> { ["var"] = 1 }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(optResult);

        var optSpecResult = await GitHubFunctions.ExecuteGraphQLOptionalAsync<JsonNode>(fake, spec, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(optSpecResult);
    }

    [Fact]
    public async Task GitHubRestRequest_fluent_methods_and_properties_behave_as_expected()
    {
        var fake = new FakeGitHub();
        var req = new GitHubRestRequest(fake, "POST", "repos/owner/repo/test")
            .WithHeaders(["X-One: 1", "X-Two: 2"])
            .WithQuery("ignored", null)
            .WithQuery("flag", true)
            .WithQueryParameters(new Dictionary<string, object?>
            {
                ["skip"] = null,
                ["limit"] = 10,
                ["search"] = "test & query",
            })
            .WithBody("{\"raw\": true}")
            .Describing("a test rest call");

        fake.On("POST", req.BuildRoute(), "{\"status\":\"ok\"}");

        Assert.Equal("POST", req.Method);
        Assert.Equal("repos/owner/repo/test", req.Route);
        Assert.Equal(["X-One: 1", "X-Two: 2"], req.Headers);
        Assert.Equal("true", req.QueryParameters["flag"]);
        Assert.Equal("10", req.QueryParameters["limit"]);
        Assert.Equal("test & query", req.QueryParameters["search"]);
        Assert.False(req.QueryParameters.ContainsKey("skip"));
        Assert.False(req.QueryParameters.ContainsKey("ignored"));
        Assert.Equal("{\"raw\": true}", req.Body);
        Assert.Equal("a test rest call", req.Description);

        var ran = await req.SendAsync(TestContext.Current.CancellationToken);
        Assert.True(ran.Ok);

        var success = await req.EnsureSuccessAsync(TestContext.Current.CancellationToken);
        Assert.True(success.Ok);

        var text = await req.ReadTextAsync(TestContext.Current.CancellationToken);
        Assert.Contains("status", text, StringComparison.Ordinal);

        var node = await req.ReadOptionalJsonNodeAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(node);
        Assert.Equal("ok", node["status"]?.GetValue<string>());

        var notFoundRan = new Ran(1, "", "HTTP 404: Not Found");
        Assert.True(GitHubRestRequest.IsNotFound(notFoundRan));
        Assert.False(GitHubRestRequest.IsNotFound(ran));

        var missingReq = new GitHubRestRequest(fake.Fail("GET", "repos/owner/missing", 404), "GET", "repos/owner/missing");
        var missingNode = await missingReq.ReadOptionalJsonNodeAsync(TestContext.Current.CancellationToken);
        Assert.Null(missingNode);
    }
}
