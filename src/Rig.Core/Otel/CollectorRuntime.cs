using System.Globalization;
using System.Net.Sockets;
using System.Text;
using Rig.Core.Processes;

namespace Rig.Core.Otel;

/// <summary>Whether something accepts TCP connections on a local port.</summary>
public interface IPortProbe
{
    /// <summary>Whether <paramref name="host"/>:<paramref name="port"/> accepts a connection within half a second.</summary>
    Task<bool> ListeningAsync(int port, string host = CollectorConfig.Host, CancellationToken cancellationToken = default);
}

/// <summary>Probes with a real TCP connection, as the Python's <c>socket.create_connection</c> did.</summary>
public sealed class TcpPortProbe : IPortProbe
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(0.5);

    /// <inheritdoc/>
    public async Task<bool> ListeningAsync(int port, string host = CollectorConfig.Host, CancellationToken cancellationToken = default)
    {
        using var client = new TcpClient();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(Timeout);
        try
        {
            await client.ConnectAsync(host, port, deadline.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception e) when (e is SocketException or OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return false;
        }
    }
}

/// <summary>Who listens on a port: its pid, its executable, and whether that is this collector.</summary>
/// <param name="Pid">The listening process.</param>
/// <param name="Command">Its command line (macOS, Linux) or image name (Windows); empty when it cannot be read.</param>
public sealed record Owner(int Pid, string Command)
{
    /// <summary>Whether it is the core collector rather than contrib or a container.</summary>
    public bool Ours => CollectorProcess.IsCollector(Command);
}

/// <summary>
/// Running the collector without a service manager, ported from <c>rig.otel_stack.runtime</c>. macOS runs it under its
/// LaunchAgent; Windows has no agent, so <c>collector ensure</c> starts the binary detached when nothing listens on the
/// HTTP port. A pid file outlives its process and the pid can then belong to anything, so only a process that still
/// matches the launched binary (and, where <c>ps</c> exists, its start time) is ever signalled.
/// </summary>
/// <param name="paths">Where the collector's files live.</param>
/// <param name="proc">Runs <c>ps</c>, <c>lsof</c>, <c>tasklist</c>, <c>netstat</c>, <c>kill</c> and <c>taskkill</c>.</param>
/// <param name="detached">Starts the collector.</param>
/// <param name="windows">Whether this is Windows, which has no <c>ps</c> or <c>lsof</c>.</param>
public sealed class CollectorRuntime(OtelPaths paths, IProc proc, IDetachedStart detached, bool windows)
{
    /// <summary>The configured forward endpoint, or null when the collector forwards nowhere.</summary>
    /// <exception cref="FormatException">The forward file holds something that is not an endpoint.</exception>
    public string? Forward()
    {
        string text;
        try
        {
            text = File.ReadAllText(paths.Forward, Encoding.UTF8).Trim();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return text.Length > 0 ? CollectorProcess.ValidateForward(text) : null;
    }

    /// <summary>Records <paramref name="endpoint"/> as the forward endpoint (null turns forwarding off). Takes effect on the next start.</summary>
    /// <exception cref="FormatException">It is not an endpoint.</exception>
    public string? SetForward(string? endpoint)
    {
        if (endpoint is null)
        {
            File.Delete(paths.Forward);
            return null;
        }

        var valid = CollectorProcess.ValidateForward(endpoint);
        _ = Directory.CreateDirectory(Path.GetDirectoryName(paths.Forward)!);
        File.WriteAllText(paths.Forward, valid + "\n", new UTF8Encoding(false));
        return valid;
    }

    /// <summary>Writes the collector config (forwarding as currently set) and creates its data folder; returns the config's path.</summary>
    public string WriteConfig()
    {
        _ = Directory.CreateDirectory(Path.GetDirectoryName(paths.Config)!);
        _ = Directory.CreateDirectory(paths.Data);
        File.WriteAllText(paths.Config, CollectorConfig.Render(paths.Data, Forward()), new UTF8Encoding(false));
        return paths.Config;
    }

    /// <summary>Starts the collector detached and records its pid. The caller checks nothing listens first.</summary>
    public async Task<int> StartAsync(string binary, CancellationToken cancellationToken = default)
    {
        var config = WriteConfig();
        _ = Directory.CreateDirectory(Path.GetDirectoryName(paths.Log)!);
        var pid = await detached.StartAsync([binary, "--config", config], paths.Log, cancellationToken: cancellationToken).ConfigureAwait(false);
        var record = new Launched(pid, binary, await ProcessStartedAsync(pid, cancellationToken).ConfigureAwait(false));
        await File.WriteAllTextAsync(paths.PidFile, record.Serialise(), new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        return pid;
    }

    /// <summary>When <paramref name="pid"/> started, as <c>ps</c> reports it; empty where that cannot be read.</summary>
    public async Task<string> ProcessStartedAsync(int pid, CancellationToken cancellationToken = default) =>
        windows ? string.Empty : (await RunAsync(cancellationToken, "ps", "-o", "lstart=", "-p", Text(pid)).ConfigureAwait(false)).Trim();

    /// <summary>The executable of <paramref name="pid"/>: its command line on macOS and Linux, its image name on Windows.</summary>
    public async Task<string> CommandOfAsync(int pid, CancellationToken cancellationToken = default)
    {
        if (!windows)
        {
            return (await RunAsync(cancellationToken, "ps", "-o", "command=", "-p", Text(pid)).ConfigureAwait(false)).Trim();
        }

        var output = await RunAsync(cancellationToken, "tasklist", "/FI", $"PID eq {Text(pid)}", "/FO", "CSV", "/NH").ConfigureAwait(false);
        return output.StartsWith('"') ? output.Split(',')[0].Trim('"') : string.Empty;
    }

    /// <summary>Whether <paramref name="record"/>'s pid is still the collector it launched, not a later process given the same pid.</summary>
    public async Task<bool> StillOursAsync(Launched record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        var command = await CommandOfAsync(record.Pid, cancellationToken).ConfigureAwait(false);
        if (command.Length == 0)
        {
            return false;
        }

        if (windows)
        {
            var name = record.Binary[(record.Binary.LastIndexOfAny(['/', '\\']) + 1)..];
            return string.Equals(command, name, StringComparison.OrdinalIgnoreCase);
        }

        return command.Contains(record.Binary, StringComparison.Ordinal)
            && (record.Started.Length == 0 || await ProcessStartedAsync(record.Pid, cancellationToken).ConfigureAwait(false) == record.Started);
    }

    /// <summary>What <see cref="StartAsync"/> last recorded, or null.</summary>
    public Launched? Recorded()
    {
        try
        {
            return Launched.Parse(File.ReadAllText(paths.PidFile, Encoding.UTF8));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The recorded pid while it is still the collector that was launched.</summary>
    public async Task<int?> RecordedPidAsync(CancellationToken cancellationToken = default) =>
        Recorded() is { } record && await StillOursAsync(record, cancellationToken).ConfigureAwait(false) ? record.Pid : null;

    /// <summary>Who listens on <paramref name="port"/>, where the platform can say; null when nobody does or it cannot be read.</summary>
    public async Task<Owner?> OwnerAsync(int port = CollectorConfig.HttpPort, CancellationToken cancellationToken = default)
    {
        var pid = windows
            ? await WindowsListenerAsync(port, cancellationToken).ConfigureAwait(false)
            : await ListenerPidAsync(port, cancellationToken).ConfigureAwait(false);
        return pid is { } found ? new Owner(found, await CommandOfAsync(found, cancellationToken).ConfigureAwait(false)) : null;
    }

    /// <summary>The pid listening on <paramref name="port"/> (<c>lsof</c>, or <c>netstat</c> on Windows), else the one recorded.</summary>
    public async Task<int?> ListenerPidAsync(int port = CollectorConfig.HttpPort, CancellationToken cancellationToken = default)
    {
        if (windows)
        {
            if (await WindowsListenerAsync(port, cancellationToken).ConfigureAwait(false) is { } found)
            {
                return found;
            }
        }
        else
        {
            var output = await RunAsync(cancellationToken, "lsof", "-nP", $"-iTCP:{Text(port)}", "-sTCP:LISTEN", "-t").ConfigureAwait(false);
            var first = output.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (first is not null && CollectorProcess.IsDigits(first) && int.TryParse(first, NumberStyles.None, CultureInfo.InvariantCulture, out var pid))
            {
                return pid;
            }
        }

        return await RecordedPidAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The resident memory of <paramref name="pid"/> in MB, where <c>ps</c> exists (not Windows).</summary>
    public async Task<double?> ResidentMbAsync(int pid, CancellationToken cancellationToken = default)
    {
        if (windows)
        {
            return null;
        }

        var text = (await RunAsync(cancellationToken, "ps", "-o", "rss=", "-p", Text(pid)).ConfigureAwait(false)).Trim();
        return CollectorProcess.IsDigits(text) && long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var kb) ? kb / 1024.0 : null;
    }

    /// <summary>
    /// Stops a collector <see cref="StartAsync"/> launched; false when there was none still running. A stale record is
    /// discarded and nothing is signalled. Unix gets SIGTERM, so the collector flushes; Windows has no such signal, and
    /// <c>taskkill /F</c> ends it as the Python's <c>os.kill</c> did there.
    /// </summary>
    public async Task<bool> StopRecordedAsync(CancellationToken cancellationToken = default)
    {
        if (Recorded() is not { } record)
        {
            return false;
        }

        var ours = await StillOursAsync(record, cancellationToken).ConfigureAwait(false);
        if (ours)
        {
            var pid = Text(record.Pid);
            _ = await proc.RunAsync(new ProcRequest(windows ? ["taskkill", "/PID", pid, "/F"] : ["kill", "-TERM", pid]), cancellationToken).ConfigureAwait(false);
        }

        File.Delete(paths.PidFile);
        return ours;
    }

    private async Task<int?> WindowsListenerAsync(int port, CancellationToken cancellationToken) =>
        CollectorProcess.ParseNetstatListener(await RunAsync(cancellationToken, "netstat", "-ano", "-p", "TCP").ConfigureAwait(false), port);

    // Standard output whatever the exit code, as the Python read `done.stdout` with check=False.
    private async Task<string> RunAsync(CancellationToken cancellationToken, params string[] argv) =>
        (await proc.RunAsync(new ProcRequest(argv), cancellationToken).ConfigureAwait(false)).Out;

    private static string Text(int number) => number.ToString(CultureInfo.InvariantCulture);
}
