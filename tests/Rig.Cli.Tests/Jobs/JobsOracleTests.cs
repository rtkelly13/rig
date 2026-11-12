using System.Text.Json;
using System.Text.Json.Nodes;
using Rig.Core.Jobs;
using Rig.Core.Tests;

namespace Rig.Cli.Tests.Jobs;

/// <summary>
/// <c>Rig.Core.Jobs</c> replayed against <c>jobs-oracle.json</c>, recorded from Python's <c>desktop_manager.jobs</c>: the
/// same registry, the same bytes in <c>state.json</c> and <c>last-&lt;trigger&gt;.json</c>, the same reading of damaged
/// files, the same plan and the same cells. The Python test <c>test_the_csharp_oracle_is_what_python_records</c> holds
/// Python to the same file while its runner still writes these files.
/// </summary>
public sealed class JobsOracleTests : IDisposable
{
    private const string RootToken = "{root}";

    private static readonly JsonElement Oracle = JsonDocument.Parse(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Jobs", "jobs-oracle.json"))).RootElement;

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
    public void A_written_state_is_byte_for_byte_what_python_writes()
    {
        foreach (var write in Oracle.GetProperty("state_writes").EnumerateArray())
        {
            var path = Path.Join(_temp.Path, "state", JobState.StateName);
            JobState.WriteState(path, Outcomes(write.GetProperty("state")));

            Assert.Equal(write.GetProperty("text").GetString()!.Replace("\n", System.Environment.NewLine, StringComparison.Ordinal), File.ReadAllText(path));
            Assert.False(File.Exists(Path.ChangeExtension(path, ".tmp")));
        }
    }

    [Fact]
    public void A_damaged_state_reads_as_python_reads_it()
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
    public void A_run_record_writes_back_byte_for_byte()
    {
        foreach (var write in Oracle.GetProperty("run_writes").EnumerateArray())
        {
            var text = write.GetProperty("text").GetString()!;
            var path = Path.Join(_temp.Path, "last-logon.json");
            JobState.WriteRun(path, JobState.ReadRun(Written(text))!);

            Assert.Equal(text.Replace("\n", System.Environment.NewLine, StringComparison.Ordinal), File.ReadAllText(path));
        }
    }

    [Fact]
    public void A_damaged_run_record_reads_as_python_reads_it()
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
    public void Every_recorded_plan_takes_and_skips_as_python_did()
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
    public void The_result_and_last_run_cells_are_python_s()
    {
        foreach (var cell in Oracle.GetProperty("cells").EnumerateArray())
        {
            var outcome = Outcomes(JsonDocument.Parse($"{{\"x\": {cell.GetProperty("outcome").GetRawText()}}}").RootElement)["x"];

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

    // POSIX separators and no .exe, as the Python records them, so the comparison holds on Windows.
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
            JobRegistry.ChromeTabsLog(Oracle.GetProperty("local_app_data").GetString(), "/home"),
            OperatingSystem.IsWindows());
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
        ["detail_log"] = job.DetailLog is null ? null : Normalise(job.DetailLog),
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
}
