using System.Text.Json.Nodes;
using Rig.Core.Estate;
using Rig.Core.Processes;
using Rig.Core.Tests.Fakes;

namespace Rig.Core.Tests.Estate;

/// <summary>Which estate repos GitHub reports archived: query shape and answer parsing, with no network. Ported from <c>tests/test_estate_github_archived.py</c>.</summary>
public sealed class GitHubArchivedTests
{
    private const string Reply = """
        {"data": {
          "r0": {"nameWithOwner": "rtkelly13/The-Vault-System", "isArchived": true},
          "r1": {"nameWithOwner": "rtkelly13/blog", "isArchived": false},
          "r2": null},
         "errors": [{"type": "NOT_FOUND"}]}
        """;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void One_aliased_lookup_per_repo_and_nothing_unsafe_is_sent()
    {
        var query = GitHubArchived.BuildQuery(["rtkelly13/blog", "rtkelly-labs/Diffra", "evil/x\") { x } #"]);

        Assert.Equal(2, query.Split("repository(").Length - 1);
        Assert.Contains("owner: \"rtkelly-labs\", name: \"Diffra\"", query, StringComparison.Ordinal);
        Assert.DoesNotContain("evil", query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Archived_repos_come_back_lowercase_and_deleted_ones_are_absent()
    {
        // gh exits non-zero when any repo is missing, but still prints the rest.
        var github = new Answering(new Ran(1, Reply, "gh: Could not resolve to a Repository"));

        var found = await GitHubArchived.SlugsAsync(github, ["rtkelly13/the-vault-system", "rtkelly13/blog", "rtkelly13/lab"], Token);

        Assert.Equal(["rtkelly13/the-vault-system"], found!);
        var request = Assert.Single(github.Calls);
        Assert.Equal(("POST", "graphql"), (request.Method, request.Route));
        Assert.Contains("repository(", (string)JsonNode.Parse(request.Body!)!["query"]!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_answer_from_github_is_null_not_an_empty_set()
    {
        var offline = new FakeGitHub();

        Assert.Null(await GitHubArchived.SlugsAsync(offline, ["rtkelly13/blog"], Token));
        Assert.Empty((await GitHubArchived.SlugsAsync(offline, [], Token))!);
        _ = Assert.Single(offline.Calls);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"errors": []}""")]
    [InlineData("""{"data": {"r0": {"nameWithOwner": 3, "isArchived": true}}}""")]
    [InlineData("""{"data": {"r0": {"nameWithOwner": "a/b", "isArchived": "yes"}}}""")]
    [InlineData("""{"data": {"r0": []}}""")]
    public void An_unreadable_reply_is_null(string reply) => Assert.Null(GitHubArchived.Parse(reply));

    private sealed class Answering(Ran answer) : IGitHubApi
    {
        public List<ApiRequest> Calls { get; } = [];

        public Task<Ran> CallAsync(ApiRequest request, CancellationToken cancellationToken = default)
        {
            Calls.Add(request);
            return Task.FromResult(answer);
        }
    }
}
