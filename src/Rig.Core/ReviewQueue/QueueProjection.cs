using System.Text;

namespace Rig.Core.ReviewQueue;

/// <summary>The regenerable Markdown projection, replaced atomically beside its destination.</summary>
public static class QueueProjection
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Write active items only, preserving surrounding notes and leaving identical output untouched.</summary>
    public static void Export(string path, IEnumerable<QueueEntry> entries)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(entries);
        var previous = File.Exists(path) ? Read(path) : null;
        var content = QueueMarkdown.Project(entries.Where(entry => entry.Active).Select(entry => entry.Item), previous);
        if (previous == content)
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var pending = Path.ChangeExtension(path, ".md.pending");
        try
        {
            File.WriteAllText(pending, content, Utf8);
            File.Move(pending, path, overwrite: true);
        }
        finally
        {
            File.Delete(pending);
        }
    }

    /// <summary>Python's UTF-8 text reads: reject invalid bytes and normalize universal newlines.</summary>
    public static string Read(string path) => Utf8.GetString(File.ReadAllBytes(path))
        .Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
}
