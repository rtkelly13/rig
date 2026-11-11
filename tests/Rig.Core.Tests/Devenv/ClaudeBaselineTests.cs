using System.Text.Json.Nodes;
using Rig.Core.Devenv;

namespace Rig.Core.Tests.Devenv;

/// <summary>The Claude Code settings baseline: a union that never clobbers. Ported from <c>test_ai_tools.py</c>.</summary>
public sealed class ClaudeBaselineTests : IDisposable
{
    private const string Hook = "node \"/h/.claude/hooks/session-ledger.mjs\"";
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private static SettingsMerge Merge(string json) => ClaudeBaseline.Merge(JsonNode.Parse(json)!.AsObject(), Hook);

    [Fact]
    public void An_empty_file_gets_the_whole_baseline_in_python_order()
    {
        var merge = Merge("{}");

        Assert.Equal(
            [
                "+ permissions.defaultMode: 'acceptEdits'",
                "+ model: 'opus-5.5'",
                "+ effortLevel: 'medium'",
                "+ autoCompactEnabled: True",
                "+ autoCompactWindow: 200000",
                "+ skipWorkflowUsageWarning: True",
                "+ workflowSizeGuideline: 'small'",
                "+ env.CLAUDE_CODE_MAX_CONCURRENT_SUBAGENTS: '3'",
                "+ env.CLAUDE_CODE_WORKFLOW_MAX_CONCURRENT_AGENTS: '3'",
                "+ attribution.commit: ''",
                "+ attribution.pr: ''",
                "+ attribution.sessionUrl: False",
                "+ mcpServers.chrome-devtools",
                $"+ hooks.SessionEnd: {Hook}",
            ],
            merge.Changes);
        Assert.Equal(
            ["permissions", "model", "effortLevel", "autoCompactEnabled", "autoCompactWindow", "skipWorkflowUsageWarning", "workflowSizeGuideline", "env", "attribution", "mcpServers", "hooks"],
            merge.Settings.Select(p => p.Key));
        Assert.Equal("""{"command":"npx","args":["-y","chrome-devtools-mcp@latest"]}""", merge.Settings["mcpServers"]!["chrome-devtools"]!.ToJsonString());
    }

    [Fact]
    public void Existing_preferences_and_permissions_are_never_clobbered()
    {
        var merge = Merge("""{"model":"claude-3-5-sonnet","permissions":{"allow":["Bash(my_custom_script:*)", 7],"deny":["Read(.env)"],"defaultMode":"dontAcceptEdits"}}""");

        Assert.Equal("claude-3-5-sonnet", (string?)merge.Settings["model"]);
        Assert.Equal("""{"allow":["Bash(my_custom_script:*)",7],"deny":["Read(.env)"],"defaultMode":"dontAcceptEdits"}""", merge.Settings["permissions"]!.ToJsonString());
        Assert.DoesNotContain(merge.Changes, c => c.Contains("permissions", StringComparison.Ordinal) || c.Contains("model", StringComparison.Ordinal));
    }

    [Fact]
    public void Caps_join_the_env_beside_existing_variables_and_a_set_cap_is_kept()
    {
        var merge = Merge("""{"env":{"MSBUILDDISABLENODEREUSE":"1","CLAUDE_CODE_MAX_CONCURRENT_SUBAGENTS":"6"}}""");

        Assert.Equal(
            """{"MSBUILDDISABLENODEREUSE":"1","CLAUDE_CODE_MAX_CONCURRENT_SUBAGENTS":"6","CLAUDE_CODE_WORKFLOW_MAX_CONCURRENT_AGENTS":"3"}""",
            merge.Settings["env"]!.ToJsonString());
        Assert.Contains("+ env.CLAUDE_CODE_WORKFLOW_MAX_CONCURRENT_AGENTS: '3'", merge.Changes);
        Assert.DoesNotContain(merge.Changes, c => c.Contains("CLAUDE_CODE_MAX_CONCURRENT_SUBAGENTS", StringComparison.Ordinal));
    }

    [Fact]
    public void Custom_attribution_is_kept()
    {
        var merge = Merge("""{"attribution":{"pr":"Custom PR footer","commit":"Assisted-by: Developer","sessionUrl":true}}""");

        Assert.Equal("""{"pr":"Custom PR footer","commit":"Assisted-by: Developer","sessionUrl":true}""", merge.Settings["attribution"]!.ToJsonString());
        Assert.DoesNotContain(merge.Changes, c => c.Contains("attribution", StringComparison.Ordinal));
    }

    [Fact]
    public void A_hand_configured_mcp_server_keeps_its_definition()
    {
        const string servers = """{"chrome-devtools":{"command":"custom","args":["--mine"],"env":{"API_KEY":"secret"}},"obsidian":{"type":"http","url":"http://127.0.0.1:27123/mcp/"}}""";
        var merge = Merge($$"""{"mcpServers":{{servers}}}""");

        Assert.Equal(servers, merge.Settings["mcpServers"]!.ToJsonString());
        Assert.DoesNotContain(merge.Changes, c => c.Contains("mcpServers", StringComparison.Ordinal));
    }

    [Fact]
    public void Misshapen_sections_are_replaced_in_place()
    {
        var merge = Merge("""{"permissions":["Bash(git:*)"],"mcpServers":"none","env":null,"attribution":3,"statusLine":{"type":"command"}}""");

        Assert.Equal("""{"defaultMode":"acceptEdits"}""", merge.Settings["permissions"]!.ToJsonString());
        Assert.Equal(["chrome-devtools"], merge.Settings["mcpServers"]!.AsObject().Select(p => p.Key));
        Assert.Equal(["permissions", "mcpServers", "env", "attribution", "statusLine"], merge.Settings.Select(p => p.Key).Take(5));
        Assert.Equal("""{"type":"command"}""", merge.Settings["statusLine"]!.ToJsonString());
    }

    [Fact]
    public void The_input_is_not_changed()
    {
        var existing = new JsonObject { ["model"] = "sonnet" };

        _ = ClaudeBaseline.Merge(existing, Hook);

        Assert.Equal("""{"model":"sonnet"}""", existing.ToJsonString());
    }

    [Fact]
    public void A_missing_file_loads_as_empty() => Assert.Equal("{}", SettingsFile.Load(_dir.Path + "/none.json")!.ToJsonString());

    [Theory]
    [InlineData("[1, 2]")]
    [InlineData("not json")]
    [InlineData("""{"a": 1, "a": 2}""")]
    [InlineData("")]
    public void A_file_that_is_not_one_json_object_is_unreadable(string text)
    {
        var path = _dir.File("settings.json");
        File.WriteAllText(path, text);

        Assert.Null(SettingsFile.Load(path));
    }

    [Theory]
    [InlineData(false, "\n")]
    [InlineData(true, "\r\n")]
    public void Save_writes_python_json_and_leaves_no_staging_file(bool windows, string newline)
    {
        var path = Path.Join(_dir.Path, ".claude", "settings.json");

        SettingsFile.Save(path, JsonNode.Parse("""{"name":"Zoë","list":[1],"tab":"\t"}""")!.AsObject(), windows);

        Assert.Equal(
            "{\n  \"name\": \"Zoë\",\n  \"list\": [\n    1\n  ],\n  \"tab\": \"\\t\"\n}\n".Replace("\n", newline, StringComparison.Ordinal),
            File.ReadAllText(path));
        Assert.Equal(["settings.json"], Directory.GetFiles(Path.GetDirectoryName(path)!).Select(Path.GetFileName));
    }

    [Fact]
    public void A_failed_save_removes_its_staging_file_and_keeps_the_original()
    {
        var path = _dir.Folder("settings.json"); // a folder where the file goes: the final move fails

        _ = Assert.ThrowsAny<IOException>(() => SettingsFile.Save(path, [], windows: false));

        Assert.True(Directory.Exists(path));
        Assert.Equal(["settings.json"], Directory.GetFileSystemEntries(_dir.Path).Select(Path.GetFileName));
    }

    [Theory]
    [InlineData("\"it's\"", "\"it's\"")]
    [InlineData("\"both ' and \\\"\"", "'both \\' and \"'")]
    [InlineData("\"a\\\\b\"", "'a\\\\b'")]
    [InlineData("7", "7")]
    public void Repr_is_python_repr(string json, string expected) =>
        Assert.Equal(expected, ClaudeBaseline.Repr(JsonNode.Parse(json)!.AsValue()));
}
