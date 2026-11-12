using System.Text;
using Rig.Core.Jobs;

namespace Rig.Core.Tests.Jobs;

/// <summary>The runner's pieces the oracle run does not reach: the real executor, the lock, rotation and the history's limits.</summary>
public sealed class JobRunnerTests : IDisposable
{
    private readonly TempDir _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void The_executor_appends_the_output_under_a_header_and_an_exit_marker_with_the_run_id_in_the_environment()
    {
        var logs = Path.Join(_temp.Path, "logs");
        var job = Git("echo", "-c", "alias.runid=!echo \"id=$SHARED_UTILITIES_JOB_RUN enc=$PYTHONIOENCODING trace=$SHARED_UTILITIES_TRACE_HOME\"", "runid");

        var code = new ProcessJobExecutor(new FixedClock(), new Dictionary<string, string> { [JobsSite.TraceHomeVariable] = "traces", ["PATH"] = "kept" })
            .Execute(job, logs, "tick-20261112-090000");

        Assert.Equal(0, code);
        var log = File.ReadAllText(JobState.LogPath(logs, "echo"));
        Assert.StartsWith("--- 2026-11-12 09:00:00 [tick-20261112-090000] git -c alias.runid=", log, StringComparison.Ordinal);
        Assert.Contains("id=tick-20261112-090000 enc=utf-8 trace=traces", log, StringComparison.Ordinal);
        Assert.EndsWith($"--- exit 0{Environment.NewLine}", log, StringComparison.Ordinal);
        Assert.Equal(["id=tick-20261112-090000 enc=utf-8 trace=traces"], JobLog.RunOutput(JobState.LogPath(logs, "echo"), "tick-20261112-090000"));
    }

    [Fact]
    public void The_executor_keeps_stderr_and_the_exit_code_of_a_failing_job()
    {
        var logs = Path.Join(_temp.Path, "logs");

        var code = new ProcessJobExecutor(new FixedClock()).Execute(Git("fails", "no-such-command-here"), logs, "tick-20261112-090000");

        Assert.NotEqual(0, code);
        var log = File.ReadAllText(JobState.LogPath(logs, "fails"));
        Assert.Contains("no-such-command-here", log, StringComparison.Ordinal);
        Assert.EndsWith($"--- exit {code}{Environment.NewLine}", log, StringComparison.Ordinal);
    }

    [Fact]
    public void The_executor_stops_a_job_that_runs_out_of_time()
    {
        var logs = Path.Join(_temp.Path, "logs");

        var code = new ProcessJobExecutor(new FixedClock(), timeout: TimeSpan.Zero).Execute(Git("slow", "--version"), logs, "tick-20261112-090000");

        Assert.Equal(JobRunner.TimedOut, code);
        Assert.EndsWith($"--- timed out after 5 min{Environment.NewLine}", File.ReadAllText(JobState.LogPath(logs, "slow")), StringComparison.Ordinal);
    }

    [Fact]
    public void The_lock_file_is_made_with_one_byte_and_on_windows_a_second_run_is_kept_out_until_the_first_lets_go()
    {
        var path = Path.Join(_temp.Path, "jobs", JobState.LockName);
        var slept = TimeSpan.Zero;

        using (var first = RunLock.Hold(path, TimeSpan.Zero, _ => { }))
        {
            Assert.NotNull(first);
            using var second = RunLock.Hold(path, TimeSpan.FromMilliseconds(100), step => slept += step);
            Assert.Equal(OperatingSystem.IsWindows(), second is null);
            Assert.Equal(OperatingSystem.IsWindows() ? TimeSpan.FromMilliseconds(100) : TimeSpan.Zero, slept);
        }

        using (var again = RunLock.Hold(path, TimeSpan.Zero, _ => { }))
        {
            Assert.NotNull(again);
        }

        // Read once no run holds it: Windows refuses a read of a locked byte.
        Assert.Equal("0"u8.ToArray(), ReadShared(path));
    }

    [Fact]
    public void A_job_waiting_for_idle_is_skipped_while_someone_is_at_the_keyboard()
    {
        var report = new List<string>();
        var executor = new ScriptExecutor();
        var job = new Job("idle", string.Empty, ["x"], new HashSet<JobTrigger> { JobTrigger.Tick }, JobCadence.EveryRun, 5) { IdleMinutes = 5 };

        var exit = Runner(executor, report, idle: 299.5).Run([job], JobTrigger.Tick, _temp.Path, Booted, new Dictionary<string, bool>());

        Assert.Equal(0, exit);
        Assert.Equal(["skip idle: in use (idle 300s, needs 5 min)"], report);
        Assert.Empty(executor.Ran);
        Assert.Empty(JobState.ReadState(Path.Join(_temp.Path, JobState.StateName)));
    }

    [Fact]
    public void A_big_log_is_rotated_before_the_run_and_a_rewritten_own_log_is_saved_whole()
    {
        var logs = JobState.LogsDir(_temp.Path);
        Directory.CreateDirectory(logs);
        var log = JobState.LogPath(logs, "own");
        File.WriteAllBytes(log, new byte[JobLog.LimitBytes + 1]);
        var own = Path.Join(_temp.Path, "own-detail.log");
        File.WriteAllText(own, new string('o', 100));
        var executor = new ScriptExecutor { Own = "--- 2026-11-12 09:00:00 own\nrewritten\n" };
        var job = new Job("own", string.Empty, ["x"], new HashSet<JobTrigger> { JobTrigger.Tick }, JobCadence.EveryRun, 5) { DetailLog = own };

        var report = new List<string>();
        var exit = Runner(executor, report).Run([job], JobTrigger.Tick, _temp.Path, Booted, new Dictionary<string, bool>());

        Assert.Equal(0, exit);
        Assert.Equal(JobLog.LimitBytes + 1, new FileInfo(log + ".1").Length);
        Assert.Equal("     own: exit 0 after 0s: rewritten", report[^1]);
        var saved = File.ReadAllText(Path.Join(_temp.Path, "runs", "own", "tick-20261112-090000.log"));
        Assert.Equal("--- 2026-11-12 09:00:00 [tick-20261112-090000] x\n--- exit 0\n--- 2026-11-12 09:00:00 own\nrewritten\n", saved);
    }

    [Fact]
    public void A_summary_keeps_two_hundred_characters_without_splitting_a_surrogate_pair()
    {
        var executor = new ScriptExecutor { Line = new string('a', 199) + "😀tail" };
        var job = new Job("emoji", string.Empty, ["x"], new HashSet<JobTrigger> { JobTrigger.Tick }, JobCadence.EveryRun, 5);

        _ = Runner(executor, []).Run([job], JobTrigger.Tick, _temp.Path, Booted, new Dictionary<string, bool>());

        Assert.Equal(new string('a', 199) + "😀", JobState.ReadState(Path.Join(_temp.Path, JobState.StateName))["emoji"].Summary);
    }

    [Fact]
    public void The_history_trims_to_its_newest_runs_once_past_the_slack()
    {
        var path = Path.Join(_temp.Path, RunHistory.HistoryName);
        File.WriteAllLines(path, Enumerable.Range(0, RunHistory.KeepRuns + RunHistory.TrimSlack).Select(index => $"{{\"n\":{index}}}"));

        RunHistory.Append(_temp.Path, new JobRun("a", "tick-20261112-090000", "2026-11-12T09:00:00+00:00", 1.5, null, "tick", "IOException: \"x\"\t\u0001", string.Empty));

        var lines = File.ReadAllLines(path);
        Assert.Equal(RunHistory.KeepRuns, lines.Length);
        Assert.Equal("{\"n\":501}", lines[0]);
        Assert.Equal(
            "{\"job\":\"a\",\"run_id\":\"tick-20261112-090000\",\"started\":\"2026-11-12T09:00:00+00:00\",\"seconds\":1.5,\"returncode\":null,"
            + "\"trigger\":\"tick\",\"error\":\"IOException: \\\"x\\\"\\t\\u0001\",\"output\":\"\",\"trace_id\":\"\",\"span_id\":\"\"}",
            lines[^1]);
        Assert.False(File.Exists(Path.ChangeExtension(path, ".tmp")));
    }

    [Fact]
    public void Saved_outputs_keep_their_end_and_the_newest_fifty_per_job_and_skip_odd_run_ids()
    {
        var folder = Path.Join(_temp.Path, "runs", "a");
        Directory.CreateDirectory(folder);
        for (var day = 1; day <= RunHistory.KeepOutputsPerJob; day++)
        {
            File.WriteAllText(Path.Join(folder, $"logon-202610{day:00}-080000.log"), "old");
        }

        var data = Encoding.UTF8.GetBytes(new string('x', RunHistory.OutputLimitBytes) + "end");

        Assert.Equal("runs/a/tick-20261112-090000.log", RunHistory.SaveOutput(_temp.Path, "a", "tick-20261112-090000", data));
        Assert.Equal(string.Empty, RunHistory.SaveOutput(_temp.Path, "a", "manual", data));
        Assert.Equal(string.Empty, RunHistory.SaveOutput(_temp.Path, "a", "tick-20261112-090001", []));

        var outputs = Directory.GetFiles(folder).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(RunHistory.KeepOutputsPerJob, outputs.Count);
        Assert.DoesNotContain("logon-20261001-080000.log", outputs);
        var saved = File.ReadAllText(Path.Join(folder, "tick-20261112-090000.log"));
        Assert.Equal(RunHistory.OutputLimitBytes, saved.Length);
        Assert.EndsWith("xend", saved, StringComparison.Ordinal);
    }

    [Fact]
    public void An_outcome_describes_itself_as_the_run_reports_it()
    {
        Assert.Equal("exit 3 after 2s", JobRunner.Describe(new Outcome("2026-11-12T09:00:00+00:00", 2.5, 3, "tick")));
        Assert.Equal("error after 4s: IOException: no", JobRunner.Describe(new Outcome("2026-11-12T09:00:00+00:00", 3.5, null, "tick") { Error = "IOException: no" }));
        Assert.Equal("2026-11-12T09:00:00-05:00", JobRunner.Iso(new DateTimeOffset(2026, 11, 12, 9, 0, 0, TimeSpan.FromHours(-5))));
    }

    private static readonly DateTimeOffset Booted = new(2026, 11, 12, 8, 0, 0, TimeSpan.Zero);

    private static Job Git(string name, params string[] args) =>
        new(name, string.Empty, ["git", .. args], new HashSet<JobTrigger> { JobTrigger.Tick }, JobCadence.EveryRun, 5);

    private static JobRunner Runner(IJobExecutor executor, List<string> report, double idle = double.PositiveInfinity) =>
        new(new JobRunnerSeams(executor, () => idle, new FixedClock(), report.Add, (_, _) => new MemoryStream()));

    private static byte[] ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    // Writes one section to the job's log, and replaces its own log when told to.
    private sealed class ScriptExecutor : IJobExecutor
    {
        public List<string> Ran { get; } = [];

        public string? Own { get; init; }

        public string? Line { get; init; }

        public long Execute(Job job, string logsDir, string runId)
        {
            Ran.Add(job.Name);
            Directory.CreateDirectory(logsDir);
            File.AppendAllText(JobState.LogPath(logsDir, job.Name), $"--- 2026-11-12 09:00:00 [{runId}] x\n{(Line is null ? string.Empty : Line + "\n")}--- exit 0\n");
            if (Own is not null && job.DetailLog is { } own)
            {
                File.WriteAllText(own, Own);
            }

            return 0;
        }
    }

    private sealed class FixedClock : TimeProvider
    {
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

        public override DateTimeOffset GetUtcNow() => new(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);

        public override long GetTimestamp() => 0;
    }
}
