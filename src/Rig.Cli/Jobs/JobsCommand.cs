using System.CommandLine;
using System.Text;
using DotMake.CommandLine;
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
/// <param name="InstallTarget">Resolves and validates the stable task targets before registration.</param>
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
    Func<CancellationToken, Task<JobsInstallTarget>> InstallTarget,
    Func<string> UserId,
    Func<string?> MissingRig);

/// <summary>
/// <c>rig jobs</c>: the coordinator's runs and task installation. The table is padded to its widest cell rather than wrapped at 80 columns.
/// </summary>
[CliCommand(Name = "jobs", Description = "Logon, every-15-minute and daily jobs that keep this machine aligned, run from one registry.")]
internal sealed class JobsCommand(JobsHost host, TextWriter output)
{
    /// <summary>The instance DotMake binds onto; rig always builds the wired one above instead.</summary>
    internal JobsCommand()
        : this(null!, TextWriter.Null)
    {
    }

    /// <summary>A job name that is not in the registry exits with this.</summary>
    public const int UnknownExit = 2;

    /// <summary>The <c>jobs</c> command, with its subcommands.</summary>
    public Command Build()
    {
        var jobs = DeclarativeCommand.BuildGroup(this);
        jobs.Add(DeclarativeCommand.Build(new StatusCommand(this)).AcceptOnlyFromAmong("--trigger", JobWords.Triggers));
        jobs.Add(DeclarativeCommand.Build(new LastCommand(this)).AcceptOnlyFromAmong("--trigger", JobWords.Triggers));
        jobs.Add(DeclarativeCommand.Build(new LogCommand(this)));
        jobs.Add(DeclarativeCommand.Build(new RunCommand(this)).AcceptOnlyFromAmong("--trigger", JobWords.Triggers));
        jobs.Add(DeclarativeCommand.Build(new UninstallCommand(this)));
        jobs.Add(DeclarativeCommand.Build(new InstallCommand(this)));
        return jobs;
    }

    private string LogsDir => JobState.LogsDir(host.JobsDir);

    internal async Task<int> Install(bool start, CancellationToken cancellationToken)
    {
        if (host.Tasks is not { } tasks)
        {
            await output.WriteLineAsync("jobs install requires Windows.").ConfigureAwait(false);
            return 1;
        }

        if (host.MissingRig() is { } missingRig)
        {
            await output.WriteLineAsync(MissingRigMessage(missingRig, refuse: true)).ConfigureAwait(false);
            return 1;
        }

        try
        {
            var target = await host.InstallTarget(cancellationToken).ConfigureAwait(false);
            var user = host.UserId();
            var now = host.Clock.GetLocalNow();
            foreach (var name in CoordinatorTasks.All)
            {
                await tasks.RegisterAsync(name, JobsTaskXml.Render(name, target, user, now), cancellationToken).ConfigureAwait(false);
                await output.WriteLineAsync($"Registered {name}").ConfigureAwait(false);
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
                await output.WriteLineAsync($"Retired {name}").ConfigureAwait(false);
            }

            await output.WriteLineAsync($"Runs from: {target.Rigw}\nPlan: {target.Plan}").ConfigureAwait(false);
            return 0;
        }
        catch (Exception error) when (error is ProcException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            await output.WriteLineAsync(error.Message).ConfigureAwait(false);
            return 1;
        }
    }

    // The published rig the tasks would run is not there; `runtime install` puts it there.
    private static string MissingRigMessage(string path, bool refuse) =>
        (refuse ? "jobs install refuses: " : "Finding: ") + $"the published rig is missing: {path}. The tasks would point at it. Run `rig runtime install`."
        + (refuse ? " No tasks were changed." : string.Empty);

    private async Task<int> Uninstall(CancellationToken cancellationToken)
    {
        if (host.Tasks is not { } tasks)
        {
            await output.WriteLineAsync("jobs uninstall requires Windows.").ConfigureAwait(false);
            return 1;
        }

        foreach (var name in CoordinatorTasks.All)
        {
            var removed = await tasks.DeleteAsync(name, cancellationToken).ConfigureAwait(false);
            await output.WriteLineAsync($"{(removed ? "Removed" : "Not registered")} {name}").ConfigureAwait(false);
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

        if (host.MissingRig() is { } missingRig)
        {
            text.Append(MissingRigMessage(missingRig, refuse: false)).Append('\n');
        }

        text.Append("Logs: ").Append(LogsDir).Append('\n');
        await output.WriteAsync(text.ToString()).ConfigureAwait(false);
        return 0;
    }

    internal int Last(string trigger)
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
            ? ScheduledRun.Run(trigger, host.JobsDir, new ScheduledSeams(host.Clock, host.WaitForShell, host.TracesDir), RunJobs)
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

    /// <summary><c>rig jobs status</c>.</summary>
    [CliCommand(Name = "status", Description = "Each job: when it runs, its last outcome, and whether the next run takes it.")]
    internal sealed class StatusCommand(JobsCommand jobs)
    {
        /// <summary>The instance DotMake binds onto; rig always builds the wired one above instead.</summary>
        internal StatusCommand()
            : this(null!)
        {
        }

        /// <summary>The trigger to plan for.</summary>
        [CliOption(Name = "--trigger", Description = "Show what a run on this trigger would do.")]
        public string Trigger { get; set; } = "tick";

        /// <summary>Shows the plan.</summary>
        public Task<int> RunAsync(CliContext context) => jobs.Status(JobWords.Trigger(Trigger), context.CancellationToken);
    }

    /// <summary><c>rig jobs last</c>.</summary>
    [CliCommand(Name = "last", Description = "The latest run on a trigger: every job it ran or skipped, why, and how each went.")]
    internal sealed class LastCommand(JobsCommand jobs)
    {
        /// <summary>The instance DotMake binds onto; rig always builds the wired one above instead.</summary>
        internal LastCommand()
            : this(null!)
        {
        }

        /// <summary>The trigger whose latest run to show.</summary>
        [CliOption(Name = "--trigger", Description = "Show the latest run on this trigger.")]
        public string Trigger { get; set; } = "logon";

        /// <summary>Shows the run.</summary>
        public int Run() => jobs.Last(Trigger);
    }

    /// <summary><c>rig jobs log</c>.</summary>
    [CliCommand(Name = "log", Description = "Show the end of a job's log.")]
    internal sealed class LogCommand(JobsCommand jobs)
    {
        /// <summary>The instance DotMake binds onto; rig always builds the wired one above instead.</summary>
        internal LogCommand()
            : this(null!)
        {
        }

        /// <summary>The job whose log to show.</summary>
        [CliArgument(Name = "job", Description = "Job name.")]
        public string Job { get; set; } = null!;

        /// <summary>How many lines from the end.</summary>
        [CliOption(Name = "--lines", Aliases = ["-n"], Description = "How many lines from the end.")]
        public int Lines { get; set; } = 40;

        /// <summary>Shows the log.</summary>
        public int Run() => jobs.Log(Job, Lines);
    }

    /// <summary><c>rig jobs run</c>.</summary>
    [CliCommand(Name = "run", Description = "Run the due jobs now, in the foreground, as the scheduled tasks do.")]
    internal sealed class RunCommand(JobsCommand jobs)
    {
        /// <summary>The instance DotMake binds onto; rig always builds the wired one above instead.</summary>
        internal RunCommand()
            : this(null!)
        {
        }

        /// <summary>The trigger to run.</summary>
        [CliOption(Name = "--trigger", Description = "Run the jobs due on this trigger.")]
        public string Trigger { get; set; } = "tick";

        /// <summary>Jobs to run whether due or not.</summary>
        [CliOption(Name = "--only", Required = false, Description = "Run just this job, due or not. Repeatable.")]
        public string[] Only { get; set; } = [];

        /// <summary>Runs every job on the trigger.</summary>
        [CliOption(Name = "--force", Description = "Run every job on the trigger, due or not.")]
        public bool Force { get; set; }

        /// <summary>Plans without running.</summary>
        [CliOption(Name = "--dry-run", Description = "Say what would run, and run nothing.")]
        public bool DryRun { get; set; }

        /// <summary>Set by the scheduled tasks.</summary>
        [CliOption(Name = "--scheduled", Hidden = true, Description = "For the scheduled tasks: narrate to coordinator.log, and on logon wait for the shell and prune old traces.")]
        public bool Scheduled { get; set; }

        /// <summary>Runs the jobs.</summary>
        public int Run() => jobs.Run(JobWords.Trigger(Trigger), Only, Force, DryRun, Scheduled);
    }

    /// <summary><c>rig jobs uninstall</c>.</summary>
    [CliCommand(Name = "uninstall", Description = "Remove the coordinator's scheduled tasks.")]
    internal sealed class UninstallCommand(JobsCommand jobs)
    {
        /// <summary>The instance DotMake binds onto; rig always builds the wired one above instead.</summary>
        internal UninstallCommand()
            : this(null!)
        {
        }

        /// <summary>Removes the tasks.</summary>
        public Task<int> RunAsync(CliContext context) => jobs.Uninstall(context.CancellationToken);
    }

    /// <summary><c>rig jobs install</c>.</summary>
    [CliCommand(Name = "install", Description = "Register the Logon, Tick and Display Guard tasks, and retire the separate tasks they replace.")]
    internal sealed class InstallCommand(JobsCommand jobs)
    {
        /// <summary>The instance DotMake binds onto; rig always builds the wired one above instead.</summary>
        internal InstallCommand()
            : this(null!)
        {
        }

        /// <summary>Leaves the display guard stopped.</summary>
        [CliOption(Name = "--no-start", Description = "Do not start the display guard now.")]
        public bool NoStart { get; set; }

        /// <summary>The default; kept so the command line stays accepted.</summary>
        [CliOption(Name = "--start", Description = "Start the display guard now as well as at logon (the default).")]
        public bool Start { get; set; }

        /// <summary>Registers the tasks.</summary>
        public Task<int> RunAsync(CliContext context) => jobs.Install(!NoStart, context.CancellationToken);
    }
}
