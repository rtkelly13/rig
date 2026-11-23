using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Rig.Core.Chrome.Extension;

/// <summary>A native-messaging frame was cut short or was not a JSON object.</summary>
public sealed class NativeFrameException : Exception
{
    /// <summary>Creates the exception.</summary>
    public NativeFrameException()
    {
    }

    /// <summary>Creates the exception with <paramref name="message"/>.</summary>
    public NativeFrameException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with <paramref name="message"/> and the failure behind it.</summary>
    public NativeFrameException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Chrome's native-messaging framing: each message is UTF-8 JSON preceded by its length as a 32-bit integer in the
/// machine's byte order, little-endian on every platform rig runs on.
/// </summary>
public static class NativeFrames
{
    /// <summary>The largest message Chrome sends to a host.</summary>
    public const int MaxLength = 64 * 1024 * 1024;

    /// <summary>Reads one message; null at a clean end of stream, which is how Chrome says the port closed.</summary>
    public static async Task<JsonObject?> ReadAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var header = new byte[4];
        var got = await stream.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
        if (got == 0)
        {
            return null;
        }

        if (got != header.Length)
        {
            throw new NativeFrameException("Truncated frame header");
        }

        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is < 0 or > MaxLength)
        {
            throw new NativeFrameException($"Frame length {length} is out of range");
        }

        var body = new byte[length];
        if (await stream.ReadAtLeastAsync(body, length, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false) != length)
        {
            throw new NativeFrameException("Truncated frame body");
        }

        try
        {
            return JsonNode.Parse(body) as JsonObject ?? throw new NativeFrameException("Frame is not a JSON object");
        }
        catch (JsonException error)
        {
            throw new NativeFrameException("Frame is not JSON", error);
        }
    }

    /// <summary>Writes <paramref name="message"/> as one frame and flushes it.</summary>
    public static async Task WriteAsync(Stream stream, JsonObject message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(message);
        var body = Encoding.UTF8.GetBytes(message.ToJsonString());
        var frame = new byte[4 + body.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, body.Length);
        body.CopyTo(frame, 4);
        await stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
