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
            ["agents"] = new JsonObject { ["explore"] = new JsonObject { ["model"] = "my-custom-model" } },
            ["custom_key"] = 123,
        };

        var merged = OpencodeDefaults.Merge(existing)!;

        Assert.Equal("my-custom-model", (string)merged["agents"]!["explore"]!["model"]!);
        Assert.Equal("github-copilot/claude-sonnet-4.6", (string)merged["agents"]!["general"]!["model"]!);
        Assert.Equal(123, (int)merged["custom_key"]!);
        Assert.Equal("https://opencode.ai/config.json", (string)merged["$schema"]!);
        Assert.False(existing["agents"]!.AsObject().ContainsKey("general"));
    }

    [Fact]
    public void A_value_of_another_type_is_kept()
    {
        var merged = OpencodeDefaults.Merge(new JsonObject { ["agents"] = "mine" })!;

        Assert.Equal("mine", (string)merged["agents"]!);
    }

    [Fact]
    public void A_legacy_agent_entry_wins_over_the_default()
    {
        var existing = new JsonObject
        {
            ["agent"] = new JsonObject { ["explore"] = new JsonObject { ["model"] = "my-custom-model" } },
        };

        var merged = OpencodeDefaults.Merge(existing)!;

        Assert.False(merged["agents"]!.AsObject().ContainsKey("explore"));
        Assert.Equal("github-copilot/claude-sonnet-4.6", (string)merged["agents"]!["general"]!["model"]!);
    }

    [Fact]
    public void A_complete_config_needs_no_merge() =>
        Assert.Null(OpencodeDefaults.Merge(OpencodeDefaults.Config));

    [Theory]
    [InlineData(new string[0], "opencode.json")]
    [InlineData(new[] { "config.json" }, "config.json")]
    [InlineData(new[] { "config.json", "opencode.jsonc" }, "opencode.jsonc")]
    [InlineData(new[] { "config.json", "opencode.jsonc", "opencode.json" }, "opencode.json")]
    public void The_config_path_prefers_the_file_opencode_reads(string[] present, string chosen)
    {
        using var dir = new TempDir();
        foreach (var name in present)
        {
            var path = Path.Join(dir.Path, ".config", "opencode", name);
            _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "{}");
        }

        Assert.Equal(Path.Join(dir.Path, ".config", "opencode", chosen), OpencodeDefaults.ConfigPath(dir.Path));
    }
}
