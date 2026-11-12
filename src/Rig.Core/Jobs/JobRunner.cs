using System.Globalization;
using System.Text;

namespace Rig.Core.Jobs;

/// <summary>Starts one job and waits for it, appending its output to its log under a header naming the run.</summary>
public interface IJobExecutor
{
    /// <summary>
    /// Runs <paramref name="job"/>, its output appended to <c>&lt;logs&gt;/&lt;job&gt;.log</c> under a
    /// <c>--- &lt;time&gt; [&lt;run id&gt;] &lt;argv&gt;</c> header. Its exit code; throwing means it never started.
    /// </summary>
    /// <param name="job">The job.</param>
    /// <param name="logsDir">The coordinator's <c>logs/</c> folder.</param>
    /// <param name="runId">The coordinator run taking it.</param>
    long Execute(Job job, string logsDir, string runId);
}

/// <summary>What a coordinator run reads besides its jobs: the executor, the idle time, the clock, the lock and its report.</summary>
/// <param name="Executor">Starts each job.</param>
/// <param name="IdleSeconds">Seconds since the last keyboard or mouse input.</param>
/// <param name="Clock">The clock: the local time stamps the run, and its timestamps time each job.</param>
/// <param name="Report">Takes each line the run reports.</param>
/// <param name="Hold">Takes the run lock at a path, waiting up to a time; null when another run holds it.</param>
public sealed record JobRunnerSeams(
    IJobExecutor Executor,
    Func<double> IdleSeconds,
    TimeProvider Clock,
    Action<string> Report,
    Func<string, TimeSpan, IDisposable?> Hold);

/// <summary>
/// One coordinator run: takes the run lock, runs each due job in turn, and records every decision in
/// <c>last-&lt;trigger&gt;.json</c>, every outcome in <c>state.json</c> and every run in <c>runs.ndjson</c>. Ported from
/// <c>desktop_manager.jobs.runner.run</c>; the history's <c>trace_id</c> and <c>span_id</c> are written empty, since
/// the C# runner makes no spans yet.
/// </summary>
public sealed class JobRunner(JobRunnerSeams seams)
{
    /// <summary>How long a logon run waits for a run that holds the lock; a tick gives up at once.</summary>
    public static readonly TimeSpan LogonLockWait = TimeSpan.FromMinutes(30);

    /// <summary>The exit code a job that ran out of time records.</summary>
    public const long TimedOut = 124;

    /// <summary>How many of a failed job's last output lines its outcome keeps.</summary>
    public const int DetailLines = 12;

    /// <summary>How long the summary line may be.</summary>
    public const int SummaryChars = 200;

    /// <summary>
    /// Runs the due jobs of <paramref name="jobs"/> on <paramref name="trigger"/>, recording them under
    /// <paramref name="directory"/>. 0 when every job that ran succeeded, or when another run holds the lock; else 1.
    /// A job with <see cref="Job.IdleMinutes"/> is checked against the idle time just before it would start, and
    /// skipped without an outcome while someone is at the keyboard.
    /// </summary>
    /// <param name="jobs">The registry, in run order.</param>
    /// <param name="trigger">The run's trigger.</param>
    /// <param name="directory">The coordinator's folder.</param>
    /// <param name="booted">When this machine booted.</param>
    /// <param name="flags">The rig flags' effective state.</param>
    /// <param name="only">The jobs asked for by name.</param>
    /// <param name="force">Run every job, due or not.</param>
    public int Run(
        IEnumerable<Job> jobs,
        JobTrigger trigger,
        string directory,
        DateTimeOffset booted,
        IReadOnlyDictionary<string, bool> flags,
        IReadOnlySet<string>? only = null,
        bool force = false)
    {
        Directory.CreateDirectory(directory);
        var lockPath = Path.Join(directory, JobState.LockName);
        using var held = seams.Hold(lockPath, trigger == JobTrigger.Logon ? LogonLockWait : TimeSpan.Zero);
        if (held is null)
        {
            seams.Report($"another jobs run holds {lockPath}; skipping this {JobWords.Wire(trigger)} run");
            return 0;
        }

        var started = seams.Clock.GetLocalNow();
        var runId = string.Create(CultureInfo.InvariantCulture, $"{JobWords.Wire(trigger)}-{started:yyyyMMdd-HHmmss}");
        var statePath = Path.Join(directory, JobState.StateName);
        var recordPath = JobState.LastRunPath(directory, JobWords.Wire(trigger));
        var state = new Dictionary<string, Outcome>(JobState.ReadState(statePath), StringComparer.Ordinal);
        var decisions = new List<Decision>();
        void Save(DateTimeOffset? finished) =>
            JobState.WriteRun(recordPath, new RunRecord(runId, JobWords.Wire(trigger), Iso(started), finished is { } end ? Iso(end) : null, [.. decisions]));

        foreach (var item in JobPlan.Plan(jobs, trigger, state, started, booted, flags, only: only, force: force))
        {
            var busy = !item.Run || item.Reason == "requested" ? null : Busy(item.Job);
            if (!item.Run || busy is not null)
            {
                var reason = busy ?? item.Reason;
                seams.Report($"skip {item.Job.Name}: {reason}");
                decisions.Add(new Decision(item.Job.Name, false, reason));
                continue;
            }

            seams.Report($"run  {item.Job.Name} ({item.Reason})");

            // Recorded before the job starts, so a run killed part way still shows in `rig jobs last`.
            Save(null);
            var outcome = RunOne(item.Job, trigger, directory, runId);
            state[item.Job.Name] = outcome;
            JobState.WriteState(statePath, state);
            decisions.Add(new Decision(item.Job.Name, true, item.Reason, outcome));
            Save(null);
            seams.Report($"     {item.Job.Name}: {Describe(outcome)}");
            foreach (var line in outcome.Detail)
            {
                seams.Report($"       | {line}");
            }
        }

        Save(seams.Clock.GetLocalNow());
        return decisions.Any(item => item.Outcome is { Ok: false }) ? 1 : 0;
    }

    /// <summary>A time as Python's <c>isoformat(timespec="seconds")</c> writes an aware one.</summary>
    public static string Iso(DateTimeOffset time) => time.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);

    /// <summary>How <c>jobs run</c> reports an outcome: <c>exit 0 after 3s: summary</c> or <c>error after 0s: why</c>.</summary>
    public static string Describe(Outcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        if (outcome.Error is { Length: > 0 } error)
        {
            return $"error after {outcome.WholeSeconds}s: {error}";
        }

        var described = string.Create(CultureInfo.InvariantCulture, $"exit {outcome.ReturnCode} after {outcome.WholeSeconds}s");
        return outcome.Summary is { Length: > 0 } summary ? $"{described}: {summary}" : described;
    }

    private string? Busy(Job job)
    {
        if (job.IdleMinutes == 0)
        {
            return null;
        }

        var idle = seams.IdleSeconds();
        return idle >= job.IdleMinutes * 60
            ? null
            : string.Create(CultureInfo.InvariantCulture, $"in use (idle {Math.Round(idle, MidpointRounding.ToEven):0}s, needs {job.IdleMinutes} min)");
    }

    private Outcome RunOne(Job job, JobTrigger trigger, string directory, string runId)
    {
        var logs = JobState.LogsDir(directory);
        var log = JobState.LogPath(logs, job.Name);
        JobLog.Rotate(log);
        var offset = JobLog.Size(log);
        var started = seams.Clock.GetLocalNow();
        var begun = seams.Clock.GetTimestamp();
        var before = JobLog.Stamp(job.DetailLog);
        var detailOffset = JobLog.Size(job.DetailLog);
        long? returnCode;
        string? error = null;
        try
        {
            returnCode = seams.Executor.Execute(job, logs, runId);
        }
#pragma warning disable CA1031 // A job that cannot start is recorded as an error, as Python's runner records any exception.
        catch (Exception crash)
#pragma warning restore CA1031
        {
            returnCode = null;
            error = $"{crash.GetType().Name}: {crash.Message}";
        }

        var seconds = Math.Round(seams.Clock.GetElapsedTime(begun).TotalSeconds, 1, MidpointRounding.ToEven);
        var printed = JobLog.RunOutput(log, runId);
        var changed = job.DetailLog is { } own && JobLog.Stamp(own) != before;

        // Its own log's last section explains this run only if this run wrote to it.
        if (printed.Count == 0 && changed)
        {
            printed = JobLog.RunOutput(job.DetailLog!);
        }

        // A job that redirects its stdout (chrome-organise) leaves only markers in the coordinator log, so the saved
        // output carries what this run added to its own log too. A log that shrank was rewritten, so all of it is new.
        var detail = changed ? JobLog.ReadFrom(job.DetailLog!, JobLog.Size(job.DetailLog) >= detailOffset ? detailOffset : 0) : [];
        var failed = returnCode != 0 || error is not null;
        var outcome = new Outcome(Iso(started), seconds, returnCode, JobWords.Wire(trigger))
        {
            Error = error,
            RunId = runId,
            Summary = printed.Count > 0 ? Truncate(printed[^1], SummaryChars) : null,
            Detail = failed ? [.. printed.TakeLast(DetailLines)] : [],
        };
        var output = RunHistory.SaveOutput(directory, job.Name, runId, [.. JobLog.ReadFrom(log, offset), .. detail]);
        RunHistory.Append(directory, new JobRun(job.Name, runId, outcome.Started, seconds, returnCode, outcome.Trigger, error, output));
        return outcome;
    }

    // Python's text[:count]: the first count code points, so a pair of surrogates is never split.
    private static string Truncate(string text, int count)
    {
        var builder = new StringBuilder();
        foreach (var rune in text.EnumerateRunes().Take(count))
        {
            builder.Append(rune.ToString());
        }

        return builder.ToString();
    }
}
