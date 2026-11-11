using System.CommandLine;
using System.Net;
using System.Text;
using Rig.Cli.Vault;
using Rig.Core.Processes;
using Rig.Core.Services;
using Rig.Core.Tests;
using Rig.Core.Tests.Fakes;
using Rig.Core.Vault;

namespace Rig.Cli.Tests.Vault;

/// <summary>
/// <c>rig vault knowledge-server</c> through its command line, ported from the Python <c>tests/obsidian/test_knowledge_server.py</c>.
/// The home, the LaunchAgents folder and rig's data folder are temp folders; launchctl, npm and claude are faked, and the
/// health probe never leaves the process.
/// </summary>
public sealed class KnowledgeServerCommandTests : IDisposable
{
    private const string SharedEntry = """{"mcpServers": {"open-knowledge": {"type": "http", "url": "http://127.0.0.1:47311/mcp"}}}""";
    private const string StdioEntry = """{"mcpServers": {"open-knowledge": {"command": "ok"}}}""";

    private readonly TempDir _temp = new();
    private readonly FakeProc _proc = new();
    private readonly StringWriter _out = new();
    private readonly StringWriter _err = new();
    private readonly Dictionary<string, string> _onPath = new(StringComparer.Ordinal);
    private readonly Handler _handler;
    private readonly HttpMessageInvoker _http;
    private HttpStatusCode _health = HttpStatusCode.OK;
    private bool _macOs = true;
    private int _built;

    public KnowledgeServerCommandTests()
    {
        _handler = new Handler(_ => new HttpResponseMessage(_health));
        _http = new HttpMessageInvoker(_handler);
        Places = new KnowledgeServerPlaces(_temp.Folder("home"), Path.Join(_temp.Path, "appdata"));
    }

    private KnowledgeServerPlaces Places { get; }

    private string Plist => Path.Join(Places.Home, "Library", "LaunchAgents", $"{KnowledgeServer.Label}.plist");

    private string Output => _out.ToString().ReplaceLineEndings("\n");

    private string Error => _err.ToString().ReplaceLineEndings("\n");

    public void Dispose()
    {
        _http.Dispose();
        _handler.Dispose();
        _out.Dispose();
        _err.Dispose();
        _temp.Dispose();
    }

    [Theory]
    [InlineData("install")]
    [InlineData("restart")]
    [InlineData("uninstall")]
    public async Task LaunchAgent_commands_do_nothing_off_macos(string name)
    {
        _macOs = false;

        Assert.Equal(KnowledgeServerCommand.NotMacOsExit, await Run(name));

        Assert.Equal("the shared server runs as a macOS LaunchAgent; nothing to do here\n", Output);
        Assert.Equal(0, _built);
        Assert.Empty(_proc.Requests);
    }

    [Fact]
    public async Task Install_loads_the_agent_and_says_how_to_connect()
    {
        Manifest(KnowledgeServer.Version);
        _onPath["node"] = "/bin/node";
        _proc.Answer(new Ran(0, string.Empty, string.Empty), "launchctl");

        Assert.Equal(0, await Run("install"));

        Assert.True(File.Exists(Plist));
        Assert.Contains(_proc.Requests, r => r.Argv.SequenceEqual(["launchctl", "bootstrap", "gui/501", Plist]));
        Assert.Equal(
            $"open-knowledge {KnowledgeServer.Version} LaunchAgent loaded: {Plist}\n"
            + $"editor http://127.0.0.1:47311/  connector http://127.0.0.1:47311/mcp  log {Places.LogPath}\n"
            + "Claude still starts its own copy per session: run `vault knowledge-server connect`.\n",
            Output);
    }

    [Fact]
    public async Task Install_leaves_out_the_connect_hint_once_claude_is_shared()
    {
        Manifest(KnowledgeServer.Version);
        ClaudeJson(SharedEntry);
        _onPath["node"] = "/bin/node";
        _proc.Answer(new Ran(0, string.Empty, string.Empty), "launchctl");

        Assert.Equal(0, await Run("install"));

        Assert.DoesNotContain("own copy per session", Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Install_without_node_reports_it_and_exits_1()
    {
        Manifest(KnowledgeServer.Version);

        Assert.Equal(1, await Run("install"));

        Assert.StartsWith("rig vault knowledge-server install: node is not on PATH", Error, StringComparison.Ordinal);
        Assert.False(File.Exists(Plist));
    }

    [Fact]
    public async Task Restart_kickstarts_the_agent()
    {
        _proc.Answer(new Ran(0, string.Empty, string.Empty), "launchctl");

        Assert.Equal(0, await Run("restart"));

        Assert.Equal("restarted\n", Output);
        Assert.Equal(["launchctl", "kickstart", "-k", $"gui/501/{KnowledgeServer.Label}"], Assert.Single(_proc.Requests).Argv);
    }

    [Fact]
    public async Task A_failed_restart_reports_launchctl_and_exits_1()
    {
        _proc.Answer(new Ran(3, string.Empty, "no such service"), "launchctl");

        Assert.Equal(1, await Run("restart"));

        Assert.StartsWith("rig vault knowledge-server restart: ", Error, StringComparison.Ordinal);
        Assert.Empty(Output);
    }

    [Fact]
    public async Task Uninstall_removes_the_agent_or_says_there_was_none()
    {
        _proc.Answer(new Ran(0, string.Empty, string.Empty), "launchctl");

        Assert.Equal(0, await Run("uninstall"));
        Assert.Equal("no LaunchAgent installed\n", Output);

        Directory.CreateDirectory(Path.GetDirectoryName(Plist)!);
        await File.WriteAllTextAsync(Plist, "<plist/>", TestContext.Current.CancellationToken);
        _out.GetStringBuilder().Clear();

        Assert.Equal(0, await Run("uninstall"));
        Assert.Equal("removed\n", Output);
        Assert.False(File.Exists(Plist));
    }

    [Fact]
    public async Task Connect_stops_when_claude_already_uses_the_shared_server()
    {
        ClaudeJson(SharedEntry);

        Assert.Equal(0, await Run("connect", "--apply"));

        Assert.Equal("already shared: http://127.0.0.1:47311/mcp\n", Output);
        Assert.Empty(_proc.Requests);
    }

    [Fact]
    public async Task Connect_needs_claude_on_path()
    {
        Assert.Equal(KnowledgeServerCommand.ClaudeMissingExit, await Run("connect"));

        Assert.Equal("claude is not on PATH\n", Error);
    }

    [Fact]
    public async Task Connect_without_apply_prints_the_commands_and_runs_nothing()
    {
        ClaudeJson(StdioEntry);
        _onPath["claude"] = "/bin/claude";

        Assert.Equal(0, await Run("connect"));

        Assert.Equal(
            "now: stdio ok\n"
            + "  /bin/claude mcp remove --scope user open-knowledge\n"
            + "  /bin/claude mcp add --scope user --transport http open-knowledge http://127.0.0.1:47311/mcp\n"
            + "dry run: pass --apply to change ~/.claude.json\n",
            Output);
        Assert.Empty(_proc.Requests);
    }

    [Fact]
    public async Task Connect_apply_adds_the_shared_entry()
    {
        _onPath["claude"] = "/bin/claude";
        _proc.Answer(new Ran(0, string.Empty, string.Empty), "/bin/claude");

        Assert.Equal(0, await Run("connect", "--apply"));

        Assert.StartsWith("now: absent\n", Output, StringComparison.Ordinal);
        Assert.EndsWith("connected; new sessions use the shared server\n", Output, StringComparison.Ordinal);
        var request = Assert.Single(_proc.Requests);
        Assert.Equal(["/bin/claude", "mcp", "add", "--scope", "user", "--transport", "http", "open-knowledge", "http://127.0.0.1:47311/mcp"], request.Argv);
        Assert.Equal(KnowledgeServerCommand.ConnectTimeout, request.Timeout);
    }

    [Theory]
    [InlineData("not found", "", "not found")]
    [InlineData("  ", "out only", "out only")]
    public async Task A_failed_claude_mcp_call_stops_connect_with_exit_1(string err, string @out, string why)
    {
        ClaudeJson(StdioEntry);
        _onPath["claude"] = "/bin/claude";
        _proc.Answer(new Ran(1, @out, err), "/bin/claude", "mcp", "remove");

        Assert.Equal(1, await Run("connect", "--apply"));

        Assert.Equal($"mcp remove --scope user open-knowledge failed: {why}\n", Error);
        Assert.Single(_proc.Requests);
        Assert.DoesNotContain("connected", Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Status_is_ok_when_everything_is_in_place()
    {
        Manifest(KnowledgeServer.Version);
        ClaudeJson(SharedEntry);
        Directory.CreateDirectory(Path.GetDirectoryName(Plist)!);
        await File.WriteAllTextAsync(Plist, "<plist/>", TestContext.Current.CancellationToken);
        _proc.Answer(new Ran(0, string.Empty, string.Empty), "launchctl", "print");

        Assert.Equal(0, await Run("status"));

        Assert.Equal(
            $"ok   package       {KnowledgeServer.Version} (pinned {KnowledgeServer.Version})\n"
            + $"ok   LaunchAgent   {Plist}\n"
            + "ok   health        http://127.0.0.1:47311/healthz\n"
            + "ok   claude entry  http http://127.0.0.1:47311/mcp\n",
            Output);
    }

    [Fact]
    public async Task Status_fails_and_says_how_to_fix_it()
    {
        Manifest("0.1.0");
        _health = HttpStatusCode.ServiceUnavailable;

        Assert.Equal(KnowledgeServerCommand.UnhealthyExit, await Run("status"));

        Assert.Equal(
            $"fail package       0.1.0 (pinned {KnowledgeServer.Version})\n"
            + $"fail LaunchAgent   {Plist}\n"
            + "fail health        http://127.0.0.1:47311/healthz\n"
            + "fail claude entry  absent\n"
            + "fix: `vault knowledge-server install`, then `vault knowledge-server connect --apply`\n",
            Output);
        Assert.Empty(_proc.Requests);
    }

    [Fact]
    public async Task Status_says_when_the_package_is_not_installed()
    {
        await Run("status");

        Assert.StartsWith($"fail package       not installed (pinned {KnowledgeServer.Version})\n", Output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, "501\n", 501u)]
    [InlineData(1, "501\n", 0u)]
    [InlineData(0, "who?\n", 0u)]
    public async Task The_user_id_comes_from_id_u(int code, string @out, uint uid)
    {
        _proc.Answer(new Ran(code, @out, string.Empty), "id", "-u");

        Assert.Equal(uid, await KnowledgeServerCommand.UserIdAsync(_proc, TestContext.Current.CancellationToken));
        Assert.Equal(["id", "-u"], Assert.Single(_proc.Requests).Argv);
    }

    private Task<int> Run(params string[] args)
    {
        var command = new KnowledgeServerCommand(_proc, Build, _macOs, Which, _out, _err);
        return command.Build().Parse(args).InvokeAsync(new InvocationConfiguration { Output = _out, Error = _err }, TestContext.Current.CancellationToken);
    }

    private Task<KnowledgeServer> Build(CancellationToken cancellationToken)
    {
        _built++;
        var launchd = new Launchd(_proc, Launchd.AgentsDirUnder(Places.Home), Launchd.GuiDomain(501));
        return Task.FromResult(new KnowledgeServer(_proc, launchd, Places, Which, _http));
    }

    private string? Which(string name) => _onPath.GetValueOrDefault(name);

    private void Manifest(string version)
    {
        var path = Path.Join(Places.Prefix, "node_modules", "@inkeep", "open-knowledge", "package.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $$"""{"version": "{{version}}"}""", new UTF8Encoding(false));
    }

    private void ClaudeJson(string json) => File.WriteAllText(Places.ClaudeJson, json, new UTF8Encoding(false));

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(send(request));
    }
}
