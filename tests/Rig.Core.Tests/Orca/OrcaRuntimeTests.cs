using System.IO.Pipes;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using Rig.Core.Orca;

namespace Rig.Core.Tests.Orca;

/// <summary><see cref="OrcaRuntime"/>, <see cref="OrcaLive"/> and the socket exchange. Ported from <c>tests/test_estate_orca_runtime.py</c> and the RPC half of <c>tests/test_workspace_orca.py</c>.</summary>
public sealed class OrcaRuntimeTests : IDisposable
{
    private const string Metadata = """{"authToken": "t0k", "transports": [{"kind": "tcp", "endpoint": "x"}, {"kind": "unix", "endpoint": "/run/orca.sock"}]}""";

    private readonly TempDir _dir = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Metadata_picks_the_first_usable_transport()
    {
        var runtime = OrcaRuntime.Parse(Metadata, new ScriptedExchange());

        Assert.NotNull(runtime);
        Assert.Equal((new OrcaTransport("unix", "/run/orca.sock"), "t0k"), (runtime.Transport, runtime.AuthToken));
    }

    [Fact]
    public void Metadata_from_older_orca_names_one_transport() =>
        Assert.Equal(
            new OrcaTransport("named-pipe", @"\\.\pipe\orca"),
            OrcaRuntime.Parse("""{"authToken": "t", "transport": {"kind": "named-pipe", "endpoint": "\\\\.\\pipe\\orca"}}""", new ScriptedExchange())?.Transport);

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"transports": [{"kind": "unix", "endpoint": "/s"}]}""")]
    [InlineData("""{"authToken": "", "transports": [{"kind": "unix", "endpoint": "/s"}]}""")]
    [InlineData("""{"authToken": "t", "transports": [{"kind": "tcp", "endpoint": "/s"}, {"kind": "unix", "endpoint": ""}, 1]}""")]
    [InlineData("""{"authToken": "t"}""")]
    public void Unusable_metadata_is_null(string metadata) => Assert.Null(OrcaRuntime.Parse(metadata, new ScriptedExchange()));

    [Fact]
    public void Missing_or_unusable_metadata_files_mean_orca_is_not_running()
    {
        var missing = Assert.Throws<OrcaUnavailableException>(() => OrcaRuntime.FromMetadata(Path.Join(_dir.Path, "none.json"), new ScriptedExchange()));
        var empty = Assert.Throws<OrcaUnavailableException>(() => OrcaRuntime.FromMetadata(_dir.File("orca-runtime.json"), new ScriptedExchange()));

        Assert.Contains("is Orca running?", missing.Message, StringComparison.Ordinal);
        Assert.Contains("restart Orca", empty.Message, StringComparison.Ordinal);
        Assert.Null(OrcaLive.TryConnect(Path.Join(_dir.Path, "none.json"), new ScriptedExchange()));
    }

    [Fact]
    public void A_metadata_file_connects()
    {
        var path = Path.Join(_dir.Path, "orca-runtime.json");
        File.WriteAllText(path, Metadata);

        Assert.Equal("t0k", OrcaLive.TryConnect(path, new ScriptedExchange())?.AuthToken);
    }

    [Fact]
    public async Task A_call_skips_noise_and_other_answers_until_its_own()
    {
        var exchange = new ScriptedExchange(
            "not json",
            """{"_keepalive": true}""",
            """{"id": "other", "ok": true, "result": 1}""",
            """{"id": "r1", "ok": true, "result": {"answer": 42}}""");
        var runtime = Runtime(exchange);

        var result = await runtime.CallAsync("repo.list", new JsonObject { ["x"] = "y" }, Token);

        Assert.Equal(42, (int?)result?["answer"]);
        var sent = Assert.Single(exchange.Requests);
        Assert.Equal(new OrcaTransport("unix", "/s"), sent.Transport);
        Assert.Equal("""{"id":"r1","authToken":"t","method":"repo.list","params":{"x":"y"}}""" + "\n", sent.Request);
    }

    [Theory]
    [InlineData("""{"id": "r1", "ok": false, "error": {"code": "bad", "message": "no such repo"}}""", "Orca refused repo.update: bad no such repo")]
    [InlineData("""{"id": "r1"}""", "Orca refused repo.update:")]
    public async Task A_refusal_names_the_method_and_error(string frame, string message)
    {
        var error = await Assert.ThrowsAsync<OrcaUnavailableException>(() => Runtime(new ScriptedExchange(frame)).CallAsync("repo.update", null, Token));

        Assert.Equal(message, error.Message);
    }

    [Fact]
    public async Task A_connection_closed_before_answering_is_unavailable()
    {
        var error = await Assert.ThrowsAsync<OrcaUnavailableException>(() => Runtime(new ScriptedExchange("""{"_keepalive": true}""")).CallAsync("repo.list", null, Token));

        Assert.Contains("closed the connection", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Transport_failures_and_timeouts_are_unavailable_but_the_callers_cancellation_is_not()
    {
        await Assert.ThrowsAsync<OrcaUnavailableException>(() => Runtime(new ScriptedExchange { Failure = new IOException("refused") }).CallAsync("m", null, Token));
        await Assert.ThrowsAsync<OrcaUnavailableException>(() => Runtime(new ScriptedExchange { Failure = new SocketException() }).CallAsync("m", null, Token));
        await Assert.ThrowsAsync<OrcaUnavailableException>(() => Runtime(new ScriptedExchange { Failure = new OperationCanceledException() }).CallAsync("m", null, Token));

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Runtime(new ScriptedExchange { Failure = new OperationCanceledException() }).CallAsync("m", null, cancelled.Token));
    }

    [Fact]
    public async Task Launch_lines_are_read_and_sent_whole()
    {
        var exchange = new ScriptedExchange(Answer("""{"settings": {"agentDefaultArgs": {"claude": "--permission-mode auto"}}}"""));
        var lines = await Runtime(exchange).SetAgentDefaultArgsAsync(new Dictionary<string, string> { ["claude"] = "--permission-mode auto" }, Token);

        Assert.Equal("--permission-mode auto", lines["claude"]);
        Assert.Contains("""
            "method":"settings.update","params":{"agentDefaultArgs":{"claude":"--permission-mode auto"}}
            """, exchange.Requests[0].Request, StringComparison.Ordinal);
        Assert.Empty(await Runtime(new ScriptedExchange(Answer("""{"settings": {}}"""))).AgentDefaultArgsAsync(Token));
        Assert.Empty(await Runtime(new ScriptedExchange(Answer("null"))).AgentDefaultArgsAsync(Token));
        await Assert.ThrowsAsync<OrcaUnavailableException>(() => Runtime(new ScriptedExchange(Answer("""{"settings": {"agentDefaultArgs": {"claude": 1}}}"""))).AgentDefaultArgsAsync(Token));
    }

    [Fact]
    public async Task A_repo_update_names_the_repo_by_path()
    {
        var exchange = new ScriptedExchange(Answer("{}"));

        await Runtime(exchange).UpdateRepoAsync("/code/rig", new RepoUpdates("#000"), Token);

        Assert.Contains("""
            "method":"repo.update","params":{"repo":"path:/code/rig","updates":{"badgeColor":"#000"}}
            """, exchange.Requests[0].Request, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_missing_list_is_an_unknown_shape_but_missing_groups_are_none()
    {
        _ = await Assert.ThrowsAsync<OrcaUnavailableException>(() => Runtime(new ScriptedExchange(Answer("{}"))).WorktreesAsync(Token));
        _ = await Assert.ThrowsAsync<OrcaUnavailableException>(() => Runtime(new ScriptedExchange(Answer("""{"truncated": false}"""))).ActivityAsync(Token));
        Assert.Empty(await Runtime(new ScriptedExchange(Answer("{}"))).GroupsAsync(Token));
        Assert.Empty(await Runtime(new ScriptedExchange(Answer("{}"))).FolderWorkspacesAsync(Token));
    }

    [Theory]
    [InlineData("""[1]""")]
    [InlineData("""{"state": "working"}""")]
    public async Task Unreadable_agents_fail_the_activity_call(string agents) =>
        _ = await Assert.ThrowsAsync<OrcaUnavailableException>(() =>
            Runtime(new ScriptedExchange(Answer($$"""{"worktrees": [{"worktreeId": "w", "agents": {{agents}}}]}"""))).ActivityAsync(Token));

    [Fact]
    public async Task Live_reports_success_and_failure_without_throwing()
    {
        var up = new OrcaLive(Runtime(new ScriptedExchange(Answer("{}"))));
        var refusing = new OrcaLive(Runtime(new ScriptedExchange("""{"id": "r1", "ok": false}""")));
        var down = new OrcaLive(null);

        Assert.True(await up.UpdateRepoAsync("/r", new RepoUpdates("#000"), Token));
        Assert.False(await refusing.UpdateRepoAsync("/r", new RepoUpdates("#000"), Token));
        Assert.False(await down.UpdateRepoAsync("/r", new RepoUpdates("#000"), Token));
        Assert.Null(await refusing.LaunchArgsAsync(Token));
        Assert.Null(await down.LaunchArgsAsync(Token));
        Assert.Null(await refusing.SendLaunchLinesAsync(new Dictionary<string, string>(), Token));
        Assert.Null(await down.SendLaunchLinesAsync(new Dictionary<string, string>(), Token));
    }

    [Fact]
    public async Task Live_launch_lines_merge_onto_what_orca_serves()
    {
        var ids = new Queue<string>(["r1", "r2", "r3"]);
        var exchange = new ScriptedExchange(
            Answer("""{"settings": {"agentDefaultArgs": {"claude": "", "codex": "--search"}}}"""),
            """{"id": "r2", "ok": true, "result": {"settings": {"agentDefaultArgs": {"claude": "--permission-mode auto", "codex": "--search"}}}}""");
        var live = new OrcaLive(new OrcaRuntime(new("unix", "/s"), "t", exchange, ids.Dequeue));

        var served = await live.SendLaunchLinesAsync(new Dictionary<string, string> { ["claude"] = "--permission-mode auto" }, Token);

        Assert.Equal("--permission-mode auto", served?["claude"]);
        Assert.Contains("""
            "agentDefaultArgs":{"claude":"--permission-mode auto","codex":"--search"}
            """, exchange.Requests[1].Request, StringComparison.Ordinal);
        Assert.Equal("", (await new OrcaLive(new OrcaRuntime(new("unix", "/s"), "t", new ScriptedExchange(Answer("""{"settings": {"agentDefaultArgs": {"claude": ""}}}""")), () => "r1")).LaunchArgsAsync(Token))?["claude"]);
    }

    [Fact]
    public async Task The_socket_exchange_talks_over_a_unix_socket()
    {
        var path = Path.Join(Path.GetTempPath(), $"o{Guid.NewGuid():N}"[..10]);
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(path));
        listener.Listen(1);
        try
        {
            var server = Task.Run(
                async () =>
                {
                    using var client = await listener.AcceptAsync(Token);
                    await using var stream = new NetworkStream(client);
                    using var reader = new StreamReader(stream, Encoding.UTF8);
                    var request = await reader.ReadLineAsync(Token);
                    await stream.WriteAsync(Encoding.UTF8.GetBytes($"{{\"_keepalive\":true}}\n{{\"id\":\"r1\",\"ok\":true,\"result\":{request}}}\n"), Token);
                },
                Token);

            var result = await new OrcaRuntime(new(OrcaTransport.Unix, path), "t", new SocketOrcaExchange(), () => "r1").CallAsync("ping", null, Token);
            await server;

            Assert.Equal("ping", (string?)result?["method"]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task The_socket_exchange_talks_over_a_named_pipe()
    {
        var name = $"rig-orca-{Guid.NewGuid():N}"[..20];
        await using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var server = Task.Run(
            async () =>
            {
                await pipe.WaitForConnectionAsync(Token);
                using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
                _ = await reader.ReadLineAsync(Token);
                await pipe.WriteAsync("""{"id":"r1","ok":true,"result":"pong"}"""u8.ToArray().Append((byte)'\n').ToArray(), Token);
                pipe.Disconnect();
            },
            Token);

        var result = await new OrcaRuntime(new(OrcaTransport.NamedPipe, $@"\\.\pipe\{name}"), "t", new SocketOrcaExchange(), () => "r1").CallAsync("ping", null, Token);
        await server;

        Assert.Equal("pong", (string?)result);
    }

    [Fact]
    public async Task An_unreachable_socket_is_unavailable()
    {
        var runtime = new OrcaRuntime(new(OrcaTransport.Unix, Path.Join(_dir.Path, "gone.sock")), "t", new SocketOrcaExchange(TimeSpan.FromSeconds(5)));

        await Assert.ThrowsAsync<OrcaUnavailableException>(() => runtime.CallAsync("ping", null, Token));
    }

    [Theory]
    [InlineData(@"\\.\pipe\orca-1", "orca-1")]
    [InlineData("orca-1", "orca-1")]
    public void Pipe_names_drop_their_prefix(string endpoint, string name) => Assert.Equal(name, SocketOrcaExchange.PipeName(endpoint));

    private static OrcaRuntime Runtime(ScriptedExchange exchange) => new(new("unix", "/s"), "t", exchange, () => "r1");

    private static string Answer(string result) => $$"""{"id": "r1", "ok": true, "result": {{result}}}""";

    /// <summary>Answers every request with the same lines, or fails with <see cref="Failure"/>.</summary>
    private sealed class ScriptedExchange(params string[] lines) : IOrcaExchange
    {
        private int _calls;

        public List<(OrcaTransport Transport, string Request)> Requests { get; } = [];

        public Exception? Failure { get; init; }

        public async IAsyncEnumerable<string> ExchangeAsync(OrcaTransport transport, string request, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Requests.Add((transport, request));
            await Task.Yield();
            if (Failure is not null)
            {
                throw Failure;
            }

            // Each request sees the lines from its own position on, so a second call can be answered by a later line.
            foreach (var line in lines.Skip(_calls++))
            {
                yield return line;
            }
        }
    }
}
