using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Rig.Core.Chrome.Extension;

/// <summary>A request to the extension failed: its host was gone, or the extension refused or did not answer.</summary>
public sealed class ExtensionCallException : Exception
{
    /// <summary>Creates the exception.</summary>
    public ExtensionCallException()
    {
    }

    /// <summary>Creates the exception with <paramref name="message"/>.</summary>
    public ExtensionCallException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with <paramref name="message"/> and the failure behind it.</summary>
    public ExtensionCallException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The extension driver refused a step: the tab moved, changed or went away, or the step was not one it can take.</summary>
public sealed class ExtensionStepRefusedException : Exception
{
    /// <summary>Creates the exception.</summary>
    public ExtensionStepRefusedException()
    {
    }

    /// <summary>Creates the exception with <paramref name="message"/>.</summary>
    public ExtensionStepRefusedException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with <paramref name="message"/> and the failure behind it.</summary>
    public ExtensionStepRefusedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>One Chrome profile whose extension is connected, through its host's socket.</summary>
/// <param name="Socket">The host's socket.</param>
/// <param name="Email">The profile's signed-in account; "" when it has none.</param>
/// <param name="Version">The extension's version.</param>
/// <param name="Browser">The browser program that started the host; "" when unknown.</param>
public sealed record ExtensionConnection(string Socket, string Email, string Version, string Browser = "")
{
    /// <summary>
    /// The browser's config folder under <c>~/.config</c>, from its program path: <c>chromium</c>, <c>google-chrome</c>
    /// and its beta and unstable channels; null for anything else.
    /// </summary>
    public string? ConfigFolder => Browser switch
    {
        _ when Browser.Contains("chromium", StringComparison.OrdinalIgnoreCase) => "chromium",
        _ when Browser.Contains("chrome-beta", StringComparison.Ordinal) => "google-chrome-beta",
        _ when Browser.Contains("chrome-unstable", StringComparison.Ordinal) => "google-chrome-unstable",
        _ when Browser.Contains("google/chrome", StringComparison.Ordinal) => "google-chrome",
        _ => null,
    };
}

/// <summary>Reaching the extension in every running profile.</summary>
public interface IExtensionBridge
{
    /// <summary>The profiles whose extension answers now.</summary>
    IReadOnlyList<ExtensionConnection> Connections();

    /// <summary>Sends <paramref name="command"/> to <paramref name="connection"/>'s extension and returns its result.</summary>
    /// <exception cref="ExtensionCallException">The host is gone, or the extension refused or did not answer.</exception>
    JsonNode? Send(ExtensionConnection connection, string command, JsonObject? args = null);
}

/// <summary>The bridge over the hosts' sockets in one folder; a socket nothing answers on is stale and removed.</summary>
/// <param name="folder">Where the hosts put their sockets (<see cref="ExtensionPlaces.SocketFolder"/>).</param>
/// <param name="timeout">How long one request may take, the extension's work included.</param>
public sealed class SocketExtensionBridge(string folder, TimeSpan timeout) : IExtensionBridge
{
    /// <inheritdoc/>
    public IReadOnlyList<ExtensionConnection> Connections()
    {
        if (!Directory.Exists(folder))
        {
            return [];
        }

        var found = new List<ExtensionConnection>();
        foreach (var socket in Directory.EnumerateFiles(folder, "*.sock").Order(StringComparer.Ordinal))
        {
            try
            {
                var hello = Request(socket, ExtensionProtocol.HelloCommand, null) as JsonObject;
                found.Add(new ExtensionConnection(socket, Text(hello, "email"), Text(hello, "version"), Text(hello, "browser")));
            }
            catch (ExtensionCallException error) when (error.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionRefused })
            {
                // Nothing listens: the host died without removing its socket.
                TryDelete(socket);
            }
            catch (ExtensionCallException)
            {
                // A host that is slow or confused is left alone and skipped this time.
            }
        }

        return found;
    }

    /// <inheritdoc/>
    public JsonNode? Send(ExtensionConnection connection, string command, JsonObject? args = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return Request(connection.Socket, command, args);
    }

    private JsonNode? Request(string socketPath, string command, JsonObject? args)
    {
        string? line;
        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified)
            {
                ReceiveTimeout = (int)timeout.TotalMilliseconds,
                SendTimeout = (int)timeout.TotalMilliseconds,
            };
            socket.Connect(new UnixDomainSocketEndPoint(socketPath));
            using var stream = new NetworkStream(socket, ownsSocket: false);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true);
            writer.WriteLine(new JsonObject { ["cmd"] = command, ["args"] = args }.ToJsonString());
            writer.Flush();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            line = reader.ReadLine();
        }
        catch (Exception error) when (error is SocketException or IOException)
        {
            throw new ExtensionCallException($"{command}: the host at {socketPath} did not answer", error is IOException { InnerException: SocketException inner } ? inner : error);
        }

        JsonObject reply;
        try
        {
            reply = (line is null ? null : JsonNode.Parse(line) as JsonObject) ?? throw new ExtensionCallException($"{command}: the host closed without answering");
        }
        catch (JsonException error)
        {
            throw new ExtensionCallException($"{command}: the host's answer is not JSON", error);
        }

        return reply["ok"] is JsonValue ok && ok.TryGetValue<bool>(out var success) && success
            ? reply["result"]
            : throw new ExtensionCallException($"{command}: {Text(reply, "error")}");
    }

    private static string Text(JsonObject? message, string name) =>
        message?[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Another command may have removed it first.
        }
    }
}
