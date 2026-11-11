using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Rig.Cli.Vault;
using Rig.Core.Estate;
using Rig.Core.Processes;
using Rig.Core.Roots;

namespace Rig.Cli.Estate;

/// <summary>Where <c>rig estate</c> looks.</summary>
/// <param name="CodeRoot">The code root: its <c>external/</c> and <c>other/</c> are untrusted, so git there runs hardened.</param>
/// <param name="Home">The home folder: <c>~</c> in a path, and the folder holding the clone cache.</param>
/// <param name="WorkingDirectory">Where <c>doctor</c> looks for a clone when given none.</param>
internal sealed record EstateHost(string CodeRoot, string Home, string WorkingDirectory)
{
    /// <summary>The personal estate folder, the default <c>--root</c>.</summary>
    public string PersonalDir => Path.Join(CodeRoot, "personal");

    /// <summary>The clone list cache, shared with the Python commands still in the group.</summary>
    public string CacheFile => CloneDiscovery.CacheFileUnder(Home);

    /// <summary><paramref name="path"/> with a leading <c>~</c> replaced by <see cref="Home"/>.</summary>
    public string ExpandHome(string path) =>
        path == "~" ? Home
        : path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal) ? Path.Join(Home, path[2..])
        : path;
}

/// <summary>
/// <c>rig estate</c>: the subcommands ported from <c>src/rig/estate/cli.py</c>. The group's other subcommands are still
/// Python's, so <see cref="Forwarding.Forwarded"/> hands any line naming one of them over. Read-only by default:
/// <c>sync</c> fetches, and only <c>reclaim</c>, <c>trim</c>, <c>tidy</c> and <c>prune-caches</c> delete, with <c>--apply</c>, each entry proving its own safety.
/// </summary>
/// <param name="host">Where it looks.</param>
/// <param name="proc">Runs git, du and lsof, the cache tools, and icacls on Windows when a tree is removed.</param>
/// <param name="github">Asks GitHub which head refs have a merged PR, for <c>doctor --merged</c> and <c>tidy --merged</c>.</param>
/// <param name="time">The clock the clone cache is aged by and the worktree checks read.</param>
/// <param name="findTool">Finds uv, pnpm, dotnet and docker for <c>prune-caches</c>; <see cref="Caches.FindTool"/> outside tests.</param>
/// <param name="output">Where reports go.</param>
/// <param name="error">Where progress and refusals go, so <c>--json</c> stays clean.</param>
internal sealed class EstateCommand(
    EstateHost host, IProc proc, IGitHubApi github, TimeProvider time, Func<string, string?> findTool, TextWriter output, TextWriter error)
{
    /// <summary>What <c>rig estate</c> is.</summary>
    public const string Description = "Manage the code assets cloned on this machine.";

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(120);

    // A worktree holding a large node_modules takes a while to delete.
    private static readonly TimeSpan WorktreeRemoveTimeout = TimeSpan.FromMinutes(10);

    private readonly Git git = new(proc, host.CodeRoot);

    /// <summary>The <c>estate</c> group with its ported subcommands.</summary>
    public Command Build()
    {
        var estate = new Command("estate", Description);
        estate.Subcommands.Add(Audit());
        estate.Subcommands.Add(Risk());
        estate.Subcommands.Add(Doctor());
        estate.Subcommands.Add(Sync());
        estate.Subcommands.Add(Clones());
        estate.Subcommands.Add(ReclaimCommand());
        estate.Subcommands.Add(TrimCommand());
        estate.Subcommands.Add(TidyCommand());
        estate.Subcommands.Add(new PruneCachesCommand(host, proc, findTool, time, output, error).Build());
        return estate;
    }

    private static Option<string?> RootOption() => new("--root") { Description = "Code root to scan (default: the personal repos dir)" };

    private static Option<bool> RefreshOption() => new("--refresh") { Description = "Rescan rather than using the cached clone list" };

    private static Option<bool> JsonOption() => new("--json") { Description = "Emit for a machine" };

    private Command Audit()
    {
        var root = RootOption();
        var refresh = RefreshOption();
        var json = JsonOption();
        var branches = new Option<bool>("--branches") { Description = "Also name the branches carrying unpushed work" };
        var audit = new Command("audit", "The dashboard: every clone, what is at risk, and what is reclaimable. Sorted by risk.")
        {
            root, refresh, json, branches,
        };
        audit.SetAction((parsed, cancellationToken) => AuditAsync(
            parsed.GetValue(root), parsed.GetValue(refresh), parsed.GetValue(json), parsed.GetValue(branches), cancellationToken));
        return audit;
    }

    private Command Risk()
    {
        var root = RootOption();
        var refresh = RefreshOption();
        var json = JsonOption();
        var risk = new Command(
            "risk",
            "Only what exists on this machine and nowhere else: uncommitted changes, stash entries and commits on no remote. "
            + "Run it before `reclaim --apply` or `tidy --apply`.")
        {
            root, refresh, json,
        };
        risk.SetAction((parsed, cancellationToken) => RiskAsync(parsed.GetValue(root), parsed.GetValue(refresh), parsed.GetValue(json), cancellationToken));
        return risk;
    }

    private Command Doctor()
    {
        var path = new Argument<string?>("path") { Description = "Clone to inspect (default: the one containing the cwd)", Arity = ArgumentArity.ZeroOrOne };
        var json = new Option<bool>("--json") { Description = "Emit findings for a machine" };
        var merged = new Option<bool>("--merged") { Description = "Fetch --prune, then ask GitHub which head refs have a merged PR" };
        var doctor = new Command("doctor", "One clone, every finding, each with the command that resolves it.") { path, json, merged };
        doctor.SetAction((parsed, cancellationToken) => DoctorAsync(parsed.GetValue(path), parsed.GetValue(json), parsed.GetValue(merged), cancellationToken));
        return doctor;
    }

    private Command Sync()
    {
        var root = RootOption();
        var refresh = RefreshOption();
        var sync = new Command(
            "sync",
            "`git fetch --prune` across the estate, so `tidy` reports an upstream as gone only once the fetch has pruned it.")
        {
            root, refresh,
        };
        sync.SetAction((parsed, cancellationToken) => SyncAsync(parsed.GetValue(root), parsed.GetValue(refresh), cancellationToken));
        return sync;
    }

    private Command Clones()
    {
        var root = RootOption();
        var refresh = new Option<bool>("--refresh") { Description = "Rescan now and rewrite the cache" };
        var clones = new Command("clones", "Show the cached clone list, and when it was last scanned.") { root, refresh };
        clones.SetAction(async (parsed, cancellationToken) =>
        {
            var scanRoot = CodeRoot(parsed.GetValue(root));
            var discovery = Discovery();
            var found = discovery.Repos(scanRoot, parsed.GetValue(refresh));
            var provenance = found.FromCache ? $"cached, scanned {Ago.Format(discovery.CacheAge(scanRoot))}" : "freshly scanned";
            await output.WriteLineAsync($"{found.Paths.Count} clones under {scanRoot}".AsMemory(), cancellationToken).ConfigureAwait(false);
            await output.WriteLineAsync($"{provenance} · {host.CacheFile}\n".AsMemory(), cancellationToken).ConfigureAwait(false);
            foreach (var clone in found.Paths)
            {
                await output.WriteLineAsync($"  {clone}".AsMemory(), cancellationToken).ConfigureAwait(false);
            }

            return 0;
        });
        return clones;
    }

    private Command ReclaimCommand()
    {
        var root = RootOption();
        var apply = new Option<bool>("--apply") { Description = "Actually delete. Off by default." };
        var force = new Option<bool>("--force") { Description = "Proceed on worktrees holding uncommitted or unpushed work" };
        var refresh = RefreshOption();
        var min = new Option<string>("--min") { Description = "Ignore anything smaller (e.g. 500MB, 2GB)", DefaultValueFactory = _ => "100MB" };
        var reclaim = new Command(
            "reclaim",
            "Disk that regenerates from a command. Refuses a dirty or only-copy worktree unless --force, and anything outside its clone.")
        {
            root, apply, force, refresh, min,
        };
        reclaim.SetAction((parsed, cancellationToken) => ReclaimAsync(
            parsed.GetValue(root), parsed.GetValue(apply), parsed.GetValue(force), parsed.GetValue(refresh), parsed.GetValue(min) ?? "100MB", cancellationToken));
        return reclaim;
    }

    private Command TrimCommand()
    {
        var root = RootOption();
        var apply = new Option<bool>("--apply") { Description = "Actually delete. Off by default." };
        var refresh = RefreshOption();
        var idleDays = new Option<int>("--idle-days") { Description = "Days since the worktree was used", DefaultValueFactory = _ => Trim.DefaultIdleDays };
        var min = new Option<string>("--min") { Description = "Ignore anything freeing less (e.g. 100MB)", DefaultValueFactory = _ => "10MB" };
        var trim = new Command(
            "trim",
            "Regenerable output inside worktrees idle --idle-days or more. The worktree, its branch and its temp/ stay; "
            + "sizes are freeable bytes, so a tree hardlinked from a store frees far less than it shows.")
        {
            root, apply, refresh, idleDays, min,
        };
        trim.SetAction((parsed, cancellationToken) => TrimAsync(
            parsed.GetValue(root), parsed.GetValue(apply), parsed.GetValue(refresh), parsed.GetValue(idleDays), parsed.GetValue(min) ?? "10MB", cancellationToken));
        return trim;
    }

    private Command TidyCommand()
    {
        var root = RootOption();
        var repos = new Option<string[]>("--repo")
        {
            Description = "Only this clone: a path to a checkout, or a clone's folder name under --root. Repeatable.",
        };
        var every = new Option<bool>("--all") { Description = "Every clone under --root. Required when --repo is not given." };
        var apply = new Option<bool>("--apply") { Description = "Actually delete. Off by default." };
        var force = new Option<bool>("--force") { Description = "Proceed on uncommitted or unpushed work, or when lsof cannot say what is in use" };
        var refresh = RefreshOption();
        var merged = new Option<bool>("--merged") { Description = "Fetch --prune, then ask GitHub which head refs have a merged PR (one call per clone)" };
        var checks = new Option<string[]>("--check") { Description = $"Act only on this finding check. Repeatable. One of: {string.Join(", ", TidyChecks.All)}." };
        var tidy = new Command(
            "tidy",
            "Stale worktrees and finished branches. Name the scope: --repo (repeatable) or --all. --merged asks GitHub, so a "
            + "squash-merged branch whose commits are on no remote can go; a folder a process is working in never does.")
        {
            root, repos, every, apply, force, refresh, merged, checks,
        };
        tidy.SetAction((parsed, cancellationToken) => TidyAsync(
            new TidyScope(parsed.GetValue(root), parsed.GetValue(repos) ?? [], parsed.GetValue(every), parsed.GetValue(refresh)),
            parsed.GetValue(apply),
            parsed.GetValue(force),
            parsed.GetValue(merged),
            parsed.GetValue(checks) ?? [],
            cancellationToken));
        return tidy;
    }

    private sealed record TidyScope(string? Root, string[] Repos, bool All, bool Refresh);

    // Squash merges are why --merged exists: a branch's commits never become ancestors of main, and deleting the remote
    // branch strands them on no remote, so the local "nothing is lost" check refuses every branch that landed. The scope
    // is always explicit, so a bare `tidy` never starts an estate-wide run nobody asked for.
    private async Task<int> TidyAsync(TidyScope scope, bool apply, bool force, bool merged, string[] checks, CancellationToken cancellationToken)
    {
        if (scope.Repos.Length > 0 == scope.All)
        {
            await error.WriteLineAsync("Choose a scope: --repo <path|name> (repeatable), or --all for every clone.".AsMemory(), cancellationToken).ConfigureAwait(false);
            return 2;
        }

        var unknown = checks.Except(TidyChecks.All, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        if (unknown.Count > 0)
        {
            await error.WriteLineAsync($"Unknown --check {string.Join(", ", unknown)}. Choose from: {string.Join(", ", TidyChecks.All)}.".AsMemory(), cancellationToken).ConfigureAwait(false);
            return 2;
        }

        var root = CodeRoot(scope.Root);
        var clones = scope.All
            ? await ClonesAsync(root, scope.Refresh, quiet: false, cancellationToken).ConfigureAwait(false)
            : await SelectedClonesAsync(root, scope.Repos, scope.Refresh, cancellationToken).ConfigureAwait(false);
        if (clones is null)
        {
            return 2;
        }

        var table = new TextTable(
            "Tidy" + (apply ? string.Empty : " (dry run — pass --apply)"),
            [("clone", false), ("check", false), ("subject", false), ("outcome", true), ("detail", false)]);
        var busy = await Busy.SnapshotAsync(proc, cancellationToken).ConfigureAwait(false);
        if (busy.Dirs is null)
        {
            await error.WriteLineAsync("Cannot read process working directories (lsof); removals need --force.".AsMemory(), cancellationToken).ConfigureAwait(false);
        }

        var checker = new RepoChecks(git, time);
        var tidy = new Tidy(git, new TreeRemoval(proc));
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var clone in clones)
        {
            var heads = merged ? await MergedHeadsAsync(clone, cancellationToken).ConfigureAwait(false) : null;
            var report = await checker.InspectAsync(clone, heads).ConfigureAwait(false);
            if (!report.Findings.Any(f => f.Actionable))
            {
                continue;
            }

            var actions = await tidy.CleanRepoAsync(report, apply, force, heads, busy, checks.Length > 0 ? checks : null, cancellationToken).ConfigureAwait(false);
            foreach (var action in actions)
            {
                counts[action.OutcomeName] = counts.GetValueOrDefault(action.OutcomeName) + 1;
                // A branch is a name, not a path: only a rooted subject is shortened against the clone.
                var subject = Path.IsPathRooted(action.Subject) ? Short(action.Subject, clone) : action.Subject;
                _ = table.Add(Path.GetFileName(clone), action.Check, subject, action.OutcomeName, action.Detail);
            }
        }

        await output.WriteAsync(table.Render().AsMemory(), cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync(
            (counts.Count > 0 ? "\n" + string.Join(" · ", counts.Select(pair => $"{pair.Value} {pair.Key}")) : "\n✓ Nothing to tidy.").AsMemory(),
            cancellationToken).ConfigureAwait(false);
        if (counts.ContainsKey(TidyAction.Name(TidyOutcome.Refused)))
        {
            await output.WriteLineAsync("Refusals are deliberate — see `estate risk`, or pass --force.".AsMemory(), cancellationToken).ConfigureAwait(false);
        }

        return 0;
    }

    // The clones --repo names: a path to a checkout skips discovery; a name matches a clone's folder name, or its path
    // relative to the root, ignoring case. Null, with the reason on stderr, when a value names no clone or several.
    private async Task<IReadOnlyList<string>?> SelectedClonesAsync(string root, string[] wanted, bool refresh, CancellationToken cancellationToken)
    {
        var chosen = new List<string>();
        var names = new List<string>();
        foreach (var value in wanted)
        {
            // On Unix `link/..` is the link target's parent, so links resolve before `..` collapses, never lexically.
            var joined = Path.Combine(host.WorkingDirectory, ExpandHome(value));
            var candidate = OperatingSystem.IsWindows() ? Path.GetFullPath(joined) : PathResolution.Resolve(joined);
            if (!Directory.Exists(Path.Join(candidate, ".git")) && !File.Exists(Path.Join(candidate, ".git")))
            {
                names.Add(value);
                continue;
            }

            if (await MainCheckoutAsync(candidate).ConfigureAwait(false) is not { } found)
            {
                await error.WriteLineAsync($"--repo {value}: cannot find the main working tree of this checkout.".AsMemory(), cancellationToken).ConfigureAwait(false);
                return null;
            }

            if (!chosen.Contains(found, PathResolution.Comparer))
            {
                chosen.Add(found);
            }
        }

        if (names.Count == 0)
        {
            return chosen;
        }

        var known = await ClonesAsync(root, refresh, quiet: false, cancellationToken).ConfigureAwait(false);
        foreach (var name in names)
        {
            var key = name.Trim().Replace('\\', '/').Trim('/');
            var hits = known.Where(c =>
                string.Equals(Path.GetFileName(c), key, StringComparison.OrdinalIgnoreCase)
                || string.Equals(Short(c, root), key, StringComparison.OrdinalIgnoreCase)).ToList();
            if (hits.Count != 1)
            {
                var problem = hits.Count == 0 ? "matches no clone" : "is ambiguous: " + string.Join(", ", hits.Select(c => Short(c, root)));
                await error.WriteLineAsync($"--repo {name} {problem} under {root}. Pass a path, or --root.".AsMemory(), cancellationToken).ConfigureAwait(false);
                return null;
            }

            if (!chosen.Contains(hits[0], PathResolution.Comparer))
            {
                chosen.Add(hits[0]);
            }
        }

        return chosen;
    }

    // The clone behind a checkout; a linked worktree resolves to its main checkout. Never derived from where the git dir
    // lives: --separate-git-dir puts it anywhere. The answer must itself be a working-tree root, because --apply acts on it.
    private async Task<string?> MainCheckoutAsync(string checkout)
    {
        var dirs = await git.LinesAsync(checkout, "rev-parse", "--path-format=absolute", "--git-dir", "--git-common-dir").ConfigureAwait(false);
        string? chosen;
        if (dirs is [var gitDir, var commonDir] && !PathResolution.Same(PathResolution.Resolve(gitDir), PathResolution.Resolve(commonDir)))
        {
            // A linked worktree: the first `worktree list` entry names the main checkout.
            var first = (await git.LinesAsync(checkout, "worktree", "list", "--porcelain").ConfigureAwait(false) ?? [])
                .FirstOrDefault(line => line.StartsWith("worktree ", StringComparison.Ordinal));
            chosen = first is null ? null : PathResolution.Resolve(first["worktree ".Length..]);
        }
        else
        {
            chosen = await TopLevelAsync(checkout).ConfigureAwait(false);
        }

        return chosen is not null && await TopLevelAsync(chosen).ConfigureAwait(false) is { } top && PathResolution.Same(top, chosen) ? chosen : null;
    }

    private async Task<string?> TopLevelAsync(string path)
    {
        var top = await git.OutAsync(path, "rev-parse", "--path-format=absolute", "--show-toplevel").ConfigureAwait(false);
        return top.Length > 0 ? PathResolution.Resolve(top) : null;
    }

    // Safety is per entry, not per clone: a commit is not endangered by deleting node_modules, so only a worktree
    // entry, the one kind that can hold live state, is refused for being dirty or the only copy of its commits.
    private async Task<int> ReclaimAsync(string? root, bool apply, bool force, bool refresh, string min, CancellationToken cancellationToken)
    {
        var floor = Bytes.ParseSize(min);
        var clones = await ClonesAsync(CodeRoot(root), refresh, quiet: false, cancellationToken).ConfigureAwait(false);
        var table = new TextTable(
            "Reclaimable" + (apply ? string.Empty : " (dry run — pass --apply)"),
            [("clone", false), ("path", false), ("size", true), ("comes back from", false), (string.Empty, true)]);
        var scanner = new Reclaim(git, new DiskUsage(proc));
        var removal = new TreeRemoval(proc);
        long freed = 0, pending = 0;
        var noted = new List<string>();
        foreach (var clone in clones)
        {
            var entries = (await scanner.ScanRepoAsync(clone, floor, cancellationToken).ConfigureAwait(false)).Entries.Where(e => e.Bytes >= floor).ToList();
            if (entries.Count == 0)
            {
                continue;
            }

            var exposure = await Exposure.InspectAsync(git, clone, perBranch: false).ConfigureAwait(false);
            if (exposure.AtRisk)
            {
                noted.Add($"{Path.GetFileName(clone)} — {exposure.LocalOnlyCommits} local-only commit(s), {exposure.DirtyFiles} uncommitted file(s)");
            }

            foreach (var entry in entries)
            {
                var status = await ReclaimOneAsync(clone, entry, apply, force, removal, cancellationToken).ConfigureAwait(false);
                freed += status == "removed" ? entry.Bytes : 0;
                pending += status == "would remove" ? entry.Bytes : 0;
                _ = table.Add(Path.GetFileName(clone), Short(entry.Path, clone), Bytes.Human(entry.Bytes), entry.Regenerate, status);
            }
        }

        await output.WriteAsync(table.Render().AsMemory(), cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync(
            (apply ? $"\nFreed {Bytes.Human(freed)}" : $"\nWould free {Bytes.Human(pending)} — pass --apply to do it").AsMemory(), cancellationToken).ConfigureAwait(false);
        if (noted.Count > 0)
        {
            await output.WriteLineAsync(
                ("\nThese clones hold work that exists only here. It is not endangered by the above —"
                + " a commit survives a deleted node_modules — but `estate risk` is worth a look:").AsMemory(),
                cancellationToken).ConfigureAwait(false);
            foreach (var line in noted)
            {
                await output.WriteLineAsync($"  {line}".AsMemory(), cancellationToken).ConfigureAwait(false);
            }
        }

        return 0;
    }

    // One entry's fate. A status or history git cannot read counts against removal: an unjudged worktree is never
    // removed unforced.
    private async Task<string> ReclaimOneAsync(string clone, Reclaimable entry, bool apply, bool force, TreeRemoval removal, CancellationToken cancellationToken)
    {
        if (entry.IsWorktree)
        {
            // git deletes a worktree's ignored files with it, and a worktree nested in its temp/ is one of them.
            var resolved = PathResolution.Resolve(entry.Path);
            if ((await Worktree.ListAsync(git, clone).ConfigureAwait(false))
                .Any(tree => Trim.IsStrictlyBelow(PathResolution.Resolve(tree.Path), resolved)))
            {
                return "holds a worktree";
            }

            // A separate clone, or another repository's worktree, in its ignored temp/ would go the same way.
            if (TreeMeasure.HasCheckoutBelow(entry.Path, ownGit: true))
            {
                return "holds a git checkout";
            }

            if (!force && (await git.LinesAsync(entry.Path, "status", "--porcelain").ConfigureAwait(false)) is null or { Count: > 0 })
            {
                return "dirty worktree";
            }

            if (!force && await OrphanedAsync(entry.Path).ConfigureAwait(false))
            {
                return "only copy";
            }

            if (!apply)
            {
                return "would remove";
            }

            var done = await git.RunAsync(clone, [.. Trim.LongPaths, "worktree", "remove", entry.Path], WorktreeRemoveTimeout, cancellationToken: cancellationToken).ConfigureAwait(false);
            return done.Ok ? "removed" : "failed";
        }

        if (!Trim.IsStrictlyBelow(entry.Path, clone))
        {
            return "outside clone";
        }

        // temp/ scratch can hold a clone or another repository's worktree, which no regenerate command brings back.
        if (entry.Category == "temp" && TreeMeasure.HasCheckoutBelow(entry.Path))
        {
            return "holds a git checkout";
        }

        if (!apply)
        {
            return "would remove";
        }

        try
        {
            await removal.RemoveAsync(entry.Path, cancellationToken).ConfigureAwait(false);
            return "removed";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return "failed";
        }
    }

    // Whether HEAD holds a commit on no remote; true when git cannot say.
    private async Task<bool> OrphanedAsync(string worktree)
    {
        var ran = await git.RunAsync(worktree, ["rev-list", "--count", "HEAD", "--not", "--remotes"]).ConfigureAwait(false);
        return !ran.Ok || !int.TryParse(ran.Out.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var count) || count > 0;
    }

    // Every folder is rechecked against the disk just before it goes: the scan may be minutes old.
    private async Task<int> TrimAsync(string? root, bool apply, bool refresh, int idleDays, string min, CancellationToken cancellationToken)
    {
        var floor = Bytes.ParseSize(min);
        var clones = await ClonesAsync(CodeRoot(root), refresh, quiet: false, cancellationToken).ConfigureAwait(false);
        var table = new TextTable(
            "Idle worktrees" + (apply ? string.Empty : " (dry run — pass --apply)"),
            [("worktree", false), ("path", false), ("frees", true), (string.Empty, true)]);
        var trim = new Trim(git, time);
        var removal = new TreeRemoval(proc);
        long freed = 0;
        foreach (var clone in clones)
        {
            foreach (var item in await trim.ScanAsync(clone, idleDays, floor).ConfigureAwait(false))
            {
                var refused = item.Refused.Length > 0 || !apply ? item.Refused : await trim.RecheckAsync(item, idleDays).ConfigureAwait(false);
                var status = refused.Length > 0 ? $"refused: {refused}"
                    : !apply ? "would remove"
                    : await Trim.RemoveAsync(item, removal, cancellationToken).ConfigureAwait(false);
                freed += status is "removed" or "would remove" ? item.Size.Freeable : 0;
                _ = table.Add(Path.GetFileName(item.Worktree), Short(item.Path, item.Worktree), Bytes.Human(item.Size.Freeable), status);
            }
        }

        await output.WriteAsync(table.Render().AsMemory(), cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync($"\n{(apply ? "Freed" : "Would free")} {Bytes.Human(freed)}".AsMemory(), cancellationToken).ConfigureAwait(false);
        return 0;
    }

    private async Task<int> AuditAsync(string? root, bool refresh, bool json, bool perBranch, CancellationToken cancellationToken)
    {
        var scanRoot = CodeRoot(root);
        var clones = await ClonesAsync(scanRoot, refresh, quiet: json, cancellationToken).ConfigureAwait(false);
        var exposures = new List<Exposure>(clones.Count);
        var reclaims = new Dictionary<string, RepoReclaim>(StringComparer.Ordinal);
        var reclaim = new Reclaim(git, new DiskUsage(proc));
        foreach (var clone in clones)
        {
            exposures.Add(await Exposure.InspectAsync(git, clone, perBranch).ConfigureAwait(false));
            reclaims[clone] = await reclaim.ScanRepoAsync(clone, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        var duplicates = DuplicateClone.Find(exposures);
        if (json)
        {
            var payload = new JsonObject
            {
                ["root"] = scanRoot,
                ["clones"] = new JsonArray([.. exposures.Select(e =>
                {
                    var row = ExposureJson(e);
                    row["reclaimable_bytes"] = reclaims[e.Repo].Total;
                    row["reclaimable_by_category"] = ByCategory(reclaims[e.Repo]);
                    return (JsonNode)row;
                })]),
                ["duplicate_clones"] = new JsonArray([.. duplicates.Select(d => (JsonNode)new JsonObject
                {
                    ["origin"] = d.Origin,
                    ["repos"] = new JsonArray([.. d.Repos.Select(r => (JsonNode)r)]),
                    ["exposure"] = new JsonObject(d.LocalOnly.Select(p => new KeyValuePair<string, JsonNode?>(p.Key, p.Value))),
                })]),
            };
            await output.WriteLineAsync(payload.ToJsonString(Indented).AsMemory(), cancellationToken).ConfigureAwait(false);
            return 0;
        }

        var table = new TextTable(
            $"Estate — {clones.Count} clones under {scanRoot}",
            [("clone", false), ("local-only", true), ("dirty", true), ("stash", true), ("reclaimable", true), ("largest", false)]);
        foreach (var exposure in exposures.OrderByDescending(e => e.Score))
        {
            var entry = reclaims[exposure.Repo];
            var largest = entry.Entries.Count > 0 ? entry.Entries[0] : null;
            _ = table.Add(
                Path.GetFileName(exposure.Repo),
                Count(exposure.LocalOnlyCommits),
                Count(exposure.DirtyFiles),
                Count(exposure.Stashes),
                entry.Total > 0 ? Bytes.Human(entry.Total) : "—",
                largest is null ? string.Empty : $"{largest.Category} {Bytes.Human(largest.Bytes)}");
        }

        await output.WriteAsync(table.Render().AsMemory(), cancellationToken).ConfigureAwait(false);
        var atRisk = exposures.Sum(e => e.LocalOnlyCommits);
        var regenerable = Bytes.Human(reclaims.Values.Sum(r => r.Total));
        await output.WriteLineAsync($"\n{atRisk} commits exist on no remote · {regenerable} regenerates from a command".AsMemory(), cancellationToken).ConfigureAwait(false);
        if (duplicates.Count > 0)
        {
            await output.WriteLineAsync("\nDuplicate clones — estate totals double-count these:".AsMemory(), cancellationToken).ConfigureAwait(false);
            foreach (var duplicate in duplicates)
            {
                var names = string.Join(", ", duplicate.LocalOnly.Select(p => $"{p.Key} ({p.Value} local-only)"));
                await output.WriteLineAsync($"  {duplicate.Origin} → {names}".AsMemory(), cancellationToken).ConfigureAwait(false);
            }
        }

        await output.WriteLineAsync("\n`estate risk` before deleting anything · `estate reclaim` for the disk".AsMemory(), cancellationToken).ConfigureAwait(false);
        return 0;
    }

    private async Task<int> RiskAsync(string? root, bool refresh, bool json, CancellationToken cancellationToken)
    {
        var clones = await ClonesAsync(CodeRoot(root), refresh, quiet: json, cancellationToken).ConfigureAwait(false);
        var exposed = new List<Exposure>();
        foreach (var clone in clones)
        {
            var exposure = await Exposure.InspectAsync(git, clone, perBranch: true).ConfigureAwait(false);
            if (exposure.AtRisk)
            {
                exposed.Add(exposure);
            }
        }

        var ranked = exposed.OrderByDescending(e => e.Score).ToList();
        if (json)
        {
            var payload = new JsonArray([.. ranked.Select(e => (JsonNode)ExposureJson(e))]);
            await output.WriteLineAsync(payload.ToJsonString(Indented).AsMemory(), cancellationToken).ConfigureAwait(false);
            return 0;
        }

        if (ranked.Count == 0)
        {
            await output.WriteLineAsync("✓ Nothing at risk — every commit is on a remote, every tree clean.".AsMemory(), cancellationToken).ConfigureAwait(false);
            return 0;
        }

        foreach (var exposure in ranked)
        {
            await output.WriteLineAsync(RiskLines(exposure).AsMemory(), cancellationToken).ConfigureAwait(false);
        }

        var total = ranked.Sum(e => e.LocalOnlyCommits);
        await output.WriteLineAsync($"\n{total} commits across {ranked.Count} clones exist only here.".AsMemory(), cancellationToken).ConfigureAwait(false);
        return 0;
    }

    private static string RiskLines(Exposure exposure)
    {
        const int Shown = 8;
        var lines = new List<string> { string.Empty, Path.GetFileName(exposure.Repo) };
        if (!exposure.HasRemote)
        {
            lines.Add("  no remote at all — every commit here is local-only");
        }

        if (exposure.LocalOnlyCommits > 0)
        {
            lines.Add($"  {exposure.LocalOnlyCommits} commit(s) on no remote");
            lines.AddRange(exposure.Branches.Take(Shown).Select(branch => $"      cd {exposure.Repo} && git push -u origin {branch}"));
            if (exposure.Branches.Count > Shown)
            {
                lines.Add($"      … and {exposure.Branches.Count - Shown} more branches");
            }
        }

        if (exposure.DirtyFiles > 0)
        {
            lines.Add($"  {exposure.DirtyFiles} uncommitted file(s)");
        }

        if (exposure.Stashes > 0)
        {
            var entries = exposure.Stashes == 1 ? "entry" : "entries";
            lines.Add($"  {exposure.Stashes} stash {entries} (one stack shared by every worktree of this repo)");
        }

        return string.Join('\n', lines);
    }

    private async Task<int> DoctorAsync(string? path, bool json, bool merged, CancellationToken cancellationToken)
    {
        var clone = await OneCloneAsync(path).ConfigureAwait(false);
        if (!Directory.Exists(Path.Join(clone, ".git")) && !File.Exists(Path.Join(clone, ".git")))
        {
            await error.WriteLineAsync($"{clone} is not a git checkout.".AsMemory(), cancellationToken).ConfigureAwait(false);
            return 2;
        }

        var heads = merged ? await MergedHeadsAsync(clone, cancellationToken).ConfigureAwait(false) : null;
        var report = await new RepoChecks(git, time).InspectAsync(clone, heads).ConfigureAwait(false);
        var exposure = await Exposure.InspectAsync(git, clone, perBranch: true).ConfigureAwait(false);
        var disk = await new Reclaim(git, new DiskUsage(proc)).ScanRepoAsync(clone, cancellationToken: cancellationToken).ConfigureAwait(false);

        if (json)
        {
            var payload = new JsonObject
            {
                ["repo"] = clone,
                ["findings"] = new JsonArray([.. report.Findings.Select(f => (JsonNode)new JsonObject
                {
                    ["check"] = f.Check,
                    ["severity"] = f.SeverityName,
                    ["subject"] = f.Subject,
                    ["message"] = f.Message,
                    ["fix"] = f.Fix,
                    ["actionable"] = f.Actionable,
                })]),
                ["local_only_commits"] = exposure.LocalOnlyCommits,
                ["unpushed_branches"] = Strings(exposure.Branches),
                ["dirty_files"] = exposure.DirtyFiles,
                ["stashes"] = exposure.Stashes,
                ["reclaimable_bytes"] = disk.Total,
                ["reclaimable_by_category"] = ByCategory(disk),
            };
            await output.WriteLineAsync(payload.ToJsonString(Indented).AsMemory(), cancellationToken).ConfigureAwait(false);
            return 0;
        }

        await output.WriteLineAsync($"\n{Path.GetFileName(clone)}  {clone}".AsMemory(), cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync(
            string.Create(
                CultureInfo.InvariantCulture,
                $"  {report.WorktreeCount} worktree(s) · {report.BranchCount} local branch(es) · {exposure.LocalOnlyCommits} local-only commit(s) · {Bytes.Human(disk.Total)} reclaimable").AsMemory(),
            cancellationToken).ConfigureAwait(false);
        if (report.Findings.Count == 0)
        {
            await output.WriteLineAsync("\n✓ Clean.".AsMemory(), cancellationToken).ConfigureAwait(false);
            return 0;
        }

        var table = new TextTable(string.Empty, [("severity", false), ("check", false), ("subject", false), ("finding", false)]);
        foreach (var finding in report.Findings.OrderBy(f => f.Severity))
        {
            _ = table.Add(finding.SeverityName, finding.Check, Short(finding.Subject, clone), finding.Message);
            if (finding.Fix.Length > 0)
            {
                _ = table.Add(string.Empty, string.Empty, string.Empty, finding.Fix);
            }
        }

        await output.WriteAsync(table.Render().AsMemory(), cancellationToken).ConfigureAwait(false);
        if (report.Blocked)
        {
            await output.WriteLineAsync("\nErrors above are live work — `tidy` will refuse this clone until they clear.".AsMemory(), cancellationToken).ConfigureAwait(false);
        }

        return 0;
    }

    // `--merged` is a network call already, and without the prune a branch deleted on merge still shows as tracked, so
    // `branch-upstream-gone` never fires for it. The same fetch as `sync`.
    private async Task<IReadOnlyDictionary<string, PullRequest>> MergedHeadsAsync(string clone, CancellationToken cancellationToken)
    {
        var fetched = await git.RunAsync(clone, ["fetch", "--prune", "--quiet"], FetchTimeout, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!fetched.Ok)
        {
            var reason = LastLine(fetched.Err) ?? "no output";
            await error.WriteLineAsync($"{Path.GetFileName(clone)}: fetch --prune failed ({reason}); merged branches may be missed".AsMemory(), cancellationToken).ConfigureAwait(false);
        }

        return await new PullRequests(git, github).MergedHeadsAsync(clone, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private async Task<int> SyncAsync(string? root, bool refresh, CancellationToken cancellationToken)
    {
        var clones = await ClonesAsync(CodeRoot(root), refresh, quiet: false, cancellationToken).ConfigureAwait(false);
        var failed = new List<string>();
        foreach (var clone in clones)
        {
            await error.WriteLineAsync($"fetching {Path.GetFileName(clone)}".AsMemory(), cancellationToken).ConfigureAwait(false);
            var done = await git.RunAsync(clone, ["fetch", "--prune", "--quiet"], FetchTimeout, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!done.Ok)
            {
                failed.Add($"{Path.GetFileName(clone)}: {LastLine(done.Err) ?? "failed"}");
            }
        }

        await output.WriteLineAsync($"✓ Fetched {clones.Count - failed.Count} of {clones.Count} clones.".AsMemory(), cancellationToken).ConfigureAwait(false);
        foreach (var line in failed)
        {
            await output.WriteLineAsync($"  {line}".AsMemory(), cancellationToken).ConfigureAwait(false);
        }

        return 0;
    }

    private CloneDiscovery Discovery() => new(host.CacheFile, time);

    // The clone list, with its provenance on stderr unless the caller wants clean output.
    private async Task<IReadOnlyList<string>> ClonesAsync(string root, bool refresh, bool quiet, CancellationToken cancellationToken)
    {
        var discovery = Discovery();
        var found = discovery.Repos(root, refresh);
        if (!quiet)
        {
            var age = discovery.CacheAge(root);
            var provenance = found.FromCache && age is not null ? $"cached {Ago.Format(age)}" : "freshly scanned";
            await error.WriteLineAsync($"{found.Paths.Count} clones under {root} ({provenance})".AsMemory(), cancellationToken).ConfigureAwait(false);
        }

        return found.Paths;
    }

    private string CodeRoot(string? explicitRoot) =>
        PathResolution.Resolve(string.IsNullOrEmpty(explicitRoot) ? host.PersonalDir : ExpandHome(explicitRoot));

    // The given path, else the checkout containing the working directory, else the working directory itself.
    private async Task<string> OneCloneAsync(string? path)
    {
        if (!string.IsNullOrEmpty(path))
        {
            return PathResolution.Resolve(ExpandHome(path));
        }

        var top = await git.OutAsync(host.WorkingDirectory, "rev-parse", "--show-toplevel").ConfigureAwait(false);
        return top.Length > 0 ? PathResolution.Resolve(top) : host.WorkingDirectory; // git answers with / on Windows
    }

    private string ExpandHome(string path) => host.ExpandHome(path);

    // A path inside the clone relative to it, else in full: a worktree can live outside its clone.
    private static string Short(string path, string clone)
    {
        var relative = Path.GetRelativePath(clone, path);
        return relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Path.IsPathRooted(relative)
            ? path
            : relative.Replace('\\', '/');
    }

    private static string? LastLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();

    private static string Count(int value) => value > 0 ? value.ToString(CultureInfo.InvariantCulture) : "—";

    private static JsonObject ExposureJson(Exposure e) => new()
    {
        ["repo"] = e.Repo,
        ["local_only_commits"] = e.LocalOnlyCommits,
        ["unpushed_branches"] = Strings(e.Branches),
        ["dirty_files"] = e.DirtyFiles,
        ["stashes"] = e.Stashes,
        ["has_remote"] = e.HasRemote,
    };

    private static JsonArray Strings(IEnumerable<string> values) => new([.. values.Select(v => (JsonNode)v)]);

    private static JsonObject ByCategory(RepoReclaim reclaim) =>
        new(reclaim.ByCategory().Select(p => new KeyValuePair<string, JsonNode?>(p.Key, p.Value)));
}
