using Rig.Cli.Agent;
using Rig.Core.Processes;
using Rig.Core.Tests.Fakes;

namespace Rig.Cli.Tests.Agent;

/// <summary>Ported from the Python <c>tests/test_agent_launcher.py</c>. Nothing here starts a real agent.</summary>
public sealed class AgentLauncherTests : IDisposable
{
    private const string WindowsCheckout = "C:/code/personal/shared-utilities";

    private readonly DirectoryInfo _home = Directory.CreateTempSubdirectory("rig-agent-");
    private readonly FakeProc _proc = new();
    private readonly StringWriter _out = new();
    private readonly StringWriter _err = new();
    private Func<string, string?> _which = p => $"/bin/{p}";

    public void Dispose()
    {
        _out.Dispose();
        _err.Dispose();
        _home.Delete(recursive: true);
    }

    private string ConfigPath => Path.Join(_home.FullName, "config.json");

    private static AgentLaunch Resolve(params string[] args) => Resolve(new AgentSettings(), args);

    private static AgentLaunch Resolve(AgentSettings settings, params string[] args) =>
        AgentLauncher.Resolve(AgentLauncher.Parse(args), settings, WindowsCheckout);

    private Task<int> Run(params string[] args) =>
        new AgentLauncher(_proc, new AgentHost(ConfigPath, WindowsCheckout, p => _which(p)), _out, _err).RunAsync(args);

    [Theory]
    [InlineData(new[] { "-agy", "status" }, "antigravity", new[] { "status" })]
    [InlineData(new[] { "-agywsl", "--mode", "auto" }, "agywsl", new[] { "--mode", "auto" })]
    [InlineData(new[] { "-oc", "run" }, "opencode", new[] { "run" })]
    [InlineData(new[] { "-opencode", "diff" }, "opencode", new[] { "diff" })]
    [InlineData(new[] { "-codex", "gen" }, "codex", new[] { "gen" })]
    [InlineData(new[] { "-cpl", "x" }, "github-copilot", new[] { "x" })]
    [InlineData(new[] { "-copilot" }, "github-copilot", new string[0])]
    [InlineData(new[] { "--use", "codex", "--flag", "value" }, "codex", new[] { "--flag", "value" })]
    [InlineData(new[] { "--use=opencode", "arg1" }, "opencode", new[] { "arg1" })]
    [InlineData(new[] { "prompt", "-ccc" }, null, new[] { "prompt", "-ccc" })]
    public void Shortcuts_pick_the_agent_and_the_first_other_token_ends_the_launchers_flags(string[] args, string? agent, string[] passthrough)
    {
        var parsed = AgentLauncher.Parse(args);

        Assert.Equal(agent, parsed.Agent);
        Assert.Equal(passthrough, parsed.Passthrough);
    }

    [Fact]
    public void The_launchers_own_flags_are_consumed()
    {
        var parsed = AgentLauncher.Parse(["-ccc", "-r", "--print-config", "-h", "--dng", "explain this"]);

        Assert.Equal(new AgentArgs("claude-code", true, true, true, true, parsed.Passthrough), parsed);
        Assert.Equal(["explain this"], parsed.Passthrough);
        Assert.True(AgentLauncher.Parse(["-dng"]).Danger);
        Assert.True(AgentLauncher.Parse(["--dangerously-skip-permissions", "-oc", "run"]).Danger);
        Assert.True(AgentLauncher.Parse(["--remote-control"]).RemoteControl);
        Assert.True(AgentLauncher.Parse(["--help"]).Help);
    }

    [Fact]
    public void Use_needs_an_agent_id()
    {
        var problem = Assert.Throws<ArgumentException>(() => AgentLauncher.Parse(["--use"]));

        Assert.Equal("--use requires an agent id", problem.Message);
    }

    [Theory]
    [InlineData("gemini", "antigravity")]
    [InlineData("claude", "claude-code")]
    [InlineData("copilot", "github-copilot")]
    [InlineData("antigravity", "antigravity")]
    [InlineData("unknown-agent", "unknown-agent")]
    public void Aliases_map_to_canonical_ids(string alias, string id) => Assert.Equal(id, AgentLauncher.CanonicalId(alias));

    [Theory]
    [InlineData(@"C:\code\personal\shared-utilities", "/mnt/c/code/personal/shared-utilities")]
    [InlineData("C:/code/personal/shared-utilities", "/mnt/c/code/personal/shared-utilities")]
    [InlineData("d:/repo/project", "/mnt/d/repo/project")]
    [InlineData("/mnt/c/code", "/mnt/c/code")]
    [InlineData("/home/user/project", "/home/user/project")]
    public void Windows_paths_map_into_wsl(string path, string wsl) => Assert.Equal(wsl, AgentLauncher.WslPath(path));

    [Theory]
    [InlineData(new[] { "-agy", "arg1", "--flag" }, new[] { "agy", "arg1", "--flag" })]
    [InlineData(new[] { "-ccc" }, new[] { "claude", "--model", "opus" })]
    [InlineData(new[] { "-ccc", "-r", "some prompt" }, new[] { "claude", "--remote-control", "--model", "opus", "some prompt" })]
    [InlineData(new[] { "-ccc", "-r", "--remote-control" }, new[] { "claude", "--remote-control", "--model", "opus" })]
    [InlineData(new[] { "-ccc", "--model", "sonnet", "prompt" }, new[] { "claude", "--model", "sonnet", "prompt" })]
    [InlineData(new[] { "-ccc", "--model=haiku" }, new[] { "claude", "--model=haiku" })]
    [InlineData(
        new[] { "-agywsl", "explain", "code" },
        new[] { "wsl", "-d", "Ubuntu", "--cd", "/mnt/c/code/personal/shared-utilities", "agy", "--mode", "auto", "explain", "code" })]
    [InlineData(
        new[] { "-agywsl", "--mode", "manual", "prompt" },
        new[] { "wsl", "-d", "Ubuntu", "--cd", "/mnt/c/code/personal/shared-utilities", "agy", "--mode", "manual", "prompt" })]
    [InlineData(new[] { "-agy", "-dng", "status" }, new[] { "agy", "--dangerously-skip-permissions", "status" })]
    [InlineData(new[] { "-ccc", "-dng", "prompt" }, new[] { "claude", "--dangerously-skip-permissions", "--model", "opus", "prompt" })]
    [InlineData(
        new[] { "-ccc", "-dng", "-r", "prompt" },
        new[] { "claude", "--remote-control", "--dangerously-skip-permissions", "--model", "opus", "prompt" })]
    [InlineData(
        new[] { "-agywsl", "-dng", "prompt" },
        new[] { "wsl", "-d", "Ubuntu", "--cd", "/mnt/c/code/personal/shared-utilities", "agy", "--mode", "auto", "--dangerously-skip-permissions", "prompt" })]
    [InlineData(new[] { "-oc", "-dng", "run" }, new[] { "opencode", "--auto", "run" })]
    [InlineData(new[] { "-codex", "-dng", "prompt" }, new[] { "codex", "--dangerously-bypass-approvals-and-sandbox", "prompt" })]
    [InlineData(new[] { "--use", "github-copilot", "-dng", "prompt" }, new[] { "copilot", "--allow-all", "prompt" })]
    [InlineData(new[] { "-cpl", "-dng", "prompt" }, new[] { "copilot", "--allow-all", "prompt" })]
    [InlineData(new[] { "-agy", "-dng", "--dangerously-skip-permissions", "status" }, new[] { "agy", "--dangerously-skip-permissions", "status" })]
    [InlineData(new[] { "-oc", "-dng", "--auto", "run" }, new[] { "opencode", "--auto", "run" })]
    [InlineData(new[] { "-codex", "-dng", "--yolo", "prompt" }, new[] { "codex", "--yolo", "prompt" })]
    [InlineData(new[] { "--use", "github-copilot", "-dng", "--yolo", "prompt" }, new[] { "copilot", "--yolo", "prompt" })]
    [InlineData(new[] { "--use", "gemini" }, new[] { "agy" })]
    public void Each_agent_gets_its_command_line(string[] args, string[] command) =>
        Assert.Equal(command, Resolve(args).Command);

    [Fact]
    public void The_resolution_names_the_agent_and_its_program()
    {
        var agy = Resolve("-agy");
        Assert.Equal(("antigravity", "Antigravity CLI", "agy"), (agy.Id, agy.DisplayName, agy.Executable));
        var wsl = Resolve("-agywsl");
        Assert.Equal(("agywsl", "Antigravity CLI (WSL Ubuntu)", "wsl"), (wsl.Id, wsl.DisplayName, wsl.Executable));
        var claude = Resolve();
        Assert.Equal(("claude-code", "Claude Code", "claude"), (claude.Id, claude.DisplayName, claude.Executable));
    }

    [Fact]
    public void Stored_settings_pick_the_agent_danger_model_and_remote_control()
    {
        Assert.Equal(["claude", "--remote-control", "--model", "haiku"], Resolve(new AgentSettings("claude-code", ClaudeModel: "haiku", ClaudeRemoteControl: true)).Command);
        Assert.Equal(["agy", "--dangerously-skip-permissions"], Resolve(new AgentSettings("antigravity", Danger: true)).Command);
        Assert.Equal(["agy"], Resolve(new AgentSettings("gemini")).Command);
        Assert.Equal(["claude", "--model", "opus"], Resolve(new AgentSettings("vim")).Command);
        Assert.Equal(["claude", "--model", "opus"], Resolve(new AgentSettings(string.Empty, ClaudeModel: string.Empty)).Command);
        Assert.Equal(["codex"], Resolve(new AgentSettings("antigravity"), "-codex").Command);
    }

    [Fact]
    public void An_unknown_agent_id_is_refused_with_the_valid_ones()
    {
        var problem = Assert.Throws<ArgumentException>(() => Resolve("--use", "invalid-agent"));

        Assert.Equal(
            "Unknown agent id 'invalid-agent'. Valid ids: antigravity, claude-code, agywsl, opencode, codex, github-copilot.",
            problem.Message);
    }

    [Fact]
    public void Settings_come_from_tools_agent_in_the_workspace_config()
    {
        Assert.Equal(
            new AgentSettings("codex", true, "sonnet", true),
            AgentSettings.Parse("""{"tools": {"agent": {"preferred": "codex", "danger": true, "defaults": {"claude-code": {"model": "sonnet", "remote_control": true}}}}, "other": 1}"""));
        Assert.Equal(
            new AgentSettings(ClaudeModel: "haiku"),
            AgentSettings.Parse("""{"tools": {"agent": {"defaults": {"claude_code": {"model": "haiku"}}}}}"""));
        Assert.Equal(new AgentSettings(), AgentSettings.Parse("""{"tools": {"agent": {"preferred": null}}}"""));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"tools": {}}""")]
    [InlineData("""{"tools": {"agent": {"preferred": "codex", "danger": "yes"}}}""")]
    [InlineData("""{"tools": {"agent": {"preferred": 3}}}""")]
    public void A_missing_or_malformed_config_gives_the_defaults(string json) =>
        Assert.Equal(new AgentSettings(), AgentSettings.Parse(json));

    [Theory]
    [InlineData(null, "/home/me/.workspace/config.json")]
    [InlineData("", "/home/me/.workspace/config.json")]
    [InlineData("/elsewhere", "/elsewhere/config.json")]
    [InlineData("~/ws", "/home/me/ws/config.json")]
    public void Workspace_home_moves_the_config(string? workspaceHome, string path) =>
        Assert.Equal(path, AgentHost.ConfigUnder(workspaceHome, "/home/me").Replace('\\', '/'));

    [Fact]
    public async Task Print_config_shows_the_agent_and_command()
    {
        Assert.Equal(0, await Run("--print-config", "-agy", "-dng", "status"));

        Assert.Equal("agent: Antigravity CLI (antigravity)\ncommand: agy --dangerously-skip-permissions status\n", _out.ToString().ReplaceLineEndings("\n"));
        Assert.Empty(_proc.Requests);
    }

    [Fact]
    public async Task Print_config_shows_the_name_the_launch_will_run()
    {
        _which = p => p == "opencode2" ? "/bin/opencode2" : null;

        Assert.Equal(0, await Run("-oc", "--print-config", "run"));

        Assert.Contains("command: opencode2 run\n", _out.ToString().ReplaceLineEndings("\n"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_config_file_is_read()
    {
        await File.WriteAllTextAsync(ConfigPath, """{"tools": {"agent": {"preferred": "codex", "danger": true}}}""", TestContext.Current.CancellationToken);

        Assert.Equal(0, await Run("--print-config"));

        Assert.Contains("command: codex --dangerously-bypass-approvals-and-sandbox\n", _out.ToString().ReplaceLineEndings("\n"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Help_prints_the_usage()
    {
        Assert.Equal(0, await Run("-h", "--use", "nonsense"));

        var help = _out.ToString();
        Assert.StartsWith("Usage: agent", help, StringComparison.Ordinal);
        foreach (var flag in new[] { "-agy", "-ccc", "-agywsl", "-dng", "antigravity, claude-code" })
        {
            Assert.Contains(flag, help, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(new[] { "--use" }, "error: --use requires an agent id\n")]
    [InlineData(new[] { "--use", "bad" }, "error: Unknown agent id 'bad'. Valid ids: antigravity, claude-code, agywsl, opencode, codex, github-copilot.\n")]
    public async Task A_bad_line_is_an_error(string[] args, string message)
    {
        Assert.Equal(2, await Run(args));

        Assert.Equal(message, _err.ToString().ReplaceLineEndings("\n"));
    }

    [Fact]
    public async Task A_missing_agent_says_so_with_127()
    {
        _which = _ => null;

        Assert.Equal(127, await Run("-agy"));

        Assert.Equal("Antigravity CLI ('agy') was not found on PATH. Install it and try again.\n", _err.ToString().ReplaceLineEndings("\n"));
        Assert.Empty(_proc.Requests);
    }

    [Fact]
    public async Task The_agent_runs_attached_by_its_found_path_and_its_exit_code_comes_back()
    {
        _which = p => p == "opencode2" ? "/opt/bin/opencode2" : null;
        _ = _proc.Answer(new Ran(42, string.Empty, string.Empty), "/opt/bin/opencode2");

        Assert.Equal(42, await Run("-oc", "run"));

        var request = Assert.Single(_proc.Requests);
        Assert.Equal(["/opt/bin/opencode2", "run"], request.Argv);
        Assert.True(request.Attached);
        Assert.Null(request.Timeout);
    }

    [Theory]
    [InlineData(new string[0], new string[0])]
    [InlineData(new[] { "-ccc", "x" }, new[] { "-ccc", "x" })]
    [InlineData(new[] { "--print-config" }, new[] { "--print-config" })]
    [InlineData(new[] { "agent", "-h" }, new[] { "-h" })]
    [InlineData(new[] { "models" }, null)]
    [InlineData(new[] { "--help" }, null)]
    public void A_launch_is_a_bare_rig_a_shortcut_or_agent(string[] args, string[]? launcher) =>
        Assert.Equal(launcher, AgentLauncher.LauncherArgs(args));
}
