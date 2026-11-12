using System.CommandLine;
using System.Text;
using Rig.Cli.Vault;
using Rig.Core.Jobs;

namespace Rig.Cli.Jobs;

/// <summary>What <c>rig jobs</c> reads: the coordinator's folder, the registry, the flags, the clock and the scheduler.</summary>
/// <param name="JobsDir">The coordinator's folder: <c>state.json</c>, <c>last-&lt;trigger&gt;.json</c> and <c>logs/</c>.</param>
/// <param name="Jobs">The registry for this machine.</param>
/// <param name="Flags">The rig flags' effective state.</param>
/// <param name="Clock">The clock <c>status</c> plans against.</param>
/// <param name="Booted">When this machine booted, given the time now.</param>
/// <param name="Tasks">The Task Scheduler to check the coordinator's tasks in; null where there is none (off Windows).</param>
internal sealed record JobsHost(
    string JobsDir,
    Func<IReadOnlyList<Job>> Jobs,
    Func<IReadOnlyDictionary<string, bool>> Flags,
    TimeProvider Clock,
    Func<DateTimeOffset, DateTimeOffset> Booted,
    IScheduledTasks? Tasks);

/// <summary>
/// <c>rig jobs status|last|log</c>: what the coordinator did and will do, read from its files. Ported from
/// <c>desktop_manager.jobs.cli</c>; <c>run</c>, <c>install</c> and <c>uninstall</c> still go to Python. The text is
/// Python's without rich's colours, and the table is padded to its widest cell rather than wrapped at 80 columns.
/// </summary>
internal sealed class JobsCommand(JobsHost host, TextWriter output)
{
    /// <summary>A job name that is not in the registry exits with this.</summary>
    public const int UnknownExit = 2;

    /// <summary>The <c>jobs</c> command, with the subcommands ported so far.</summary>
    public Command Build()
    {
        var statusTrigger = TriggerOption("tick", "Show what a run on this trigger would do.");
        var status = new Command("status", "Each job: when it runs, its last outcome, and whether the next run takes it.") { statusTrigger };
        status.SetAction((parsed, cancellationToken) => Status(JobWords.Trigger(parsed.GetRequiredValue(statusTrigger)), cancellationToken));

        var lastTrigger = TriggerOption("logon", "Show the latest run on this trigger.");
        var last = new Command("last", "The latest run on a trigger: every job it ran or skipped, why, and how each went.") { lastTrigger };
        last.SetAction(parsed => Last(parsed.GetRequiredValue(lastTrigger)));

        var job = new Argument<string>("job") { Description = "Job name." };
        var lines = new Option<int>("--lines", "-n") { Description = "How many lines from the end.", DefaultValueFactory = _ => 40 };
        var log = new Command("log", "Show the end of a job's log.") { job, lines };
        log.SetAction(parsed => Log(parsed.GetRequiredValue(job), parsed.GetValue(lines)));

        return new Command("jobs", "Logon, every-15-minute and daily jobs that keep this machine aligned, run from one registry.")
        {
            status,
            last,
            log,
        };
    }

    private static Option<string> TriggerOption(string defaultValue, string description)
    {
        var option = new Option<string>("--trigger") { Description = description, DefaultValueFactory = _ => defaultValue };
        option.AcceptOnlyFromAmong([.. JobWords.Triggers]);
        return option;
    }

    private string LogsDir => JobState.LogsDir(host.JobsDir);

    private async Task<int> Status(JobTrigger trigger, CancellationToken cancellationToken)
    {
        var state = JobState.ReadState(Path.Join(host.JobsDir, JobState.StateName));
        var now = host.Clock.GetLocalNow();
        var table = new TextTable(
            null,
            [("job", false), ("runs on", false), ("last run", false), ("result", false), ($"next {JobWords.Wire(trigger)}", false)]);
        var failed = new List<(string Name, Outcome Outcome)>();
        foreach (var item in JobPlan.Plan(host.Jobs(), trigger, state, now, host.Booted(now), host.Flags()))
        {
            var last = state.GetValueOrDefault(item.Job.Name);
            if (last is { Ok: false })
            {
                failed.Add((item.Job.Name, last));
            }

            table.Add(
                item.Job.Name,
                $"{item.Job.TriggerList} ({JobWords.Wire(item.Job.Cadence)})",
                last is null ? "-" : last.LastRun,
                last is null ? "-" : last.Result,
                item.Run ? "run" : item.Reason);
        }

        var text = new StringBuilder(table.Render());
        foreach (var (name, outcome) in failed)
        {
            Detail(text, name, outcome);
        }

        if (host.Tasks is { } tasks)
        {
            var missing = new List<string>();
            foreach (var name in CoordinatorTasks.All)
            {
                if (!await tasks.ExistsAsync(name, cancellationToken).ConfigureAwait(false))
                {
                    missing.Add(name);
                }
            }

            if (missing.Count > 0)
            {
                text.Append("Not registered: ").AppendJoin(", ", missing).Append(". Run `desktop-manager jobs install`.\n");
            }
        }

        text.Append("Logs: ").Append(LogsDir).Append('\n');
        await output.WriteAsync(text.ToString()).ConfigureAwait(false);
        return 0;
    }

    private int Last(string trigger)
    {
        if (JobState.ReadRun(JobState.LastRunPath(host.JobsDir, trigger)) is not { } run)
        {
            output.Write($"No {trigger} run recorded yet.\n");
            return 0;
        }

        var text = new StringBuilder().Append(run.RunId).Append("  ").Append(run.Started).Append(" -> ")
            .Append(run.Finished ?? "did not finish").Append('\n');
        var table = new TextTable(null, [("job", false), (string.Empty, false), ("took", false), ("result", false)]);
        foreach (var item in run.Decisions)
        {
            _ = item.Outcome is { } outcome
                ? table.Add(item.Job, "ran", $"{outcome.WholeSeconds}s", outcome.Result)
                : table.Add(item.Job, "skip", string.Empty, item.Reason);
        }

        text.Append(table.Render());
        foreach (var item in run.Failed)
        {
            Detail(text, item.Job, item.Outcome!);
        }

        text.Append("Job logs: ").Append(LogsDir).Append(" (sections headed [").Append(run.RunId).Append("])\n");
        output.Write(text.ToString());
        return 0;
    }

    private int Log(string name, int lines)
    {
        if (!host.Jobs().Any(item => item.Name == name))
        {
            output.Write($"Unknown job: {name}\n");
            return UnknownExit;
        }

        var path = JobState.LogPath(LogsDir, name);
        if (!File.Exists(path))
        {
            output.Write($"No log yet: {path}\n");
            return 0;
        }

        output.Write(string.Join('\n', Tail(ReadShared(path), lines)) + "\n");
        return 0;
    }

    // Python's text.splitlines()[-lines:]: 0 keeps every line, and a negative count drops that many from the start.
    private static IEnumerable<string> Tail(string text, int lines)
    {
        var all = text.Split(["\r\n", "\n", "\r"], StringSplitOptions.None);
        var count = all.Length > 0 && all[^1].Length == 0 ? all.Length - 1 : all.Length;
        var start = lines > 0 ? Math.Max(0, count - lines) : Math.Min(count, -lines);
        return all.Take(count).Skip(start);
    }

    // The log as it stands while a running job may still be appending to it: Windows refuses a plain read of a file
    // another process holds open for writing.
    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    // Only when the outcome kept the end of its output.
    private static void Detail(StringBuilder text, string name, Outcome outcome)
    {
        if (outcome.Detail.Count == 0)
        {
            return;
        }

        text.Append('\n').Append(name).Append(" failed; the end of its output:\n");
        foreach (var line in outcome.Detail)
        {
            text.Append("  | ").Append(line).Append('\n');
        }
    }
}
