using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Rig.Core.Chrome.Extension;

/// <summary>
/// The native-messaging host Chrome starts for one profile's extension: a relay, with no logic of its own. It listens
/// on a socket in <see cref="ExtensionPlaces.SocketFolder"/>, and forwards each request a <c>rig chrome-tabs</c> command
/// sends there to the extension over Chrome's pipe, then relays the answer back. It ends, removing its socket, when
/// Chrome closes the pipe.
/// </summary>
/// <param name="fromChrome">Chrome's frames: the extension's <c>hello</c> and its answers.</param>
/// <param name="toChrome">Frames to the extension: the requests.</param>
/// <param name="socketPath">Where commands reach this host.</param>
/// <param name="timeout">How long a request waits for the extension's answer.</param>
/// <param name="browser">The program that started the host, which tells Chromium from Chrome; "" when unknown.</param>
public sealed class ExtensionHost(Stream fromChrome, Stream toChrome, string socketPath, TimeSpan timeout, string browser = "") : IDisposable
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonObject>> _pending = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private JsonObject _hello = [];
    private long _next;

    /// <summary>Relays until Chrome closes the pipe or <paramref name="cancellationToken"/> fires.</summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var folder = Path.GetDirectoryName(socketPath)!;
        ExtensionPlaces.CreatePrivate(folder);
        File.Delete(socketPath);
        var accepting = AcceptAsync(stop.Token);
        try
        {
            await ReadChromeAsync(stop.Token).ConfigureAwait(false);
        }
        finally
        {
            await stop.CancelAsync().ConfigureAwait(false);
            await accepting.ConfigureAwait(false);
            foreach (var waiting in _pending.Values)
            {
                waiting.TrySetCanceled(CancellationToken.None);
            }

            File.Delete(socketPath);
        }
    }

    /// <inheritdoc/>
    public void Dispose() => _writeLock.Dispose();

    private async Task ReadChromeAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            JsonObject? message;
            try
            {
                message = await NativeFrames.ReadAsync(fromChrome, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (message is null)
            {
                return;
            }

            switch (Text(message, "type"))
            {
                case "hello":
                    message.Remove("type");
                    _hello = message;
                    break;
                case "response" when Text(message, "id") is { } id && _pending.TryRemove(id, out var waiting):
                    waiting.TrySetResult(message);
                    break;
                default:
                    break;
            }
        }
    }

    /// <summary>Binds the socket before its first await, so it is listening by the time Chrome's frames are read.</summary>
    private async Task AcceptAsync(CancellationToken cancellationToken)
    {
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(socketPath));
        listener.Listen(16);
        var running = new List<Task>();
        while (!cancellationToken.IsCancellationRequested)
        {
            Socket client;
            try
            {
                client = await listener.AcceptAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

#pragma warning disable CA2025 // ServeAsync owns and disposes the client; the listener outlives it through WhenAll below.
            running.Add(ServeAsync(client, cancellationToken));
#pragma warning restore CA2025
            running.RemoveAll(task => task.IsCompleted);
        }

        await Task.WhenAll(running).ConfigureAwait(false);
    }

    private async Task ServeAsync(Socket client, CancellationToken cancellationToken)
    {
        await Task.Yield();
        using var stream = new NetworkStream(client, ownsSocket: true);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
        await using (writer.ConfigureAwait(false))
        {
            try
            {
                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    return;
                }

                var reply = await AnswerAsync(line, cancellationToken).ConfigureAwait(false);
                await writer.WriteLineAsync(reply.ToJsonString().AsMemory(), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is IOException or OperationCanceledException or SocketException)
            {
                // The command went away, or the host is stopping: nothing is left to answer.
            }
        }
    }

    private async Task<JsonObject> AnswerAsync(string line, CancellationToken cancellationToken)
    {
        JsonObject request;
        try
        {
            request = JsonNode.Parse(line) as JsonObject ?? throw new JsonException("not an object");
        }
        catch (JsonException error)
        {
            return Failed($"bad request: {error.Message}");
        }

        var command = Text(request, "cmd");
        if (command is null)
        {
            return Failed("bad request: no cmd");
        }

        if (command == ExtensionProtocol.HelloCommand)
        {
            var hello = (JsonObject)_hello.DeepClone();
            hello["pid"] = Environment.ProcessId;
            hello["browser"] = browser;
            return new JsonObject { ["ok"] = true, ["result"] = hello };
        }

        var id = Interlocked.Increment(ref _next).ToString(CultureInfo.InvariantCulture);
        var waiting = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = waiting;
        var frame = new JsonObject { ["type"] = "request", ["id"] = id, ["cmd"] = command, ["args"] = request["args"]?.DeepClone() };
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await NativeFrames.WriteAsync(toChrome, frame, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }

        try
        {
            var answer = await waiting.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            return answer["ok"]?.GetValue<bool>() == true
                ? new JsonObject { ["ok"] = true, ["result"] = answer["result"]?.DeepClone() }
                : Failed(Text(answer, "error") ?? "the extension failed without saying why");
        }
        catch (TimeoutException)
        {
            _pending.TryRemove(id, out _);
            return Failed($"the extension did not answer {command} within {timeout.TotalSeconds:0}s");
        }
    }

    private static JsonObject Failed(string error) => new() { ["ok"] = false, ["error"] = error };

    private static string? Text(JsonObject message, string name) =>
        message[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
