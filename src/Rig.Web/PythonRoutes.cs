using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using Rig.Core.Otel;

namespace Rig.Web;

/// <summary>A server process the web host started and owns.</summary>
public interface IChildServer
{
    /// <summary>Whether it has exited.</summary>
    bool HasExited { get; }

    /// <summary>Asks it to stop by closing its standard input, then kills it (and only it) after <paramref name="grace"/>.</summary>
    Task StopAsync(TimeSpan grace);
}

/// <summary>
/// The Python <c>StudioServer</c> serving the routes not ported yet, run as a child of the web host on a free loopback
/// port (<c>desktop-manager web upstream --port N</c>). The host starts it, forwards to it and stops it; nothing else signals it.
/// </summary>
/// <remarks>
/// The child exits when its standard input closes, so it goes with the host even when the host is killed outright.
/// Deleted with <see cref="Forwarder"/> when the last route group is ported.
/// </remarks>
public sealed class PythonRoutes(Func<int, IChildServer> spawn, IPortProbe probe, Func<TimeSpan, CancellationToken, Task> delay) : IAsyncDisposable
{
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(5);
    private IChildServer? _child;
    private Uri? _address;

    /// <summary>Where the Python routes listen, or null when they are not running: never started, failed or exited since.</summary>
    public Uri? Address => _child is { HasExited: false } ? _address : null;

    /// <summary>
    /// Starts the child on a free port and waits up to <paramref name="timeout"/> for it to accept connections. Returns
    /// null when it is ready, else why not; the host still serves its own routes either way.
    /// </summary>
    public async Task<string?> StartAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var port = FreePort();
        try
        {
            _child = spawn(port);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            // uv missing from PATH, say: the host still serves its own routes, and the rest are a 404.
            return $"the Python web routes did not start: {e.Message}";
        }

        _address = new Uri($"http://127.0.0.1:{port}");
        for (var waited = TimeSpan.Zero; waited < timeout; waited += Poll)
        {
            if (_child.HasExited)
            {
                return "the Python web routes exited on start";
            }

            if (await probe.ListeningAsync(port, "127.0.0.1", cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            await delay(Poll, cancellationToken).ConfigureAwait(false);
        }

        return $"the Python web routes did not answer on port {port} within {timeout.TotalSeconds:g}s";
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        _address = null;
        if (_child is { HasExited: false } child)
        {
            await child.StopAsync(Grace).ConfigureAwait(false);
        }
    }

    // A port the system just handed out: free now, and not one of the fixed ports rig's services use.
    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}

/// <summary>The real child: <c>desktop-manager web upstream</c>, started through uv with a pipe on its standard input.</summary>
[ExcludeFromCodeCoverage(Justification = "Starts the real Python server; PythonRoutes is tested through IChildServer.")]
public sealed class ProcessChildServer : IChildServer
{
    private readonly Process _process;

    private ProcessChildServer(Process process) => _process = process;

    /// <inheritdoc/>
    public bool HasExited => _process.HasExited;

    /// <summary>Starts <paramref name="argv"/> with its standard input piped and its output shared with this console.</summary>
    public static ProcessChildServer Start(IReadOnlyList<string> argv)
    {
        ArgumentNullException.ThrowIfNull(argv);
        var info = new ProcessStartInfo(argv[0]) { RedirectStandardInput = true, UseShellExecute = false };
        foreach (var arg in argv.Skip(1))
        {
            info.ArgumentList.Add(arg);
        }

        return new(Process.Start(info) ?? throw new InvalidOperationException($"Could not start {argv[0]}."));
    }

    /// <inheritdoc/>
    public async Task StopAsync(TimeSpan grace)
    {
        _process.StandardInput.Close();
        using var wait = new CancellationTokenSource(grace);
        try
        {
            await _process.WaitForExitAsync(wait.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The pid is the one this host started, so only its own server is signalled.
            _process.Kill(entireProcessTree: true);
        }
        finally
        {
            _process.Dispose();
        }
    }
}
