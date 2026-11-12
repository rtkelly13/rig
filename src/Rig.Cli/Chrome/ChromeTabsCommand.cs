using System.CommandLine;
using System.Globalization;
using System.Text.Json.Nodes;
using Rig.Core.Chrome;
using Rig.Core.GitHub;
using Rig.Core.Policy;

namespace Rig.Cli.Chrome;

/// <summary>
/// <c>rig chrome-tabs</c>: the ported half of <c>desktop-manager chrome-tabs</c>. <c>traces</c> runs here everywhere;
/// <c>list</c>, <c>rank</c>, <c>prune</c>, <c>install</c> and <c>uninstall</c> where there is a driver. The forwarding
/// table hands every other line to the Python group. Ported from <c>desktop_manager.chrome.traces_cli</c> and
/// <c>desktop_manager.chrome.cli</c>, whose messages all go to stdout.
/// </summary>
/// <param name="environment">Reads an environment variable (<c>LOCALAPPDATA</c> places the traces).</param>
/// <param name="home">The user's home, where the traces live when <c>LOCALAPPDATA</c> is unset.</param>
/// <param name="time">The clock that picks today's day file.</param>
/// <param name="zone">The zone run start times are shown in.</param>
/// <param name="output">Where every line goes.</param>
/// <param name="driver">Opens Chrome for <c>list</c>, <c>rank</c> and <c>prune</c>; null where only the Python has a driver (macOS).</param>
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
    /// The <c>chrome-tabs</c> command. <c>list</c>, <c>rank</c>, <c>prune</c>, <c>install</c> and <c>uninstall</c> are only registered
    /// with a driver (Windows): without one, the forwarding table hands them to the Python, which has the macOS driver
    /// and refuses the Windows-only installs itself.
    /// </summary>
    public Command Build()
    {
        var command = new Command(Name, Description) { Traces() };
        if (driver is not null)
        {
            command.Subcommands.Add(List(driver));
            command.Subcommands.Add(Rank(driver));
            command.Subcommands.Add(Prune(driver));
            command.Subcommands.Add(Install());
            command.Subcommands.Add(Uninstall());
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

    private Command Prune(ChromeTabsDriver chrome)
    {
        var prune = new Command(
            "prune",
            "Close merged, duplicate and superseded GitHub PR tabs, and duplicate pages, per profile across windows. [Windows/macOS only]\n\n"
            + "Tabs close one window at a time in screen order, and each window's active tab is put back before the next window starts.");
        var dryRun = new Option<bool>("--dry-run") { Description = "Print the plan grouped by reason; close nothing." };
        var profile = Profile();
        var github = new Option<bool>("--github") { Description = "Query GitHub for PR state, chains, size, review and checks (the default)." };
        var noGithub = new Option<bool>("--no-github") { Description = "Skip the PR rules and every GitHub call; GitHub pages are still de-duplicated by title." };
        var sites = new Option<bool>("--sites") { Description = "Close duplicate non-PR pages by per-site title rules (the default)." };
        var noSites = new Option<bool>("--no-sites") { Description = "Leave duplicate non-PR pages alone." };
        var maxMemory = new Option<double?>("--max-memory")
        {
            Description = "GB budget for the memory Chrome reports per tab. After the other rules, close the lowest-ranked "
                + "tabs (biggest first) until the rest fit. Calls, audible, pinned and active tabs are never closed.",
        };
        maxMemory.Validators.Add(result =>
        {
            if (result.GetValueOrDefault<double?>() is < 0.1)
            {
                result.AddError("--max-memory must be at least 0.1.");
            }
        });
        var unattended = new Option<bool>("--unattended")
        {
            Description = "For scheduled runs: close nothing while a call is live, and hold back every window's active tab "
                + "and everything in the foreground window.",
        };
        foreach (var option in new Option[] { dryRun, profile, github, noGithub, sites, noSites, maxMemory, unattended })
        {
            prune.Options.Add(option);
        }

        prune.SetAction((parsed, cancellationToken) => PruneTabs(
            chrome,
            new PruneOptions(
                parsed.GetValue(dryRun),
                parsed.GetValue(profile),
                !parsed.GetValue(noGithub),
                !parsed.GetValue(noSites),
                parsed.GetValue(maxMemory),
                parsed.GetValue(unattended)),
            cancellationToken));
        return prune;
    }

    private static Option<string?> Profile() =>
        new("--profile") { Description = "Only this profile: directory ('Profile 4') or display name." };

    private Command Install()
    {
        var install = new Command(
            "install",
            "Put a `chrome-tabs` command on PATH that runs the installed runtime copy. [Windows only]\n\n"
            + "Install the copy with `desktop-manager runtime install`; scheduled organise runs come from `jobs install`.");
        install.SetAction(_ => WriteShim());
        return install;
    }

    private Command Uninstall()
    {
        var uninstall = new Command("uninstall", "Remove the `chrome-tabs` command, and the snapshot environment older installs built. [Windows only]");
        uninstall.SetAction(_ => RemoveShim());
        return uninstall;
    }

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

    private int WriteShim()
    {
        var target = ChromeInstall.RuntimeDesktopManager(environment, home);
        var shim = ChromeInstall.ShimPath(home);
        _ = System.IO.Directory.CreateDirectory(Path.GetDirectoryName(shim)!);
        File.WriteAllText(shim, ChromeInstall.RenderShim(target));
        Say($"Command: {shim} -> {target}");
        if (!File.Exists(target))
        {
            Say("No runtime copy yet: run `desktop-manager runtime install`.");
        }

        return 0;
    }

    private int RemoveShim()
    {
        var root = ChromeInstall.Root(environment, home);
        var shim = ChromeInstall.ShimPath(home);
        if (File.Exists(shim))
        {
            File.Delete(shim);
        }

        try
        {
            System.IO.Directory.Delete(Path.Join(root, "venv"), recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // As shutil.rmtree(ignore_errors=True): a missing or locked snapshot environment is left alone.
        }

        Say($"Removed the command; kept logs in {root}");
        return 0;
    }

    private async Task<int> PruneTabs(ChromeTabsDriver chrome, PruneOptions options, CancellationToken cancellationToken)
    {
        var started = time.GetTimestamp();
        var session = chrome.Open();
        var windows = FilterProfile(session.ReadWindows(), options.Profile);
        if (options.Unattended && !options.DryRun && Unattended.LiveCall(session.Presence, windows) is { } live)
        {
            Say($"Skipped: {live}; left everything for the next run");
            return 0;
        }

        var enumerated = time.GetTimestamp();
        var resolution = options.GitHub
            ? await PrResolver.ResolveAsync(chrome.GitHub, PrPrune.PrTabs(windows).Select(item => item.Pr).Distinct(), cancellationToken: cancellationToken).ConfigureAwait(false)
            : Resolution.Empty;
        var resolved = time.GetTimestamp();

        IReadOnlyList<IClosePlan> plans = ClosePlansFor(windows, resolution, options);
        var planned = time.GetTimestamp();
        var repos = resolution.States.Keys.Select(pr => pr.Slug).Distinct().Count();
        var timings = string.Create(
            CultureInfo.InvariantCulture,
            $"enumerate {time.GetElapsedTime(started, enumerated).TotalSeconds:F2}s, resolve {time.GetElapsedTime(enumerated, resolved).TotalSeconds:F2}s "
            + $"({repos} repos), plan {time.GetElapsedTime(resolved, planned).TotalSeconds:F3}s");

        if (options.Unattended)
        {
            var runnable = ClosePlans.HoldUnattended(plans, session.Foreground());
            plans = [runnable];
            if (runnable.Held.Count > 0)
            {
                Say($"Held back: active tab or foreground window ({runnable.Held.Count.ToString(CultureInfo.InvariantCulture)})");
                SayAll(runnable.Held.Select(close => TabPlanText.PlanLine(close.Item, "left for an attended run")));
            }
        }

        if (options.DryRun || !plans.Any(plan => plan.Closes.Count > 0))
        {
            var prefix = options.DryRun ? "Dry run: nothing closed. " : "";
            Say(string.Create(CultureInfo.InvariantCulture, $"{prefix}{timings}, total {time.GetElapsedTime(started, planned).TotalSeconds:F2}s"));
            return 0;
        }

        var order = ChromeWindow.ScreenOrder(windows, session.Desktops.Current()).Select(window => window.Handle).ToList();
        var start = session.Desktops.Current();
        IReadOnlyList<AppliedClose> applied;
        try
        {
            applied = ClosePlans.Apply(plans, windows, session.Actions, order);
        }
        finally
        {
            session.Desktops.ReturnTo(start);
        }

        var failures = applied.Where(result => !result.Closed).ToList();
        foreach (var failure in failures)
        {
            Say($"Not closed {failure.Planned.Item.Label}: {failure.Detail}");
        }

        var perClose = applied.Sum(result => result.Elapsed.TotalSeconds) / applied.Count;
        Say(string.Create(
            CultureInfo.InvariantCulture,
            $"Closed {applied.Count - failures.Count}/{applied.Count} {timings}, {perClose:F2}s per close, total {time.GetElapsedTime(started).TotalSeconds:F2}s"));
        return failures.Count > 0 ? 1 : 0;
    }

    /// <summary>Every close plan <c>prune</c> runs, each printed as it is made. Ported from <c>_make_close_plans</c>.</summary>
    private List<IClosePlan> ClosePlansFor(IReadOnlyList<ChromeWindow> windows, Resolution resolution, PruneOptions options)
    {
        var plans = new List<IClosePlan>();
        var held = new HashSet<RuntimeId>();
        if (options.GitHub)
        {
            var prPlan = PrPrune.Plan(windows, resolution);
            plans.Add(prPlan);
            held.UnionWith(prPlan.Reports.Select(report => report.Item.Tab.RuntimeId));
            foreach (var (slug, error) in resolution.FailedRepos.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                Say($"{slug}: open PRs unreadable, chain rules skipped: {error}");
            }

            SayAll(TabPlanText.Prune(prPlan));
        }

        if (options.Sites)
        {
            var sitePlan = Dedupe.Plan(windows);
            plans.Add(sitePlan);
            held.UnionWith(sitePlan.Reports.Select(report => report.Item.Tab.RuntimeId));
            SayAll(TabPlanText.Dedupe(sitePlan));
        }

        if (options.MaxMemoryGb is { } budget)
        {
            var closing = plans.SelectMany(plan => plan.Closes).Select(close => close.Item.Tab.RuntimeId).ToHashSet();
            var memoryPlan = TabRank.PlanMemory(TabRank.Rank(windows, resolution), budget * 1024, closing, held);
            plans.Add(memoryPlan);
            SayAll(TabPlanText.Memory(memoryPlan));
        }

        return plans;
    }

    private void SayAll(IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            Say(line);
        }
    }

    private int ListTabs(ChromeTabsDriver chrome, string? profile, bool prsOnly, bool json)
    {
        var started = time.GetTimestamp();
        var windows = FilterProfile(chrome.Open().ReadWindows(), profile);
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
        var windows = FilterProfile(chrome.Open().ReadWindows(), profile);
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

/// <summary><c>chrome-tabs prune</c>'s flags.</summary>
/// <param name="DryRun">Print the plans; close nothing.</param>
/// <param name="Profile">Only this profile.</param>
/// <param name="GitHub">Run the PR rules, asking GitHub.</param>
/// <param name="Sites">Close duplicate non-PR pages.</param>
/// <param name="MaxMemoryGb">The memory budget, if any.</param>
/// <param name="Unattended">A scheduled run: leave calls, active tabs and the foreground window alone.</param>
internal sealed record PruneOptions(bool DryRun, string? Profile, bool GitHub, bool Sites, double? MaxMemoryGb, bool Unattended);
