using System.Text;
using System.Text.RegularExpressions;
using Rig.Core.Storage;
using Rig.Core.Text;

namespace Rig.Core.Jobs;

/// <summary>
/// The job logs: each run appends a section headed <c>--- &lt;timestamp&gt; [&lt;run id&gt;] &lt;argv&gt;</c>.
/// </summary>
public static partial class JobLog
{
    /// <summary>A log past this many bytes starts a new generation before the next run; one previous is kept.</summary>
    public const long LimitBytes = 512 * 1024;

    /// <summary>How much of a log's end <see cref="RunOutput"/> reads.</summary>
    public const int TailBytes = 64 * 1024;

    /// <summary>
    /// The non-blank lines of one section of the log at <paramref name="path"/>: the one whose header names
    /// <paramref name="runId"/>, else the last. Empty when the log is missing or, given a run id, has no section for it.
    /// Only the last <see cref="TailBytes"/> are read.
    /// </summary>
    public static IReadOnlyList<string> RunOutput(string path, string? runId = null)
    {
        string text;
        try
        {
            using var stream = OpenShared(path);
            var size = stream.Length;
            stream.Position = Math.Max(0, size - TailBytes);
            var buffer = new byte[size - stream.Position];
            stream.ReadExactly(buffer);
            text = Encoding.UTF8.GetString(buffer);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        var lines = UnicodeText.SplitLines(text);
        var headers = Enumerable.Range(0, lines.Count).Where(index => SectionHeader().IsMatch(lines[index])).ToList();
        var chosen = headers.Where(index => runId is null || lines[index].Contains($"[{runId}]", StringComparison.Ordinal)).ToList();
        if (chosen.Count == 0)
        {
            return [];
        }

        var start = chosen[^1] + 1;
        var end = headers.FirstOrDefault(index => index > chosen[^1], lines.Count);
        return [.. lines.Skip(start).Take(end - start)
            .Where(line => line.Trim().Length > 0 && !line.StartsWith("--- ", StringComparison.Ordinal))
            .Select(line => line.TrimEnd())];
    }

    /// <summary>Moves a log past <see cref="LimitBytes"/> to <c>.log.1</c>, over the previous one; a failure leaves it.</summary>
    public static void Rotate(string path)
    {
        try
        {
            if (File.Exists(path) && new FileInfo(path).Length > LimitBytes)
            {
                File.Move(path, path + ".1", overwrite: true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Rotation gives up quietly too: the log just grows until the next run.
        }
    }

    /// <summary>The file's length, or 0 when it cannot be read.</summary>
    public static long Size(string? path)
    {
        return FileGuard.Try(() => path is not null && File.Exists(path) ? new FileInfo(path).Length : 0, 0);
    }

    /// <summary>What was appended to the file after it was <paramref name="offset"/> bytes long; empty when it cannot be read.</summary>
    public static byte[] ReadFrom(string path, long offset)
    {
        try
        {
            using var stream = OpenShared(path);
            stream.Position = Math.Min(offset, stream.Length);
            var buffer = new byte[stream.Length - stream.Position];
            stream.ReadExactly(buffer);
            return buffer;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    // Shared, since a running job may still be writing the file.
    private static FileStream OpenShared(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    [GeneratedRegex(@"^--- \d", RegexOptions.CultureInvariant)]
    private static partial Regex SectionHeader();
}
