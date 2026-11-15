using System.Globalization;

namespace Rig.Core.Jobs;

/// <summary>How one job's last run went, as the ops views show it.</summary>
/// <param name="Started">When it started, in ISO 8601.</param>
/// <param name="Seconds">How long it took.</param>
/// <param name="ReturnCode">Its exit code; null when it never exited on its own.</param>
/// <param name="Trigger">The run's trigger.</param>
/// <param name="Error">Why it did not run to an exit code.</param>
/// <param name="Ok">Exit 0 with no error.</param>
public sealed record LastRunView(string Started, double Seconds, long? ReturnCode, string Trigger, string? Error, bool Ok);

/// <summary>One registered job, as <c>list_jobs</c> shows it.</summary>
/// <param name="Name">The job's name.</param>
/// <param name="Description">One line on what it does.</param>
/// <param name="Cadence">When it runs, in words: <c>every run, idle 3 min, flag claude-session-restore</c>.</param>
/// <param name="Triggers">Its triggers, sorted.</param>
/// <param name="TimeoutMinutes">How long the runner lets it go.</param>
/// <param name="Last">Its last outcome; null when it has not run.</param>
public sealed record JobView(string Name, string Description, string Cadence, IReadOnlyList<string> Triggers, int TimeoutMinutes, LastRunView? Last);

/// <summary>One recorded job run, as <c>list_runs</c> shows it.</summary>
/// <param name="Job">The job's name.</param>
/// <param name="RunId">The coordinator run that took it.</param>
/// <param name="Started">When it started.</param>
/// <param name="Seconds">How long it took.</param>
/// <param name="ReturnCode">Its exit code; null when it never exited on its own.</param>
/// <param name="Trigger">The run's trigger.</param>
/// <param name="Error">Why it did not run to an exit code.</param>
/// <param name="Ok">Exit 0 with no error.</param>
/// <param name="HasOutput">Whether the run saved output that has not been pruned.</param>
/// <param name="TraceId">The trace its span belongs to; empty when nothing traced it.</param>
public sealed record RunView(
    string Job,
    string RunId,
    string Started,
    double Seconds,
    long? ReturnCode,
    string Trigger,
    string? Error,
    bool Ok,
    bool HasOutput,
    string TraceId);

/// <summary>What one job run printed.</summary>
/// <param name="Job">The job's name.</param>
/// <param name="RunId">The run's id.</param>
/// <param name="Output">What it printed; null when it saved none or it has been pruned.</param>
public sealed record RunOutput(string Job, string RunId, string? Output);

/// <summary>A run the ops tool started in the background.</summary>
/// <param name="Job">The job asked for; null for every job that is due.</param>
/// <param name="Pid">The process that runs it.</param>
public sealed record RunStarted(string? Job, int Pid);

/// <summary>
/// The jobs coordinator as the ops pages and MCP tools show it. Ported from <c>desktop_manager.jobs.web</c>: the same
/// <c>state.json</c> and <c>runs.ndjson</c> reads, so an agent and the pages never disagree.
/// </summary>
public static class JobViews
{
    /// <summary>How many runs <c>list_runs</c> shows unless asked for another number.</summary>
    public const int DefaultLimit = 200;

    /// <summary>The most runs one answer may carry.</summary>
    public const int MaxLimit = 1000;

    private static readonly Dictionary<JobCadence, string> CadenceWords = new()
    {
        [JobCadence.EveryRun] = "every run",
        [JobCadence.OncePerBoot] = "once per boot",
        [JobCadence.Daily] = "daily",
    };

    /// <summary>When the job runs, in words: <c>every run, idle 3 min, flag claude-session-restore</c>.</summary>
    public static string CadenceText(Job job)
    {
        ArgumentNullException.ThrowIfNull(job);
        List<string> parts = [CadenceWords[job.Cadence]];
        if (job.IdleMinutes > 0)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"idle {job.IdleMinutes} min"));
        }

        if (job.Feature is { Length: > 0 } feature)
        {
            parts.Add($"flag {feature}");
        }

        return string.Join(", ", parts);
    }

    /// <summary>Every job with its last outcome from <c>state.json</c> in <paramref name="directory"/>.</summary>
    public static IReadOnlyList<JobView> Build(IEnumerable<Job> jobs, string directory)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        var state = JobState.ReadState(Path.Join(directory, JobState.StateName));
        return
        [
            .. jobs.Select(job => new JobView(
                job.Name,
                job.Description,
                CadenceText(job),
                [.. job.Triggers.Select(JobWords.Wire).Order(StringComparer.Ordinal)],
                job.TimeoutMinutes,
                state.TryGetValue(job.Name, out var outcome)
                    ? new LastRunView(outcome.Started, outcome.Seconds, outcome.ReturnCode, outcome.Trigger, outcome.Error, outcome.Ok)
                    : null)),
        ];
    }

    /// <summary>
    /// The newest <paramref name="limit"/> runs from the history, plus a last outcome the history does not hold,
    /// newest first. <paramref name="job"/> narrows it to one job; null shows every job.
    /// </summary>
    public static IReadOnlyList<RunView> Runs(string directory, string? job = null, int limit = DefaultLimit)
    {
        var records = RunHistory.ReadRuns(directory, job, RunHistory.KeepRuns + RunHistory.TrimSlack);
        var known = records.Select(record => (record.Job, record.RunId)).ToHashSet();
        var views = records.Select(Run).ToList();
        foreach (var (name, outcome) in JobState.ReadState(Path.Join(directory, JobState.StateName)))
        {
            var runId = OutcomeRunId(outcome);
            if ((job is not null && name != job) || known.Contains((name, runId)))
            {
                continue;
            }

            views.Add(new RunView(
                name,
                runId,
                outcome.Started,
                outcome.Seconds,
                outcome.ReturnCode,
                outcome.Trigger,
                outcome.Error,
                outcome.Ok,
                LoggedOutput(directory, name, outcome) is not null,
                string.Empty));
        }

        return [.. views.OrderByDescending(view => Started(view.Started)).Take(limit)];
    }

    /// <summary>
    /// What one run printed. Throws <see cref="KeyNotFoundException"/> for a job the registry lacks and
    /// <see cref="ArgumentException"/> for a run id or run nobody recorded.
    /// </summary>
    public static RunOutput Output(IEnumerable<Job> jobs, string directory, string job, string runId)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        if (!jobs.Any(item => item.Name == job))
        {
            throw new KeyNotFoundException($"No job named '{job}'");
        }

        if (!RunHistory.IsRunId(runId))
        {
            throw new ArgumentException($"No run '{runId}' of {job}", nameof(runId));
        }

        if (RunHistory.FindRun(directory, job, runId) is { } record)
        {
            return new RunOutput(job, runId, RunHistory.ReadOutput(directory, record));
        }

        var outcome = JobState.ReadState(Path.Join(directory, JobState.StateName)).GetValueOrDefault(job);
        return outcome is not null && OutcomeRunId(outcome) == runId
            ? new RunOutput(job, runId, LoggedOutput(directory, job, outcome))
            : throw new ArgumentException($"No run '{runId}' of {job}", nameof(runId));
    }

    /// <summary>The run that produced <paramref name="outcome"/>; outcomes saved before runs had ids get one from their start.</summary>
    public static string OutcomeRunId(Outcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        return outcome.RunId is { Length: > 0 } runId
            ? runId
            : string.Create(CultureInfo.InvariantCulture, $"{outcome.Trigger}-{outcome.StartedAt:yyyyMMdd-HHmmss}");
    }

    /// <summary>A pre-history run's output: its section of the job log, while the log still holds it.</summary>
    public static string? LoggedOutput(string directory, string job, Outcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        if (outcome.RunId is not { Length: > 0 } runId)
        {
            return null;
        }

        var lines = JobLog.RunOutput(JobState.LogPath(JobState.LogsDir(directory), job), runId);
        return lines.Count == 0 ? null : string.Join('\n', lines) + "\n";
    }

    private static RunView Run(JobRun record) => new(
        record.Job,
        record.RunId,
        record.Started,
        record.Seconds,
        record.ReturnCode,
        record.Trigger,
        record.Error,
        record.ReturnCode == 0 && record.Error is null,
        record.Output.Length > 0,
        record.TraceId);

    // Python's _started: an unreadable time sorts as the minimum, and one without an offset is taken as local.
    private static DateTimeOffset Started(string text) => IsoStamp.Parse(text) ?? DateTimeOffset.MinValue;
}
