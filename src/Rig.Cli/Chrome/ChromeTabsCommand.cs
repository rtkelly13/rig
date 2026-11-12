using System.CommandLine;
using System.Globalization;
using System.Text.Json.Nodes;
using Rig.Core.Chrome;
using Rig.Core.Policy;

namespace Rig.Cli.Chrome;

/// <summary>
/// <c>rig chrome-tabs</c>: the ported half of <c>desktop-manager chrome-tabs</c>. Only <c>traces</c> runs here so far;
/// the forwarding table hands every other line to the Python group. Ported from <c>desktop_manager.chrome.traces_cli</c>,
/// whose messages all go to stdout.
/// </summary>
/// <param name="environment">Reads an environment variable (<c>LOCALAPPDATA</c> places the traces).</param>
/// <param name="home">The user's home, where the traces live when <c>LOCALAPPDATA</c> is unset.</param>
/// <param name="time">The clock that picks today's day file.</param>
/// <param name="zone">The zone run start times are shown in.</param>
/// <param name="output">Where every line goes.</param>
internal sealed class ChromeTabsCommand(Func<string, string?> environment, string home, TimeProvider time, TimeZoneInfo zone, TextWriter output)
{
    /// <summary>The group's name, shared with the forwarding row.</summary>
    public const string Name = "chrome-tabs";

    /// <summary>The group's help, as <c>desktop-manager chrome-tabs</c> words it.</summary>
    public const string Description =
        "Chrome tabs via UI Automation: list them, prune GitHub PR tabs and duplicate pages, group by repo, or organise (both, window by window).";

    /// <summary><c>traces show</c> exits with this when no run matches.</summary>
    public const int NoRunExit = 1;

    /// <summary>The <c>chrome-tabs</c> command.</summary>
    public Command Build() => new(Name, Description) { Traces() };

    private Command Traces()
    {
        var runs = new Command("runs", "Recent organise runs, newest first: duration, outcome, tabs closed, grouping, the wait and its last block.");
        var runsDays = Days(7);
        var limit = AtLeastOne(new Option<int>("--limit") { Description = "Only the N most recent runs.", DefaultValueFactory = _ => 20 });
        var runsJson = new Option<bool>("--json") { Description = "Machine-readable output." };
        var runsDir = Dir();
        runs.Options.Add(runsDays);
        runs.Options.Add(limit);
        runs.Options.Add(runsJson);
        runs.Options.Add(runsDir);
        runs.SetAction(parsed => Runs(parsed.GetValue(runsDays), parsed.GetValue(limit), parsed.GetValue(runsJson), parsed.GetValue(runsDir)));

        var show = new Command("show", "One run's span tree: every phase with its duration, attributes and events.");
        var runId = new Argument<string?>("run_id") { Description = "Run id from `traces runs` (a prefix will do). Default: the latest.", Arity = ArgumentArity.ZeroOrOne };
        var showDays = Days(30);
        var showDir = Dir();
        show.Arguments.Add(runId);
        show.Options.Add(showDays);
        show.Options.Add(showDir);
        show.SetAction(parsed => Show(parsed.GetValue(runId), parsed.GetValue(showDays), parsed.GetValue(showDir)));

        var summary = new Command("summary", "Organise runs over N days: success rate, how often grouping happened or gave up, waits and blocks.");
        var summaryDays = Days(7);
        var summaryJson = new Option<bool>("--json") { Description = "Machine-readable output." };
        var summaryDir = Dir();
        summary.Options.Add(summaryDays);
        summary.Options.Add(summaryJson);
        summary.Options.Add(summaryDir);
        summary.SetAction(parsed => Summary(parsed.GetValue(summaryDays), parsed.GetValue(summaryJson), parsed.GetValue(summaryDir)));

        var prune = new Command("prune", "Delete trace files older than --keep-days. The first scheduled run of each day does this anyway.");
        var keepDays = AtLeastOne(new Option<int>("--keep-days") { Description = "Keep this many days of trace files.", DefaultValueFactory = _ => ChromeTraces.KeepDays });
        var dryRun = new Option<bool>("--dry-run") { Description = "List the files that would go; delete nothing." };
        var pruneDir = Dir();
        prune.Options.Add(keepDays);
        prune.Options.Add(dryRun);
        prune.Options.Add(pruneDir);
        prune.SetAction(parsed => Prune(parsed.GetValue(keepDays), parsed.GetValue(dryRun), parsed.GetValue(pruneDir)));

        return new Command("traces", "Reports over the organise runs' traces: recent runs, one run's span tree, a summary, and pruning.")
        {
            runs,
            show,
            summary,
            prune,
        };
    }

    private static Option<int> Days(int days) =>
        AtLeastOne(new Option<int>("--days") { Description = "How many days of trace files to read.", DefaultValueFactory = _ => days });

    private static Option<string?> Dir() =>
        new("--dir") { Description = "Traces directory. Default: %LOCALAPPDATA%/shared-utilities/chrome-tabs/traces." };

    private static Option<int> AtLeastOne(Option<int> option)
    {
        option.Validators.Add(result =>
        {
            if (result.GetValueOrDefault<int>() is var value && value < 1)
            {
                result.AddError($"Invalid value for '{option.Name}': {value} is not in the range x>=1.");
            }
        });
        return option;
    }

    private DateOnly Today => DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);

    private string Directory(string? chosen) => chosen ?? ChromeTraces.TracesDir(environment, home);

    private IReadOnlyList<TraceRun> Load(string? directory, int days) =>
        ChromeTraces.Runs(ChromeTraces.LoadSpans(Directory(directory), Today, days));

    private int Runs(int days, int limit, bool json, string? directory)
    {
        var all = Load(directory, days);
        var found = all.Skip(Math.Max(all.Count - limit, 0)).ToList();
        if (json)
        {
            var rows = new JsonArray([.. found.AsEnumerable().Reverse().Select(run => (JsonNode)ChromeTraces.RunJson(run, zone))]);
            Say(PyJson.Dumps(rows, ensureAscii: false));
            return 0;
        }

        if (found.Count == 0)
        {
            Say($"No organise runs in the last {days} days.");
            return 0;
        }

        Say(ChromeTraces.RunsHeader);
        foreach (var line in ChromeTraces.RenderRuns(found, zone))
        {
            Say(line);
        }

        return 0;
    }

    private int Show(string? runId, int days, string? directory)
    {
        if (ChromeTraces.FindRun(Load(directory, days), runId) is not { } run)
        {
            Say($"No organise run {runId ?? string.Empty} in the last {days} days.");
            return NoRunExit;
        }

        foreach (var line in ChromeTraces.RenderTree(run))
        {
            Say(line);
        }

        return 0;
    }

    private int Summary(int days, bool json, string? directory)
    {
        var summary = ChromeTraces.Summarise(Load(directory, days));
        if (json)
        {
            var grouping = new JsonObject();
            foreach (var (verdict, count) in summary.Grouping)
            {
                grouping[verdict] = count;
            }

            Say(PyJson.Dumps(new JsonObject
            {
                ["runs"] = summary.Runs,
                ["completed"] = summary.Completed,
                ["ok"] = summary.Ok,
                ["incomplete"] = summary.Incomplete,
                ["grouping"] = grouping,
                ["waits"] = new JsonArray([.. summary.Waits.Select(wait => (JsonNode?)wait)]),
                ["durations"] = new JsonArray([.. summary.Durations.Select(duration => (JsonNode?)duration)]),
                ["closed"] = summary.Closed,
                ["blocks"] = new JsonArray([.. summary.Blocks.Select(block => (JsonNode?)new JsonArray(block.Reason, block.Count))]),
                ["success_rate"] = summary.SuccessRate,
            }));
            return 0;
        }

        foreach (var line in ChromeTraces.RenderSummary(summary, days))
        {
            Say(line);
        }

        return 0;
    }

    private int Prune(int keepDays, bool dryRun, string? directory)
    {
        var removed = ChromeTraces.Prune(Directory(directory), Today, keepDays, dryRun);
        foreach (var path in removed)
        {
            Say($"  {Path.GetFileName(path)}");
        }

        Say($"{(dryRun ? "Would delete" : "Deleted")} {removed.Count.ToString(CultureInfo.InvariantCulture)} trace files older than {keepDays.ToString(CultureInfo.InvariantCulture)} days");
        return 0;
    }

    private void Say(string line) => output.Write(line + "\n");
}
