using System.CommandLine;
using System.Net;
using System.Net.Sockets;
using Rig.Cli.Web;
using Rig.Core.Tests;
using Rig.Web;

namespace Rig.Cli.Tests.Web;

/// <summary><c>rig web</c>: the host on the asked port, answering every route itself.</summary>
public sealed class WebCommandTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly SignalWriter _output = new("Rig Web Dashboard running at");
    private readonly SignalWriter _error = new("could not open a browser");
    private readonly List<Uri> _opened = [];

    [Fact]
    public async Task Serves_until_stopped_and_answers_the_c4_viewer_from_the_checkout()
    {
        using var stop = new CancellationTokenSource();
        string? status = null;
        string? viewer = null;
        Directory.CreateDirectory(Path.Join(_dir.Path, "src", "Rig.Web", "C4", "static"));
        await File.WriteAllTextAsync(Path.Join(_dir.Path, "src", "Rig.Web", "C4", "static", "index.html"), "<title>C4</title>", TestContext.Current.CancellationToken);

        var code = await RunAsync(["web", "--port", "0"], stop, async page =>
        {
            using var client = new HttpClient();
            status = await client.GetStringAsync(new Uri(page, "/api/status"));
            viewer = await client.GetStringAsync(new Uri(page, "/c4/"));
        });

        Assert.Equal(0, code);
        Assert.Contains("\"environment\": \"PROD\"", status, StringComparison.Ordinal);
        Assert.Equal("<title>C4</title>", viewer);
        Assert.StartsWith("[PROD MODE] Rig Web Dashboard running at http://127.0.0.1:", _output.ToString(), StringComparison.Ordinal);
        Assert.Single(_opened);
    }

    [Fact]
    public async Task Preview_shows_in_the_banner()
    {
        using var stop = new CancellationTokenSource();

        var code = await RunAsync(["web", "--port", "0", "--preview"], stop, _ => Task.CompletedTask);

        Assert.Equal(0, code);
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
    public async Task A_taken_port_is_exit_2()
    {
        using var taken = new TcpListener(IPAddress.Loopback, 0);
        taken.Start();
        var port = ((IPEndPoint)taken.LocalEndpoint).Port;
        using var stop = new CancellationTokenSource();

        var code = await RunAsync(["web", "--port", port.ToString(System.Globalization.CultureInfo.InvariantCulture), "--no-open"], stop, _ => Task.CompletedTask);

        Assert.Equal(2, code);
        Assert.Contains($"could not listen on 127.0.0.1:{port}", _error.ToString(), StringComparison.Ordinal);
    }

    public void Dispose()
    {
        _output.Dispose();
        _error.Dispose();
        _dir.Dispose();
    }

    // Runs the command; once the host is up, `opened` sees its address and then the run is stopped. When `opened`
    // throws, the run stops only after rig web has reported it: the token is forwarded, so a stop first would skip it.
    private async Task<int> RunAsync(string[] args, CancellationTokenSource stop, Func<Uri, Task> opened)
    {
        var command = new WebCommand(
            new WebPlaces(_dir.Path, Path.Join(_dir.Path, "managed-projects.json"), _dir.Path, Path.Join(_dir.Path, "rig.db"), _dir.Path),
            page =>
            {
                _opened.Add(page);
                try
                {
                    opened(page).GetAwaiter().GetResult();
                }
                catch
                {
                    _error.Seen = stop.Cancel;
                    throw;
                }

                stop.Cancel();
            },
            _output,
            _error);
        var root = new RootCommand { command.Build() };
        // Without a browser to open, the banner is the sign the host is serving; the long timeout only guards a hang.
        _output.Seen = args.Contains("--no-open") ? stop.Cancel : null;
        stop.CancelAfter(TimeSpan.FromMinutes(1));

        return await root.Parse(args).InvokeAsync(new InvocationConfiguration { Output = _output, Error = _error }, stop.Token);
    }

    // Captures what is written and calls Seen once a line containing the marker is written.
    private sealed class SignalWriter(string marker) : StringWriter
    {
        public Action? Seen { get; set; }

        public override async Task WriteLineAsync(string? value)
        {
            await base.WriteLineAsync(value);
            if (value?.Contains(marker, StringComparison.Ordinal) == true)
            {
                Seen?.Invoke();
            }
        }
    }
}
