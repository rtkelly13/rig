using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;

namespace Rig.Core.Jobs;

/// <summary>What a scheduled run reads besides the run itself.</summary>
/// <param name="Clock">Names the run and stamps each line of <c>coordinator.log</c>.</param>
/// <param name="WaitForShell">Waits up to a time for Explorer's shell window; false on timeout.</param>
/// <param name="TracesDir">The shared traces folder a logon run prunes.</param>
public sealed record ScheduledSeams(TimeProvider Clock, Func<TimeSpan, bool> WaitForShell, string TracesDir);

/// <summary>
/// What the scheduled tasks add around a coordinator run, ported from <c>desktop_manager.jobs.scheduled</c>. A task has
/// no console, so the run's narrative goes to <c>coordinator.log</c>, each line naming the run. A logon run first waits
/// for Explorer's shell window, the precondition for the virtual-desktop APIs the restore uses, and deletes trace files
/// older than <see cref="TraceKeepDays"/>. A run that crashes is logged and exits 1.
/// </summary>
public static class ScheduledRun
{
    /// <summary>The narrative's file name in the coordinator's folder.</summary>
    public const string LogName = "coordinator.log";

    /// <summary>Past this size the log moves to <c>coordinator.log.1</c> before the run.</summary>
    public const long LogLimitBytes = 512 * 1024;

    /// <summary>Trace files last written longer ago than this many days are deleted on logon.</summary>
    public const int TraceKeepDays = 14;

    /// <summary>How long a logon run waits for the shell before going on without it.</summary>
    public static readonly TimeSpan ShellTimeout = TimeSpan.FromSeconds(120);

    /// <summary>
    /// Runs <paramref name="run"/> as a scheduled task does, with the run's id and a report that appends to
    /// <c>&lt;jobsDir&gt;/coordinator.log</c>. Its exit code, or 1 when it throws.
    /// </summary>
    /// <param name="trigger">The run's trigger.</param>
    /// <param name="jobsDir">The coordinator's folder.</param>
    /// <param name="seams">The clock, the shell wait and the traces folder.</param>
    /// <param name="run">The coordinator run, given the run id and the report.</param>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "A scheduled run has no console: whatever it throws is logged and exits 1, as Python's did.")]
    public static int Run(JobTrigger trigger, string jobsDir, ScheduledSeams seams, Func<string, Action<string>, int> run)
    {
        ArgumentNullException.ThrowIfNull(seams);
        ArgumentNullException.ThrowIfNull(run);
        var runId = JobRunner.RunId(trigger, seams.Clock.GetLocalNow());
        Directory.CreateDirectory(jobsDir);
        var logPath = Path.Join(jobsDir, LogName);
        if (File.Exists(logPath) && new FileInfo(logPath).Length > LogLimitBytes)
        {
            File.Move(logPath, logPath + ".1", overwrite: true);
        }

        using var log = new StreamWriter(logPath, append: true, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { AutoFlush = true };
        void Report(string line) =>
            log.Write(string.Create(CultureInfo.InvariantCulture, $"{seams.Clock.GetLocalNow():yyyy-MM-dd HH:mm:ss} [{runId}] {line}\n"));

        try
        {
            if (trigger == JobTrigger.Logon)
            {
                Report(seams.WaitForShell(ShellTimeout) ? "Explorer shell ready" : "shell probe timed out; continuing");
                var removed = PruneTraces(seams.TracesDir, TraceKeepDays, seams.Clock.GetUtcNow());
                if (removed > 0)
                {
                    Report($"pruned {removed} trace file(s) older than {TraceKeepDays} days");
                }
            }

            return run(runId, Report);
        }
        catch (Exception error)
        {
            Report($"Jobs coordinator run crashed (trigger {JobWords.Wire(trigger)}): {error}");
            return 1;
        }
    }

    /// <summary>Deletes the <c>*.ndjson</c> files under <paramref name="directory"/> last written over <paramref name="keepDays"/> days before <paramref name="now"/>; how many went.</summary>
    /// <param name="directory">The traces folder; a missing one prunes nothing.</param>
    /// <param name="keepDays">The days a file is kept.</param>
    /// <param name="now">The time now.</param>
    public static int PruneTraces(string directory, int keepDays, DateTimeOffset now)
    {
        if (!Directory.Exists(directory))
        {
            return 0;
        }

        var cutoff = now.UtcDateTime - TimeSpan.FromDays(keepDays);
        var removed = 0;
        foreach (var path in Directory.EnumerateFiles(directory, "*.ndjson", SearchOption.AllDirectories))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(path) < cutoff)
                {
                    File.Delete(path);
                    removed++;
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // a file in use or locked stays for the next logon
            }
        }

        return removed;
    }
}
