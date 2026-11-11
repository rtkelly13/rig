using System.Text.Json.Nodes;
using Rig.Core.Devenv;

namespace Rig.Core.Tests.Devenv;

/// <summary>OpenCode's defaults merge; ported from <c>test_ai_tools.py</c>.</summary>
public sealed class OpencodeDefaultsTests
{
    [Fact]
    public void The_merge_adds_what_is_missing_and_keeps_what_the_user_set()
    {
        var existing = new JsonObject
        {
            ["agent"] = new JsonObject { ["explore"] = new JsonObject { ["model"] = "my-custom-model" } },
            ["custom_key"] = 123,
        };

        var merged = OpencodeDefaults.Merge(existing)!;

        Assert.Equal("my-custom-model", (string)merged["agent"]!["explore"]!["model"]!);
        Assert.Equal("github-copilot/claude-sonnet-4.6", (string)merged["agent"]!["general"]!["model"]!);
        Assert.Equal(123, (int)merged["custom_key"]!);
        Assert.Equal("https://opencode.ai/config.json", (string)merged["$schema"]!);
        Assert.False(existing["agent"]!.AsObject().ContainsKey("general"));
    }

    [Fact]
    public void A_value_of_another_type_is_kept()
    {
        var merged = OpencodeDefaults.Merge(new JsonObject { ["agent"] = "mine" })!;

        Assert.Equal("mine", (string)merged["agent"]!);
    }

    [Fact]
    public void A_complete_config_needs_no_merge() =>
        Assert.Null(OpencodeDefaults.Merge(OpencodeDefaults.Config));

    [Fact]
    public void The_config_lives_under_dot_config() =>
        Assert.Equal(Path.Join("/h", ".config", "opencode", "config.json"), OpencodeDefaults.ConfigPath("/h"));
}
