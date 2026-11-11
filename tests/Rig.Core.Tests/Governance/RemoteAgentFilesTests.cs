using System.Text.Json.Nodes;
using Rig.Core.Governance;
using Rig.Core.Processes;
using Rig.Core.Tests.Fakes;

namespace Rig.Core.Tests.Governance;

/// <summary>The GitHub-side sweep for agent files that shadow AGENTS.md, over a fake <c>gh</c>.</summary>
public sealed class RemoteAgentFilesTests
{
    private readonly FakeProc _gh = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void Legacy_and_bespoke_names_are_both_reported() =>
        Assert.Equal(["CLAUDE.md", ".github/copilot-instructions.md"], RemoteAgentFiles.FindRedundant(["README.md", "CLAUDE.md", ".github/copilot-instructions.md"]));

    [Fact]
    public void A_folder_entry_is_reported_once_however_many_files_it_holds() =>
        Assert.Equal([".github/instructions"], RemoteAgentFiles.FindRedundant([".github/instructions/a.instructions.md", ".github/instructions/b.instructions.md"]));

    [Theory]
    [InlineData(".github/agents/workflows.agent.md", ".github/prompts/review.prompt.md", ".claude/skills/x/SKILL.md")]
    [InlineData("AGENTS.md", "README.md", ".github/workflows/ci.yml")]
    public void Tool_features_and_clean_trees_report_nothing(params string[] paths) => Assert.Empty(RemoteAgentFiles.FindRedundant(paths));

    [Fact]
    public async Task An_unlistable_owner_or_unreadable_tree_is_unread_not_clean()
    {
        List("rtkelly13", Row("rtkelly13/docs", "main"), Row("rtkelly13/locked", "main"));
        Tree("rtkelly13/docs", "main", "AGENTS.md");

        var sweep = await Sweep("rtkelly13", "someone-else");

        Assert.Empty(sweep.Findings);
        Assert.Equal(["rtkelly13/locked", "someone-else"], sweep.Unread);
    }

    [Fact]
    public async Task Archived_repos_are_reported_and_repos_without_a_branch_skipped()
    {
        List("rtkelly13", Row("rtkelly13/the-vault-system", "main", archived: true), Row("rtkelly13/docs", "main"), Row("rtkelly13/empty", null));
        Tree("rtkelly13/docs", "main", "AGENTS.md", "CLAUDE.md");
        Tree("rtkelly13/the-vault-system", "main", "AGENTS.md", "CLAUDE.md");

        var sweep = await Sweep("rtkelly13");

        Assert.Empty(sweep.Unread);
        Assert.Equal(
            [new RemoteAgentFile("rtkelly13/docs", "CLAUDE.md", false), new RemoteAgentFile("rtkelly13/the-vault-system", "CLAUDE.md", true)],
            sweep.Findings);
        Assert.Equal(
            ["gh", "api", "repos/rtkelly13/the-vault-system/git/trees/main?recursive=1"],
            _gh.Requests[1].Argv);
        Assert.Equal("""{"repo": "rtkelly13/docs", "path": "CLAUDE.md", "archived": false}""", Rig.Core.Policy.PyJson.DumpsLine(sweep.Findings[0].ToJson()));
    }

    [Fact]
    public async Task Forks_are_skipped_unless_a_profile_registers_them()
    {
        List("rtkelly13", Row("rtkelly13/nodatime", "master", fork: true), Row("rtkelly13/Resultful", "main", fork: true));
        Tree("rtkelly13/nodatime", "master", "CLAUDE.md");
        Tree("rtkelly13/Resultful", "main", "CLAUDE.md");

        var unregistered = await Sweep("rtkelly13");
        var registered = await new RemoteAgentFiles(_gh).SweepAsync(["rtkelly13"], name => name.EndsWith("/Resultful", StringComparison.Ordinal), Token);

        Assert.Equal(RemoteSweep.None.Findings, unregistered.Findings);
        Assert.Equal(["rtkelly13/Resultful"], registered.Findings.Select(f => f.Repo));
    }

    [Theory]
    [InlineData("""[{"nameWithOwner": 3}]""")]
    [InlineData("""[{"nameWithOwner": "o/r", "isFork": "no"}]""")]
    [InlineData("""[{"nameWithOwner": "o/r", "defaultBranchRef": "main"}]""")]
    [InlineData("""{"not": "a list"}""")]
    [InlineData("not json")]
    [InlineData("")]
    public async Task A_listing_of_the_wrong_shape_is_unread(string listing)
    {
        _ = _gh.Answer(new Ran(0, listing, string.Empty), "gh", "repo", "list", "o");

        Assert.Equal(["o"], (await Sweep("o")).Unread);
    }

    [Theory]
    [InlineData("""{"tree": {}}""", true)]
    [InlineData("""{"tree": [3]}""", true)]
    [InlineData("""{"tree": [{"path": 3}]}""", true)]
    [InlineData("""{"truncated": false}""", false)]
    [InlineData("""{"tree": [{"type": "tree"}, {"path": ""}]}""", false)]
    public async Task A_tree_of_the_wrong_shape_is_unread(string tree, bool unread)
    {
        _ = _gh.Answer(new Ran(0, """[{"nameWithOwner": "o/r", "defaultBranchRef": {"name": "main"}}]""", string.Empty), "gh", "repo", "list", "o");
        _ = _gh.Answer(new Ran(0, tree, string.Empty), "gh", "api", "repos/o/r/git/trees/main?recursive=1");

        var sweep = await Sweep("o");

        Assert.Equal(unread ? ["o/r"] : [], sweep.Unread);
        Assert.Empty(sweep.Findings);
    }

    [Fact]
    public async Task A_branch_ref_without_a_name_is_skipped()
    {
        _ = _gh.Answer(new Ran(0, """[{"nameWithOwner": "o/r", "defaultBranchRef": {}}]""", string.Empty), "gh", "repo", "list", "o");

        var sweep = await Sweep("o");

        Assert.Equal((0, 0), (sweep.Findings.Count, sweep.Unread.Count));
        Assert.Single(_gh.Requests);
    }

    private static JsonObject Row(string name, string? branch, bool archived = false, bool fork = false) => new()
    {
        ["nameWithOwner"] = name,
        ["isArchived"] = archived,
        ["isFork"] = fork,
        ["defaultBranchRef"] = branch is null ? null : new JsonObject { ["name"] = branch },
    };

    private void List(string owner, params JsonObject[] rows) =>
        _gh.Answer(new Ran(0, new JsonArray(rows).ToJsonString(), string.Empty), "gh", "repo", "list", owner, "--limit", "200", "--json", "nameWithOwner,isArchived,isFork,defaultBranchRef");

    private void Tree(string repo, string branch, params string[] paths) =>
        _gh.Answer(
            new Ran(0, new JsonObject { ["tree"] = new JsonArray([.. paths.Select(p => (JsonNode)new JsonObject { ["path"] = p })]) }.ToJsonString(), string.Empty),
            "gh", "api", $"repos/{repo}/git/trees/{branch}?recursive=1");

    private Task<RemoteSweep> Sweep(params string[] owners) => new RemoteAgentFiles(_gh).SweepAsync(owners, _ => false, Token);
}
