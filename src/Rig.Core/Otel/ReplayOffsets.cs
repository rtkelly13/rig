using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Rig.Core.Vault;

namespace Rig.Core.Otel;

/// <summary>How far one file (by inode) has been replayed.</summary>
/// <param name="Inode">The file's inode.</param>
/// <param name="Sent">Bytes of complete lines already sent.</param>
/// <param name="Head">The sha256 of the first <c>min(Sent, 4096)</c> bytes; empty in state written before it existed.</param>
public sealed record ReplayOffset(long Inode, long Sent, string Head = "");

/// <summary>
/// The replay state file, ported from <c>rig.otel_stack.replay</c>: a JSON object keyed by inode, each entry the byte
/// offset already sent and a hash of the file's start, so a later file handed the same inode is told apart.
/// </summary>
public static class ReplayOffsets
{
    /// <summary>The state file's name, under the otel folder.</summary>
    public const string StateFile = "replay-state.json";

    /// <summary>How much of a file's start is hashed to tell it from a later file on the same inode.</summary>
    public const int HeadBytes = 4096;

    /// <summary>The sha256 hex of the first <c>min(<paramref name="sent"/>, 4096)</c> bytes of <paramref name="raw"/>.</summary>
    public static string HeadHash(ReadOnlySpan<byte> raw, long sent)
    {
        var length = (int)Math.Clamp(Math.Min(sent, HeadBytes), 0, raw.Length);
        return Convert.ToHexStringLower(SHA256.HashData(raw[..length]));
    }

    /// <summary>
    /// Whether <paramref name="held"/> still describes <paramref name="raw"/>: no longer than it, and the same start. A
    /// negative offset (only a hand-edited or corrupt state file holds one) is refused, so the file is replayed from its
    /// start; the Python accepted it and sliced from the end.
    /// </summary>
    public static bool SameFile(ReplayOffset? held, ReadOnlySpan<byte> raw) =>
        held is not null && held.Sent >= 0 && held.Sent <= raw.Length && (held.Head.Length == 0 || held.Head == HeadHash(raw, held.Sent));

    /// <summary>The offsets in <paramref name="path"/>; none when it is missing, unreadable, or any entry is malformed.</summary>
    public static Dictionary<string, ReplayOffset> Read(string path)
    {
        string text;
        try
        {
            text = File.ReadAllText(path, Encoding.UTF8);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        return Parse(text);
    }

    /// <summary>The offsets in <paramref name="text"/>; none when any entry is malformed, as the Python's decode was all or nothing.</summary>
    public static Dictionary<string, ReplayOffset> Parse(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return [];
            }

            var offsets = new Dictionary<string, ReplayOffset>(StringComparer.Ordinal);
            foreach (var entry in document.RootElement.EnumerateObject())
            {
                if (entry.Value.ValueKind != JsonValueKind.Object
                    || LaxJson.Int(LaxJson.Field(entry.Value, "inode")) is not { } inode
                    || LaxJson.Int(LaxJson.Field(entry.Value, "sent")) is not { } sent)
                {
                    return [];
                }

                var head = LaxJson.Field(entry.Value, "head");
                if (head is { ValueKind: not JsonValueKind.String })
                {
                    return [];
                }

                offsets[entry.Name] = new ReplayOffset(inode, sent, head?.GetString() ?? string.Empty);
            }

            return offsets;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>The state file's text: sorted by key, two-space indented, ending in a newline.</summary>
    public static string Render(IReadOnlyDictionary<string, ReplayOffset> offsets)
    {
        ArgumentNullException.ThrowIfNull(offsets);
        var w = new PyJsonWriter();
        _ = w.Object(() =>
        {
            foreach (var key in offsets.Keys.Order(PyText.CodePointOrder))
            {
                var offset = offsets[key];
                _ = w.Key(key).Object(() => w.Key("inode").Number(offset.Inode).Key("sent").Number(offset.Sent).Key("head").String(offset.Head));
            }
        });
        return w + "\n";
    }

    /// <summary>Writes <paramref name="offsets"/> to <paramref name="path"/> through a temporary file, so a crash never leaves it half written.</summary>
    public static void Write(string path, IReadOnlyDictionary<string, ReplayOffset> offsets)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = Path.ChangeExtension(path, ".tmp");
        File.WriteAllText(temporary, Render(offsets), new UTF8Encoding(false));
        File.Move(temporary, path, overwrite: true);
    }
}
