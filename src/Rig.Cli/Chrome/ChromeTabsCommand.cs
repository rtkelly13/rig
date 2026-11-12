using System.CommandLine;
using System.Globalization;
using System.Text.Json.Nodes;
using Rig.Core.Chrome;
using Rig.Core.GitHub;
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
/// <param name="driver">Reads Chrome for <c>list</c> and <c>rank</c>; null where only the Python has a driver (macOS).</param>
internal sealed class ChromeTabsCommand(
    Func<string, string?> environment, string home, TimeProvider time, TimeZoneInfo zone, TextWriter output, ChromeTabsDriver? driver = null)
{
    /// <summary>The group's name, shared with the forwarding row.</summary>
    public const string Name = "chrome-tabs";

    /// <summary>The group's help, as <c>desktop-manager chrome-tabs</c> words it.</summary>
    public const string Description =
        "Chrome tabs via UI Automation: list them, prune GitHub PR tabs and duplicate pages, group by repo, or organise (both, window by window).";

    /// <summary><c>traces show</c> exits with this when no run matches.</summary>
    public const int NoRunExit = 1;

    /// <summary>
    /// The <c>chrome-tabs</c> command. <c>list</c> and <c>rank</c> are only registered with a driver
    /// (Windows): without one, the forwarding table hands them to the Python, which has the macOS driver.
    /// </summary>
    public Command Build()
    {
        var command = new Command(Name, Description) { Traces() };
        if (driver is not null)
        {
            command.Subcommands.Add(List(driver));
            command.Subcommands.Add(Rank(driver));
        }

        return command;
    }

    private Command List(ChromeTabsDriver chrome)
    {
        var list = new Command("list", "List every Chrome window's tabs on every desktop (flags: Selected Pinned Loaded Audible). [Windows/macOS only]");
        var profile = Profile();
        var prsOnly = new Option<bool>("--prs-only") { Description = "Only tabs showing a GitHub pull request." };
        var json = new Option<bool>("--json") { Description = "Machine-readable output." };
        list.Options.Add(profile);
        list.Options.Add(prsOnly);
        list.Options.Add(json);
        list.SetAction(parsed => ListTabs(chrome, parsed.GetValue(profile), parsed.GetValue(prsOnly), parsed.GetValue(json)));
        return list;
    }

    private Command Rank(ChromeTabsDriver chrome)
    {
        var rank = new Command("rank", "Rank every tab by keep priority, with the reasons: protected first, the first to close last. [Windows/macOS only]");
        var profile = Profile();
        var github = new Option<bool>("--github") { Description = "Query GitHub for PR state, chains, size, review and checks (the default)." };
        var noGithub = new Option<bool>("--no-github") { Description = "Skip the PR rules and every GitHub call; GitHub pages are still de-duplicated by title." };
        var limit = AtLeastOne(new Option<int?>("--limit") { Description = "Only the N lowest-ranked tabs (the first to go)." });
        var json = new Option<bool>("--json") { Description = "Machine-readable output." };
        rank.Options.Add(profile);
        rank.Options.Add(github);
        rank.Options.Add(noGithub);
        rank.Options.Add(limit);
        rank.Options.Add(json);
        rank.SetAction((parsed, cancellationToken) => RankTabs(
            chrome, parsed.GetValue(profile), !parsed.GetValue(noGithub), parsed.GetValue(limit), parsed.GetValue(json), cancellationToken));
        return rank;
    }

    private static Option<string?> Profile() =>
        new("--profile") { Description = "Only this profile: directory ('Profile 4') or display name." };

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

    private static Option<T> AtLeastOne<T>(Option<T> option)
    {
        option.Validators.Add(result =>
        {
            if (result.GetValueOrDefault<T>() is int value && value < 1)
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

    private int ListTabs(ChromeTabsDriver chrome, string? profile, bool prsOnly, bool json)
    {
        var started = time.GetTimestamp();
        var windows = FilterProfile(chrome.ReadWindows(), profile);
        var elapsed = time.GetElapsedTime(started);
        if (json)
        {
            Say(PyJson.Dumps(
                new JsonArray([.. windows.Select(window => (JsonNode)WindowJson(window, [.. window.Tabs.Where(tab => !prsOnly || PrPrune.RefFromTitle(tab.Title) is not null)]))]),
                ensureAscii: false));
            return 0;
        }

        foreach (var (window, number) in windows.Select((window, index) => (window, index + 1)))
        {
            Say(WindowHeading(number, window));
            foreach (var tab in window.Tabs)
            {
                var pr = PrPrune.RefFromTitle(tab.Title);
                if (prsOnly && pr is null)
                {
                    continue;
                }

                var key = pr is not null ? $"{pr} " : SiteRules.Identify(tab.Title) is { } identity ? $"{identity.Key} " : string.Empty;
                Say(string.Create(CultureInfo.InvariantCulture, $"  {tab.Index + 1,3} {Flags(tab)} {Memory(tab),7} {key}{tab.Title}"));
            }
        }

        var total = windows.Sum(window => window.Tabs.Count);
        var memory = windows.SelectMany(window => window.Tabs).Sum(tab => tab.MemoryMb ?? 0);
        Say(string.Create(
            CultureInfo.InvariantCulture,
            $"{windows.Count} windows, {total} tabs, {TabRank.FormatSize(memory)} reported by tabs, enumerated in {elapsed.TotalSeconds:F2}s"));
        return 0;
    }

    private async Task<int> RankTabs(ChromeTabsDriver chrome, string? profile, bool github, int? limit, bool json, CancellationToken cancellationToken)
    {
        var windows = FilterProfile(chrome.ReadWindows(), profile);
        var resolution = github
            ? await PrResolver.ResolveAsync(chrome.GitHub, PrPrune.PrTabs(windows).Select(item => item.Pr).Distinct(), cancellationToken: cancellationToken).ConfigureAwait(false)
            : Resolution.Empty;
        var ranked = TabRank.Rank(windows, resolution);
        var shown = limit is { } last ? ranked.Skip(Math.Max(ranked.Count - last, 0)).ToList() : ranked;
        if (json)
        {
            Say(PyJson.Dumps(new JsonArray([.. shown.Select(item => (JsonNode)RankedJson(item))]), ensureAscii: false));
            return 0;
        }

        Say("  #  score   memory  page");
        foreach (var line in TabPlanText.Rank(shown))
        {
            Say(line);
        }

        var memory = TabRank.FormatSize(ranked.Sum(item => item.MemoryMb));
        Say(string.Create(CultureInfo.InvariantCulture, $"{ranked.Count} tabs, {ranked.Count(item => item.Protected.Length > 0)} protected, {memory} reported by tabs"));
        return 0;
    }

    /// <summary>The windows of <paramref name="profile"/>, by directory or display name ignoring case; all of them for null.</summary>
    internal static IReadOnlyList<ChromeWindow> FilterProfile(IReadOnlyList<ChromeWindow> windows, string? profile)
    {
        if (profile is null)
        {
            return windows;
        }

        return [.. windows.Where(window => string.Equals(window.ProfileDir, profile, StringComparison.OrdinalIgnoreCase) || string.Equals(window.ProfileName, profile, StringComparison.OrdinalIgnoreCase))];
    }

    private static string WindowHeading(int number, ChromeWindow window)
    {
        var where = window.DesktopNumber is { } desktop ? string.Create(CultureInfo.InvariantCulture, $"desktop {desktop}") : "desktop ?";
        var state = window.Minimized ? ", minimised" : string.Empty;
        var profile = string.IsNullOrEmpty(window.ProfileName) ? window.ProfileKey : window.ProfileName;
        return string.Create(CultureInfo.InvariantCulture, $"Window {number} [{profile} / {window.ProfileKey}] {where}{state}: {window.Tabs.Count} tabs");
    }

    private static string Flags(ChromeTab tab) =>
        string.Concat((tab.Selected ? "S" : "."), (tab.Pinned ? "P" : "."), (tab.Loaded ? "L" : "."), (tab.Audible ? "A" : "."));

    private static string Memory(ChromeTab tab) => tab.MemoryMb is { } mb ? TabRank.FormatSize(mb) : "-";

    /// <summary>A window as <c>list --json</c> prints it: the Python dataclass's fields in order, <c>rect</c> and <c>monitor</c> as arrays.</summary>
    internal static JsonObject WindowJson(ChromeWindow window, IEnumerable<ChromeTab> tabs) => new()
    {
        ["hwnd"] = window.Handle,
        ["title"] = window.Title,
        ["profile_dir"] = window.ProfileDir,
        ["profile_name"] = window.ProfileName,
        ["age_rank"] = window.AgeRank,
        ["minimized"] = window.Minimized,
        ["desktop_number"] = window.DesktopNumber,
        ["tabs"] = new JsonArray([.. tabs.Select(tab => (JsonNode)TabJson(tab))]),
        ["rect"] = window.Rect is { } rect ? new JsonArray(rect.Left, rect.Top, rect.Right, rect.Bottom) : null,
        ["monitor"] = window.Monitor is { } monitor ? new JsonArray(monitor.X, monitor.Y) : null,
        ["profile_scope"] = window.ProfileScope,
    };

    private static JsonObject TabJson(ChromeTab tab) => new()
    {
        ["runtime_id"] = RuntimeIdJson(tab.RuntimeId),
        ["window_hwnd"] = tab.WindowHandle,
        ["index"] = tab.Index,
        ["name"] = tab.Name,
        ["title"] = tab.Title,
        ["selected"] = tab.Selected,
        ["pinned"] = tab.Pinned,
        ["loaded"] = tab.Loaded,
        ["audible"] = tab.Audible,
        ["alerts"] = new JsonArray([.. tab.Alerts.Select(alert => (JsonNode?)alert)]),
        ["memory_mb"] = tab.MemoryMb,
        ["group"] = tab.Group is { } group ? new JsonObject { ["runtime_id"] = RuntimeIdJson(group.RuntimeId), ["title"] = group.Title } : null,
        ["url"] = tab.Url?.OriginalString,
    };

    private static JsonArray RuntimeIdJson(RuntimeId id) => new([.. id.Parts.Select(part => (JsonNode?)part)]);

    private static JsonObject RankedJson(RankedTab item) => new()
    {
        ["label"] = item.Label,
        ["title"] = item.Tab.Title,
        ["profile"] = item.Profile,
        ["window"] = item.WindowNumber,
        ["tab"] = item.Tab.Index + 1,
        ["protected"] = item.Protected,
        ["score"] = item.Score,
        ["memory_mb"] = item.Tab.MemoryMb,
        ["reasons"] = new JsonArray([.. item.Reasons.Select(reason => (JsonNode?)reason)]),
        ["flags"] = new JsonArray([.. item.Flags.Select(flag => (JsonNode?)flag)]),
        ["details"] = item.Details is { } details
            ? new JsonObject
            {
                ["additions"] = details.Additions,
                ["deletions"] = details.Deletions,
                ["changed_files"] = details.ChangedFiles,
                ["draft"] = details.Draft,
                ["review_decision"] = details.ReviewDecision,
                ["checks"] = details.Checks,
                ["authored_by_viewer"] = details.AuthoredByViewer,
                ["review_requested"] = details.ReviewRequested,
            }
            : null,
    };

    private void Say(string line) => output.Write(line + "\n");
}
