using System.Net;
using System.Text;
using Rig.Core.Processes;
using Rig.Core.Services;
using Rig.Core.Tests.Fakes;
using Rig.Core.Vault;

namespace Rig.Core.Tests.Vault;

/// <summary>
/// What the oracle cannot show of <see cref="KnowledgeServer"/>: installing, the LaunchAgent, the health probe and reading
/// <c>~/.claude.json</c> from disk. Nothing here runs npm, launchctl or claude, or touches the real home.
/// </summary>
public sealed class KnowledgeServerTests : IDisposable
{
    private readonly TempDir temp = new();
    private readonly FakeProc proc = new();
    private readonly Dictionary<string, string> onPath = new(StringComparer.Ordinal);
    private Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send =
        (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));

    private readonly Handler handler;
    private readonly HttpMessageInvoker http;

    public KnowledgeServerTests()
    {
        handler = new Handler((r, t) => send(r, t));
        http = new HttpMessageInvoker(handler);
        Places = new KnowledgeServerPlaces(temp.Folder("home"), Path.Join(temp.Path, "appdata"));
        Agents = Path.Join(Places.Home, "Library", "LaunchAgents");
    }

    private KnowledgeServerPlaces Places { get; }

    private string Agents { get; }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        http.Dispose();
        handler.Dispose();
        temp.Dispose();
    }

    [Fact]
    public void Places_are_under_the_home_and_app_data_folders()
    {
        Assert.Equal(Path.Join(Places.Home, "code", "personal", "obsidian-vault"), Places.ProjectRoot);
        Assert.Equal(Path.Join(Places.AppData, "open-knowledge"), Places.Prefix);
        Assert.Equal(Path.Join(Places.AppData, "logs", "open-knowledge.log"), Places.LogPath);
        Assert.Equal(Path.Join(Places.Home, ".claude.json"), Places.ClaudeJson);
        Assert.Equal(Path.Join(Agents, "dev.rtkelly.rig.open-knowledge.plist"), Server().PlistPath);
        Assert.Same(Places, Server().Places);
        Assert.Equal("http://127.0.0.1:8080/mcp", KnowledgeServer.McpUrl(8080));
    }

    [Fact]
    public async Task Install_is_skipped_when_the_pinned_version_is_there()
    {
        Manifest(Places.Prefix, $$"""{"version": "{{KnowledgeServer.Version}}"}""");

        var entry = await Server().InstallAsync(cancellationToken: Token);

        Assert.Equal(Path.Join(Places.Prefix, "node_modules", "@inkeep", "open-knowledge", "dist", "cli.mjs"), entry);
        Assert.Empty(proc.Requests);
    }

    [Fact]
    public async Task Install_runs_npm_into_the_prefix_when_another_version_is_there()
    {
        Manifest(Places.Prefix, """{"version": "0.1.0"}""");
        onPath["npm"] = "/bin/npm";
        proc.Answer(new Ran(0, string.Empty, string.Empty), "/bin/npm");

        await Server().InstallAsync(cancellationToken: Token);

        var request = Assert.Single(proc.Requests);
        Assert.Equal(["/bin/npm", "install", "--prefix", Places.Prefix, "--no-fund", "--no-audit", "@inkeep/open-knowledge@0.89.4"], request.Argv);
        Assert.Equal(KnowledgeServer.InstallTimeout, request.Timeout);
    }

    [Fact]
    public async Task Install_into_a_new_prefix_creates_it_and_a_failed_npm_throws()
    {
        var prefix = Path.Join(temp.Path, "fresh");
        onPath["npm"] = "/bin/npm";
        proc.Answer(new Ran(1, string.Empty, "E404"), "/bin/npm");

        await Assert.ThrowsAsync<ProcException>(() => Server().InstallAsync(prefix, Token));

        Assert.True(Directory.Exists(prefix));
    }

    [Fact]
    public async Task Install_without_npm_says_so()
    {
        var error = await Assert.ThrowsAsync<FileNotFoundException>(() => Server().InstallAsync(cancellationToken: Token));

        Assert.Contains("npm is not on PATH", error.Message, StringComparison.Ordinal);
        Assert.Empty(proc.Requests);
    }

    [Fact]
    public async Task Installing_the_service_writes_the_plist_and_bootstraps_it()
    {
        onPath["node"] = "/bin/node";
        proc.Answer(new Ran(0, string.Empty, string.Empty), "launchctl");
        var server = Server();

        var path = await server.InstallServiceAsync("/x/cli.mjs", cancellationToken: Token);

        Assert.Equal(server.PlistPath, path);
        Assert.Equal(server.Render("/bin/node", "/x/cli.mjs", Places.ProjectRoot), await File.ReadAllBytesAsync(path, Token));
        Assert.True(Directory.Exists(Path.GetDirectoryName(Places.LogPath)));
        Assert.Equal([["launchctl", "bootout", "gui/501", path], ["launchctl", "bootstrap", "gui/501", path]], proc.Requests.Select(r => r.Argv));
        Assert.True(server.ServiceInstalled);
    }

    [Fact]
    public async Task Installing_the_service_without_node_says_so()
    {
        var error = await Assert.ThrowsAsync<FileNotFoundException>(() => Server().InstallServiceAsync("/x/cli.mjs", "/root", Token));

        Assert.Contains("node is not on PATH", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Uninstalling_removes_an_installed_service_only()
    {
        var server = Server();
        Assert.False(server.ServiceInstalled);
        Assert.False(await server.UninstallServiceAsync(Token));

        Directory.CreateDirectory(Agents);
        await File.WriteAllTextAsync(server.PlistPath, "plist", Token);

        Assert.True(await server.UninstallServiceAsync(Token));
        Assert.False(File.Exists(server.PlistPath));
        Assert.Equal(["launchctl", "bootout", "gui/501", server.PlistPath], Assert.Single(proc.Requests).Argv);
    }

    [Fact]
    public async Task Loaded_and_restart_go_to_launchctl_by_label()
    {
        proc.Answer(new Ran(0, string.Empty, string.Empty), "launchctl");

        Assert.True(await Server().ServiceLoadedAsync(Token));
        await Server().RestartAsync(Token);

        Assert.Equal(
            [["launchctl", "print", "gui/501/dev.rtkelly.rig.open-knowledge"], ["launchctl", "kickstart", "-k", "gui/501/dev.rtkelly.rig.open-knowledge"]],
            proc.Requests.Select(r => r.Argv));
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, true)]
    [InlineData(HttpStatusCode.NoContent, false)]
    [InlineData(HttpStatusCode.Found, false)]
    [InlineData(HttpStatusCode.ServiceUnavailable, false)]
    public async Task Only_a_200_from_healthz_is_healthy(HttpStatusCode status, bool healthy)
    {
        Uri? asked = null;
        send = (request, _) =>
        {
            asked = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(status));
        };

        Assert.Equal(healthy, await Server().HealthyAsync(cancellationToken: Token));
        Assert.Equal(new Uri("http://127.0.0.1:47311/healthz"), asked);
    }

    [Fact]
    public async Task A_refused_connection_is_unhealthy()
    {
        send = (_, _) => throw new HttpRequestException("refused");

        Assert.False(await Server().HealthyAsync(cancellationToken: Token));
    }

    [Fact]
    public async Task A_server_that_does_not_answer_in_time_is_unhealthy()
    {
        send = async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        };

        Assert.False(await Server().HealthyAsync(timeout: TimeSpan.FromMilliseconds(50), cancellationToken: Token));
    }

    [Fact]
    public async Task Cancelling_the_probe_is_not_read_as_unhealthy()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        send = (_, token) => Task.FromCanceled<HttpResponseMessage>(token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Server().HealthyAsync(cancellationToken: cancelled.Token));
    }

    [Fact]
    public async Task The_current_registration_is_read_from_claude_json()
    {
        Assert.Equal(Registration.Absent, Server().CurrentRegistration());

        await File.WriteAllTextAsync(
            Places.ClaudeJson,
            """{"mcpServers": {"open-knowledge": {"type": "http", "url": "http://127.0.0.1:47311/mcp"}}}""",
            Token);

        Assert.True(Server().CurrentRegistration().Shared);
    }

    [Fact]
    public void A_claude_json_that_is_a_folder_reads_as_absent()
    {
        Directory.CreateDirectory(Places.ClaudeJson);

        Assert.Equal(Registration.Absent, Server().CurrentRegistration());
    }

    [Fact]
    public void Named_floating_point_literals_make_claude_json_unreadable()
    {
        // pydantic's parser took NaN and Infinity; System.Text.Json does not. Claude Code never writes them.
        var text = """{"x": NaN, "mcpServers": {"open-knowledge": {"type": "http"}}}"""u8.ToArray();

        Assert.Equal(Registration.Absent, KnowledgeServer.ReadRegistration(text));
    }

    [Fact]
    public void A_manifest_that_cannot_be_read_has_no_version()
    {
        Directory.CreateDirectory(Path.Join(temp.Path, "p", "node_modules", "@inkeep", "open-knowledge", "package.json"));

        Assert.Null(KnowledgeServer.InstalledVersion(Path.Join(temp.Path, "p")));
        Assert.Null(KnowledgeServer.InstalledVersion(Path.Join(temp.Path, "missing")));
    }

    private KnowledgeServer Server() =>
        new(proc, new Launchd(proc, Agents, "gui/501"), Places, onPath.GetValueOrDefault, http);

    private static void Manifest(string prefix, string json)
    {
        var path = Path.Join(prefix, "node_modules", "@inkeep", "open-knowledge", "package.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json, new UTF8Encoding(false));
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }
}
