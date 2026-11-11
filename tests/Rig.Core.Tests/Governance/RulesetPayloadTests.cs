using System.Text.Json.Nodes;
using Rig.Core.Governance;
using Rig.Core.Policy;

namespace Rig.Core.Tests.Governance;

/// <summary>The ruleset every governed repo carries: review threads must be resolved, and checks are never borrowed.</summary>
public sealed class RulesetPayloadTests
{
    private const string Registered = "acme/widget";

    private const string Unregistered = "acme/no-such-registered-repo";

    private static readonly RepoPolicies Policies = RepoPolicies.Parse(
        new JsonObject
        {
            ["defaults"] = new JsonObject { ["expected_owner"] = "acme", ["allowed_owners"] = new JsonArray("acme") },
            ["repo_overrides"] = new JsonObject { ["widget"] = new JsonObject { ["required_checks"] = new JsonArray("ci") } },
        },
        "policy.json");

    private static JsonObject? Rule(string repo, string type) =>
        RulesetResource.Payload(Policies, repo)["rules"]!.AsArray().Select(r => r!.AsObject()).FirstOrDefault(r => (string?)r["type"] == type);

    [Theory]
    [InlineData(Registered)]
    [InlineData(Unregistered)]
    public void Every_ruleset_requires_review_threads_resolved(string repo)
    {
        var pullRequest = Rule(repo, "pull_request");

        Assert.NotNull(pullRequest);
        Assert.True((bool)pullRequest["parameters"]!["required_review_thread_resolution"]!);
    }

    [Fact]
    public void An_unregistered_repo_gets_no_status_checks_rather_than_borrowed_ones()
    {
        Assert.NotNull(Rule(Registered, "required_status_checks"));
        Assert.Null(Rule(Unregistered, "required_status_checks"));
    }
}
