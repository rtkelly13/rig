using System.CommandLine;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Rig.Cli.Vault;
using Rig.Core.Estate;
using Rig.Core.Processes;
using Rig.Core.Roots;
using Rig.Core.Workspace;

namespace Rig.Cli.Estate;

/// <summary>What <c>estate wrapup</c> needs beyond the estate: the rig an asked agent runs, the background follower, the clock and ask ids.</summary>
/// <param name="RigExecutable">The <c>rig</c> the prompt tells the agent to run <c>report</c> with.</param>
/// <param name="Follower">Starts and locks the background follower <c>--apply</c> hands the asking to.</param>
/// <param name="Clock">Times the rolling asks.</param>
/// <param name="NewAskId">Mints the id each sent ask carries.</param>
internal sealed record WrapupHost(string RigExecutable, IWrapupFollower Follower, IWrapupClock Clock, Func<string> NewAskId)
{
    /// <summary>A host that starts no follower and runs <c>rig</c> from PATH: what tests get unless they say otherwise.</summary>
    public static WrapupHost Inert(TimeProvider time) => new("rig", new NoWrapupFollower(), new SystemWrapupClock(time), WrapupStamps.NewAskId);
}

/// <summary>
/// <c>rig estate wrapup</c>: asks idle Orca Claude sessions whether their change is finished, and <c>report</c>, how they answer.
/// Ported from the Python <c>estate/wrapup_cli.py</c>; the decisions are <see cref="Wrapup"/>'s and <see cref="WrapupRun"/>'s.
/// </summary>
/// <param name="host">The estate's folders, the prompt template's home and the working directory.</param>
/// <param name="git">Reads each worktree and writes its state file.</param>
/// <param name="proc">Runs <c>orca</c>.</param>
/// <param name="time">The clock idleness is measured by.</param>
/// <param name="wrapup">The agent's rig, the follower, the rolling clock and ask ids.</param>
/// <param name="output">Where the rows go.</param>
/// <param name="error">Where refusals and progress go, so <c>--json</c> stays clean.</param>
internal sealed partial class WrapupCommand(EstateHost host, Git git, IProc proc, TimeProvider time, WrapupHost wrapup, TextWriter output, TextWriter error)
{
    /// <summary>Exit code when Orca cannot be reached.</summary>
    public const int OrcaUnavailableExit = 3;

    private static readonly JsonSerializerOptions Compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static readonly Dictionary<string, long> UnitSeconds = new(StringComparer.Ordinal) { ["s"] = 1, ["m"] = 60, [""] = 60, ["h"] = 3600, ["d"] = 86400 };

    private readonly FileWrapupStore store = new(git);

    /// <summary>The <c>wrapup</c> subcommand with its <c>report</c>.</summary>
    public Command Build()
    {
        var personal = new Option<bool>("--personal") { Description = "Personal and org repos (personal/, rtkelly-labs/, Resultful/)" };
        var work = new Option<bool>("--work") { Description = "Work repos: the work_dirs in ~/.workspace/config.json" };
        var repo = new Option<string[]>("--repo", "-r") { Description = "Orca repo name; repeatable" };
        var all = new Option<bool>("--all") { Description = "Every repo in the chosen scope" };
        var worktree = new Option<string[]>("--worktree", "-w") { Description = "Worktree name, folder, branch or path; repeatable. Narrows --repo/--all" };
        var children = new Option<bool>("--children") { Description = "Include child worktrees (Orca subtasks), across repos" };
        var idle = new Option<string>("--idle") { Description = "Ask only sessions idle at least this long (30m, 2h)", DefaultValueFactory = _ => "30m" };
        var max = new Option<int>("--max")
        {
            Description = "Most sessions asked at once. With --wait 0, most asked in one run, and the rest show as deferred",
            DefaultValueFactory = _ => Wrapup.DefaultMaxSends,
        };
        max.Validators.Add(result =>
        {
            if (result.GetValueOrDefault<int>() is var value && value < 0)
            {
                result.AddError($"Invalid value for '--max': {value} is not in the range x>=0.");
            }
        });
        var wait = new Option<string>("--wait")
        {
            Description = "With --apply, how long each asked session gets to answer before the next is asked anyway (0: no waiting)",
            DefaultValueFactory = _ => "10m",
        };
        var includeSettled = new Option<bool>("--include-settled") { Description = "Also ask worktrees the git and PR facts already show as finished" };
        var template = new Option<string?>("--template") { Description = "Prompt template (default config/orca-wrapup.md)" };
        var showPrompt = new Option<bool>("--show-prompt") { Description = "Print the prompt each asked session gets" };
        var apply = new Option<bool>("--apply") { Description = "Send the prompts (default previews)" };
        var follow = new Option<bool>("--follow") { Description = "With --apply, ask in this terminal instead of starting a background follower" };
        var json = new Option<bool>("--json") { Description = "Emit rows as JSON" };
        var command = new Command(
            "wrapup",
            "Ask idle Orca Claude sessions whether their change is finished, then report each verdict.\n\n"
            + "The prompt asks each agent to run `rig estate wrapup report` when it has checked what it needs, which records its verdict "
            + "in the worktree's temp/.rig/wrapup.json. --apply starts a background follower that asks --max sessions at a time: as each "
            + "one answers, starts waiting on you, or has had --wait, the next is asked, subtasks before their parents. Run again without "
            + "--apply to read the answers. --follow does the asking in this terminal instead.\n\n"
            + "Clean, pushed, merged worktrees count as settled and are not asked. Busy sessions, sessions waiting on a question and "
            + "sub-agent panes are never sent anything.")
        {
            personal, work, repo, all, worktree, children, idle, max, wait, includeSettled, template, showPrompt, apply, follow, json,
        };
        command.SetAction((parsed, cancellationToken) => RunAsync(
            new(
                parsed.GetValue(personal),
                parsed.GetValue(work),
                parsed.GetValue(repo) ?? [],
                parsed.GetValue(all),
                parsed.GetValue(worktree) ?? [],
                parsed.GetValue(children),
                parsed.GetValue(idle) ?? "30m",
                parsed.GetValue(max),
                parsed.GetValue(wait) ?? "10m",
                parsed.GetValue(includeSettled),
                parsed.GetValue(template),
                parsed.GetValue(showPrompt),
                parsed.GetValue(apply),
                parsed.GetValue(follow),
                parsed.GetValue(json)),
            cancellationToken));
        command.Subcommands.Add(Report());
        return command;
    }

    /// <summary><c>30m</c>, <c>2h</c>, <c>45s</c>, <c>1d</c>; a bare number is minutes. Null when it is none of those.</summary>
    internal static TimeSpan? ParseDuration(string text)
    {
        var found = DurationPattern().Match(text ?? "");
        return found.Success && long.TryParse(found.Groups["n"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n)
            ? TimeSpan.FromSeconds(n * UnitSeconds[found.Groups["unit"].Value])
            : null;
    }

    /// <summary>The rows as <c>estate wrapup --json</c> writes them: one compact line.</summary>
    internal static string Dump(IReadOnlyList<WrapupRow> rows) =>
        new JsonArray([.. rows.Select(r => (JsonNode)new JsonObject
        {
            ["repo"] = r.Repo,
            ["worktree"] = r.Worktree,
            ["path"] = r.Path.Replace('\\', '/'),
            ["branch"] = r.Branch,
            ["depth"] = r.Depth,
            ["session"] = r.Session,
            ["state"] = r.State,
            ["status"] = r.Status,
            ["idle_minutes"] = r.IdleMinutes,
            ["facts"] = Wrapup.DescribeFacts(r.Facts, r.Pr),
            ["settled"] = Wrapup.IsSettled(r.Facts, r.Pr),
            ["verdict"] = r.Verdict is null ? null : r.Verdict.Done ? "done" : "pending",
            ["verdict_detail"] = r.Verdict?.Detail ?? "",
            ["open_children"] = Wrapup.OpenChildren(r, rows),
            ["note"] = r.Note,
            ["asked_at"] = r.Asked?.At,
            ["recorded_verdict"] = r.Recorded?.Verdict,
            ["recorded_at"] = r.Recorded?.At,
        })]).ToJsonString(Compact);

    /// <summary>The table and its counts line, as the dry run, the applied run or the hand-off to a follower prints them.</summary>
    internal static string Render(IReadOnlyList<WrapupRow> rows, bool applied, bool background = false)
    {
        if (rows.Count == 0)
        {
            return "No matching Claude sessions in Orca.\n";
        }

        var mode = background ? "ASKING IN THE BACKGROUND" : applied ? "APPLIED" : "DRY-RUN (pass --apply to send)";
        var table = new TextTable(
            $"Orca wrap-up ({mode})",
            [("Repo / worktree", false), ("Session", false), ("Idle", true), ("Facts", false), ("Status", false), ("Answer / note", false)]);
        foreach (var row in rows)
        {
            var indent = new string(' ', 2 * row.Depth) + (row.Depth > 0 ? "└ " : "");
            List<string> answer = [];
            if (row.Verdict is not null)
            {
                answer.Add(row.Verdict.Done ? "done" : "pending" + (row.Verdict.Detail.Length > 0 ? $": {row.Verdict.Detail}" : ""));
            }

            if (row.Note.Length > 0)
            {
                answer.Add(row.Note);
            }

            if (row.Children.Count > 0)
            {
                answer.Add($"{Wrapup.OpenChildren(row, rows)} of the subtask sessions not finished");
            }

            table.Add(
                $"{indent}{row.Repo}/{row.Worktree}",
                row.Session,
                row.IdleMinutes is { } idle ? $"{idle}m" : "",
                Wrapup.DescribeFacts(row.Facts, row.Pr),
                row.Status,
                string.Join("; ", answer));
        }

        var counts = rows.GroupBy(r => r.Status, StringComparer.Ordinal).Select(g => $"{g.Count()} {g.Key}");
        return table.Render() + string.Join(" · ", counts) + "\n";
    }

    [GeneratedRegex(@"^\s*(?<n>\d+)\s*(?<unit>[smhd]?)\s*$")]
    private static partial Regex DurationPattern();

    private Command Report()
    {
        var ask = new Option<string>("--ask") { Description = "The ask id from the prompt. Default: the latest unanswered ask here", DefaultValueFactory = _ => "" };
        var done = new Option<bool>("--done") { Description = "The change has reached its natural end" };
        var pending = new Option<string?>("--pending") { Description = "Short summary of what is left" };
        var todo = new Option<string[]>("--todo") { Description = "One thing left for the owner; repeatable" };
        var report = new Command(
            "report",
            "Record your wrap-up verdict for the worktree you are in. Run by the asked agent, from its worktree.\n\n"
            + "Pass --done, or --pending \"<what's left>\" with one --todo per item. Items are PRs to open or merge, issues to file, "
            + "pushes to make, and decisions only the owner can make.")
        {
            ask, done, pending, todo,
        };
        report.SetAction((parsed, cancellationToken) => ReportAsync(
            parsed.GetValue(ask) ?? "", parsed.GetValue(done), parsed.GetValue(pending), parsed.GetValue(todo) ?? [], cancellationToken));
        return report;
    }

    private async Task<int> ReportAsync(string askId, bool done, string? pending, string[] todo, CancellationToken cancellationToken)
    {
        if (done == (pending is not null))
        {
            return await RefuseAsync("pass exactly one of --done or --pending TEXT", cancellationToken).ConfigureAwait(false);
        }

        if (pending is not null && pending.Trim().Length == 0 && todo.Length == 0)
        {
            return await RefuseAsync("--pending needs a summary of what is left, or at least one --todo", cancellationToken).ConfigureAwait(false);
        }

        var root = await WrapupRun.WorktreeRootAsync(git, host.WorkingDirectory).ConfigureAwait(false);
        if (root is null)
        {
            await error.WriteLineAsync("Not inside a git worktree: run this from the worktree you were asked about.".AsMemory(), cancellationToken).ConfigureAwait(false);
            return 1;
        }

        var result = await WrapupRun.ReportAsync(
            root,
            askId,
            done,
            pending ?? "",
            todo,
            time.GetUtcNow(),
            () => git.OutAsync(root, "rev-parse", "HEAD"),
            store,
            cancellationToken).ConfigureAwait(false);
        if (result.Answer is null)
        {
            await error.WriteLineAsync($"Not recorded: {result.Error}. Reply with the WRAPUP line from the prompt instead.".AsMemory(), cancellationToken).ConfigureAwait(false);
            return 1;
        }

        var said = done ? "done" : $"pending: {result.Answer.Detail}";
        await output.WriteLineAsync($"Recorded for {WrapupText.BaseName(root)}: {said}".AsMemory(), cancellationToken).ConfigureAwait(false);
        return 0;
    }

    private async Task<int> RefuseAsync(string message, CancellationToken cancellationToken)
    {
        await error.WriteLineAsync($"Invalid value: {message}".AsMemory(), cancellationToken).ConfigureAwait(false);
        return 2;
    }

    private async Task<int> RunAsync(Args args, CancellationToken cancellationToken)
    {
        if (args.Personal == args.Work)
        {
            return await RefuseAsync("pass exactly one of --personal or --work", cancellationToken).ConfigureAwait(false);
        }

        if (args.All && args.Repos.Length > 0)
        {
            return await RefuseAsync("--all and --repo are exclusive", cancellationToken).ConfigureAwait(false);
        }

        if (!args.All && args.Repos.Length == 0 && args.Worktrees.Length == 0)
        {
            return await RefuseAsync("name a target: --repo NAME (repeatable), --worktree NAME, or --all", cancellationToken).ConfigureAwait(false);
        }

        var target = new WrapupTarget(args.Personal ? WrapupScope.Personal : WrapupScope.Work, args.Repos, args.All, args.Worktrees, args.Children);
        if (ParseDuration(args.Wait) is not { } waitFor)
        {
            return await RefuseAsync($"{PyRepr(args.Wait)}: expected a duration such as 30m, 2h or 45s", cancellationToken).ConfigureAwait(false);
        }

        if (ParseDuration(args.Idle) is not { } idleFor)
        {
            return await RefuseAsync($"{PyRepr(args.Idle)}: expected a duration such as 30m, 2h or 45s", cancellationToken).ConfigureAwait(false);
        }

        var rolling = waitFor > TimeSpan.Zero;
        var options = new WrapupOptions(idleFor, rolling ? null : args.Max, args.IncludeSettled);
        var templateFile = args.Template is { Length: > 0 } given ? Path.GetFullPath(host.ExpandHome(given), host.WorkingDirectory) : Wrapup.TemplatePath(host.ManagedProjectsFile);
        string promptTemplate;
        try
        {
            promptTemplate = await File.ReadAllTextAsync(templateFile, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException)
        {
            await error.WriteLineAsync($"Cannot read the prompt template {templateFile.Replace('\\', '/')}: {exc.Message}".AsMemory(), cancellationToken).ConfigureAwait(false);
            return 1;
        }

        var orca = new WrapupOrca(proc);
        WrapupSnapshot snapshot;
        try
        {
            snapshot = await orca.ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (WrapupException exc)
        {
            await error.WriteLineAsync(exc.Message.AsMemory(), cancellationToken).ConfigureAwait(false);
            return OrcaUnavailableExit;
        }

        var now = time.GetUtcNow();
        var personalDirs = EstateScopes.Load(host.ScopesFile).RepoParentDirs(host.PersonalDir);
        var workDirs = MachineConfig.WorkDirs(host.MachineConfigFile, host.CodeRoot, host.Home);
        var placed = Wrapup.Select(snapshot, target, personalDirs, workDirs, host.Home, host.WorkingDirectory);
        var rows = await Wrapup.PlanAsync(
            placed, snapshot.Terminals, now, options, (path, token) => Wrapup.ReadGitFactsAsync(git, path, token), store, cancellationToken).ConfigureAwait(false);
        var byId = new Dictionary<string, WrapupWorktree>(StringComparer.Ordinal);
        foreach (var p in placed)
        {
            byId[p.Worktree.WorktreeId] = p.Worktree;
        }

        if (args.ShowPrompt)
        {
            foreach (var i in Wrapup.SendOrder(rows))
            {
                var row = rows[i];
                await output.WriteLineAsync($"── {row.Repo}/{row.Worktree}: {row.Session} ──".AsMemory(), cancellationToken).ConfigureAwait(false);
                await output.WriteLineAsync(Wrapup.RenderPrompt(promptTemplate, row, rows, byId, wrapup.RigExecutable).AsMemory(), cancellationToken).ConfigureAwait(false);
            }
        }

        // Verdicts are recorded on every run, asks only when sent: temp/.rig/ is bookkeeping, not the estate.
        if (args.Apply && rolling && !args.Follow)
        {
            rows = await WrapupRun.RecordAsync(rows, now, null, store, cancellationToken).ConfigureAwait(false);
            return await InBackgroundAsync(rows, args, templateFile, now, snapshot.Truncated, cancellationToken).ConfigureAwait(false);
        }

        if (args.Apply && rolling)
        {
            using var held = wrapup.Follower.TryHold();
            if (held is null)
            {
                await error.WriteLineAsync("Another wrap-up follower is asking sessions; this one stops.".AsMemory(), cancellationToken).ConfigureAwait(false);
                return 1;
            }

            rows = await WrapupRun.RecordAsync(rows, now, null, store, cancellationToken).ConfigureAwait(false);
            var rollingRun = new WrapupRolling(orca, store, wrapup.Clock, args.Max, waitFor, wrapup.NewAskId, wrapup.RigExecutable);
            rows = await WrapupRun.AskRollingAsync(
                rows, promptTemplate, byId, rollingRun, (row, message) => error.WriteLine($"{row.Repo}/{row.Worktree}: {message}"), cancellationToken).ConfigureAwait(false);
        }
        else
        {
            if (args.Apply)
            {
                rows = await WrapupRun.ApplySendsAsync(rows, promptTemplate, byId, orca, wrapup.NewAskId, wrapup.RigExecutable, cancellationToken).ConfigureAwait(false);
            }

            rows = await WrapupRun.RecordAsync(rows, now, args.Apply ? promptTemplate : null, store, cancellationToken).ConfigureAwait(false);
        }

        if (args.Json)
        {
            await output.WriteLineAsync(Dump(rows).AsMemory(), CancellationToken.None).ConfigureAwait(false);
        }
        else
        {
            await PrintAsync(rows, args.Apply, snapshot.Truncated, false, CancellationToken.None).ConfigureAwait(false);
            var asks = rows.Count(r => r.Status == WrapupStatus.Ask);
            if (rolling && !args.Apply && asks > 0)
            {
                await output.WriteLineAsync($"--apply asks these {asks}, {Math.Max(args.Max, 1)} at a time, waiting up to {args.Wait} each".AsMemory(), CancellationToken.None).ConfigureAwait(false);
            }
        }

        return rows.Any(r => r.Status == WrapupStatus.SendFailed) ? 1 : 0;
    }

    private async Task PrintAsync(IReadOnlyList<WrapupRow> rows, bool applied, bool truncated, bool background, CancellationToken cancellationToken)
    {
        await output.WriteAsync(Render(rows, applied, background).AsMemory(), cancellationToken).ConfigureAwait(false);
        if (rows.Count > 0 && truncated)
        {
            await error.WriteLineAsync("Orca returned only part of the worktree list; some sessions are missing.".AsMemory(), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Starts a follower for the asks, unless one is already running, and says where its log is.</summary>
    private async Task<int> InBackgroundAsync(IReadOnlyList<WrapupRow> rows, Args args, string templateFile, DateTimeOffset now, bool truncated, CancellationToken cancellationToken)
    {
        var asks = rows.Count(r => r.Status == WrapupStatus.Ask);
        if (args.Json)
        {
            await output.WriteLineAsync(Dump(rows).AsMemory(), cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await PrintAsync(rows, false, truncated, asks > 0, cancellationToken).ConfigureAwait(false);
        }

        if (asks == 0)
        {
            return 0;
        }

        if (wrapup.Follower.Running())
        {
            await error.WriteLineAsync(
                $"A wrap-up follower is already asking sessions; nothing new was started. Its log is in {wrapup.Follower.LogDir.Replace('\\', '/')}.".AsMemory(),
                cancellationToken).ConfigureAwait(false);
            return 1;
        }

        var log = await wrapup.Follower.StartAsync(FollowerArgs(args, templateFile), now, cancellationToken).ConfigureAwait(false);
        await error.WriteLineAsync(
            ($"Asking {asks} in the background, {Math.Max(args.Max, 1)} at a time, up to {args.Wait} each. Log: {log.Replace('\\', '/')}\n"
            + "Agents record their answers with `rig estate wrapup report`; run this again without --apply to read them.").AsMemory(),
            cancellationToken).ConfigureAwait(false);
        return 0;
    }

    /// <summary>The targeting flags, handed to the background follower unchanged.</summary>
    internal static IReadOnlyList<string> FollowerArgs(Args args, string templateFile)
    {
        List<string> argv = [args.Personal ? "--personal" : "--work"];
        argv.AddRange(args.Repos.SelectMany(r => new[] { "--repo", r }));
        if (args.All)
        {
            argv.Add("--all");
        }

        argv.AddRange(args.Worktrees.SelectMany(w => new[] { "--worktree", w }));
        if (args.Children)
        {
            argv.Add("--children");
        }

        argv.AddRange(["--idle", args.Idle, "--max", args.Max.ToString(CultureInfo.InvariantCulture), "--wait", args.Wait]);
        if (args.IncludeSettled)
        {
            argv.Add("--include-settled");
        }

        argv.AddRange(["--template", Path.GetFullPath(templateFile), "--apply"]);
        return argv;
    }

    // Python's repr of a str, which the duration error quotes.
    private static string PyRepr(string text) => text.Contains('\'', StringComparison.Ordinal) && !text.Contains('"', StringComparison.Ordinal) ? $"\"{text}\"" : $"'{text.Replace("'", "\\'", StringComparison.Ordinal)}'";

    /// <summary>The parsed flags of one run.</summary>
    internal sealed record Args(
        bool Personal,
        bool Work,
        string[] Repos,
        bool All,
        string[] Worktrees,
        bool Children,
        string Idle,
        int Max,
        string Wait,
        bool IncludeSettled,
        string? Template,
        bool ShowPrompt,
        bool Apply,
        bool Follow,
        bool Json);
}
