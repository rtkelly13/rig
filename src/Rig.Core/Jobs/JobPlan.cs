using System.Globalization;

namespace Rig.Core.Jobs;

/// <summary>One job's place in a run: whether the run takes it, and why.</summary>
/// <param name="Job">The job.</param>
/// <param name="Run">Whether the run takes it.</param>
/// <param name="Reason">Why: its cadence, <c>requested</c>, a retry, or why it is skipped.</param>
public sealed record Planned(Job Job, bool Run, string Reason);

/// <summary>Which jobs a run takes and why. Ported from <c>desktop_manager.jobs.runner.plan</c>.</summary>
public static class JobPlan
{
    /// <summary>
    /// Each of <paramref name="jobs"/> (only those named in <paramref name="only"/>, when it names any) with whether a run
    /// on <paramref name="trigger"/> takes it. In order: a missing required path skips it; a flag that is off skips it
    /// unless it is named; <paramref name="force"/> or naming it runs it; a tick reruns a job whose run this boot exited
    /// with one of its retry codes; then the trigger and the cadence decide.
    /// </summary>
    /// <param name="jobs">The jobs, in run order.</param>
    /// <param name="trigger">The run's trigger.</param>
    /// <param name="state">Each job's last outcome.</param>
    /// <param name="now">The time now.</param>
    /// <param name="booted">When this machine booted.</param>
    /// <param name="flags">The rig flags' effective state; a flag it does not name is off.</param>
    /// <param name="exists">Whether a path exists; the file system when null.</param>
    /// <param name="only">The jobs asked for by name.</param>
    /// <param name="force">Run every job, due or not.</param>
    public static IReadOnlyList<Planned> Plan(
        IEnumerable<Job> jobs,
        JobTrigger trigger,
        IReadOnlyDictionary<string, Outcome> state,
        DateTimeOffset now,
        DateTimeOffset booted,
        IReadOnlyDictionary<string, bool> flags,
        Func<string, bool>? exists = null,
        IReadOnlySet<string>? only = null,
        bool force = false)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(flags);
        exists ??= path => File.Exists(path) || Directory.Exists(path);
        only ??= new HashSet<string>(StringComparer.Ordinal);
        var planned = new List<Planned>();
        foreach (var job in jobs)
        {
            if (only.Count > 0 && !only.Contains(job.Name))
            {
                continue;
            }

            var last = state.GetValueOrDefault(job.Name);
            var named = only.Contains(job.Name);
            planned.Add(
                job.Requires.FirstOrDefault(path => !exists(path)) is { } missing ? new(job, false, $"missing {missing}")
                : job.Feature is { Length: > 0 } feature && !flags.GetValueOrDefault(feature) && !named ? new(job, false, $"flag {feature} off")
                : force || named ? new(job, true, "requested")
                : Retry(job, trigger, last, booted) is { } retry ? new(job, true, retry)
                : !job.Triggers.Contains(trigger) ? new(job, false, $"not on {JobWords.Wire(trigger)}")
                : !JobState.IsDue(job, last, now, booted) ? new(job, false, $"not due ({JobWords.Wire(job.Cadence)})")
                : new(job, true, JobWords.Wire(job.Cadence)));
        }

        return planned;
    }

    // Why a tick reruns a job whose run this boot said it could not run yet; null if it does not.
    private static string? Retry(Job job, JobTrigger trigger, Outcome? last, DateTimeOffset booted) =>
        trigger == JobTrigger.Tick && last is { ReturnCode: { } code } && last.StartedAt >= booted && job.RetryExitCodes.Contains(code)
            ? string.Create(CultureInfo.InvariantCulture, $"retry: last run exited {code}")
            : null;
}
