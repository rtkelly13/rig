using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Rig.Core.Processes;

namespace Rig.Core.Workspace;

/// <summary>The runner a job ran on, read from its name, and what GitHub bills a minute of it as.</summary>
public enum RunnerOs
{
    /// <summary>Linux; one billable minute a minute.</summary>
    Ubuntu,

    /// <summary>Windows; two.</summary>
    Windows,

    /// <summary>macOS; ten.</summary>
    MacOs,
}

/// <summary>One job of a workflow run.</summary>
/// <param name="JobId">The job's id.</param>
/// <param name="Name">The job's name.</param>
/// <param name="Runner">The runner inferred from the name.</param>
/// <param name="DurationSeconds">Wall time from start to completion; zero when either is missing or unreadable.</param>
public sealed record JobTelemetry(long JobId, string Name, RunnerOs Runner, double DurationSeconds)
{
    /// <summary>What a minute on <see cref="Runner"/> bills as.</summary>
    public double Multiplier => CiTelemetry.Multiplier(Runner);

    /// <summary>The duration in Linux-equivalent billable seconds.</summary>
    public double BillableSeconds => DurationSeconds * Multiplier;
}

/// <summary>One workflow run and its jobs.</summary>
/// <param name="Repo">The managed project's name.</param>
/// <param name="RunId">The run's id.</param>
/// <param name="WorkflowName">The workflow's name.</param>
/// <param name="Event">What triggered it.</param>
/// <param name="HeadBranch">The branch it ran on, or empty.</param>
/// <param name="Conclusion">success, failure, cancelled, or <c>unknown</c> while it runs.</param>
/// <param name="DurationSeconds">Wall time from start (or creation) to last update.</param>
/// <param name="Jobs">Its jobs.</param>
public sealed record RunTelemetry(
    string Repo, long RunId, string WorkflowName, string Event, string HeadBranch, string Conclusion, double DurationSeconds,
    IReadOnlyList<JobTelemetry> Jobs)
{
    /// <summary>The jobs' billable seconds together.</summary>
    public double BillableSeconds => Jobs.Sum(job => job.BillableSeconds);
}

/// <summary>
/// CI utilisation across the estate: recent workflow runs read through <c>gh api</c>, each job's wall time weighted by
/// what its runner bills. The runner is inferred from the job's name, as the Python did; GitHub's job record does not
/// carry the billed OS.
/// </summary>
/// <param name="github">The GitHub REST seam.</param>
public sealed class CiTelemetry(IGitHubApi github)
{
    /// <summary>The runner a job named <paramref name="jobName"/> ran on.</summary>
    public static RunnerOs InferRunner(string jobName)
    {
        ArgumentNullException.ThrowIfNull(jobName);
        var name = jobName.ToLowerInvariant();
        if (name.Contains("windows", StringComparison.Ordinal) || name.Contains("win32", StringComparison.Ordinal))
        {
            return RunnerOs.Windows;
        }

        // "mac" covers "macos" too; "darwin" is the other spelling.
        return name.Contains("mac", StringComparison.Ordinal) || name.Contains("darwin", StringComparison.Ordinal)
            ? RunnerOs.MacOs
            : RunnerOs.Ubuntu;
    }

    /// <summary>What a minute on <paramref name="runner"/> bills as.</summary>
    public static double Multiplier(RunnerOs runner) => runner switch
    {
        RunnerOs.Windows => 2.0,
        RunnerOs.MacOs => 10.0,
        _ => 1.0,
    };

    /// <summary>The seconds from <paramref name="start"/> to <paramref name="end"/>; zero when either is absent or unreadable, never negative.</summary>
    public static double Seconds(string? start, string? end) =>
        Instant(start) is { } from && Instant(end) is { } to ? Math.Max(0.0, (to - from).TotalSeconds) : 0.0;

    /// <summary>A job from its <c>actions/runs/{id}/jobs</c> record.</summary>
    public static JobTelemetry Job(JsonObject job)
    {
        ArgumentNullException.ThrowIfNull(job);
        var name = Text(job, "name") ?? "unknown";
        return new(Number(job, "id"), name, InferRunner(name), Seconds(Text(job, "started_at"), Text(job, "completed_at")));
    }

    /// <summary>A run from its <c>actions/runs</c> record and its jobs' records.</summary>
    public static RunTelemetry Run(string repo, JsonObject run, IEnumerable<JsonObject> jobs)
    {
        ArgumentNullException.ThrowIfNull(run);
        var started = Text(run, "run_started_at") is { Length: > 0 } runStarted ? runStarted : Text(run, "created_at");
        return new(
            repo,
            Number(run, "id"),
            Text(run, "name") ?? "Unknown",
            Text(run, "event") ?? "unknown",
            Text(run, "head_branch") ?? string.Empty,
            Text(run, "conclusion") ?? "unknown",
            Seconds(started, Text(run, "updated_at")),
            [.. jobs.Select(Job)]);
    }

    /// <summary>The <paramref name="limit"/> most recent runs of <paramref name="slug"/> with their jobs; none when gh fails.</summary>
    public async Task<IReadOnlyList<RunTelemetry>> RecentAsync(string repo, string slug, int limit, CancellationToken cancellationToken = default)
    {
        var runs = await ListAsync($"repos/{slug}/actions/runs?per_page={limit}", "workflow_runs", cancellationToken).ConfigureAwait(false);
        var telemetry = new List<RunTelemetry>(runs.Count);
        foreach (var run in runs)
        {
            var jobs = await ListAsync($"repos/{slug}/actions/runs/{Number(run, "id")}/jobs", "jobs", cancellationToken).ConfigureAwait(false);
            telemetry.Add(Run(repo, run, jobs));
        }

        return telemetry;
    }

    private async Task<IReadOnlyList<JsonObject>> ListAsync(string route, string field, CancellationToken cancellationToken)
    {
        var ran = await github.CallAsync(ApiRequest.Get(route), cancellationToken).ConfigureAwait(false);
        if (!ran.Ok)
        {
            return [];
        }

        try
        {
            return JsonNode.Parse(ran.Out) is JsonObject root && root[field] is JsonArray items ? [.. items.OfType<JsonObject>()] : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static DateTimeOffset? Instant(string? text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var instant) ? instant : null;

    private static string? Text(JsonObject node, string name) =>
        node[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static long Number(JsonObject node, string name) =>
        node[name] is JsonValue value && value.TryGetValue<long>(out var number) ? number : 0;
}
