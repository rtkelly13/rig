using System.Text.Json;
using System.Text.Json.Nodes;
using Rig.Core.GitHub;
using Rig.Core.Processes;
using Rig.Core.Tests.Fakes;

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
}
