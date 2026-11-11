using System.Globalization;
using System.Text;
using Rig.Core.Vault;

namespace Rig.Core.Otel;

/// <summary>What replay did with one file.</summary>
/// <param name="Path">The file.</param>
/// <param name="Lines">Lines sent (or that would be, on a dry run).</param>
/// <param name="Error">Why it stopped, or empty.</param>
public sealed record ReplayResult(string Path, int Lines, string Error = "");

/// <summary>
/// Replays the collector's logs and metrics files into ClickHouse, each line once; ported from
/// <c>rig.otel_stack.replay</c>. Log records and metric points have no identity ClickHouse can be asked about, so
/// each file is tracked by inode with the byte offset already sent. The collector rotates by renaming, which keeps the
/// inode, so lines are not resent after rotation. The offset moves past each line as soon as it is accepted, and only
/// complete lines are sent. A hash of each file's start tells a file apart from a later one given the same inode.
/// </summary>
/// <param name="sender">Sends to the contrib collector.</param>
public sealed class Replay(IOtlpSender sender)
{
    /// <summary>The signals replayed by position.</summary>
    public static readonly IReadOnlyList<string> Signals = ["logs", "metrics"];

    /// <summary>The <paramref name="signal"/> files in <paramref name="folder"/>: the active one and its rotated copies.</summary>
    public static IReadOnlyList<string> FilesFor(string signal, string folder) =>
        Directory.Exists(folder) ? [.. Directory.EnumerateFiles(folder, $"{signal}*.jsonl").Order(PyText.CodePointOrder)] : [];

    /// <summary>Sends the unsent complete lines of every logs and metrics file in <paramref name="folder"/>; stops at the first rejection.</summary>
    /// <param name="folder">The collector's data folder.</param>
    /// <param name="statePath">The offsets file.</param>
    /// <param name="dryRun">Count what would be sent; send and record nothing.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    public async Task<IReadOnlyList<ReplayResult>> RunAsync(string folder, string statePath, bool dryRun = false, CancellationToken cancellationToken = default)
    {
        var offsets = ReplayOffsets.Read(statePath);
        List<ReplayResult> results = [];
        var present = new HashSet<string>(StringComparer.Ordinal);
        foreach (var signal in Signals)
        {
            foreach (var path in FilesFor(signal, folder))
            {
                if (FileIds.Inode(path) is { } inode)
                {
                    _ = present.Add(Key(inode));
                }

                var result = await FileAsync(path, signal, offsets, dryRun, cancellationToken).ConfigureAwait(false);
                results.Add(result);
                if (!dryRun)
                {
                    ReplayOffsets.Write(statePath, offsets);
                }

                if (result.Error.Length > 0)
                {
                    return results;
                }
            }
        }

        if (!dryRun)
        {
            foreach (var gone in offsets.Keys.Where(key => !present.Contains(key)).ToList())
            {
                _ = offsets.Remove(gone);
            }

            ReplayOffsets.Write(statePath, offsets);
        }

        return results;
    }

    /// <summary>
    /// The lines of <paramref name="raw"/>, each with its ending, as Python's <c>bytes.splitlines(keepends=True)</c>:
    /// split after <c>\n</c>, <c>\r\n</c> or a lone <c>\r</c>.
    /// </summary>
    public static IEnumerable<ReadOnlyMemory<byte>> SplitLines(ReadOnlyMemory<byte> raw)
    {
        var start = 0;
        for (var i = 0; i < raw.Length; i++)
        {
            var at = raw.Span[i];
            if (at == '\r' && i + 1 < raw.Length && raw.Span[i + 1] == '\n')
            {
                i++;
            }
            else if (at is not ((byte)'\r' or (byte)'\n'))
            {
                continue;
            }

            yield return raw[start..(i + 1)];
            start = i + 1;
        }

        if (start < raw.Length)
        {
            yield return raw[start..];
        }
    }

    private static string Key(ulong inode) => inode.ToString(CultureInfo.InvariantCulture);

    private async Task<ReplayResult> FileAsync(string path, string signal, Dictionary<string, ReplayOffset> offsets, bool dryRun, CancellationToken cancellationToken)
    {
        byte[] raw;
        ulong inode;
        try
        {
            inode = FileIds.Inode(path) ?? throw new FileNotFoundException("cannot read its inode", path);
            raw = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new ReplayResult(path, 0, $"{Path.GetFileName(path)}: {e.Message}");
        }

        var key = Key(inode);
        var held = offsets.GetValueOrDefault(key);
        var start = ReplayOffsets.SameFile(held, raw) ? (int)held!.Sent : 0;
        var end = Array.LastIndexOf(raw, (byte)'\n') + 1;
        if (end <= start)
        {
            return new ReplayResult(path, 0);
        }

        var position = start;
        var sent = 0;
        foreach (var chunk in SplitLines(raw.AsMemory(start, end - start)))
        {
            var line = Encoding.UTF8.GetString(chunk.Span).Trim();
            if (line.Length > 0 && !dryRun && await sender.SendAsync(signal, line, cancellationToken).ConfigureAwait(false) is { } problem)
            {
                return new ReplayResult(path, sent, problem);
            }

            position += chunk.Length;
            sent += line.Length > 0 ? 1 : 0;
            if (!dryRun)
            {
                offsets[key] = new ReplayOffset(inode, position, ReplayOffsets.HeadHash(raw, position));
            }
        }

        return new ReplayResult(path, sent);
    }
}
