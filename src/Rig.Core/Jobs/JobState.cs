using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Rig.Core.Policy;

namespace Rig.Core.Jobs;

/// <summary>How one job's last run went, as <c>state.json</c> and <c>last-&lt;trigger&gt;.json</c> keep it.</summary>
/// <param name="Started">When it started, in ISO 8601 with its offset, as Python's <c>isoformat</c> writes it.</param>
/// <param name="Seconds">How long it took.</param>
/// <param name="ReturnCode">Its exit code; null when it never exited on its own (stopped, or never started).</param>
/// <param name="Trigger">The trigger of the run that started it.</param>
public sealed record Outcome(string Started, double Seconds, int? ReturnCode, string Trigger)
{
    /// <summary>Why it did not run to an exit code, such as <c>timed out after 20 min</c>.</summary>
    public string? Error { get; init; }

    /// <summary>The coordinator run that started it; its log section is headed with this id.</summary>
    public string? RunId { get; init; }

    /// <summary>The last line the job printed, such as <c>align: 0 moved, 11 in place</c>.</summary>
    public string? Summary { get; init; }

    /// <summary>When it failed, the end of its output: the reason, without opening the log.</summary>
    public IReadOnlyList<string> Detail { get; init; } = [];

    /// <summary><see cref="Started"/> as a time; it throws when that is not one, which a read file never holds.</summary>
    public DateTimeOffset StartedAt => IsoStamp.Parse(Started) ?? throw new InvalidOperationException($"Not an ISO time: {Started}");

    /// <summary>Exit 0 with no error.</summary>
    public bool Ok => ReturnCode == 0 && Error is null;

    /// <summary>
    /// The result cell <c>jobs status</c> and <c>jobs last</c> print: <c>ok</c>, the error or <c>exit N</c>, then
    /// <c>: summary</c> when there is one. An empty error or summary counts as none, as it did in Python.
    /// </summary>
    public string Result
    {
        get
        {
            var status = Ok ? "ok" : !string.IsNullOrEmpty(Error) ? Error : $"exit {ReturnCode?.ToString(CultureInfo.InvariantCulture) ?? "None"}";
            return string.IsNullOrEmpty(Summary) ? status : $"{status}: {Summary}";
        }
    }

    /// <summary>The last-run cell <c>jobs status</c> prints: the start's weekday and time at its own offset, and the seconds.</summary>
    public string LastRun => string.Create(CultureInfo.InvariantCulture, $"{StartedAt:ddd HH:mm} ({WholeSeconds}s)");

    /// <summary>The seconds as Python's <c>:.0f</c> prints them: rounded half to even.</summary>
    public string WholeSeconds => Math.Round(Seconds, MidpointRounding.ToEven).ToString("0", CultureInfo.InvariantCulture);
}

/// <summary>One job in one run: whether it ran, why, and how it went.</summary>
/// <param name="Job">The job's name.</param>
/// <param name="Ran">Whether the run took it.</param>
/// <param name="Reason">Why it ran or was skipped.</param>
/// <param name="Outcome">How it went; null when it was skipped (or its record is unreadable).</param>
public sealed record Decision(string Job, bool Ran, string Reason, Outcome? Outcome = null);

/// <summary>The latest run on one trigger: every job it ran or skipped.</summary>
/// <param name="RunId">The run's id, <c>&lt;trigger&gt;-&lt;YYYYmmdd-HHMMSS&gt;</c>.</param>
/// <param name="Trigger">The trigger it ran on.</param>
/// <param name="Started">When it started.</param>
/// <param name="Finished">When it finished; null while it is going, or when it was killed part-way.</param>
/// <param name="Decisions">Each job it considered, in order.</param>
public sealed record RunRecord(string RunId, string Trigger, string Started, string? Finished, IReadOnlyList<Decision> Decisions)
{
    /// <summary>The jobs it ran that did not end <see cref="Outcome.Ok"/>.</summary>
    public IReadOnlyList<Decision> Failed => [.. Decisions.Where(item => item.Outcome is { Ok: false })];
}

/// <summary>
/// The coordinator's files: <c>state.json</c> (each job's last <see cref="Outcome"/>) and <c>last-&lt;trigger&gt;.json</c>
/// (the latest <see cref="RunRecord"/> per trigger). Ported from <c>desktop_manager.jobs.state</c>, byte for byte: the
/// Python runner, web page and MCP read and write the same files. An unreadable file reads as empty, which only makes
/// every job due once.
/// </summary>
public static class JobState
{
    /// <summary>The state file's name in the jobs folder.</summary>
    public const string StateName = "state.json";

    /// <summary>The lock a run holds.</summary>
    public const string LockName = "run.lock";

    /// <summary>The jobs folder under rig's app data folder.</summary>
    public static string JobsDir(string appData) => Path.Join(appData, "jobs");

    /// <summary>Where each job's own log lives.</summary>
    public static string LogsDir(string jobsDir) => Path.Join(jobsDir, "logs");

    /// <summary>The job's log in <paramref name="logsDir"/>.</summary>
    public static string LogPath(string logsDir, string job) => Path.Join(logsDir, $"{job}.log");

    /// <summary>The latest run on <paramref name="trigger"/> in <paramref name="jobsDir"/>.</summary>
    public static string LastRunPath(string jobsDir, string trigger) => Path.Join(jobsDir, $"last-{trigger}.json");

    /// <summary>
    /// Each job's last outcome in <paramref name="path"/>. An entry missing its start, seconds or trigger, or whose start
    /// is not a time, is dropped; any other field of the wrong type reads as unset. A missing or broken file is empty.
    /// </summary>
    public static IReadOnlyDictionary<string, Outcome> ReadState(string path)
    {
        var state = new Dictionary<string, Outcome>(StringComparer.Ordinal);
        if (Parse(path) is { ValueKind: JsonValueKind.Object } root)
        {
            foreach (var entry in root.EnumerateObject())
            {
                if (ReadOutcome(entry.Value) is { } outcome)
                {
                    state[entry.Name] = outcome;
                }
                else
                {
                    _ = state.Remove(entry.Name);
                }
            }
        }

        return state;
    }

    /// <summary>Writes <paramref name="state"/> to <paramref name="path"/>, sorted by name, as Python's <c>json.dumps(indent=2)</c> does.</summary>
    public static void WriteState(string path, IReadOnlyDictionary<string, Outcome> state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var root = new JsonObject();
        foreach (var (name, outcome) in state.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            root[name] = ToJson(outcome);
        }

        Write(path, root);
    }

    /// <summary>
    /// The run in <paramref name="path"/>, or null when it is missing, broken or any decision is malformed. An outcome
    /// that does not read leaves its decision without one.
    /// </summary>
    public static RunRecord? ReadRun(string path)
    {
        if (Parse(path) is not { ValueKind: JsonValueKind.Object } root
            || Text(root, "run_id") is not { } runId
            || Text(root, "trigger") is not { } trigger
            || Text(root, "started") is not { } started
            || !root.TryGetProperty("decisions", out var list)
            || list.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var decisions = new List<Decision>();
        foreach (var item in list.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || Text(item, "job") is not { } job
                || !item.TryGetProperty("ran", out var ran)
                || ran.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                || Text(item, "reason") is not { } reason)
            {
                return null;
            }

            decisions.Add(new Decision(job, ran.GetBoolean(), reason, item.TryGetProperty("outcome", out var outcome) ? ReadOutcome(outcome) : null));
        }

        return new RunRecord(runId, trigger, started, Text(root, "finished"), decisions);
    }

    /// <summary>Writes <paramref name="run"/> to <paramref name="path"/>, fields in Python's order.</summary>
    public static void WriteRun(string path, RunRecord run)
    {
        ArgumentNullException.ThrowIfNull(run);
        var decisions = new JsonArray();
        foreach (var item in run.Decisions)
        {
            decisions.Add(new JsonObject
            {
                ["job"] = item.Job,
                ["ran"] = item.Ran,
                ["reason"] = item.Reason,
                ["outcome"] = item.Outcome is null ? null : ToJson(item.Outcome),
            });
        }

        Write(path, new JsonObject
        {
            ["run_id"] = run.RunId,
            ["trigger"] = run.Trigger,
            ["started"] = run.Started,
            ["finished"] = run.Finished,
            ["decisions"] = decisions,
        });
    }

    /// <summary>Whether <paramref name="job"/>'s cadence wants it now. Triggers are the caller's to check.</summary>
    public static bool IsDue(Job job, Outcome? last, DateTimeOffset now, DateTimeOffset booted)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (job.Cadence == JobCadence.EveryRun || last is null)
        {
            return true;
        }

        return job.Cadence == JobCadence.OncePerBoot ? last.StartedAt < booted : now - last.StartedAt >= TimeSpan.FromHours(Job.DailyHours);
    }

    private static JsonObject ToJson(Outcome outcome) => new()
    {
        ["started"] = outcome.Started,
        // A float node, so 3.0 prints as 3.0 and not 3.
        ["seconds"] = JsonNode.Parse(PyJson.FloatRepr(outcome.Seconds)),
        ["returncode"] = outcome.ReturnCode,
        ["trigger"] = outcome.Trigger,
        ["error"] = outcome.Error,
        ["run_id"] = outcome.RunId,
        ["summary"] = outcome.Summary,
        ["detail"] = new JsonArray([.. outcome.Detail.Select(line => (JsonNode?)line)]),
    };

    // Python's write_text: written beside the file, then moved over it; newlines are the platform's.
    private static void Write(string path, JsonNode payload)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = Path.ChangeExtension(path, ".tmp");
        var text = (PyJson.Dumps(payload) + "\n").Replace("\n", Environment.NewLine, StringComparison.Ordinal);
        File.WriteAllText(temporary, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(temporary, path, overwrite: true);
    }

    private static JsonElement? Parse(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
            return document.RootElement.Clone();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    // pydantic's strict _StoredOutcome: started, seconds and trigger must be there and typed; the rest are Lenient.
    private static Outcome? ReadOutcome(JsonElement stored)
    {
        if (stored.ValueKind != JsonValueKind.Object
            || Text(stored, "started") is not { } started
            || IsoStamp.Parse(started) is null
            || !stored.TryGetProperty("seconds", out var seconds)
            || seconds.ValueKind != JsonValueKind.Number
            || !seconds.TryGetDouble(out var value)
            || Text(stored, "trigger") is not { } trigger)
        {
            return null;
        }

        return new Outcome(started, value, Integer(stored, "returncode"), trigger)
        {
            Error = Text(stored, "error"),
            RunId = Text(stored, "run_id"),
            Summary = Text(stored, "summary"),
            Detail = Lines(stored, "detail"),
        };
    }

    private static string? Text(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    // StrictInt: a number written without a point or exponent; 3.0 and true are not.
    private static int? Integer(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.GetRawText().IndexOfAny(['.', 'e', 'E']) < 0
        && value.TryGetInt32(out var number)
            ? number
            : null;

    // A list of strings, or nothing when it is anything else.
    private static IReadOnlyList<string> Lines(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Array
        && value.EnumerateArray().All(item => item.ValueKind == JsonValueKind.String)
            ? [.. value.EnumerateArray().Select(item => item.GetString()!)]
            : [];
}

/// <summary>
/// The ISO 8601 times Python's <c>datetime.fromisoformat</c> reads that rig writes or a person would: a date, then
/// optionally <c>T</c> or a space, hours, minutes, seconds and a fraction, and an offset or <c>Z</c>. Week dates and the
/// compact date forms are not read. A time without an offset is taken as local.
/// </summary>
public static partial class IsoStamp
{
    /// <summary><paramref name="text"/> as a time, or null when it is not one.</summary>
    public static DateTimeOffset? Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var match = Pattern().Match(text);
        if (!match.Success)
        {
            return null;
        }

        int Group(string name) => match.Groups[name].Success ? int.Parse(match.Groups[name].Value, CultureInfo.InvariantCulture) : 0;
        try
        {
            var fraction = match.Groups["f"].Value;
            var ticks = fraction.Length == 0 ? 0 : int.Parse(fraction.PadRight(7, '0')[..7], CultureInfo.InvariantCulture) / 10 * 10;
            var local = new DateTime(Group("y"), Group("mo"), Group("d"), Group("h"), Group("mi"), Group("s"), DateTimeKind.Unspecified).AddTicks(ticks);
            if (match.Groups["z"].Success)
            {
                return new DateTimeOffset(local, TimeSpan.Zero);
            }

            if (!match.Groups["sign"].Success)
            {
                return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
            }

            var offset = new TimeSpan(Group("oh"), Group("om"), Group("os"));
            return new DateTimeOffset(local, match.Groups["sign"].Value == "-" ? -offset : offset);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    [GeneratedRegex(
        @"^(?<y>\d{4})-(?<mo>\d{2})-(?<d>\d{2})(?:[T ](?<h>\d{2})(?::?(?<mi>\d{2})(?::?(?<s>\d{2})(?:[.,](?<f>\d+))?)?)?(?:(?<z>Z)|(?<sign>[+-])(?<oh>\d{2})(?::?(?<om>\d{2})(?::?(?<os>\d{2}))?)?)?)?$",
        RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}
