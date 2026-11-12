using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Rig.Core.Policy;

namespace Rig.Core.Jobs;

/// <summary>One job run, as one line of <c>runs.ndjson</c> keeps it.</summary>
/// <param name="Job">The job's name.</param>
/// <param name="RunId">The coordinator run that took it.</param>
/// <param name="Started">When it started.</param>
/// <param name="Seconds">How long it took.</param>
/// <param name="ReturnCode">Its exit code; null when it never exited on its own.</param>
/// <param name="Trigger">The run's trigger.</param>
/// <param name="Error">Why it did not run to an exit code.</param>
/// <param name="Output">Its saved output, relative to the jobs folder; empty when nothing was captured.</param>
public sealed record JobRun(string Job, string RunId, string Started, double Seconds, long? ReturnCode, string Trigger, string? Error, string Output);

/// <summary>
/// Every job run the coordinator made: one <see cref="JobRun"/> line in <c>runs.ndjson</c> and the run's slice of the
/// job log as <c>runs/&lt;job&gt;/&lt;run id&gt;.log</c>. Ported from <c>desktop_manager.jobs.history</c>'s writers;
/// the web page and the MCP read them. Write failures are swallowed: the history never gates a run.
/// </summary>
public static partial class RunHistory
{
    /// <summary>The history file's name in the jobs folder.</summary>
    public const string HistoryName = "runs.ndjson";

    /// <summary>The newest lines kept once the file is trimmed.</summary>
    public const int KeepRuns = 2000;

    /// <summary>How far past <see cref="KeepRuns"/> the file grows before it is trimmed.</summary>
    public const int TrimSlack = 500;

    /// <summary>The output files each job keeps.</summary>
    public const int KeepOutputsPerJob = 50;

    /// <summary>An output is cut to its last this many bytes.</summary>
    public const int OutputLimitBytes = 256 * 1024;

    /// <summary>
    /// Saves one run's output under <paramref name="directory"/>, keeping its end, and deletes the job's oldest past
    /// <see cref="KeepOutputsPerJob"/>. The path relative to <paramref name="directory"/>, or empty when nothing was saved.
    /// </summary>
    public static string SaveOutput(string directory, string job, string runId, byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length == 0 || !RunIdPattern().IsMatch(runId))
        {
            return string.Empty;
        }

        var folder = Path.Join(directory, "runs", job);
        try
        {
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(Path.Join(folder, $"{runId}.log"), data.Length > OutputLimitBytes ? data[^OutputLimitBytes..] : data);
            var outputs = Directory.GetFiles(folder, "*.log").OrderBy(TimeOf, StringComparer.Ordinal).ToList();
            foreach (var stale in outputs.Take(outputs.Count - KeepOutputsPerJob))
            {
                File.Delete(stale);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }

        return $"runs/{job}/{runId}.log";
    }

    /// <summary>Appends <paramref name="run"/>, trimming to the newest <see cref="KeepRuns"/> once past the slack.</summary>
    public static void Append(string directory, JobRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        var path = Path.Join(directory, HistoryName);
        try
        {
            Directory.CreateDirectory(directory);
            File.AppendAllText(path, Line(run) + Environment.NewLine, new UTF8Encoding(false));
            var lines = File.ReadAllLines(path, Encoding.UTF8);
            if (lines.Length > KeepRuns + TrimSlack)
            {
                var temporary = Path.ChangeExtension(path, ".tmp");
                File.WriteAllText(temporary, string.Join(Environment.NewLine, lines[^KeepRuns..]) + Environment.NewLine, new UTF8Encoding(false));
                File.Move(temporary, path, overwrite: true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The history is a convenience; the run goes on.
        }
    }

    /// <summary>
    /// <paramref name="run"/> as pydantic writes a <c>JobRun</c>: compact, fields in order, non-ASCII kept, and empty
    /// trace and span ids, since the C# runner does not trace yet.
    /// </summary>
    public static string Line(JobRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        var text = new StringBuilder("{");
        text.Append("\"job\":").Append(Quote(run.Job))
            .Append(",\"run_id\":").Append(Quote(run.RunId))
            .Append(",\"started\":").Append(Quote(run.Started))
            .Append(",\"seconds\":").Append(PyJson.FloatRepr(run.Seconds))
            .Append(",\"returncode\":").Append(run.ReturnCode?.ToString(CultureInfo.InvariantCulture) ?? "null")
            .Append(",\"trigger\":").Append(Quote(run.Trigger))
            .Append(",\"error\":").Append(run.Error is null ? "null" : Quote(run.Error))
            .Append(",\"output\":").Append(Quote(run.Output))
            .Append(",\"trace_id\":\"\",\"span_id\":\"\"}");
        return text.ToString();
    }

    // serde_json's escaping: the quote, the backslash and control characters; everything else as itself.
    private static string Quote(string value)
    {
        var text = new StringBuilder("\"");
        foreach (var c in value)
        {
            _ = c switch
            {
                '"' => text.Append("\\\""),
                '\\' => text.Append("\\\\"),
                '\n' => text.Append("\\n"),
                '\r' => text.Append("\\r"),
                '\t' => text.Append("\\t"),
                '\b' => text.Append("\\b"),
                '\f' => text.Append("\\f"),
                < ' ' => text.Append(CultureInfo.InvariantCulture, $"\\u{(int)c:x4}"),
                _ => text.Append(c),
            };
        }

        return text.Append('"').ToString();
    }

    // tick-20261011-080201.log -> 20261011-080201, so outputs sort by time whatever their trigger.
    private static string TimeOf(string path)
    {
        var stem = Path.GetFileNameWithoutExtension(path);
        var dash = stem.IndexOf('-', StringComparison.Ordinal);
        return dash < 0 ? string.Empty : stem[(dash + 1)..];
    }

    [GeneratedRegex(@"^[a-z]+-\d{8}-\d{6}\z", RegexOptions.CultureInvariant)]
    private static partial Regex RunIdPattern();
}
