using System.CommandLine;
using Rig.Cli.Forwarding;
using Rig.Cli.Jobs;
using Rig.Core.Jobs;
using Rig.Core.Tests;

namespace Rig.Cli.Tests.Jobs;

/// <summary><c>rig jobs status|last|log</c> over a scratch coordinator folder, a fixed clock and a fake Task Scheduler.</summary>
public sealed class JobsCommandTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);

    private readonly TempDir _temp = new();
    private readonly StringWriter _out = new();
    private readonly FakeExecutor _executor = new();
    private double _idle = double.PositiveInfinity;
    private bool _held;
    private bool _shellReady = true;
    private Func<IReadOnlyDictionary<string, bool>> _flags = () => new Dictionary<string, bool>();
    private readonly List<string> _tracing = [];
    private readonly List<TimeSpan> _shellWaits = [];

    private string TracesDir => Path.Join(_temp.Path, "traces");

    private string CoordinatorLog => Path.Join(_temp.Path, ScheduledRun.LogName);

    public void Dispose()
    {
        _temp.Dispose();
        _out.Dispose();
    }

    [Fact]
    public async Task Status_shows_each_job_the_failures_output_and_the_missing_tasks()
    {
        JobState.WriteState(Path.Join(_temp.Path, JobState.StateName), new Dictionary<string, Outcome>
        {
            ["every"] = new("2026-11-12T08:45:00+00:00", 2.4, 1, "tick") { Detail = ["boom", "bang"] },
            ["daily"] = new("2026-11-12T08:00:00+00:00", 12.5, 0, "logon") { Summary = "3 moved" },
        });

        var exit = await Run(new FakeTasks(CoordinatorTasks.Guard), "status");

        Assert.Equal(0, exit);
        Assert.Equal(
            "job    runs on              last run         result       next tick\n"
            + "every  tick (every-run)     Thu 08:45 (2s)   exit 1       run\n"
            + "daily  logon, tick (daily)  Thu 08:00 (12s)  ok: 3 moved  not due (daily)\n"
            + "never  logon (every-run)    -                -            not on tick\n"
            + "\nevery failed; the end of its output:\n  | boom\n  | bang\n"
            + $"Not registered: {CoordinatorTasks.Guard}. Run `desktop-manager jobs install`.\n"
            + $"Logs: {Path.Join(_temp.Path, "logs")}\n",
            _out.ToString());
    }

    [Fact]
    public async Task Status_without_a_scheduler_or_a_state_plans_every_job_from_scratch()
    {
        var exit = await Run(null, "status", "--trigger", "logon");

        Assert.Equal(0, exit);
        Assert.StartsWith("job    runs on              last run  result  next logon\nevery  tick (every-run)     -         -       not on logon\n", _out.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Not registered", _out.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Status_rejects_a_trigger_that_is_not_one()
    {
        Assert.NotEqual(0, await Run(null, "status", "--trigger", "hourly"));
    }

    [Fact]
    public async Task Last_says_so_when_no_run_is_recorded()
    {
        Assert.Equal(0, await Run(null, "last", "--trigger", "tick"));
        Assert.Equal("No tick run recorded yet.\n", _out.ToString());
    }

    [Fact]
    public async Task Last_shows_every_decision_and_the_failures_output()
    {
        JobState.WriteRun(
            JobState.LastRunPath(_temp.Path, "logon"),
            new RunRecord(
                "20261112-090000",
                "logon",
                "2026-11-12T09:00:00+00:00",
                null,
                [
                    new Decision("every", true, "every-run", new Outcome("2026-11-12T09:00:00+00:00", 1.5, 0, "logon")),
                    new Decision("daily", false, "not due (daily)"),
                    new Decision("never", true, "every-run", new Outcome("2026-11-12T09:00:02+00:00", 30, null, "logon") { Error = "timed out", Detail = ["stuck"] }),
                ]));

        Assert.Equal(0, await Run(null, "last"));
        Assert.Equal(
            "20261112-090000  2026-11-12T09:00:00+00:00 -> did not finish\n"
            + "job          took  result\n"
            + "every  ran   2s    ok\n"
            + "daily  skip        not due (daily)\n"
            + "never  ran   30s   timed out\n"
            + "\nnever failed; the end of its output:\n  | stuck\n"
            + $"Job logs: {Path.Join(_temp.Path, "logs")} (sections headed [20261112-090000])\n",
            _out.ToString());
    }

    [Fact]
    public async Task Log_prints_the_end_of_a_job_s_log()
    {
        Directory.CreateDirectory(Path.Join(_temp.Path, "logs"));
        await File.WriteAllTextAsync(Path.Join(_temp.Path, "logs", "every.log"), "one\r\ntwo\nthree\n", TestContext.Current.CancellationToken);

        Assert.Equal(0, await Run(null, "log", "every", "-n", "2"));
        Assert.Equal("two\nthree\n", _out.ToString());
    }

    [Fact]
    public async Task Log_reads_a_log_the_running_job_still_holds_open()
    {
        Directory.CreateDirectory(Path.Join(_temp.Path, "logs"));
        await using (var writer = new FileStream(Path.Join(_temp.Path, "logs", "every.log"), FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
        {
            await writer.WriteAsync("still going\n"u8.ToArray(), TestContext.Current.CancellationToken);
            await writer.FlushAsync(TestContext.Current.CancellationToken);

            Assert.Equal(0, await Run(null, "log", "every"));
        }

        Assert.Equal("still going\n", _out.ToString());
    }

    [Theory]
    [InlineData("0", "one\ntwo\nthree\n")]
    [InlineData("-1", "two\nthree\n")]
    [InlineData("9", "one\ntwo\nthree\n")]
    public async Task Log_counts_lines_as_a_python_slice_does(string lines, string expected)
    {
        Directory.CreateDirectory(Path.Join(_temp.Path, "logs"));
        await File.WriteAllTextAsync(Path.Join(_temp.Path, "logs", "every.log"), "one\ntwo\nthree", TestContext.Current.CancellationToken);

        Assert.Equal(0, await Run(null, "log", "every", "--lines", lines));
        Assert.Equal(expected, _out.ToString());
    }

    [Fact]
    public async Task Log_says_so_when_the_job_has_no_log_yet()
    {
        Assert.Equal(0, await Run(null, "log", "daily"));
        Assert.Equal($"No log yet: {Path.Join(_temp.Path, "logs", "daily.log")}\n", _out.ToString());
    }

    [Fact]
    public async Task Log_refuses_a_job_the_registry_does_not_have()
    {
        Assert.Equal(JobsCommand.UnknownExit, await Run(null, "log", "nope"));
        Assert.Equal("Unknown job: nope\n", _out.ToString());
    }

    [Fact]
    public async Task Run_runs_the_due_jobs_reports_each_and_exits_one_when_one_fails()
    {
        _executor.Codes["every"] = 3;

        var exit = await Run(null, "run");

        Assert.Equal(1, exit);
        Assert.Equal(["every", "daily"], _executor.Ran);
        Assert.Equal(
            "run  every (every-run)\n     every: exit 3 after 0s: every done\n       | every done\n"
            + "run  daily (daily)\n     daily: exit 0 after 0s: daily done\n"
            + "skip never: not on tick\n",
            _out.ToString());
        Assert.Equal(3, JobState.ReadState(Path.Join(_temp.Path, JobState.StateName))["every"].ReturnCode);
        Assert.Equal("tick-20261112-090000", JobState.ReadRun(JobState.LastRunPath(_temp.Path, "tick"))!.RunId);
    }

    [Fact]
    public async Task Run_with_only_runs_the_named_job_on_any_trigger()
    {
        var exit = await Run(null, "run", "--trigger", "logon", "--only", "every");

        Assert.Equal(0, exit);
        Assert.Equal(["every"], _executor.Ran);
        Assert.Equal("run  every (requested)\n     every: exit 0 after 0s: every done\n", _out.ToString());
    }

    [Fact]
    public async Task Run_skips_when_another_run_holds_the_lock()
    {
        _held = true;

        Assert.Equal(0, await Run(null, "run", "--force"));
        Assert.Empty(_executor.Ran);
        Assert.Equal($"another jobs run holds {Path.Join(_temp.Path, JobState.LockName)}; skipping this tick run\n", _out.ToString());
    }

    [Fact]
    public async Task Run_dry_run_says_what_would_run_and_runs_nothing()
    {
        _idle = 0;

        Assert.Equal(0, await Run(null, "run", "--dry-run", "--force"));
        Assert.Empty(_executor.Ran);
        Assert.Equal("run  every: requested\nrun  daily: requested\nrun  never: requested\n", _out.ToString());
        Assert.False(File.Exists(Path.Join(_temp.Path, JobState.StateName)));
    }

    [Fact]
    public async Task A_plain_run_traces_its_jobs_and_never_rotates_the_coordinator_log_or_prunes_traces()
    {
        BigCoordinatorLog();
        OldAndNewTraces();

        var exit = await Run(null, "run", "--trigger", "logon");

        Assert.Equal(0, exit);
        Assert.Equal(["daily", "never"], _executor.Ran);
        Assert.Equal(["start", "written"], _tracing);
        Assert.Contains("run  daily (", _out.ToString(), StringComparison.Ordinal);
        Assert.Equal(ScheduledRun.LogLimitBytes + 1, new FileInfo(CoordinatorLog).Length);
        Assert.False(File.Exists(CoordinatorLog + ".1"));
        Assert.True(File.Exists(Path.Join(TracesDir, "tool", "old.ndjson")));
        Assert.Empty(_shellWaits);
    }

    [Fact]
    public async Task A_dry_run_starts_no_tracing()
    {
        Assert.Equal(0, await Run(null, "run", "--dry-run", "--scheduled"));

        Assert.Empty(_tracing);
        Assert.False(File.Exists(CoordinatorLog));
    }

    [Fact]
    public async Task A_scheduled_logon_run_waits_for_the_shell_prunes_old_traces_and_narrates_to_a_rotated_coordinator_log()
    {
        BigCoordinatorLog();
        OldAndNewTraces();
        _shellReady = false;

        var exit = await Run(null, "run", "--trigger", "logon", "--scheduled");

        Assert.Equal(0, exit);
        Assert.Empty(_out.ToString());
        Assert.Equal([ScheduledRun.ShellTimeout], _shellWaits);
        Assert.Equal(["start", "written"], _tracing);
        Assert.Equal(ScheduledRun.LogLimitBytes + 1, new FileInfo(CoordinatorLog + ".1").Length);
        var lines = Lines(CoordinatorLog);
        Assert.Equal("2026-11-12 09:00:00 [logon-20261112-090000] shell probe timed out; continuing", lines[0]);
        Assert.Equal("2026-11-12 09:00:00 [logon-20261112-090000] pruned 1 trace file(s) older than 14 days", lines[1]);
        Assert.Contains(lines, line => line.StartsWith("2026-11-12 09:00:00 [logon-20261112-090000] run  daily (", StringComparison.Ordinal));
        Assert.False(File.Exists(Path.Join(TracesDir, "tool", "old.ndjson")));
        Assert.True(File.Exists(Path.Join(TracesDir, "tool", "new.ndjson")));
        Assert.True(File.Exists(Path.Join(TracesDir, "old.txt")));
        Assert.Contains("[logon-20261112-090000]", string.Join("\n", Lines(JobState.LogPath(JobState.LogsDir(_temp.Path), "daily"))), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_scheduled_tick_run_neither_waits_nor_prunes_and_a_crash_is_logged_and_exits_one()
    {
        OldAndNewTraces();
        _flags = () => throw new InvalidOperationException("no flags");

        var exit = await Run(null, "run", "--scheduled");

        Assert.Equal(1, exit);
        Assert.Empty(_shellWaits);
        Assert.True(File.Exists(Path.Join(TracesDir, "tool", "old.ndjson")));
        Assert.StartsWith(
            "2026-11-12 09:00:00 [tick-20261112-090000] Jobs coordinator run crashed (trigger tick): System.InvalidOperationException: no flags",
            Lines(CoordinatorLog)[0],
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_scheduled_logon_run_reports_a_ready_shell_and_no_prune_when_there_are_no_traces()
    {
        Assert.Equal(0, await Run(null, "run", "--trigger", "logon", "--scheduled", "--only", "never"));

        var lines = Lines(CoordinatorLog);
        Assert.Equal("2026-11-12 09:00:00 [logon-20261112-090000] Explorer shell ready", lines[0]);
        Assert.Contains("2026-11-12 09:00:00 [logon-20261112-090000] run  never (requested)", lines);
    }

    [Fact]
    public void The_scheduled_flag_is_hidden_from_help()
    {
        var run = new JobsCommand(Host(null), _out).Build().Subcommands.Single(command => command.Name == "run");

        Assert.True(run.Options.Single(option => option.Name == "--scheduled").Hidden);
    }

    [Fact]
    public async Task Run_refuses_jobs_the_registry_does_not_have()
    {
        Assert.Equal(JobsCommand.UnknownExit, await Run(null, "run", "--only", "zed", "--only", "every", "--only", "abc"));
        Assert.Equal("Unknown job: abc, zed\n", _out.ToString());
        Assert.Empty(_executor.Ran);
    }

    [Theory]
    [InlineData(new[] { "jobs", "status" }, false)]
    [InlineData(new[] { "jobs", "last", "--trigger", "tick" }, false)]
    [InlineData(new[] { "jobs", "log", "align" }, false)]
    [InlineData(new[] { "jobs", "run", "--trigger", "tick" }, false)]
    [InlineData(new[] { "jobs", "install" }, true)]
    [InlineData(new[] { "jobs", "uninstall" }, true)]
    [InlineData(new[] { "jobs" }, true)]
    public void Only_the_installers_still_go_to_python(string[] args, bool python)
    {
        var root = new RootCommand { new JobsCommand(Host(null), _out).Build() };

        Assert.Equal(python, Forwarded.Owns(args, root));
    }

    private static IReadOnlyList<Job> Registry() =>
    [
        new("every", string.Empty, ["x"], new HashSet<JobTrigger> { JobTrigger.Tick }, JobCadence.EveryRun, 5),
        new("daily", string.Empty, ["x"], new HashSet<JobTrigger> { JobTrigger.Logon, JobTrigger.Tick }, JobCadence.Daily, 5),
        new("never", string.Empty, ["x"], new HashSet<JobTrigger> { JobTrigger.Logon }, JobCadence.EveryRun, 5),
    ];

    private JobsHost Host(IScheduledTasks? tasks) => new(
        _temp.Path,
        Registry,
        () => _flags(),
        new FixedTime(Now),
        now => now - TimeSpan.FromHours(1),
        tasks,
        _executor,
        () => _idle,
        (_, _) => _held ? null : new MemoryStream(),
        () =>
        {
            _tracing.Add("start");
            return new Disposer(() => _tracing.Add("written"));
        },
        timeout =>
        {
            _shellWaits.Add(timeout);
            return _shellReady;
        },
        TracesDir);

    private Task<int> Run(IScheduledTasks? tasks, params string[] args) =>
        new JobsCommand(Host(tasks), _out).Build().Parse(args)
            .InvokeAsync(new InvocationConfiguration { Output = _out, Error = _out }, TestContext.Current.CancellationToken);

    private void BigCoordinatorLog() => File.WriteAllBytes(CoordinatorLog, new byte[ScheduledRun.LogLimitBytes + 1]);

    private static string[] Lines(string path) => File.ReadAllLines(path);

    private void OldAndNewTraces()
    {
        Directory.CreateDirectory(Path.Join(TracesDir, "tool"));
        File.WriteAllText(Path.Join(TracesDir, "tool", "old.ndjson"), "{}");
        File.SetLastWriteTimeUtc(Path.Join(TracesDir, "tool", "old.ndjson"), Now.UtcDateTime - TimeSpan.FromDays(15));
        File.WriteAllText(Path.Join(TracesDir, "tool", "new.ndjson"), "{}");
        File.SetLastWriteTimeUtc(Path.Join(TracesDir, "tool", "new.ndjson"), Now.UtcDateTime - TimeSpan.FromDays(13));
        File.WriteAllText(Path.Join(TracesDir, "old.txt"), "kept");
        File.SetLastWriteTimeUtc(Path.Join(TracesDir, "old.txt"), Now.UtcDateTime - TimeSpan.FromDays(30));
    }

    private sealed class Disposer(Action disposed) : IDisposable
    {
        public void Dispose() => disposed();
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

        public override DateTimeOffset GetUtcNow() => now;

        public override long GetTimestamp() => 0;
    }

    // Writes each job's run as the real executor would, and exits with the code set for it.
    private sealed class FakeExecutor : IJobExecutor
    {
        public Dictionary<string, long> Codes { get; } = [];

        public List<string> Ran { get; } = [];

        public long Execute(Job job, string logsDir, string runId)
        {
            Ran.Add(job.Name);
            var code = Codes.GetValueOrDefault(job.Name);
            Directory.CreateDirectory(logsDir);
            File.AppendAllText(JobState.LogPath(logsDir, job.Name), $"--- 2026-11-12 09:00:00 [{runId}] x\n{job.Name} done\n--- exit {code}\n");
            return code;
        }
    }

    private sealed class FakeTasks(params string[] missing) : IScheduledTasks
    {
        public Task<bool> ExistsAsync(string name, CancellationToken cancellationToken = default) => Task.FromResult(!missing.Contains(name));
    }
}
