using System.CommandLine;
using System.Net;
using System.Net.Sockets;
using Rig.Cli.Web;
using Rig.Core.Otel;
using Rig.Core.Tests;
using Rig.Web;

namespace Rig.Cli.Tests.Web;

/// <summary><c>rig web</c>: the host on the asked port, the Python routes as its child, both stopped together.</summary>
public sealed class WebCommandTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly BannerWriter _output = new();
    private readonly StringWriter _error = new();
    private readonly FakeChild _child = new();
    private readonly List<IReadOnlyList<string>> _started = [];
    private readonly List<Uri> _opened = [];
    private Uri? _pythonRoutes;

    // Accepts one connection, answers it with `body` and returns its request line, giving up when `cancel` fires.
    private static async Task<string> AnswerOnceAsync(TcpListener listener, string body, CancellationToken cancel)
    {
        using var client = await listener.AcceptTcpClientAsync(cancel);
        var stream = client.GetStream();
        using var reader = new StreamReader(stream, leaveOpen: true);
        var requestLine = await reader.ReadLineAsync(cancel) ?? "";
        while (!string.IsNullOrEmpty(await reader.ReadLineAsync(cancel)))
        {
            // The headers; the request has no body.
        }

        var response = System.Text.Encoding.ASCII.GetBytes(
            $"HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}");
        await stream.WriteAsync(response, cancel);
        return requestLine;
    }

    [Fact]
    public async Task Serves_until_stopped_then_stops_the_python_routes()
    {
        using var stop = new CancellationTokenSource();
        string? status = null;

        var code = await RunAsync(["web", "--port", "0"], stop, async page =>
        {
            using var client = new HttpClient();
            status = await client.GetStringAsync(new Uri(page, "/api/status"));
        });

        Assert.Equal(0, code);
        Assert.Contains("\"environment\": \"PROD\"", status, StringComparison.Ordinal);
        var argv = Assert.Single(_started);
        Assert.Equal(["web", "upstream", "--port"], argv.Take(3));
        Assert.True(int.Parse(argv[3], System.Globalization.CultureInfo.InvariantCulture) > 0);
        Assert.Equal(4, argv.Count);
        Assert.Equal(TimeSpan.FromSeconds(5), _child.StoppedWith);
        Assert.StartsWith("[PROD MODE] Rig Web Dashboard running at http://127.0.0.1:", _output.ToString(), StringComparison.Ordinal);
        Assert.Single(_opened);
    }

    [Fact]
    public async Task Preview_shows_in_the_banner_and_not_in_the_python_routes()
    {
        using var stop = new CancellationTokenSource();

        var code = await RunAsync(["web", "--port", "0", "--preview"], stop, _ => Task.CompletedTask);

        Assert.Equal(0, code);
        Assert.Equal(4, Assert.Single(_started).Count);
        Assert.StartsWith("[PREVIEW MODE]", _output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_open_opens_nothing()
    {
        using var stop = new CancellationTokenSource();

        var code = await RunAsync(["web", "--port", "0", "--no-open"], stop, _ => Task.CompletedTask);

        Assert.Equal(0, code);
        Assert.Empty(_opened);
    }

    [Fact]
    public async Task A_browser_that_will_not_open_is_reported_and_the_host_still_serves()
    {
        using var stop = new CancellationTokenSource();

        var code = await RunAsync(["web", "--port", "0"], stop, _ => throw new System.ComponentModel.Win32Exception("no browser"));

        Assert.Equal(0, code);
        Assert.Contains("rig web: could not open a browser: no browser", _error.ToString(), StringComparison.Ordinal);
        Assert.Contains("Stopping server...", _output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_taken_port_is_exit_2_and_the_python_routes_still_stop()
    {
        using var taken = new TcpListener(IPAddress.Loopback, 0);
        taken.Start();
        var port = ((IPEndPoint)taken.LocalEndpoint).Port;
        using var stop = new CancellationTokenSource();

        var code = await RunAsync(["web", "--port", port.ToString(System.Globalization.CultureInfo.InvariantCulture), "--no-open"], stop, _ => Task.CompletedTask);

        Assert.Equal(2, code);
        Assert.Contains($"could not listen on 127.0.0.1:{port}", _error.ToString(), StringComparison.Ordinal);
        Assert.NotNull(_child.StoppedWith);
    }

    [Fact]
    public async Task Python_routes_that_exit_on_start_are_reported_and_the_host_still_serves()
    {
        _child.HasExited = true;
        using var stop = new CancellationTokenSource();

        var code = await RunAsync(["web", "--port", "0"], stop, _ => Task.CompletedTask);

        Assert.Equal(0, code);
        Assert.Contains("the Python web routes exited on start; only the routes ported to C# will answer.", _error.ToString(), StringComparison.Ordinal);
        Assert.Null(_child.StoppedWith);
    }

    [Fact]
    public async Task Python_routes_already_running_are_fronted_and_no_child_is_started()
    {
        using var upstream = new TcpListener(IPAddress.Loopback, 0);
        upstream.Start();
        _pythonRoutes = new Uri($"http://127.0.0.1:{((IPEndPoint)upstream.LocalEndpoint).Port}");
        using var stop = new CancellationTokenSource();
        string? forwarded = null;
        string? requestLine = null;

        var code = await RunAsync(["web", "--port", "0"], stop, async page =>
        {
            using var client = new HttpClient();
            var get = client.GetStringAsync(new Uri(page, "/api/not-ported"));
            using var bound = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            bound.CancelAfter(TimeSpan.FromSeconds(30));
            requestLine = await AnswerOnceAsync(upstream, "from the running routes", bound.Token);
            forwarded = await get;
        });

        Assert.Equal(0, code);
        Assert.Equal("from the running routes", forwarded);
        Assert.Equal("GET /api/not-ported HTTP/1.1", requestLine);
        Assert.Empty(_started);
    }

    [Theory]
    [InlineData("http://127.0.0.1:5123", "http://127.0.0.1:5123/")]
    [InlineData("https://127.0.0.1:5123", null)]
    [InlineData("127.0.0.1:5123", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void The_running_routes_variable_takes_only_an_absolute_http_url(string? configured, string? expected) =>
        Assert.Equal(expected, WebPlaces.PythonRoutesFrom(configured)?.ToString());

    public void Dispose()
    {
        _output.Dispose();
        _error.Dispose();
        _dir.Dispose();
    }

    // Runs the command; once the host is up, `opened` sees its address and then the run is stopped.
    private async Task<int> RunAsync(string[] args, CancellationTokenSource stop, Func<Uri, Task> opened)
    {
        var command = new WebCommand(
            new WebPlaces(_dir.Path, Path.Join(_dir.Path, "managed-projects.json"), _dir.Path, Path.Join(_dir.Path, "rig.db"), _dir.Path, _pythonRoutes),
            argv =>
            {
                _started.Add(argv);
                return _child;
            },
            new AlwaysListening(),
            page =>
            {
                _opened.Add(page);
                try
                {
                    opened(page).GetAwaiter().GetResult();
                }
                finally
                {
                    stop.Cancel();
                }
            },
            (_, _) => Task.CompletedTask,
            _output,
            _error);
        var root = new RootCommand { command.Build() };
        // Without a browser to open, the banner is the sign the host is serving; the long timeout only guards a hang.
        _output.Served = args.Contains("--no-open") ? stop.Cancel : null;
        stop.CancelAfter(TimeSpan.FromMinutes(1));

        return await root.Parse(args).InvokeAsync(new InvocationConfiguration { Output = _output, Error = _error }, stop.Token);
    }

    private sealed class FakeChild : IChildServer
    {
        public bool HasExited { get; set; }

        public TimeSpan? StoppedWith { get; private set; }

        public Task StopAsync(TimeSpan grace)
        {
            StoppedWith = grace;
            return Task.CompletedTask;
        }
    }

    // Captures the output and calls Served once the "running at" banner is written.
    private sealed class BannerWriter : StringWriter
    {
        public Action? Served { get; set; }

        public override async Task WriteLineAsync(string? value)
        {
            await base.WriteLineAsync(value);
            if (value?.Contains("Rig Web Dashboard running at", StringComparison.Ordinal) == true)
            {
                Served?.Invoke();
            }
        }
    }

    private sealed class AlwaysListening : IPortProbe
    {
        public Task<bool> ListeningAsync(int port, string host = CollectorConfig.Host, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }
}
