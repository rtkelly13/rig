using System.CommandLine;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Rig.Cli.Vault;
using Rig.Cli.Workspace;
using Rig.Core.Estate;
using Rig.Core.Orca;
using Rig.Core.Processes;
using Rig.Core.Roots;

namespace Rig.Cli.Estate;

/// <summary>
/// <c>rig estate orca-worktrees</c>: Orca's worktrees whose work is finished and, with <c>--apply</c>, their removal through
/// Orca. Ported from the Python <c>estate/orca_worktrees_cli.py</c>; the verdicts are <see cref="OrcaWorktrees"/>'.
/// </summary>
/// <param name="host">The working directory, which is always kept, and <c>~</c> in a path.</param>
/// <param name="orca">Where each Orca profile keeps its files, and the exchange that talks to it.</param>
/// <param name="git">Reads each worktree.</param>
/// <param name="proc">Runs <c>gh</c> for each branch's PRs, and the tree removal's helpers.</param>
/// <param name="time">The clock idleness is measured by.</param>
/// <param name="output">Where the rows go.</param>
/// <param name="error">Where refusals and unreachable profiles are reported, so <c>--json</c> stays clean.</param>
internal sealed class OrcaWorktreesCommand(EstateHost host, OrcaHost orca, Git git, IProc proc, TimeProvider time, TextWriter output, TextWriter error)
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>The <c>orca-worktrees</c> subcommand.</summary>
    public Command Build()
    {
        var repos = new Option<string[]>("--repo")
        {
            Description = "Only this Orca project: its folder or display name, or a path to its checkout or a worktree. Repeatable.",
            AllowMultipleArgumentsPerToken = false,
        };
        var every = new Option<bool>("--all") { Description = "Every Orca project. Required when --repo is not given." };
        var userData = new Option<string?>("--orca-data") { Description = "The estate's Orca user-data folder (default: orca-personal on Windows)" };
        var workData = new Option<string?>("--work-orca-data") { Description = "The work Orca profile (default: Orca's own folder on Windows; none elsewhere)" };
        var personalOnly = new Option<bool>("--personal-only") { Description = "Leave the work profile alone" };
        var idleDays = new Option<double>("--idle-days")
        {
            Description = "Days a worktree holding nothing beyond the default branch must sit untouched to count as landed",
            DefaultValueFactory = _ => OrcaWorktrees.DefaultIdleDays,
        };
        var kept = new Option<bool>("--kept") { Description = "List kept worktrees too, with why each is kept" };
        var apply = new Option<bool>("--apply") { Description = "Remove the finished worktrees through Orca" };
        var json = new Option<bool>("--json") { Description = "Write every row as JSON to stdout" };
        var command = new Command(
            "orca-worktrees",
            "Orca's worktrees whose work is finished: merged, landed, or already half-deleted.\n\n"
            + "The scope is always explicit, as for `estate tidy`: `--repo` names Orca projects, `--all` takes every project in "
            + "both profiles. Neither, or both, is an error.\n\n"
            + "Read-only unless --apply. A worktree is finished when its branch's newest PR merged with nothing local since "
            + "(`merged`), when it holds nothing outside the default branch and has sat idle for --idle-days (`landed`), when its "
            + "folder is gone (`missing`), or when an interrupted delete left a folder git no longer knows and every file in it "
            + "matches the branch (`leftover`). A dirty tree, a running or waiting agent, an open or closed-unmerged PR, unpushed "
            + "commits, a worktree whose git is not its project's, one whose PRs GitHub could not list, and the worktree this "
            + "command runs in are always kept. Each is re-checked just before removal. --apply removes through Orca's own "
            + "delete, which stops the worktree's terminals and keeps any branch git calls unmerged. Exit code 1 when any "
            + "removal failed, with or without --json. Exit code 2 for a missing or unmatched scope, or when the estate's Orca "
            + "is not running.")
        {
            repos, every, userData, workData, personalOnly, idleDays, kept, apply, json,
        };
        command.SetAction((parsed, cancellationToken) => RunAsync(
            new(
                parsed.GetValue(repos) ?? [],
                parsed.GetValue(every),
                parsed.GetValue(userData),
                parsed.GetValue(workData),
                parsed.GetValue(personalOnly),
                parsed.GetValue(idleDays),
                parsed.GetValue(kept),
                parsed.GetValue(apply),
                parsed.GetValue(json)),
            cancellationToken));
        return command;
    }

    /// <summary>The rows as <c>estate orca-worktrees --json</c> writes them.</summary>
    internal static string Dump(IEnumerable<WorktreeRow> rows) =>
        new JsonArray([.. rows.Select(r => (JsonNode)new JsonObject
        {
            ["profile"] = r.Profile,
            ["project"] = r.Project,
            ["path"] = r.Path,
            ["branch"] = r.Branch,
            ["verdict"] = r.Verdict,
            ["reason"] = r.Reason,
            ["outcome"] = r.Outcome,
            ["worktree_id"] = r.WorktreeId,
        })]).ToJsonString(Indented);

    /// <summary>The line above the table: how many are finished or, applied, what happened to them.</summary>
    internal static string Headline(IReadOnlyList<WorktreeRow> rows, bool applied)
    {
        var removable = rows.Where(r => r.Removable).ToList();
        if (!applied)
        {
            return $"{removable.Count} of {rows.Count} Orca worktree(s) finished; the rest are kept";
        }

        var removed = removable.Count(r => r.Removed);
        var failed = removable.Count(r => r.Failed);
        var skipped = removable.Count - removed - failed;
        return $"{removed} of {rows.Count} Orca worktree(s) removed, {failed} failed, {skipped} skipped as changed since review; the rest are kept";
    }

    private async Task<int> RunAsync(Args args, CancellationToken cancellationToken)
    {
        if (args.Repos.Length > 0 == args.Every)
        {
            await error.WriteLineAsync("Choose a scope: --repo <name|path> (repeatable), or --all for every project.".AsMemory(), cancellationToken).ConfigureAwait(false);
            return 2;
        }

        var live = await ConnectAsync(Profiles(args), cancellationToken).ConfigureAwait(false);
        if (live is null)
        {
            return 2;
        }

        var scopes = new IReadOnlySet<string>?[live.Count];
        if (args.Repos.Length > 0)
        {
            var chosen = await ScopesAsync(live, args.Repos, cancellationToken).ConfigureAwait(false);
            if (chosen is null)
            {
                return 2;
            }

            chosen.CopyTo(scopes, 0);
        }

        var nowMs = (double)time.GetUtcNow().ToUnixTimeMilliseconds();
        var finished = new OrcaWorktrees(git, proc);
        var rows = new List<WorktreeRow>();
        for (var i = 0; i < live.Count; i++)
        {
            var (name, runtime) = live[i];
            if (scopes[i] is { Count: 0 })
            {
                continue;
            }

            try
            {
                var found = await finished.ReviewAsync(runtime, name, host.WorkingDirectory, nowMs, args.IdleDays, scopes[i], cancellationToken).ConfigureAwait(false);
                rows.AddRange(args.Apply ? await finished.ApplyAsync(runtime, found, cancellationToken).ConfigureAwait(false) : found);
            }
            catch (OrcaUnavailableException e)
            {
                await error.WriteLineAsync($"{name} Orca profile failed: {e.Message}".AsMemory(), cancellationToken).ConfigureAwait(false);
                return 2;
            }
        }

        await ReportAsync(rows, args, cancellationToken).ConfigureAwait(false);
        return args.Apply && rows.Any(r => r.Failed) ? 1 : 0;
    }

    private List<(string Name, string Folder)> Profiles(Args args)
    {
        var estate = string.IsNullOrEmpty(args.OrcaData) ? orca.Locations.EstateUserData() : host.ExpandHome(args.OrcaData);
        var chosen = new List<(string, string)> { ("personal", estate) };
        var work = string.IsNullOrEmpty(args.WorkOrcaData) ? orca.Locations.WorkUserData() : host.ExpandHome(args.WorkOrcaData);
        if (!args.PersonalOnly && work is not null && !PathResolution.Same(PathResolution.Resolve(work), PathResolution.Resolve(estate)))
        {
            chosen.Add(("work", work));
        }

        return chosen;
    }

    // The reachable profiles, or null when the estate's is not: the work profile only warns.
    private async Task<List<(string Name, OrcaRuntime Runtime)>?> ConnectAsync(List<(string Name, string Folder)> chosen, CancellationToken cancellationToken)
    {
        var live = new List<(string, OrcaRuntime)>();
        for (var i = 0; i < chosen.Count; i++)
        {
            var (name, folder) = chosen[i];
            try
            {
                var runtime = OrcaRuntime.FromMetadata(orca.Locations.RuntimeMetadata(folder), orca.Exchange);
                _ = await runtime.ProjectsAsync(cancellationToken).ConfigureAwait(false);
                live.Add((name, runtime));
            }
            catch (OrcaUnavailableException e)
            {
                if (i == 0)
                {
                    await error.WriteLineAsync(e.Message.AsMemory(), cancellationToken).ConfigureAwait(false);
                    return null;
                }

                await error.WriteLineAsync($"{name} Orca profile not checked: {e.Message}".AsMemory(), cancellationToken).ConfigureAwait(false);
            }
        }

        return live;
    }

    // Per profile, the project ids --repo names; null after reporting a name that matches nothing anywhere, or two
    // projects in one profile, rather than a silent empty run.
    private async Task<IReadOnlySet<string>[]?> ScopesAsync(List<(string Name, OrcaRuntime Runtime)> live, string[] wanted, CancellationToken cancellationToken)
    {
        var scopes = live.Select(_ => (IReadOnlySet<string>)new HashSet<string>(StringComparer.Ordinal)).ToArray();
        foreach (var name in wanted)
        {
            var hits = new List<IReadOnlySet<string>>();
            foreach (var (_, runtime) in live)
            {
                hits.Add(await OrcaWorktrees.SelectAsync(runtime, name, host.Home, cancellationToken).ConfigureAwait(false));
            }

            if (hits.Any(h => h.Count > 1))
            {
                await error.WriteLineAsync($"--repo {name} is ambiguous: it names more than one Orca project.".AsMemory(), cancellationToken).ConfigureAwait(false);
                return null;
            }

            if (hits.All(h => h.Count == 0))
            {
                await error.WriteLineAsync($"--repo {name} matches no Orca project. Pass a name or a path.".AsMemory(), cancellationToken).ConfigureAwait(false);
                return null;
            }

            scopes = [.. scopes.Zip(hits, (scope, hit) => (IReadOnlySet<string>)scope.Union(hit).ToHashSet(StringComparer.Ordinal))];
        }

        return scopes;
    }

    private async Task ReportAsync(List<WorktreeRow> rows, Args args, CancellationToken cancellationToken)
    {
        if (args.Json)
        {
            await output.WriteLineAsync(Dump(rows).AsMemory(), cancellationToken).ConfigureAwait(false);
            return;
        }

        var shown = args.Kept ? rows : [.. rows.Where(r => r.Removable)];
        var headline = Headline(rows, args.Apply);
        if (shown.Count == 0)
        {
            await output.WriteLineAsync(headline.AsMemory(), cancellationToken).ConfigureAwait(false);
            return;
        }

        List<(string, bool)> columns = [("profile", false), ("project", false), ("worktree", false), ("branch", false), ("verdict", false), ("reason", false)];
        if (args.Apply)
        {
            columns.Add(("outcome", false));
        }

        var table = new TextTable(headline, columns);
        var ordered = shown
            .OrderBy(r => !r.Removable)
            .ThenBy(r => r.Profile, StringComparer.Ordinal)
            .ThenBy(r => r.Project, StringComparer.Ordinal)
            .ThenBy(r => r.Path, StringComparer.Ordinal);
        foreach (var row in ordered)
        {
            string[] cells = [row.Profile, row.Project, Path.GetFileName(Path.TrimEndingDirectorySeparator(row.Path)), row.Branch, row.Verdict, row.Reason];
            _ = table.Add(args.Apply ? [.. cells, row.Outcome] : cells);
        }

        await output.WriteAsync(table.Render().AsMemory(), cancellationToken).ConfigureAwait(false);
    }

    private sealed record Args(string[] Repos, bool Every, string? OrcaData, string? WorkOrcaData, bool PersonalOnly, double IdleDays, bool Kept, bool Apply, bool Json);
}
