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

    [Theory]
    [InlineData(new[] { "jobs", "status" }, false)]
    [InlineData(new[] { "jobs", "last", "--trigger", "tick" }, false)]
    [InlineData(new[] { "jobs", "log", "align" }, false)]
    [InlineData(new[] { "jobs", "run", "--trigger", "tick" }, true)]
    [InlineData(new[] { "jobs", "install" }, true)]
    [InlineData(new[] { "jobs", "uninstall" }, true)]
    [InlineData(new[] { "jobs" }, true)]
    public void Run_and_the_installers_still_go_to_python(string[] args, bool python)
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
        () => new Dictionary<string, bool>(),
        new FixedTime(Now),
        now => now - TimeSpan.FromHours(1),
        tasks);

    private Task<int> Run(IScheduledTasks? tasks, params string[] args) =>
        new JobsCommand(Host(tasks), _out).Build().Parse(args)
            .InvokeAsync(new InvocationConfiguration { Output = _out, Error = _out }, TestContext.Current.CancellationToken);

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FakeTasks(params string[] missing) : IScheduledTasks
    {
        public Task<bool> ExistsAsync(string name, CancellationToken cancellationToken = default) => Task.FromResult(!missing.Contains(name));
    }
}
