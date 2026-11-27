#pragma warning disable RS0030 // Legacy System.CommandLine awaiting DotMake migration (Issue #234)
using System.CommandLine;
using System.Globalization;
using System.Text.Json.Nodes;
using Rig.Core.Chrome;
using Rig.Core.GitHub;
using Rig.Core.Jobs;
using Rig.Core.Otel;
using Rig.Core.Text;

namespace Rig.Cli.Chrome;

/// <summary>
/// <c>rig chrome-tabs</c>: <c>traces</c> runs everywhere, and <c>organise</c>
/// is registered everywhere because the scheduled job runs it on every platform, refusing where there is no driver;
/// <c>list</c>, <c>rank</c>, <c>prune</c>, <c>group</c>, <c>saved-groups</c>, <c>install</c> and <c>uninstall</c> only where
/// there is one. Every message goes to stdout.
/// </summary>
/// <param name="environment">Reads an environment variable (<c>LOCALAPPDATA</c> places the traces).</param>
/// <param name="home">The user's home, where the traces live when <c>LOCALAPPDATA</c> is unset.</param>
/// <param name="time">The clock that picks today's day file.</param>
/// <param name="zone">The zone run start times are shown in.</param>
/// <param name="output">Where every line goes.</param>
/// <param name="driver">Opens Chrome for the commands that read it; null where there is no driver.</param>
/// <param name="windows">Whether this is the Windows composition, which alone has the PATH shim to install.</param>
/// <param name="notes">Writes and opens the note an interrupted unattended run leaves; <see cref="ChromeNotes.Default"/> when null.</param>
/// <param name="error">Where a span skipped as malformed is reported; the process's standard error when null.</param>
/// <param name="extension">Builds <c>extension</c>, the Linux driver's install and host; null elsewhere.</param>
internal sealed class ChromeTabsCommand(
    Func<string, string?> environment,
    string home,
    TimeProvider time,
    TimeZoneInfo zone,
    TextWriter output,
    ChromeTabsDriver? driver = null,
    bool windows = true,
    ChromeNotes? notes = null,
    TextWriter? error = null,
    Func<Command>? extension = null)
{
    /// <summary>The group's name, shared with the forwarding row.</summary>
    public const string Name = "chrome-tabs";

    /// <summary>The group's help.</summary>
    public const string Description =
        "Chrome tabs via UI Automation, or rig's extension on Linux: list them, prune GitHub PR tabs and duplicate pages, group by repo, or organise (both, window by window).";

    /// <summary><c>traces show</c> exits with this when no run matches.</summary>
    public const int NoRunExit = 1;

    /// <summary>The trigger the scheduled job passes: it alone prunes old trace files.</summary>
    public const string ScheduledTrigger = "scheduled";

    /// <summary>
    /// How long a scheduled run goes before it writes the spans still running: two minutes short of the job's limit,
    /// so a run killed there still shows in <c>traces runs</c>, marked incomplete.
    /// </summary>
    public static readonly TimeSpan LastWordsAfter = TimeSpan.FromMinutes(JobRegistry.ChromeOrganiseLimitMinutes - 2);

    /// <summary>
    /// The <c>chrome-tabs</c> command. <c>list</c>, <c>rank</c>, <c>prune</c>, <c>group</c>, <c>saved-groups</c>, <c>install</c> and
    /// <c>uninstall</c> are only registered with a driver (Windows or macOS).
    /// </summary>
    public Command Build()
    {
        var command = new Command(Name, Description) { Traces(), Organise() };
        if (driver is not null)
        {
            command.Subcommands.Add(List(driver));
            command.Subcommands.Add(Rank(driver));
            command.Subcommands.Add(Prune(driver));
            command.Subcommands.Add(Group(driver));
            if (windows)
            {
                command.Subcommands.Add(SavedGroupsCommand(driver));
                command.Subcommands.Add(Install());
                command.Subcommands.Add(Uninstall());
            }
        }

        if (extension is not null)
        {
            command.Subcommands.Add(extension());
        }

        return command;
    }

    private Command List(ChromeTabsDriver chrome)
    {
        var list = new Command("list", "List every Chrome window's tabs on every desktop (flags: Selected Pinned Loaded Audible). [Windows/macOS/Linux]");
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
        var rank = new Command("rank", "Rank every tab by keep priority, with the reasons: protected first, the first to close last. [Windows/macOS/Linux]");
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
            "Close merged, duplicate and superseded GitHub PR tabs, and duplicate pages, per profile across windows. [Windows/macOS/Linux]\n\n"
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

    private Command Group(ChromeTabsDriver chrome)
    {
        var group = new Command(
            "group",
            "Gather GitHub tabs into one group per repo, all in the window holding the most GitHub tabs, and Google, Asana, "
            + "YouTrack and Claude tabs into one group per site. [Windows/macOS/Linux]\n\n"
            + "Chrome only groups through the tab's right-click menu, so this brings each window forward in turn and uses the "
            + "mouse for a moment per tab: leave the mouse and keyboard alone until it finishes. A window on another desktop is "
            + "brought onto this one while it is worked on, then sent back. Call tabs never move. "
            + "On Linux the extension groups through Chrome's own API: no window comes forward and the mouse is never used.");
        var dryRun = new Option<bool>("--dry-run") { Description = "Print the plan; move nothing." };
        var profile = Profile();
        var minSize = AtLeastOne(new Option<int>("--min-size")
        {
            Description = "Fewest tabs of one repo or site that start a new group.",
            DefaultValueFactory = _ => 2,
        });
        var thisDesktop = new Option<bool>("--this-desktop") { Description = "Group only windows on the current desktop; never bring others over." };
        foreach (var option in new Option[] { dryRun, profile, minSize, thisDesktop })
        {
            group.Options.Add(option);
        }

        group.SetAction(parsed => GroupTabs(
            chrome, parsed.GetValue(dryRun), parsed.GetValue(profile), parsed.GetValue(minSize), parsed.GetValue(thisDesktop)));
        return group;
    }

    private Command Organise()
    {
        var organise = new Command(
            "organise",
            "Prune, then group, one window at a time: the whole tidy in one command. [Windows/macOS/Linux]\n\n"
            + "Windows go in screen order: the current desktop first, then monitor by monitor, top to bottom. Each window has "
            + "its tabs closed and its active tab put back before the next starts. Then grouping (as `group`) runs window by "
            + "window over what is left; it brings each window forward and uses the mouse.\n\n"
            + "`--consolidate-saved` then deletes closed duplicate saved groups, but only when grouping created a new group: "
            + "joining an open group never saves a duplicate. A consolidation that failed or deleted anything is looked at "
            + "again next run, since a delete can bring chips out of the overflow.\n\n"
            + "`--unattended` is what the scheduled job runs. It does nothing at all while a call is live: a tab recording or "
            + "sharing the screen, audio in a meeting room (not a call site's home page), or Chrome holding the microphone or camera. A tab whose audio is "
            + "unknown never counts; an open meeting page of that kind holds back grouping only. Grouping then waits, for "
            + "up to `--wait-minutes`, until the machine has been idle for `--idle-minutes` and is not locked or on a call. "
            + "Grouping stops at the first key press or mouse move, opens a note, and goes again once the machine has been "
            + "left alone for 30 seconds. It waits up to 5 minutes for that, at most twice, then leaves the rest to the next run.\n\n"
            + "Every step that brings windows forward puts you back on the virtual desktop you were on when it started.\n\n"
            + "Every run is traced to the `chrome-tabs traces` directory; see `traces runs`.");
        var dryRun = new Option<bool>("--dry-run") { Description = "Print both plans; change nothing." };
        var profile = Profile();
        var github = new Option<bool>("--github") { Description = "Query GitHub for PR state, chains, size, review and checks (the default)." };
        var noGithub = new Option<bool>("--no-github") { Description = "Skip the PR rules and every GitHub call; GitHub pages are still de-duplicated by title." };
        var sites = new Option<bool>("--sites") { Description = "Close duplicate non-PR pages by per-site title rules (the default)." };
        var noSites = new Option<bool>("--no-sites") { Description = "Leave duplicate non-PR pages alone." };
        var minSize = AtLeastOne(new Option<int>("--min-size")
        {
            Description = "Fewest tabs of one repo or site that start a new group.",
            DefaultValueFactory = _ => 2,
        });
        var unattended = new Option<bool>("--unattended")
        {
            Description = "For the schedule: prune as `prune --unattended`, and group only while nobody is using the machine.",
        };
        var idleMinutes = NotNegative(new Option<double>("--idle-minutes")
        {
            Description = "With --unattended: minutes without input before grouping may start. Keep it under the machine's "
                + "auto-lock: a locked session cannot be grouped.",
            DefaultValueFactory = _ => 1,
        });
        var waitMinutes = NotNegative(new Option<double>("--wait-minutes")
        {
            Description = "With --unattended: how long to keep waiting for the machine to go idle before leaving grouping "
                + "to the next run. 0 gives up at once.",
            DefaultValueFactory = _ => ChromeInstall.DefaultWaitMinutes,
        });
        var thisDesktop = new Option<bool>("--this-desktop") { Description = "Group only windows on the current desktop; never bring others over." };
        var consolidate = new Option<bool>("--consolidate-saved")
        {
            Description = "When grouping created a new group, afterwards delete closed duplicate saved groups, as `saved-groups --consolidate`.",
        };
        var trigger = new Option<string>("--trigger") { Description = "What started the run, for its trace.", DefaultValueFactory = _ => "manual", Hidden = true };
        foreach (var option in new Option[] { dryRun, profile, github, noGithub, sites, noSites, minSize, unattended, idleMinutes, waitMinutes, thisDesktop, consolidate, trigger })
        {
            organise.Options.Add(option);
        }

        organise.SetAction((parsed, cancellationToken) => OrganiseTabs(
            new OrganiseOptions(
                parsed.GetValue(dryRun),
                parsed.GetValue(profile),
                !parsed.GetValue(noGithub),
                !parsed.GetValue(noSites),
                parsed.GetValue(minSize),
                parsed.GetValue(unattended),
                parsed.GetValue(idleMinutes),
                parsed.GetValue(waitMinutes),
                parsed.GetValue(thisDesktop),
                parsed.GetValue(consolidate),
                parsed.GetValue(trigger) ?? "manual"),
            cancellationToken));
        return organise;
    }

    private Command SavedGroupsCommand(ChromeTabsDriver chrome)
    {
        var saved = new Command(
            "saved-groups",
            "List Chrome's saved tab groups, and plan (or with --consolidate, make) one per title. [Windows only]\n\n"
            + "Chrome saves every group, and a group created with an old group's title saves a new copy beside the closed one, so "
            + "the bookmarks bar fills with closed duplicates. This deletes a closed copy only when the same title is open, or "
            + "another closed copy of it stays: never an open group, never the last copy of a title. Deleting a saved group "
            + "forgets the tabs it held; they are printed as each one goes.\n\n"
            + "Reads the bookmarks bar, so a profile with the bar hidden shows nothing, and chips that only fit in the bar's "
            + "overflow are not seen until deletes make room. Needs no focus and leaves the mouse alone.");
        var consolidate = new Option<bool>("--consolidate") { Description = "Delete the closed duplicates the plan lists; otherwise only look." };
        var profile = Profile();
        saved.Options.Add(consolidate);
        saved.Options.Add(profile);
        saved.SetAction(parsed => ConsolidateSaved(chrome, parsed.GetValue(consolidate), parsed.GetValue(profile)));
        return saved;
    }

    private static Option<string?> Profile() =>
        new("--profile") { Description = "Only this profile: directory ('Profile 4') or display name." };

    private Command Install()
    {
        var install = new Command(
            "install",
            "Put a `chrome-tabs` command on PATH that runs the installed runtime copy. [Windows only]\n\n"
            + "Install the copy with `rig runtime install`; scheduled organise runs come from `jobs install`.");
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
        new("--dir") { Description = "Traces directory. Default: %LOCALAPPDATA%/rig/chrome-tabs/traces." };

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

    private static Option<double> NotNegative(Option<double> option)
    {
        option.Validators.Add(result =>
        {
            if (result.GetValueOrDefault<double>() < 0)
            {
                result.AddError(string.Create(CultureInfo.InvariantCulture, $"Invalid value for '{option.Name}': {result.GetValueOrDefault<double>()} is not in the range x>=0."));
            }
        });
        return option;
    }

    private DateOnly Today => DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);

    private string Directory(string? chosen) => chosen ?? ChromeTraces.TracesDir(environment, home);

    private IReadOnlyList<TraceRun> Load(string? directory, int days) =>
        ChromeTraces.Runs(ChromeTraces.LoadSpans(Directory(directory), Today, days, Skipped));

    private void Skipped(string problem) => (error ?? Console.Error).Write($"rig: skipped {problem}\n");

    private int Runs(int days, int limit, bool json, string? directory)
    {
        var all = Load(directory, days);
        var found = all.Skip(Math.Max(all.Count - limit, 0)).ToList();
        if (json)
        {
            var rows = new JsonArray([.. found.AsEnumerable().Reverse().Select(run => (JsonNode)ChromeTraces.RunJson(run, zone))]);
            Say(JsonOutput.Format(rows, keepNonAscii: true));
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

            Say(JsonOutput.Format(new JsonObject
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
        var target = ChromeInstall.RuntimeRig(environment, home);
        var shim = ChromeInstall.ShimPath(home);
        _ = System.IO.Directory.CreateDirectory(Path.GetDirectoryName(shim)!);
        File.WriteAllText(shim, ChromeInstall.RenderShim(target));
        Say($"Command: {shim} -> {target}");
        if (!File.Exists(target))
        {
            Say("No runtime copy yet: run `rig runtime install`.");
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

        IReadOnlyList<IClosePlan> plans = ChromeClosePlans.Plan(
            windows, resolution, new ClosePlanOptions(options.GitHub, options.Sites, options.MaxMemoryGb), Say);
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

    private async Task<int> OrganiseTabs(OrganiseOptions options, CancellationToken cancellationToken)
    {
        if (driver is null)
        {
            Say("organise is only available on Windows/macOS.");
            return 1;
        }

        // The run's spans go to the chrome-tabs traces folder, under the scope `traces` reads; the `rig` root span that
        // parents them stays in rig's own file.
        var directory = ChromeTraces.TracesDir(environment, home);
        using var traces = new TraceFileWriter(
            Name, new Dictionary<string, string> { [OrganiseRunner.SourceName] = ChromeTraces.Scope }, environment, directory, time);
        traces.SetDirectory(directory);
        traces.FlushPeriodically(TimeSpan.FromSeconds(ChromeTraces.FlushIntervalSeconds));
        using var lastWords = options.Trigger == ScheduledTrigger
            ? time.CreateTimer(_ => traces.Flush(includeOpen: true), null, LastWordsAfter, Timeout.InfiniteTimeSpan)
            : null;
        if (options.Trigger == ScheduledTrigger && ChromeTraces.PruneDaily(directory, Today) is { Count: > 0 } pruned)
        {
            Say(string.Create(CultureInfo.InvariantCulture, $"Pruned {pruned.Count} trace files older than {ChromeTraces.KeepDays} days"));
        }

        var note = (notes ?? ChromeNotes.Default).Once();
        var runner = new OrganiseRunner(
            driver.Open(),
            driver.GitHub,
            driver.Owners,
            SavedConsolidation.MarkerPath(environment, home),
            Say,
            () => note(UnattendedGrouping.PausedNote, UnattendedGrouping.PausedNoteLines),
            seconds =>
            {
                _ = cancellationToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(seconds));
                cancellationToken.ThrowIfCancellationRequested();
            },
            () => time.GetTimestamp() / (double)time.TimestampFrequency,
            time);
        var result = await runner.RunAsync(options, cancellationToken).ConfigureAwait(false);
        return result.Failed ? 1 : 0;
    }

    private int GroupTabs(ChromeTabsDriver chrome, bool dryRun, string? profile, int minSize, bool thisDesktop)
    {
        var started = time.GetTimestamp();
        var session = chrome.Open();
        var windows = FilterProfile(session.ReadWindows(), profile);
        var current = session.Desktops.Current();
        var plan = GroupRunner.Plan(ChromeWindow.ScreenOrder(windows, current), current, minSize, thisDesktop, session.HostsByTitle, chrome.Owners());
        SayAll(TabPlanText.Groups(plan));
        if (dryRun || plan.Moves.Count == 0)
        {
            var prefix = dryRun ? "Dry run: nothing moved. " : "";
            Say(string.Create(CultureInfo.InvariantCulture, $"{prefix}planned in {time.GetElapsedTime(started).TotalSeconds:F2}s"));
            return 0;
        }

        var ran = new GroupRunner(session.Grouping, session.Desktops, session.Presence.SessionLocked).Run(plan);
        if (ran.Stopped is { } error)
        {
            Say(string.Create(CultureInfo.InvariantCulture, $"Stopped {error}. {ran.Moved}/{plan.Moves.Count} moved."));
            return 1;
        }

        Say(string.Create(CultureInfo.InvariantCulture, $"Moved {ran.Moved}/{plan.Moves.Count} in {time.GetElapsedTime(started).TotalSeconds:F2}s"));
        return 0;
    }

    private int ConsolidateSaved(ChromeTabsDriver chrome, bool consolidate, string? profile)
    {
        var started = time.GetTimestamp();
        var session = chrome.Open();
        var consolidation = new SavedConsolidation(session.Saved, session.Desktops, Say);
        var planned = consolidation.Plan(FilterProfile(session.ReadWindows(), profile));
        var plan = planned.Plan;
        SayAll(TabPlanText.SavedGroups(plan));
        if (!consolidate || plan.Deletes.Count == 0)
        {
            var prefix = consolidate ? "" : "Looked only: pass --consolidate to delete. ";
            Say(string.Create(CultureInfo.InvariantCulture, $"{prefix}read in {time.GetElapsedTime(started).TotalSeconds:F2}s"));
            return 0;
        }

        var done = consolidation.Consolidate(planned);
        if (done.Stopped is { } error)
        {
            Say(string.Create(CultureInfo.InvariantCulture, $"Stopped {error}. {done.Deleted}/{plan.Total} deleted."));
            return 1;
        }

        Say(string.Create(CultureInfo.InvariantCulture, $"Deleted {done.Deleted}/{plan.Total} in {time.GetElapsedTime(started).TotalSeconds:F2}s"));
        return 0;
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
            Say(JsonOutput.Format(
                new JsonArray([.. windows.Select(window => (JsonNode)WindowJson(window, [.. window.Tabs.Where(tab => !prsOnly || PrPrune.RefFromTitle(tab.Title) is not null)]))]),
                keepNonAscii: true));
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
            Say(JsonOutput.Format(new JsonArray([.. shown.Select(item => (JsonNode)RankedJson(item))]), keepNonAscii: true));
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
    internal static IReadOnlyList<ChromeWindow> FilterProfile(IReadOnlyList<ChromeWindow> windows, string? profile) =>
        ChromeReads.FilterProfile(windows, profile);

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

    /// <summary>A window as <c>list --json</c> prints it: the fields in order, <c>rect</c> and <c>monitor</c> as arrays.</summary>
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

