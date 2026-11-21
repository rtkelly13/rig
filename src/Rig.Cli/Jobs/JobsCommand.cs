#pragma warning disable RS0030 // Legacy System.CommandLine awaiting DotMake migration (Issue #234)
using System.CommandLine;
using System.Text;
using Rig.Cli.Rendering;
using Rig.Core.Jobs;
using Rig.Core.Processes;

namespace Rig.Cli.Jobs;

/// <summary>What <c>rig jobs</c> reads: the coordinator's folder, the registry, the flags, the clock and the scheduler.</summary>
/// <param name="JobsDir">The coordinator's folder: <c>state.json</c>, <c>last-&lt;trigger&gt;.json</c> and <c>logs/</c>.</param>
/// <param name="Jobs">The registry for this machine.</param>
/// <param name="Flags">The rig flags' effective state.</param>
/// <param name="Clock">The clock <c>status</c> plans against.</param>
/// <param name="Booted">When this machine booted, given the time now.</param>
/// <param name="Tasks">The Task Scheduler to check the coordinator's tasks in; null where there is none (off Windows).</param>
/// <param name="Executor">Starts each job <c>run</c> takes.</param>
/// <param name="IdleSeconds">Seconds since the last keyboard or mouse input.</param>
/// <param name="Hold">Takes the run lock; <see cref="RunLock.Hold"/> outside tests.</param>
/// <param name="Trace">Starts writing a real run's job spans; disposing it writes them out.</param>
/// <param name="WaitForShell">Waits up to a time for Explorer's shell window, before a <c>--scheduled</c> logon run.</param>
/// <param name="TracesDir">The shared traces folder a <c>--scheduled</c> logon run prunes.</param>
/// <param name="InstallTarget">Resolves and validates the stable task targets before registration, for a launcher and an optional pythonw.</param>
/// <param name="UserId">The interactive identity used by the tasks.</param>
/// <param name="MissingRig">The published <c>rig</c> the tasks would point at when it does not exist, else null.</param>
internal sealed record JobsHost(
    string JobsDir,
    Func<IReadOnlyList<Job>> Jobs,
    Func<IReadOnlyDictionary<string, bool>> Flags,
    TimeProvider Clock,
    Func<DateTimeOffset, DateTimeOffset> Booted,
    IScheduledTasks? Tasks,
    IJobExecutor Executor,
    Func<double> IdleSeconds,
    Func<string, TimeSpan, IDisposable?> Hold,
    Func<IDisposable> Trace,
    Func<TimeSpan, bool> WaitForShell,
    string TracesDir,
    Func<JobsLauncher, string?, CancellationToken, Task<JobsInstallTarget>> InstallTarget,
    Func<string> UserId,
    Func<string?> MissingRig)
{
    /// <summary>The macOS launchd adapter; null off macOS.</summary>
    public MacJobsScheduler? MacScheduler { get; init; }
}

/// <summary>
/// <c>rig jobs</c>: the coordinator's runs and task installation. The table is padded to its widest cell rather than wrapped at 80 columns.
/// </summary>
internal sealed class JobsCommand(JobsHost host, TextWriter output)
{
    /// <summary>A job name that is not in the registry exits with this.</summary>
    public const int UnknownExit = 2;

    /// <summary>The <c>jobs</c> command, with its subcommands.</summary>
    public Command Build()
    {
        var statusTrigger = TriggerOption("tick", "Show what a run on this trigger would do.");
        var status = new Command("status", "Each job: when it runs, its last outcome, and whether the next run takes it.") { statusTrigger };
        status.SetAction((parsed, cancellationToken) => Status(JobWords.Trigger(parsed.GetRequiredValue(statusTrigger)), cancellationToken));

        var lastTrigger = TriggerOption(host.MacScheduler is null ? "logon" : "tick", "Show the latest run on this trigger.");
        var last = new Command("last", "The latest run on a trigger: every job it ran or skipped, why, and how each went.") { lastTrigger };
        last.SetAction(parsed => Last(parsed.GetRequiredValue(lastTrigger)));

        var job = new Argument<string>("job") { Description = "Job name." };
        var lines = new Option<int>("--lines", "-n") { Description = "How many lines from the end.", DefaultValueFactory = _ => 40 };
        var log = new Command("log", "Show the end of a job's log.") { job, lines };
        log.SetAction(parsed => Log(parsed.GetRequiredValue(job), parsed.GetValue(lines)));

        var runTrigger = TriggerOption("tick", "Run the jobs due on this trigger.");
        var only = new Option<string[]>("--only") { Description = "Run just this job, due or not. Repeatable.", DefaultValueFactory = _ => [] };
        var force = new Option<bool>("--force") { Description = "Run every job on the trigger, due or not." };
        var dryRun = new Option<bool>("--dry-run") { Description = "Say what would run, and run nothing." };
        var scheduled = new Option<bool>("--scheduled")
        {
            Description = "For the scheduled tasks: narrate to coordinator.log, and on logon wait for the shell and prune old traces.",
            Hidden = true,
        };
        var run = new Command("run", "Run the due jobs now, in the foreground, as the scheduled tasks do.") { runTrigger, only, force, dryRun, scheduled };
        run.SetAction(parsed => Run(
            JobWords.Trigger(parsed.GetRequiredValue(runTrigger)),
            parsed.GetValue(only) ?? [],
            parsed.GetValue(force),
            parsed.GetValue(dryRun),
            parsed.GetValue(scheduled)));

        var uninstall = new Command("uninstall", "Remove the coordinator's scheduled tasks.");
        uninstall.SetAction((_, cancellationToken) => Uninstall(cancellationToken));

        var noStart = new Option<bool>("--no-start") { Description = "Do not start the display guard now." };
        var start = new Option<bool>("--start") { Description = "Start the display guard now as well as at logon (the default)." };
        var launcher = new Option<string>("--launcher")
        {
            Description = "How the tasks start rig: pythonw opens no window; direct needs no Python but flashes a console briefly.",
            DefaultValueFactory = _ => "pythonw",
        };
        launcher.AcceptOnlyFromAmong("pythonw", "direct");
        var pythonw = new Option<string?>("--pythonw") { Description = "The pythonw.exe that starts rig. Default: uv's managed Python." };
        var install = new Command("install", "Register the macOS periodic LaunchAgent or the Windows Logon, Tick and Display Guard tasks.") { noStart, start, launcher, pythonw };
        install.SetAction((parsed, cancellationToken) => Install(
            !parsed.GetValue(noStart),
            parsed.GetRequiredValue(launcher) == "direct" ? JobsLauncher.Direct : JobsLauncher.Pythonw,
            parsed.GetValue(pythonw),
            cancellationToken));

        return new Command("jobs", "Logon, every-15-minute and daily jobs that keep this machine aligned, run from one registry.")
        {
            status,
            last,
            log,
            run,
            uninstall,
            install,
        };
    }

    private static Option<string> TriggerOption(string defaultValue, string description)
    {
        var option = new Option<string>("--trigger") { Description = description, DefaultValueFactory = _ => defaultValue };
        option.AcceptOnlyFromAmong([.. JobWords.Triggers]);
        return option;
    }

    private string LogsDir => JobState.LogsDir(host.JobsDir);

    private async Task<int> Install(bool start, JobsLauncher launcher, string? pythonw, CancellationToken cancellationToken)
    {
        if (host.MacScheduler is { } mac)
        {
            try
            {
                var target = await host.InstallTarget(JobsLauncher.Direct, null, cancellationToken).ConfigureAwait(false);
                await mac.InstallAsync(target, cancellationToken).ConfigureAwait(false);
                await output.WriteLineAsync($"Registered {MacJobsScheduler.Label}: at login and every {Job.TickMinutes} minutes.\nRuns from: {target.Rig}\nNo resident coordinator or Windows desktop guard.", cancellationToken).ConfigureAwait(false);
                return 0;
            }
            catch (Exception error) when (error is ProcException or InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                await output.WriteLineAsync(error.Message, cancellationToken).ConfigureAwait(false);
                return 1;
            }
        }

        if (host.Tasks is not { } tasks)
        {
            await output.WriteLineAsync("jobs install requires Windows or macOS.", cancellationToken).ConfigureAwait(false);
            return 1;
        }

        if (host.MissingRig() is { } missingRig)
        {
            await output.WriteLineAsync(MissingRigMessage(missingRig, refuse: true), cancellationToken).ConfigureAwait(false);
            return 1;
        }

        try
        {
            var target = await host.InstallTarget(launcher, pythonw, cancellationToken).ConfigureAwait(false);
            var user = host.UserId();
            var now = host.Clock.GetLocalNow();
            foreach (var name in CoordinatorTasks.All)
            {
                await tasks.RegisterAsync(name, JobsTaskXml.Render(name, target, user, now), cancellationToken).ConfigureAwait(false);
                await output.WriteLineAsync($"Registered {name}", cancellationToken).ConfigureAwait(false);
            }

            var retired = new List<string>();
            foreach (var name in CoordinatorTasks.Retired)
            {
                if (await tasks.DeleteAsync(name, cancellationToken).ConfigureAwait(false))
                {
                    retired.Add(name);
                }
            }

            if (start)
            {
                await tasks.StartAsync(CoordinatorTasks.Guard, cancellationToken).ConfigureAwait(false);
            }

            foreach (var name in retired)
            {
                await output.WriteLineAsync($"Retired {name}", cancellationToken).ConfigureAwait(false);
            }

            await output.WriteLineAsync($"Runs from: {target.Rig}\nLauncher: {target.Pythonw ?? "direct"}\nPlan: {target.Plan}", cancellationToken).ConfigureAwait(false);
            return 0;
        }
        catch (Exception error) when (error is ProcException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            await output.WriteLineAsync(error.Message, cancellationToken).ConfigureAwait(false);
            return 1;
        }
    }

    // The published rig the tasks would run is not there; `runtime install` puts it there.
    private static string MissingRigMessage(string path, bool refuse) =>
        (refuse ? "jobs install refuses: " : "Finding: ") + $"the published rig is missing: {path}. The tasks would point at it. Run `rig runtime install`."
        + (refuse ? " No tasks were changed." : string.Empty);

    private async Task<int> Uninstall(CancellationToken cancellationToken)
    {
        if (host.MacScheduler is { } mac)
        {
            var removed = await mac.UninstallAsync(cancellationToken).ConfigureAwait(false);
            await output.WriteLineAsync($"{(removed ? "Removed" : "Not registered")} {MacJobsScheduler.Label}", cancellationToken).ConfigureAwait(false);
            return 0;
        }

        if (host.Tasks is not { } tasks)
        {
            await output.WriteLineAsync("jobs uninstall requires Windows or macOS.", cancellationToken).ConfigureAwait(false);
            return 1;
        }

        foreach (var name in CoordinatorTasks.All)
        {
            var removed = await tasks.DeleteAsync(name, cancellationToken).ConfigureAwait(false);
            await output.WriteLineAsync($"{(removed ? "Removed" : "Not registered")} {name}", cancellationToken).ConfigureAwait(false);
        }

        return 0;
    }

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
                text.Append("Not registered: ").AppendJoin(", ", missing).Append(". Run `rig jobs install`.\n");
            }
        }

        if (host.MacScheduler is { } mac)
        {
            var registered = await mac.RegisteredAsync(cancellationToken).ConfigureAwait(false);
            text.Append("Mac schedule: ").Append(registered ? "registered" : "not registered")
                .Append(" (").Append(MacJobsScheduler.Label).Append(").")
                .Append(registered ? " Idle between ticks.\n" : " Run `rig jobs install`.\n");
        }

        if (host.MissingRig() is { } missingRig)
        {
            text.Append(MissingRigMessage(missingRig, refuse: false)).Append('\n');
        }

        text.Append("Logs: ").Append(LogsDir).Append('\n');
        await output.WriteAsync(text.ToString(), cancellationToken).ConfigureAwait(false);
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

    private int Run(JobTrigger trigger, string[] only, bool force, bool dryRun, bool scheduled)
    {
        var jobs = host.Jobs();
        var unknown = only.Except(jobs.Select(job => job.Name), StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        if (unknown.Count > 0)
        {
            output.Write($"Unknown job: {string.Join(", ", unknown)}\n");
            return UnknownExit;
        }

        var named = new HashSet<string>(only, StringComparer.Ordinal);
        if (dryRun)
        {
            var now = host.Clock.GetLocalNow();
            var state = JobState.ReadState(Path.Join(host.JobsDir, JobState.StateName));
            foreach (var item in JobPlan.Plan(jobs, trigger, state, now, host.Booted(now), host.Flags(), only: named, force: force))
            {
                output.Write($"{(item.Run ? "run " : "skip")} {item.Job.Name}: {item.Reason}\n");
            }

            return 0;
        }

        using var tracing = host.Trace();
        int RunJobs(string? runId, Action<string> report) =>
            new JobRunner(new JobRunnerSeams(host.Executor, host.IdleSeconds, host.Clock, report, host.Hold))
                .Run(jobs, trigger, host.JobsDir, host.Booted(host.Clock.GetLocalNow()), host.Flags(), named, force, runId);

        return scheduled
            ? ScheduledRun.Run(trigger, host.JobsDir, new ScheduledSeams(host.Clock, host.WaitForShell, host.TracesDir) { PruneOnTick = host.MacScheduler is not null }, RunJobs)
            : RunJobs(null, line => output.Write(line + "\n"));
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

    // The last <lines> lines: 0 keeps every line, and a negative count drops that many from the start.
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
