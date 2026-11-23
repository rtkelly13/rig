using System.Net.Sockets;

namespace Rig.Core.Processes;

/// <summary>Whether something accepts TCP connections on a local port.</summary>
public interface IPortProbe
{
    /// <summary>Whether <paramref name="host"/>:<paramref name="port"/> accepts a connection within half a second.</summary>
    Task<bool> ListeningAsync(int port, string host = "127.0.0.1", CancellationToken cancellationToken = default);
}

/// <summary>Probes with a real TCP connection.</summary>
public sealed class TcpPortProbe : IPortProbe
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(0.5);

    /// <inheritdoc/>
    public async Task<bool> ListeningAsync(int port, string host = "127.0.0.1", CancellationToken cancellationToken = default)
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
