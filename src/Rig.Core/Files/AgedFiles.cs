namespace Rig.Core.Files;

/// <summary>
/// Age-based retention for a folder of JSON-lines files: the files a predicate picks that were last written more than a
/// number of days ago. The OTel collector's rotated files (<see cref="Otel.Retention"/>) and the workload sampler's daily
/// files (<see cref="Workload.WorkloadPaths"/>) both prune this way, on every run of the job that writes them.
/// </summary>
public static class AgedFiles
{
    /// <summary>How many days rig's local JSON-lines records are kept.</summary>
    public const int KeepDays = 7;

    /// <summary>
    /// The <c>*.jsonl</c> files in <paramref name="folder"/> whose name <paramref name="pick"/> takes, last written more than
    /// <paramref name="keepDays"/> ago, in name order.
    /// </summary>
    public static IReadOnlyList<string> Expired(string folder, Func<string, bool> pick, DateTimeOffset now, int keepDays = KeepDays)
    {
        ArgumentNullException.ThrowIfNull(pick);
        if (!Directory.Exists(folder))
        {
            return [];
        }

        var cutoff = now - TimeSpan.FromDays(keepDays);
        return
        [
            .. Directory.EnumerateFiles(folder, "*.jsonl")
                .Where(path => pick(Path.GetFileName(path)))
                .Where(path => File.GetLastWriteTimeUtc(path) < cutoff.UtcDateTime)
                .Order(StringComparer.Ordinal),
        ];
    }

    /// <summary>Deletes (with <paramref name="apply"/>) the <see cref="Expired"/> files; returns what was, or would be, removed.</summary>
    public static IReadOnlyList<string> Prune(string folder, Func<string, bool> pick, DateTimeOffset now, bool apply, int keepDays = KeepDays)
    {
        var doomed = Expired(folder, pick, now, keepDays);
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
