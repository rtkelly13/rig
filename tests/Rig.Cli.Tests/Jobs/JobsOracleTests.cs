using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Rig.Core.Jobs;
using Rig.Core.Tests;

namespace Rig.Cli.Tests.Jobs;

/// <summary>
/// <c>Rig.Core.Jobs</c> replayed against <c>jobs-oracle.json</c>, a frozen recorded baseline whose generator is gone: the registry, the data in <c>state.json</c> and <c>last-&lt;trigger&gt;.json</c>, the reading of damaged files, the plan and the cells.
/// </summary>
public sealed class JobsOracleTests : IDisposable
{
    private const string RootToken = "{root}";

    private static readonly JsonElement Oracle = JsonElement.Parse(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Jobs", "jobs-oracle.json")));

    private readonly TempDir _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void The_registry_is_the_recorded_one_in_order()
    {
        var jobs = JobRegistry.Default(Environment(workspace: true));

        Assert.Equal(
            Oracle.GetProperty("registry").EnumerateArray().Select(job => job.GetRawText()).Select(Canonical),
            jobs.Select(Record).Select(node => Canonical(node.ToJsonString())));
    }

    [Fact]
    public void Without_a_workspace_repo_its_jobs_are_left_out()
    {
        Assert.Equal(
            Oracle.GetProperty("registry_without_workspace").EnumerateArray().Select(name => name.GetString()),
            JobRegistry.Default(Environment(workspace: false)).Select(job => job.Name));
    }

    [Fact]
    public void A_written_state_is_canonically_what_was_recorded()
    {
        foreach (var write in Oracle.GetProperty("state_writes").EnumerateArray())
        {
            var path = Path.Join(_temp.Path, "state", JobState.StateName);
            JobState.WriteState(path, Outcomes(write.GetProperty("state")));

            AssertSameJson(write.GetProperty("text").GetString()!, File.ReadAllText(path));
            Assert.False(File.Exists(Path.ChangeExtension(path, ".tmp")));
        }
    }

    [Fact]
    public void A_damaged_state_reads_as_recorded()
    {
        foreach (var read in Oracle.GetProperty("state_reads").EnumerateArray())
        {
            var state = JobState.ReadState(Written(read.GetProperty("text").GetString()!));

            Assert.True(
                JsonNode.DeepEquals(JsonNode.Parse(read.GetProperty("state").GetRawText()), StateJson(state)),
                $"{read.GetProperty("text").GetString()} read as {StateJson(state).ToJsonString()}");
        }
    }

    [Fact]
    public void A_run_record_writes_back_the_same_data()
    {
        foreach (var write in Oracle.GetProperty("run_writes").EnumerateArray())
        {
            var text = write.GetProperty("text").GetString()!;
            var path = Path.Join(_temp.Path, "last-logon.json");
            JobState.WriteRun(path, JobState.ReadRun(Written(text))!);

            AssertSameJson(text, File.ReadAllText(path));
        }
    }

    [Fact]
    public void A_damaged_run_record_reads_as_recorded()
    {
        foreach (var read in Oracle.GetProperty("run_reads").EnumerateArray())
        {
            var run = JobState.ReadRun(Written(read.GetProperty("text").GetString()!));

            var expected = read.GetProperty("run");
            if (expected.ValueKind == JsonValueKind.Null)
            {
                Assert.Null(run);
                continue;
            }

            var path = Path.Join(_temp.Path, "rewritten.json");
            JobState.WriteRun(path, run!);
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected.GetRawText()), JsonNode.Parse(File.ReadAllText(path))), read.GetProperty("text").GetString());
        }
    }

    [Fact]
    public void Every_recorded_plan_takes_and_skips_as_recorded()
    {
        File.WriteAllText(Path.Join(_temp.Path, "present"), string.Empty);
        var state = new Dictionary<string, Outcome>(StringComparer.Ordinal);
        foreach (var entry in Oracle.GetProperty("plan_state").EnumerateObject())
        {
            state[entry.Name] = new Outcome(
                entry.Value.GetProperty("started").GetString()!,
                3.0,
                entry.Value.GetProperty("returncode").GetInt32(),
                entry.Value.GetProperty("trigger").GetString()!);
        }

        foreach (var plan in Oracle.GetProperty("plans").EnumerateArray())
        {
            var planned = JobPlan.Plan(
                PlanJobs(_temp.Path),
                JobWords.Trigger(plan.GetProperty("trigger").GetString()!),
                plan.TryGetProperty("state", out _) ? new Dictionary<string, Outcome>() : state,
                IsoStamp.Parse(plan.GetProperty("now").GetString()!)!.Value,
                IsoStamp.Parse(plan.GetProperty("booted").GetString()!)!.Value,
                plan.GetProperty("flags").EnumerateObject().ToDictionary(flag => flag.Name, flag => flag.Value.GetBoolean()),
                only: plan.TryGetProperty("only", out var only) ? only.EnumerateArray().Select(name => name.GetString()!).ToHashSet() : null,
                force: plan.TryGetProperty("force", out var force) && force.GetBoolean());

            Assert.Equal(
                plan.GetProperty("planned").EnumerateArray().Select(row => Canonical(row.GetRawText())),
                planned.Select(item => new JsonArray(item.Job.Name, item.Run, Normalise(item.Reason.Replace(_temp.Path, RootToken, StringComparison.Ordinal))).ToJsonString()));
        }
    }

    [Fact]
    public void The_result_and_last_run_cells_are_recorded()
    {
        foreach (var cell in Oracle.GetProperty("cells").EnumerateArray())
        {
            var outcome = Outcomes(JsonElement.Parse($"{{\"x\": {cell.GetProperty("outcome").GetRawText()}}}"))["x"];

            Assert.Equal(cell.GetProperty("result").GetString(), outcome.Result);
            Assert.Equal(cell.GetProperty("last_run").GetString(), outcome.LastRun);
        }
    }

    // The jobs runner_plan's cases run over: one per branch of the plan, the requires against a scratch folder.
    internal static IReadOnlyList<Job> PlanJobs(string root)
    {
        HashSet<JobTrigger> tick = [JobTrigger.Tick];
        HashSet<JobTrigger> both = [JobTrigger.Logon, JobTrigger.Tick];
        return
        [
            new("every", string.Empty, ["x"], tick, JobCadence.EveryRun, 5),
            new("logon-only", string.Empty, ["x"], new HashSet<JobTrigger> { JobTrigger.Logon }, JobCadence.EveryRun, 5) { RetryExitCodes = new HashSet<int> { 4 } },
            new("boot", string.Empty, ["x"], both, JobCadence.OncePerBoot, 5),
            new("daily", string.Empty, ["x"], both, JobCadence.Daily, 5),
            new("needs", string.Empty, ["x"], tick, JobCadence.EveryRun, 5) { Requires = [Path.Join(root, "present"), Path.Join(root, "absent")] },
            new("has", string.Empty, ["x"], tick, JobCadence.EveryRun, 5) { Requires = [Path.Join(root, "present")] },
            new("flagged", string.Empty, ["x"], tick, JobCadence.EveryRun, 5) { Feature = JobRegistry.ClaudeSessionRestoreFlag },
        ];
    }

    [Fact]
    public void A_whole_run_writes_what_was_recorded_and_reports_what_it_reports()
    {
        var index = 0;
        foreach (var recorded in Oracle.GetProperty("runs").EnumerateArray())
        {
            var directory = Path.Join(_temp.Path, $"run-{index++}");
            var logs = JobState.LogsDir(directory);
            Directory.CreateDirectory(logs);
            if (recorded.TryGetProperty("seed", out var seed))
            {
                foreach (var log in seed.EnumerateObject())
                {
                    File.WriteAllBytes(JobState.LogPath(logs, log.Name), Encoding.UTF8.GetBytes(log.Value.GetString()!));
                }
            }

            if (recorded.TryGetProperty("prior", out var prior))
            {
                File.WriteAllText(Path.Join(directory, JobState.StateName), prior.GetString());
            }

            var report = new List<string>();
            var idle = recorded.GetProperty("idle").GetDouble();
            var runner = new JobRunner(new JobRunnerSeams(new ScriptedExecutor(), () => idle, new RunClock(), report.Add, (_, _) => new MemoryStream()));
            var only = recorded.TryGetProperty("only", out var named) ? named.EnumerateArray().Select(name => name.GetString()!).ToHashSet() : null;

            var exit = runner.Run(
                RunJobs(directory),
                JobWords.Trigger(recorded.GetProperty("trigger").GetString()!),
                directory,
                new DateTimeOffset(2026, 11, 12, 8, 0, 0, TimeSpan.Zero),
                new Dictionary<string, bool>(),
                only,
                recorded.TryGetProperty("force", out var force) && force.GetBoolean());

            var trigger = recorded.GetProperty("trigger").GetString()!;
            Assert.Equal(recorded.GetProperty("exit").GetInt32(), exit);
            Assert.Equal(recorded.GetProperty("report").EnumerateArray().Select(line => line.GetString()), report.Select(Crash));
            AssertSameJson(recorded.GetProperty("state").GetString()!, RunText(Path.Join(directory, JobState.StateName))!);
            AssertSameJson(recorded.GetProperty("last").GetString()!, RunText(JobState.LastRunPath(directory, trigger))!);
            AssertSameJsonLines(recorded.GetProperty("history").GetString()!, RunText(Path.Join(directory, RunHistory.HistoryName))!);
            Assert.Equal(
                recorded.GetProperty("outputs").EnumerateObject().Select(output => (output.Name, output.Value.GetString())),
                Directory.GetFiles(Path.Join(directory, "runs"), "*.log", SearchOption.AllDirectories)
                    .Select(path => (Path.GetRelativePath(directory, path).Replace('\\', '/'), (string?)File.ReadAllText(path)))
                    .OrderBy(output => output.Item1, StringComparer.Ordinal));
        }
    }

    [Fact]
    public void A_run_section_reads_as_recorded()
    {
        var path = Path.Join(_temp.Path, "output.log");
        foreach (var recorded in Oracle.GetProperty("run_outputs").EnumerateArray())
        {
            var bytes = recorded.GetProperty("bytes");
            var target = bytes.ValueKind == JsonValueKind.Null ? Path.Join(_temp.Path, "missing.log") : path;
            if (bytes.ValueKind != JsonValueKind.Null)
            {
                File.WriteAllBytes(target, [.. Enumerable.Repeat((byte)'x', recorded.GetProperty("pad").GetInt32()), .. Convert.FromHexString(bytes.GetString()!)]);
            }

            Assert.Equal(
                recorded.GetProperty("lines").EnumerateArray().Select(line => line.GetString()),
                JobLog.RunOutput(target, recorded.GetProperty("run_id").GetString()));
        }
    }

    // The recorded run's jobs: each script's name, triggers and idle need, in the recorded order.
    private static List<Job> RunJobs(string directory) =>
    [
        .. Oracle.GetProperty("run_scripts").EnumerateObject().Select(script => new Job(
            script.Name,
            string.Empty,
            ["x", script.Name],
            new HashSet<JobTrigger> { script.Value.TryGetProperty("logon_only", out _) ? JobTrigger.Logon : JobTrigger.Tick },
            JobCadence.EveryRun,
            5)
        {
            IdleMinutes = script.Value.TryGetProperty("idle_minutes", out var idle) ? idle.GetInt32() : 0,
        }),
    ];

    // The recorder writes "{crash}" where the crash's exception type, which differs by language, appeared.
    private static string Crash(string text) => text.Replace(nameof(IOException), "{crash}", StringComparison.Ordinal);

    private static string? RunText(string path) =>
        File.Exists(path) ? Crash(File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal)) : null;

    // The recorded fake execution: the script's lines under a header, then the end marker.
    private sealed class ScriptedExecutor : IJobExecutor
    {
        public long Execute(Job job, string logsDir, string runId)
        {
            var script = Oracle.GetProperty("run_scripts").GetProperty(job.Name);
            if (script.TryGetProperty("raise", out var message))
            {
                throw new IOException(message.GetString());
            }

            string Lines(string name) => string.Concat(script.GetProperty(name).EnumerateArray().Select(line => line.GetString() + "\n"));
            var body = $"--- 2026-11-12 09:00:00 [{runId}] x {job.Name}\n" + Lines("lines");
            long code = script.TryGetProperty("timeout", out _) ? JobRunner.TimedOut : script.GetProperty("code").GetInt64();
            body += code == JobRunner.TimedOut ? "--- timed out after 5 min\n" : $"--- exit {code}\n";
            File.AppendAllText(JobState.LogPath(logsDir, job.Name), body);
            return code;
        }
    }

    // The recorder's clock: 09:00 UTC, and no time passing.
    private sealed class RunClock : TimeProvider
    {
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

        public override DateTimeOffset GetUtcNow() => new(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);

        public override long GetTimestamp() => 0;
    }

    // POSIX separators and no .exe, as recorded, so the comparison holds on Windows.
    private static string Normalise(string value)
    {
        var text = value.Replace('\\', '/');
        return text.EndsWith(".exe", StringComparison.Ordinal) ? text[..^4] : text;
    }

    private static string Canonical(string json) => JsonNode.Parse(json)!.ToJsonString();

    private static JobsEnvironment Environment(bool workspace)
    {
        var env = Oracle.GetProperty("environment");
        string Text(string name) => env.GetProperty(name).GetString()!;
        return new JobsEnvironment(
            Text("scripts"),
            Text("plan"),
            workspace ? Text("workspace_root") : null,
            Text("personal_root"),
            [.. env.GetProperty("work_roots").EnumerateArray().Select(root => root.GetString()!)],
            Text("decisions_feed"),
            Text("orca_user_data"),
            // The oracle is the Windows registry the Python scheduler composed, so it is replayed as Windows everywhere.
            Windows: true);
    }

    private static JsonObject Record(Job job) => new()
    {
        ["name"] = job.Name,
        ["description"] = job.Description,
        ["argv"] = new JsonArray([.. job.Argv.Select(arg => (JsonNode?)Normalise(arg))]),
        ["triggers"] = new JsonArray([.. job.Triggers.Select(JobWords.Wire).Order(StringComparer.Ordinal).Select(word => (JsonNode?)word)]),
        ["cadence"] = JobWords.Wire(job.Cadence),
        ["timeout_minutes"] = job.TimeoutMinutes,
        ["requires"] = new JsonArray([.. job.Requires.Select(path => (JsonNode?)Normalise(path))]),
        ["idle_minutes"] = job.IdleMinutes,
        ["retry_exit_codes"] = new JsonArray([.. job.RetryExitCodes.Order().Select(code => (JsonNode?)code)]),
        ["feature"] = job.Feature,
    };

    // The outcomes in a recorded state, read through the C# reader: every recorded one is a valid entry.
    private IReadOnlyDictionary<string, Outcome> Outcomes(JsonElement state)
    {
        var outcomes = JobState.ReadState(Written(state.GetRawText()));
        Assert.Equal(state.EnumerateObject().Count(), outcomes.Count);
        return outcomes;
    }

    private JsonNode StateJson(IReadOnlyDictionary<string, Outcome> state)
    {
        var path = Path.Join(_temp.Path, "rewritten-state.json");
        JobState.WriteState(path, state);
        return JsonNode.Parse(File.ReadAllText(path))!;
    }

    private string Written(string text)
    {
        var path = Path.Join(_temp.Path, "read.json");
        File.WriteAllText(path, text);
        return path;
    }

    // The oracle holds the recorded bytes; after the writer moved to System.Text.Json, the same data is all that is
    // claimed, not the same spelling.
    private static void AssertSameJson(string expected, string actual) =>
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected), JsonNode.Parse(actual)), $"Expected {expected}{System.Environment.NewLine}Actual {actual}");

    // A history file is JSON Lines: one object per line.
    private static void AssertSameJsonLines(string expected, string actual)
    {
        var left = expected.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var right = actual.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        Assert.Equal(left.Length, right.Length);
        for (var i = 0; i < left.Length; i++)
        {
            AssertSameJson(left[i], right[i]);
        }
    }
}
