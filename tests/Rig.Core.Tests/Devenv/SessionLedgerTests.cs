using System.Text;
using System.Text.Json.Nodes;
using Rig.Core.Devenv;

namespace Rig.Core.Tests.Devenv;

/// <summary>The session-ledger hook: recognising an install, wiring it once, and the state doctor reports. Ported from <c>test_session_ledger.py</c>.</summary>
public sealed class SessionLedgerTests : IDisposable
{
    private const string Command = "node \"/home/me/.claude/hooks/session-ledger.mjs\"";
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private string HookPath => SessionLedger.HookPath(_dir.Path);

    private static JsonObject Settings(string json) => JsonNode.Parse(json)!.AsObject();

    [Theory]
    [InlineData("node \"C:/Users/me/.claude/hooks/session-ledger.mjs\"", true)]
    [InlineData("node C:\\Users\\me\\.claude\\hooks\\session-ledger.mjs", true)]
    [InlineData("node --no-warnings /home/me/hooks/session-ledger.mjs", true)]
    [InlineData("\"/usr/bin/node.exe\" ~/session-ledger.mjs", true)]
    [InlineData("/home/me/.claude/hooks/session-ledger.mjs", true)]
    [InlineData("NODE /home/me/session-ledger.mjs", true)]
    [InlineData("echo session-ledger.mjs", false)]
    [InlineData("node \"/home/me/other.mjs\" session-ledger.mjs", false)]
    [InlineData("node --flag", false)]
    [InlineData("node \"unterminated", false)]
    [InlineData("", false)]
    public void Detection_matches_the_script_basename(string command, bool expected) =>
        Assert.Equal(expected, SessionLedger.RunsLedger(command));

    [Fact]
    public void The_command_uses_forward_slashes() =>
        Assert.Equal("node \"C:/Users/me/.claude/hooks/session-ledger.mjs\"", SessionLedger.Command("C:\\Users\\me\\.claude\\hooks\\session-ledger.mjs"));

    [Fact]
    public void Merge_adds_the_hook_once()
    {
        var settings = new JsonObject();

        Assert.Equal([$"+ hooks.SessionEnd: {Command}"], SessionLedger.Merge(settings, Command));
        Assert.Empty(SessionLedger.Merge(settings, Command));
        var hook = settings["hooks"]!["SessionEnd"]!.AsArray().Single()!["hooks"]!.AsArray().Single()!;
        Assert.Equal("command", (string?)hook["type"]);
        Assert.Equal(Command, (string?)hook["command"]);
        Assert.Equal(5, (int?)hook["timeout"]);
    }

    [Fact]
    public void An_install_by_the_workspace_devenv_is_not_duplicated()
    {
        var settings = Settings("""{"hooks":{"SessionEnd":[{"hooks":[{"type":"command","command":"node C:\\ws\\session-ledger.mjs"}]}]}}""");

        Assert.Empty(SessionLedger.Merge(settings, Command));
    }

    [Fact]
    public void Other_session_end_hooks_are_kept_beside_the_ledger()
    {
        var settings = Settings("""{"hooks":{"SessionEnd":[{"hooks":[{"type":"command","command":"echo bye"}]}],"Stop":[]}}""");

        Assert.Single(SessionLedger.Merge(settings, Command));
        Assert.Equal(2, settings["hooks"]!["SessionEnd"]!.AsArray().Count);
        Assert.NotNull(settings["hooks"]!["Stop"]);
    }

    [Theory]
    [InlineData("""{"hooks":[]}""")]
    [InlineData("""{"hooks":null}""")]
    [InlineData("""{"hooks":{"SessionEnd":{}}}""")]
    [InlineData("""{"hooks":{"SessionEnd":null}}""")]
    public void Misshapen_hooks_are_left_alone(string json)
    {
        var settings = Settings(json);

        Assert.Empty(SessionLedger.Merge(settings, Command));
        Assert.Equal(Settings(json).ToJsonString(), settings.ToJsonString());
    }

    [Theory]
    [InlineData("""[1, {"hooks": "no"}, {"hooks": [2, {"command": 3}, {"command": "echo"}]}]""")]
    [InlineData("""[{"nohooks": []}]""")]
    public void Malformed_groups_do_not_count_as_wired(string groups) =>
        Assert.False(SessionLedger.HasHook(JsonNode.Parse(groups)!.AsArray()));

    [Fact]
    public void State_walks_from_not_wired_to_current()
    {
        var wired = new JsonObject();
        _ = SessionLedger.Merge(wired, SessionLedger.Command(HookPath));

        Assert.Equal(LedgerHookState.NotWired, SessionLedger.State([], HookPath));
        Assert.Equal(LedgerHookState.NotWired, SessionLedger.State(Settings("""{"hooks":[]}"""), HookPath));
        Assert.Equal(LedgerHookState.ScriptMissing, SessionLedger.State(wired, HookPath));

        File.WriteAllText(_dir.File(".claude/hooks/session-ledger.mjs"), "// an older script\n");
        Assert.Equal(LedgerHookState.ScriptOutdated, SessionLedger.State(wired, HookPath));

        SessionLedger.InstallScript(HookPath);
        Assert.Equal(LedgerHookState.Current, SessionLedger.State(wired, HookPath));
    }

    [Fact]
    public void A_crlf_copy_of_the_script_is_current()
    {
        var crlf = Encoding.UTF8.GetString(SessionLedger.Script).Replace("\n", "\r\n", StringComparison.Ordinal);
        File.WriteAllText(_dir.File(".claude/hooks/session-ledger.mjs"), crlf);

        Assert.True(SessionLedger.ScriptCurrent(HookPath));
    }

    [Fact]
    public void The_installed_script_is_the_bundled_one_with_lf_endings()
    {
        SessionLedger.InstallScript(HookPath);

        var installed = File.ReadAllBytes(HookPath);
        Assert.Equal(SessionLedger.Script.ToArray(), installed);
        Assert.DoesNotContain((byte)'\r', installed);
        Assert.Contains("SessionEnd", Encoding.UTF8.GetString(installed), StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_script_is_not_current() => Assert.False(SessionLedger.ScriptCurrent(HookPath));

    [Theory]
    [InlineData(LedgerHookState.Current, "installed")]
    [InlineData(LedgerHookState.NotWired, "not wired in settings.json")]
    [InlineData(LedgerHookState.ScriptMissing, "script missing")]
    [InlineData(LedgerHookState.ScriptOutdated, "script out of date")]
    public void States_are_described_as_doctor_printed_them(LedgerHookState state, string text) =>
        Assert.Equal(text, state.Describe());
}
