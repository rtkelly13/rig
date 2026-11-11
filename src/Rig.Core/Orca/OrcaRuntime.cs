using System.IO.Pipes;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Rig.Core.Orca;

/// <summary>Orca is not running, could not be reached, or refused the request.</summary>
public sealed class OrcaUnavailableException : Exception
{
    /// <summary>Creates the exception.</summary>
    public OrcaUnavailableException()
    {
    }

    /// <summary>Creates the exception with <paramref name="message"/>.</summary>
    public OrcaUnavailableException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with <paramref name="message"/>, caused by <paramref name="innerException"/>.</summary>
    public OrcaUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Where Orca's runtime listens: a Unix socket, or a named pipe on Windows.</summary>
/// <param name="Kind"><c>unix</c> or <c>named-pipe</c>.</param>
/// <param name="Endpoint">The socket's path, or the pipe's <c>\\.\pipe\name</c>.</param>
public sealed record OrcaTransport(string Kind, string Endpoint)
{
    /// <summary>A Unix domain socket.</summary>
    public const string Unix = "unix";

    /// <summary>A Windows named pipe.</summary>
    public const string NamedPipe = "named-pipe";
}

/// <summary>Sends one request line to Orca's runtime and yields the lines it answers with, as they arrive.</summary>
public interface IOrcaExchange
{
    /// <summary>Writes <paramref name="request"/> (one JSON line) to <paramref name="transport"/> and reads its answer lines.</summary>
    IAsyncEnumerable<string> ExchangeAsync(OrcaTransport transport, string request, CancellationToken cancellationToken);
}

/// <summary>The real transports: a Unix domain socket, or a named pipe.</summary>
/// <param name="timeout">How long one exchange may take before Orca counts as unreachable.</param>
public sealed class SocketOrcaExchange(TimeSpan timeout) : IOrcaExchange
{
    private const string PipePrefix = @"\\.\pipe\";

    /// <summary>The exchange with the 30 seconds rig always gave Orca.</summary>
    public SocketOrcaExchange()
        : this(TimeSpan.FromSeconds(30))
    {
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<string> ExchangeAsync(OrcaTransport transport, string request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transport);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var stream = await ConnectAsync(transport, deadline.Token).ConfigureAwait(false);
        await using var owned = stream.ConfigureAwait(false);
        await stream.WriteAsync(Encoding.UTF8.GetBytes(request), deadline.Token).ConfigureAwait(false);
        await stream.FlushAsync(deadline.Token).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (await reader.ReadLineAsync(deadline.Token).ConfigureAwait(false) is { } line)
        {
            yield return line;
        }
    }

    /// <summary>The pipe name in <paramref name="endpoint"/>, without its <c>\\.\pipe\</c> prefix.</summary>
    public static string PipeName(string endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        return endpoint.StartsWith(PipePrefix, StringComparison.OrdinalIgnoreCase) ? endpoint[PipePrefix.Length..] : endpoint;
    }

    private static async Task<Stream> ConnectAsync(OrcaTransport transport, CancellationToken cancellationToken)
    {
        if (transport.Kind == OrcaTransport.NamedPipe)
        {
            var pipe = new NamedPipeClientStream(".", PipeName(transport.Endpoint), PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await pipe.ConnectAsync(cancellationToken).ConfigureAwait(false);
                return pipe;
            }
            catch
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        Socket? socket = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(transport.Endpoint), cancellationToken).ConfigureAwait(false);
            var stream = new NetworkStream(socket, ownsSocket: true);
            socket = null;
            return stream;
        }
        finally
        {
            socket?.Dispose();
        }
    }
}

/// <summary>
/// A client for the running Orca app's runtime API, the one its own CLI talks to. Orca writes
/// <c>orca-runtime.json</c> (its transports and an auth token) into its user-data folder at start-up. A request is one
/// JSON line <c>{id, authToken, method, params}</c>; the answer is the line with the same <c>id</c>, either
/// <c>{ok: true, result}</c> or <c>{ok: false, error: {code, message}}</c>, possibly after <c>{_keepalive: true}</c> lines.
/// These methods are Orca internals, not a published API, so a change in a later release shows up as
/// <see cref="OrcaUnavailableException"/>, never as a guess.
/// </summary>
public sealed class OrcaRuntime
{
    private readonly IOrcaExchange _exchange;
    private readonly Func<string> _newId;

    /// <summary>A client for the runtime at <paramref name="transport"/>, authenticated with <paramref name="authToken"/>.</summary>
    public OrcaRuntime(OrcaTransport transport, string authToken, IOrcaExchange exchange, Func<string>? newId = null)
    {
        Transport = transport;
        AuthToken = authToken;
        _exchange = exchange;
        _newId = newId ?? (() => Guid.NewGuid().ToString());
    }

    /// <summary>Where the runtime listens.</summary>
    public OrcaTransport Transport { get; }

    /// <summary>The token each request carries.</summary>
    public string AuthToken { get; }

    /// <summary>Connects through the runtime metadata file at <paramref name="metadataPath"/>.</summary>
    /// <exception cref="OrcaUnavailableException">The file is missing, unreadable, or names no usable transport or token.</exception>
    public static OrcaRuntime FromMetadata(string metadataPath, IOrcaExchange exchange)
    {
        string text;
        try
        {
            text = File.ReadAllText(metadataPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new OrcaUnavailableException($"could not read {metadataPath}; is Orca running?", e);
        }

        return Parse(text, exchange) ?? throw new OrcaUnavailableException($"{metadataPath} has no usable transport or token; restart Orca");
    }

    /// <summary>
    /// The client <paramref name="metadata"/> describes: the first Unix or named-pipe transport in <c>transports</c>, then the
    /// single <c>transport</c> older Orca versions wrote. Null when it is not JSON or has no token or usable transport.
    /// </summary>
    public static OrcaRuntime? Parse(string metadata, IOrcaExchange exchange)
    {
        JsonObject root;
        try
        {
            root = OrcaJson.Object(metadata);
        }
        catch (InvalidDataException)
        {
            return null;
        }

        var candidates = (root["transports"] as JsonArray ?? []).Append(root["transport"]);
        var transport = candidates.OfType<JsonObject>().Select(ReadTransport).FirstOrDefault(t => t is not null);
        return Text(root, "authToken") is { Length: > 0 } token && transport is not null ? new OrcaRuntime(transport, token, exchange) : null;
    }

    /// <summary>Sends one request and returns its <c>result</c>.</summary>
    /// <exception cref="OrcaUnavailableException">Orca could not be reached, refused the request, or hung up before answering.</exception>
    public async Task<JsonNode?> CallAsync(string method, JsonObject? parameters, CancellationToken cancellationToken)
    {
        var id = _newId();
        var request = new JsonObject { ["id"] = id, ["authToken"] = AuthToken, ["method"] = method, ["params"] = parameters };
        try
        {
            await foreach (var line in _exchange.ExchangeAsync(Transport, request.ToJsonString() + "\n", cancellationToken).ConfigureAwait(false))
            {
                if (Frame(line) is not { } frame || frame["_keepalive"] is JsonValue keepalive && keepalive.GetValueKind() == JsonValueKind.True || Text(frame, "id") != id)
                {
                    continue;
                }

                if (frame["ok"]?.GetValueKind() != JsonValueKind.True)
                {
                    var error = frame["error"] as JsonObject;
                    throw new OrcaUnavailableException($"Orca refused {method}: {Text(error, "code")} {Text(error, "message")}".Trim());
                }

                return frame["result"]?.DeepClone();
            }
        }
        catch (Exception e) when (e is IOException or SocketException or UnauthorizedAccessException or TimeoutException
            || e is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new OrcaUnavailableException($"Orca's runtime did not answer {method}: {e.Message}", e);
        }

        throw new OrcaUnavailableException($"Orca's runtime closed the connection before answering {method}");
    }

    /// <summary>What the running Orca launches each agent with: <c>settings.get</c>'s <c>agentDefaultArgs</c>.</summary>
    /// <exception cref="OrcaUnavailableException">Orca could not be asked.</exception>
    public async Task<IReadOnlyDictionary<string, string>> AgentDefaultArgsAsync(CancellationToken cancellationToken) =>
        LaunchLines(await CallAsync("settings.get", null, cancellationToken).ConfigureAwait(false), "settings.get");

    /// <summary>
    /// Sends every agent's whole launch line and returns the lines Orca then serves. Orca replaces the map whole, so pass
    /// every agent, not only the ones that change; it splits each line again into permission flags and custom arguments.
    /// </summary>
    /// <exception cref="OrcaUnavailableException">Orca could not be reached or refused the update.</exception>
    public async Task<IReadOnlyDictionary<string, string>> SetAgentDefaultArgsAsync(IReadOnlyDictionary<string, string> lines, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var map = new JsonObject();
        foreach (var (agent, line) in lines)
        {
            map[agent] = line;
        }

        var result = await CallAsync("settings.update", new JsonObject { ["agentDefaultArgs"] = map }, cancellationToken).ConfigureAwait(false);
        return LaunchLines(result, "settings.update");
    }

    /// <summary>Sends one <c>repo.update</c> for the repo at <paramref name="repoPath"/>.</summary>
    /// <exception cref="OrcaUnavailableException">Orca could not be reached or refused the update.</exception>
    public Task UpdateRepoAsync(string repoPath, RepoUpdates updates, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(updates);
        return CallAsync("repo.update", new JsonObject { ["repo"] = $"path:{repoPath}", ["updates"] = updates.ToJson() }, cancellationToken);
    }

    private static Dictionary<string, string> LaunchLines(JsonNode? result, string method)
    {
        try
        {
            var settings = result is JsonObject reply ? OrcaJson.Object(reply, "settings") : null;
            return (settings is null ? null : OrcaJson.StringMap(settings, "agentDefaultArgs")) ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch (InvalidDataException e)
        {
            throw new OrcaUnavailableException($"Orca answered {method} in a shape rig does not know: {e.Message}", e);
        }
    }

    private static OrcaTransport? ReadTransport(JsonObject raw) =>
        Text(raw, "kind") is OrcaTransport.Unix or OrcaTransport.NamedPipe && Text(raw, "endpoint") is { Length: > 0 } endpoint
            ? new OrcaTransport(Text(raw, "kind")!, endpoint)
            : null;

    private static JsonObject? Frame(string line)
    {
        try
        {
            return JsonNode.Parse(line) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonObject? parent, string key) =>
        parent?[key] is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;
}
