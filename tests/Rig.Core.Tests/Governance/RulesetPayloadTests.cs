using System.Text.Json.Nodes;
using Rig.Core.Governance;

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

    [Fact]
    public void RulesetShape_From_parses_valid_and_rejects_malformed_nodes()
    {
        Assert.Null(RulesetShape.From(null));
        Assert.Null(RulesetShape.From(new JsonArray()));
        Assert.Null(RulesetShape.From(new JsonObject { ["conditions"] = "not an object" }));
        Assert.Null(RulesetShape.From(new JsonObject { ["conditions"] = new JsonObject { ["ref_name"] = new JsonObject { ["include"] = 123 } } }));
        Assert.Null(RulesetShape.From(new JsonObject { ["bypass_actors"] = 123 }));
        Assert.Null(RulesetShape.From(new JsonObject { ["rules"] = "not an array" }));
        Assert.Null(RulesetShape.From(new JsonObject { ["rules"] = new JsonArray("not an object") }));

        var valid = new JsonObject
        {
            ["enforcement"] = "active",
            ["target"] = "branch",
            ["conditions"] = new JsonObject
            {
                ["ref_name"] = new JsonObject
                {
                    ["include"] = new JsonArray("refs/heads/main"),
                    ["exclude"] = new JsonArray(),
                },
            },
            ["bypass_actors"] = new JsonArray(new JsonObject { ["actor_id"] = 1 }),
            ["rules"] = new JsonArray(
                new JsonObject
                {
                    ["type"] = "pull_request",
                    ["parameters"] = new JsonObject { ["required_approving_review_count"] = 1L },
                },
                new JsonObject
                {
                    ["type"] = "pull_request",
                    ["parameters"] = new JsonObject { ["required_approving_review_count"] = 2L },
                }),
        };

        var shape = RulesetShape.From(valid);
        Assert.NotNull(shape);
        Assert.Equal("active", shape.Enforcement);
        Assert.Equal("branch", shape.Target);
        Assert.Equal(["refs/heads/main"], shape.Include);
        Assert.Empty(shape.Exclude!);
        Assert.Equal(1, shape.BypassActorCount);
        Assert.Single(shape.Rules);
        Assert.Contains("2", shape.Rules[0].Value, StringComparison.Ordinal);
    }
}
