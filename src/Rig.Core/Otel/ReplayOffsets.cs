using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Rig.Core.Text;

namespace Rig.Core.Otel;

/// <summary>How far one file (by inode) has been replayed.</summary>
/// <param name="Inode">The file's inode (its file id on Windows), as <see cref="FileIds.Inode"/> reads it.</param>
/// <param name="Sent">Bytes of complete lines already sent.</param>
/// <param name="Head">The sha256 of the first <c>min(Sent, 4096)</c> bytes; empty in state written before it existed.</param>
public sealed record ReplayOffset(ulong Inode, long Sent, string Head = "");

/// <summary>
/// The replay state file,
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
    /// start; it once sliced from the end.
    /// </summary>
    public static bool SameFile(ReplayOffset? held, ReadOnlySpan<byte> raw) =>
        held is not null && held.Sent >= 0 && held.Sent <= raw.Length && (held.Head.Length == 0 || held.Head == HeadHash(raw, held.Sent));

    /// <summary>
    /// The offsets in <paramref name="path"/>; none when it is missing, unreadable, or any entry is malformed, in which case
    /// <paramref name="problem"/> hears <c>path: $.json.path: expected X, found Y</c>.
    /// </summary>
    public static Dictionary<string, ReplayOffset> Read(string path, Action<string>? problem = null)
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

        return Parse(text, problem, path);
    }

    /// <summary>
    /// The offsets in <paramref name="text"/>; none when any entry is malformed, as the decode is all or nothing, in which case
    /// <paramref name="problem"/> hears <c>source: $.json.path: expected X, found Y</c>.
    /// </summary>
    public static Dictionary<string, ReplayOffset> Parse(string text, Action<string>? problem = null, string source = "replay offsets")
    {
        try
        {
            return JsonProblem.Run(source, () => Decode(text));
        }
        catch (JsonInputException e)
        {
            problem?.Invoke(e.Message);
            return [];
        }
    }

    private static Dictionary<string, ReplayOffset> Decode(string text)
    {
        using var document = JsonDocument.Parse(text);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw JsonProblem.Expected("$", "an object", JsonProblem.Describe(document.RootElement));
        }

        var offsets = new Dictionary<string, ReplayOffset>(StringComparer.Ordinal);
        foreach (var entry in document.RootElement.EnumerateObject())
        {
            var path = JsonProblem.Child("$", entry.Name);
            if (entry.Value.ValueKind != JsonValueKind.Object)
            {
                throw JsonProblem.Expected(path, "an object", JsonProblem.Describe(entry.Value));
            }

            offsets[entry.Name] = new ReplayOffset(
                JsonProblem.UnsignedInteger(entry.Value, "inode", path),
                JsonProblem.WholeNumber(entry.Value, "sent", path),
                JsonProblem.OptionalString(entry.Value, "head", path) ?? string.Empty);
        }

        return offsets;
    }

    /// <summary>The state file's text: sorted by key, two-space indented, ending in a newline.</summary>
    public static string Render(IReadOnlyDictionary<string, ReplayOffset> offsets)
    {
        ArgumentNullException.ThrowIfNull(offsets);
        var w = new IndentedJsonWriter();
        _ = w.Object(() =>
        {
            foreach (var key in offsets.Keys.Order(UnicodeText.CodePointOrder))
            {
                var offset = offsets[key];
                _ = w.Key(key).Object(() => w.Key("inode").Number(offset.Inode).Key("sent").Number(offset.Sent).Key("head").String(offset.Head));
            }
        });
        return w.ToString() + "\n";
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
