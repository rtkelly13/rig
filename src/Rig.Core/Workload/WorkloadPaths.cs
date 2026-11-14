using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Rig.Core.Files;

namespace Rig.Core.Workload;

/// <summary>
/// Where samples live, one JSON-lines file per UTC day under rig's per-user folder, and how they are written, pruned and
/// read back. Seven days are kept (<see cref="AgedFiles"/>, as the collector keeps its telemetry); today's file is never
/// pruned.
/// </summary>
/// <param name="BaseDir">rig's per-user folder (<see cref="Roots.AppData.Current"/> in production).</param>
public sealed record WorkloadPaths(string BaseDir)
{
    /// <summary>How samples are written: snake case, no nulls, one line each.</summary>
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private const string DayFormat = "yyyy-MM-dd";

    /// <summary>The folder of day files.</summary>
    public string Folder => Path.Join(BaseDir, "workload");

    /// <summary>The scheduled agent's log on macOS.</summary>
    public string Log => Path.Join(BaseDir, "logs", "workload.log");

    /// <summary>The file samples taken at <paramref name="at"/> go to: <c>YYYY-MM-DD.jsonl</c> by UTC date.</summary>
    public string DayFile(DateTimeOffset at) => Path.Join(Folder, at.UtcDateTime.ToString(DayFormat, CultureInfo.InvariantCulture) + ".jsonl");

    /// <summary><paramref name="sample"/> as the one line it is stored as, without the newline.</summary>
    public static string Line(WorkloadSample sample) => JsonSerializer.Serialize(sample, Json);

    /// <summary>Appends <paramref name="sample"/> to its day's file; returns the file and the line's length in bytes.</summary>
    public (string File, int Bytes) Append(WorkloadSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        var path = DayFile(new DateTimeOffset(sample.Ts, TimeSpan.Zero));
        _ = Directory.CreateDirectory(Folder);
        var line = Line(sample) + "\n";
        File.AppendAllText(path, line, new UTF8Encoding(false));
        return (path, Encoding.UTF8.GetByteCount(line));
    }

    /// <summary>Deletes (with <paramref name="apply"/>) day files last written more than a week before <paramref name="now"/>, never today's.</summary>
    public IReadOnlyList<string> Prune(DateTimeOffset now, bool apply)
    {
        var today = Path.GetFileName(DayFile(now));
        return AgedFiles.Prune(Folder, name => IsDayFile(name) && !name.Equals(today, StringComparison.Ordinal), now, apply);
    }

    /// <summary>The day files present, oldest first.</summary>
    public IReadOnlyList<string> DayFiles() =>
        Directory.Exists(Folder)
            ? [.. Directory.EnumerateFiles(Folder, "*.jsonl").Where(path => IsDayFile(Path.GetFileName(path))).Order(StringComparer.Ordinal)]
            : [];

    /// <summary>Samples taken at or after <paramref name="since"/>, oldest first; lines that do not parse are skipped.</summary>
    public IReadOnlyList<WorkloadSample> Read(DateTimeOffset since)
    {
        var first = Path.GetFileName(DayFile(since));
        var samples = new List<WorkloadSample>();
        foreach (var path in DayFiles().Where(path => string.CompareOrdinal(Path.GetFileName(path), first) >= 0))
        {
            foreach (var line in File.ReadLines(path))
            {
                if (Parse(line) is { } sample && sample.Ts >= since.UtcDateTime)
                {
                    samples.Add(sample);
                }
            }
        }

        return [.. samples.OrderBy(sample => sample.Ts)];
    }

    /// <summary>One stored line as a sample; null when it is blank or not a sample.</summary>
    public static WorkloadSample? Parse(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        try
        {
            // The deserializer leaves a missing field null whatever the record says, so a partial object is not a sample.
            var sample = JsonSerializer.Deserialize<WorkloadSample>(line, Json);
            return sample is { Host: not null, Ram: not null, Processes: not null, Apps: not null, Claude.Children: not null, Large: not null, Bursts: not null }
                ? sample
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsDayFile(string name) =>
        DateTime.TryParseExact(Path.GetFileNameWithoutExtension(name), DayFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
}
