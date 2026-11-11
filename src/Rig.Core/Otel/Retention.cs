namespace Rig.Core.Otel;

/// <summary>
/// Keeps 7 days of the collector's rotated files. Ported from <c>rig.otel_stack.retention</c>. The file exporter's own
/// <c>max_days</c> only prunes when a file rotates (at 100 MB), which a quiet week may never trigger, so <c>collector
/// ensure</c>, every tick, prunes by age as well. The active <c>&lt;signal&gt;.jsonl</c> is never touched: the collector
/// holds it open and it is the newest data.
/// </summary>
public static class Retention
{
    /// <summary>How many days of rotated files are kept.</summary>
    public const int KeepDays = 7;

    /// <summary>Rotated files (<c>&lt;signal&gt;-&lt;timestamp&gt;.jsonl</c>) in <paramref name="data"/> last written more than <paramref name="keepDays"/> ago.</summary>
    public static IReadOnlyList<string> Expired(string data, DateTimeOffset now, int keepDays = KeepDays)
    {
        if (!Directory.Exists(data))
        {
            return [];
        }

        var cutoff = now - TimeSpan.FromDays(keepDays);
        return
        [
            .. Directory.EnumerateFiles(data, "*.jsonl")
                .Where(path => OtelPaths.Signals.Any(signal => Path.GetFileName(path).StartsWith($"{signal}-", StringComparison.Ordinal)))
                .Where(path => File.GetLastWriteTimeUtc(path) < cutoff.UtcDateTime)
                .Order(StringComparer.Ordinal),
        ];
    }

    /// <summary>Deletes (with <paramref name="apply"/>) the expired rotated files; returns what was, or would be, removed.</summary>
    public static IReadOnlyList<string> Prune(string data, DateTimeOffset now, bool apply, int keepDays = KeepDays)
    {
        var doomed = Expired(data, now, keepDays);
        if (apply)
        {
            foreach (var path in doomed)
            {
                File.Delete(path);
            }
        }

        return doomed;
    }
}
