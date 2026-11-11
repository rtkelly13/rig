using System.CommandLine;
using System.Globalization;
using System.Text.Json.Nodes;
using Rig.Cli.Vault;
using Rig.Core.Estate;
using Rig.Core.Policy;
using Rig.Core.Processes;
using Rig.Core.Roots;

namespace Rig.Cli.Estate;

/// <summary>
/// <c>rig estate shelve</c>, <c>unshelve</c> and <c>shelved</c>: set aside a worktree <c>tidy</c> refuses by pushing
/// what exists only there to a <c>TEMP-SHELVED:</c> side ref, list what is shelved, and bring a shelf back. Ported from
/// <c>src/rig/estate/shelve_cli.py</c>; the work is in <see cref="Shelver"/> and <see cref="ShelfStore"/>.
/// </summary>
/// <param name="host">Where it looks; the ledger lives in its home folder.</param>
/// <param name="git">Runs git.</param>
/// <param name="proc">Removes a tree git stopped deleting part-way.</param>
/// <param name="github">Names each branch's PR.</param>
/// <param name="time">Stamps markers and the ledger, and ages the live-session guard.</param>
/// <param name="clones">The clones under a code root: <c>(root, refresh, quiet, token)</c>.</param>
/// <param name="codeRoot">The code root <c>--root</c> names, or the default.</param>
/// <param name="oneClone">The clone <c>--repo</c> names, or the one holding the working directory.</param>
/// <param name="output">Where reports go.</param>
/// <param name="error">Where progress and refusals go, so <c>--json</c> stays clean.</param>
internal sealed class ShelveCommand(
    EstateHost host,
    Git git,
    IProc proc,
    IGitHubApi github,
    TimeProvider time,
    Func<string, bool, bool, CancellationToken, Task<IReadOnlyList<string>>> clones,
    Func<string?, string> codeRoot,
    Func<string?, Task<string>> oneClone,
    TextWriter output,
    TextWriter error)
{
    /// <summary>The ledger file under the home folder: one JSON line per removal.</summary>
    public const string LedgerName = ".estate-shelved.jsonl";

    // Wider than tidy's window: a shelf's trailer should name its PR however old it is.
    private const int PrLimit = 1000;

    private readonly ShelfStore store = new(git);

    /// <summary>The three commands, for the <c>estate</c> group.</summary>
    public IEnumerable<Command> Build() => [Shelve(), Unshelve(), Shelved()];

    /// <summary>
    /// <c>2h</c>, <c>30m</c>, <c>1d</c>, or <c>0</c> to turn the live-session guard off; null when it is none of those.
    /// </summary>
    public static TimeSpan? ParseDuration(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var value = text.Trim().ToLowerInvariant();
        if (value is "" or "0")
        {
            return TimeSpan.Zero;
        }

        var number = value[..^1];
        var dot = number.IndexOf('.', StringComparison.Ordinal);
        var digits = dot < 0 ? number : number.Remove(dot, 1); // Python's isdigit after dropping one dot.
        if (value[^1] is not ('m' or 'h' or 'd') || digits.Length == 0 || !digits.All(char.IsAsciiDigit))
        {
            return null;
        }

        var amount = double.Parse(number, CultureInfo.InvariantCulture);
        return value[^1] switch
        {
            'm' => TimeSpan.FromMinutes(amount),
            'h' => TimeSpan.FromHours(amount),
            _ => TimeSpan.FromDays(amount),
        };
    }

    private static Option<string?> RootOption() => new("--root") { Description = "Code root for --all" };

    private static Option<bool> RefreshOption() => new("--refresh") { Description = "Rescan rather than using the cached list" };

    private static Option<string?> RepoOption(string description) => new("--repo") { Description = description };

    private static bool Shelvable(Worktree tree) => !tree.IsMain && !tree.Bare && !tree.Prunable && Directory.Exists(tree.Path);

    private static string WithoutHeads(string reference) =>
        reference.StartsWith("refs/heads/", StringComparison.Ordinal) ? reference["refs/heads/".Length..] : reference;

    private static string Name(string path) => Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));

    private Command Shelve()
    {
        var worktrees = new Argument<string[]>("worktrees") { Description = "Worktree paths (default: the one containing the cwd)", Arity = ArgumentArity.ZeroOrMore };
        var repo = RepoOption("Every linked worktree of this clone");
        var every = new Option<bool>("--all") { Description = "Every linked worktree across the estate" };
        var root = RootOption();
        var refresh = RefreshOption();
        var apply = new Option<bool>("--apply") { Description = "Push and remove. Off by default." };
        var json = new Option<bool>("--json") { Description = "Emit the plans (or results) for a machine" };
        var idle = new Option<string>("--idle") { Description = "Refuse trees touched more recently than this (0 disables)", DefaultValueFactory = _ => "2h" };
        var include = new Option<bool>("--include-ignored") { Description = "Carry non-regenerable ignored files (an .env) in the snapshot" };
        var discard = new Option<bool>("--discard-ignored") { Description = "Accept losing non-regenerable ignored files" };
        var upstream = new Option<bool>("--to-upstream") { Description = "Also push local commits to the branch's own upstream (fast-forward only)" };
        var shelve = new Command(
            "shelve",
            "Resolve worktrees `tidy` refuses: push what exists only here, then remove them. Uncommitted work becomes one "
            + "`TEMP-SHELVED:` commit on top of HEAD, made from a throwaway index so the branch and its PR never move, pushed to "
            + "`shelved/<user>/<name>`. The worktree is removed only after origin is shown to hold it; `estate unshelve <name>` "
            + "brings it back. Exit 0 when everything resolved (or would), 2 when a plan needs a decision, 1 on a failure.")
        {
            worktrees, repo, every, root, refresh, apply, json, idle, include, discard, upstream,
        };
        shelve.SetAction((parsed, cancellationToken) => GuardedAsync(() => ShelveAsync(
            new ShelveScope(parsed.GetValue(worktrees) ?? [], parsed.GetValue(repo), parsed.GetValue(every), parsed.GetValue(root), parsed.GetValue(refresh)),
            parsed.GetValue(idle) ?? "2h",
            new ShelveOptions(TimeSpan.Zero, parsed.GetValue(include), parsed.GetValue(discard), parsed.GetValue(upstream)),
            parsed.GetValue(apply),
            parsed.GetValue(json),
            cancellationToken), cancellationToken));
        return shelve;
    }

    private sealed record ShelveScope(string[] Paths, string? Repo, bool All, string? Root, bool Refresh);

    private sealed record Target(string Repo, List<Worktree> Trees);

    private async Task<int> ShelveAsync(ShelveScope scope, string idleText, ShelveOptions flags, bool apply, bool json, CancellationToken cancellationToken)
    {
        if (flags.IncludeIgnored && flags.DiscardIgnored)
        {
            return await RefuseAsync("--include-ignored and --discard-ignored are opposites", cancellationToken).ConfigureAwait(false);
        }

        if (ParseDuration(idleText) is not { } idle)
        {
            return await RefuseAsync($"--idle: expected e.g. 30m, 2h, 1d — got '{idleText.Trim().ToLowerInvariant()}'", cancellationToken).ConfigureAwait(false);
        }

        var options = flags with { Idle = idle };
        var targets = await TargetsAsync(scope, cancellationToken).ConfigureAwait(false);
        if (targets.Refusal is { } refusal)
        {
            return await RefuseAsync(refusal, cancellationToken).ConfigureAwait(false);
        }

        var shelver = new Shelver(git, new TreeRemoval(proc), time, Path.Join(host.Home, LedgerName));
        var plans = new List<ShelvePlan>();
        foreach (var target in targets.Found)
        {
            var clone = target.Repo;
            await error.WriteLineAsync($"planning {Name(clone)}: {target.Trees.Count} worktree(s)".AsMemory(), cancellationToken).ConfigureAwait(false);
            var user = await shelver.ShelfUserAsync(clone).ConfigureAwait(false);
            var shelves = await store.RemoteShelvesAsync(clone).ConfigureAwait(false);
            IReadOnlyDictionary<string, PullRequest> prs = target.Trees.Any(tree => tree.Branch.Length > 0)
                ? await new PullRequests(git, github).HeadsAsync(clone, PrLimit, cancellationToken: cancellationToken).ConfigureAwait(false)
                : new Dictionary<string, PullRequest>(StringComparer.Ordinal);
            foreach (var tree in target.Trees)
            {
                plans.Add(await shelver.PlanAsync(clone, tree, user, shelves, prs, options).ConfigureAwait(false));
            }
        }

        var results = new List<ShelveResult>();
        if (apply)
        {
            foreach (var plan in plans)
            {
                // Between plans, never inside one: an interrupted apply leaves each worktree either shelved or untouched.
                cancellationToken.ThrowIfCancellationRequested();
                results.Add(await shelver.ApplyAsync(plan).ConfigureAwait(false));
            }
        }

        if (json)
        {
            JsonArray payload = apply ? [.. results.Select(result => (JsonNode)result.ToJson())] : [.. plans.Select(plan => (JsonNode)plan.ToJson())];
            await output.WriteLineAsync(PyJson.Dumps(payload).AsMemory(), cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await output.WriteAsync(RenderShelve(plans, results, apply).AsMemory(), cancellationToken).ConfigureAwait(false);
        }

        return results.Any(result => result.Outcome == "failed") ? 1 : plans.Any(plan => plan.DecisionNeeded.Count > 0) ? 2 : 0;
    }

    private static string RenderShelve(List<ShelvePlan> plans, List<ShelveResult> results, bool apply)
    {
        var table = new TextTable(
            "Shelve" + (apply ? string.Empty : " (dry run — pass --apply)"),
            [("clone", false), ("worktree", false), ("branch / PR", false), ("plan", true), ("why", false)]);
        var byPlan = results.ToDictionary(result => result.Plan); // A plan is a class: matched by reference.
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var ordered = plans
            .OrderBy(plan => plan.DecisionNeeded.Count > 0)
            .ThenBy(plan => Name(plan.Clone), StringComparer.Ordinal)
            .ThenBy(plan => plan.Name, StringComparer.Ordinal);
        foreach (var plan in ordered)
        {
            var result = byPlan.GetValueOrDefault(plan);
            var label = result?.Outcome ?? (plan.DecisionNeeded.Count > 0 ? "needs-decision" : plan.Action);
            counts[label] = counts.GetValueOrDefault(label) + 1;
            var why = string.Join("; ", plan.DecisionNeeded.Count > 0 ? plan.DecisionNeeded : plan.Reasons);
            if (result is { Detail.Length: > 0 } && plan.DecisionNeeded.Count == 0)
            {
                why = result.Detail;
            }

            var pr = plan.Pr is { } number and not 0 ? $" #{number.ToString(CultureInfo.InvariantCulture)} {plan.PrState.ToLowerInvariant()}" : string.Empty;
            var branch = plan.Branch.Length > 0 ? plan.Branch : $"detached@{plan.Head[..Math.Min(7, plan.Head.Length)]}";
            _ = table.Add(Name(plan.Clone), EstateCommand.Short(plan.Path, plan.Clone), branch + pr, label, why);
        }

        var text = table.Render();
        return counts.Count > 0 ? text + "\n" + string.Join(" · ", counts.Select(pair => $"{pair.Value} {pair.Key}")) + "\n" : text;
    }

    // Groups the worktrees to consider by clone. A main checkout is never a target.
    private async Task<(List<Target> Found, string? Refusal)> TargetsAsync(ShelveScope scope, CancellationToken cancellationToken)
    {
        if (scope.All || scope.Repo is not null)
        {
            var chosen = scope.All
                ? await clones(codeRoot(scope.Root), scope.Refresh, false, cancellationToken).ConfigureAwait(false)
                : [Absolute(scope.Repo!)];
            var found = new List<Target>();
            foreach (var clone in chosen)
            {
                var trees = (await Worktree.ListAsync(git, clone).ConfigureAwait(false)).Where(Shelvable).ToList();
                if (trees.Count > 0)
                {
                    found.Add(new Target(clone, trees));
                }
            }

            return (found, null);
        }

        var wanted = scope.Paths.Length > 0 ? scope.Paths.Select(Absolute).ToList() : [PathResolution.Resolve(host.WorkingDirectory)];
        var grouped = new List<Target>();
        foreach (var want in wanted)
        {
            var clone = await CloneOfAsync(want).ConfigureAwait(false);
            if (clone is null)
            {
                return ([], $"{want} is not inside a git worktree");
            }

            var match = (await Worktree.ListAsync(git, clone).ConfigureAwait(false))
                .Where(tree => PathResolution.IsUnder(want, PathResolution.Resolve(tree.Path)))
                .OrderByDescending(tree => PathResolution.Resolve(tree.Path).Length) // git prints / on Windows: compare resolved paths
                .FirstOrDefault();
            if (match is null || !Shelvable(match))
            {
                return ([], $"{want} is not a linked worktree (a clone's main checkout is never shelved)");
            }

            var target = grouped.FirstOrDefault(entry => entry.Repo == clone);
            if (target is null)
            {
                target = new Target(clone, []);
                grouped.Add(target);
            }

            if (!target.Trees.Any(tree => PathResolution.Same(PathResolution.Resolve(tree.Path), PathResolution.Resolve(match.Path))))
            {
                target.Trees.Add(match);
            }
        }

        return (grouped, null);
    }

    // The clone a worktree belongs to, via its common git dir.
    private async Task<string?> CloneOfAsync(string path)
    {
        if (!Directory.Exists(path))
        {
            return null;
        }

        var common = await git.OutAsync(path, "rev-parse", "--path-format=absolute", "--git-common-dir").ConfigureAwait(false);
        if (common.Length == 0)
        {
            return null;
        }

        var resolved = common.TrimEnd('/', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        return Path.GetFileName(resolved) == ".git" ? Path.GetDirectoryName(resolved) : resolved;
    }

    private Command Unshelve()
    {
        var name = new Argument<string>("name") { Description = "Shelf name (as `estate shelved` lists it) or full ref" };
        var repo = RepoOption("Clone the shelf belongs to (default: the cwd's)");
        var path = new Option<string?>("--path") { Description = "Where to recreate the worktree (default: Shelved-Path)" };
        var user = new Option<string?>("--user") { Description = "Whose shelf (default: yours)" };
        var drop = new Option<bool>("--drop") { Description = "Delete the shelf from origin once restored" };
        var unshelve = new Command("unshelve", "Recreate a shelved worktree; uncommitted work comes back unstaged.") { name, repo, path, user, drop };
        unshelve.SetAction((parsed, cancellationToken) => GuardedAsync(() => UnshelveAsync(
            parsed.GetValue(name)!, parsed.GetValue(repo), parsed.GetValue(path), parsed.GetValue(user), parsed.GetValue(drop), cancellationToken), cancellationToken));
        return unshelve;
    }

    private async Task<int> UnshelveAsync(string name, string? repo, string? path, string? user, bool drop, CancellationToken cancellationToken)
    {
        // The main checkout, not the cwd's worktree: Shelved-Path is relative to it.
        var clone = repo is null
            ? await CloneOfAsync(PathResolution.Resolve(host.WorkingDirectory)).ConfigureAwait(false) ?? await oneClone(null).ConfigureAwait(false)
            : await oneClone(repo).ConfigureAwait(false);
        var shelves = await store.ListAsync(clone).ConfigureAwait(false);
        var mine = Shelver.ShelfRef(user ?? await new Shelver(git, new TreeRemoval(proc), time, null).ShelfUserAsync(clone).ConfigureAwait(false), name);
        var exact = shelves.Where(shelf => shelf.Ref == name || shelf.Ref == $"refs/heads/{name}" || shelf.Ref == mine).ToList();
        if (exact.Count == 0)
        {
            exact = [.. shelves.Where(shelf => shelf.Name == name)];
        }

        if (exact.Count != 1)
        {
            var found = exact.Count > 0 ? string.Join(", ", exact.Select(shelf => WithoutHeads(shelf.Ref))) : "none";
            return await RefuseAsync($"'{name}' matches {exact.Count} shelves in {Name(clone)} ({found})", cancellationToken).ConfigureAwait(false);
        }

        var chosen = exact[0];
        var restored = await store.UnshelveAsync(chosen, path is null ? null : Absolute(path)).ConfigureAwait(false);
        if (!restored.Ok)
        {
            await output.WriteLineAsync($"✗ {restored.Detail}".AsMemory(), cancellationToken).ConfigureAwait(false);
            return 1;
        }

        await output.WriteLineAsync($"✓ {WithoutHeads(chosen.Ref)} → {restored.Detail}".AsMemory(), cancellationToken).ConfigureAwait(false);
        if (drop)
        {
            var dropped = await store.DropAsync(chosen).ConfigureAwait(false);
            await output.WriteLineAsync((dropped.Ok ? $"  shelf dropped from {ShelfStore.Remote}" : $"  drop failed: {dropped.Detail}").AsMemory(), cancellationToken).ConfigureAwait(false);
            return dropped.Ok ? 0 : 1;
        }

        return 0;
    }

    private Command Shelved()
    {
        var repo = RepoOption("Clone to list (default: the cwd's)");
        var every = new Option<bool>("--all") { Description = "Every clone in the estate" };
        var root = RootOption();
        var refresh = RefreshOption();
        var prune = new Option<bool>("--prune") { Description = "Drop shelves whose work landed (dry run unless --apply)" };
        var apply = new Option<bool>("--apply") { Description = "With --prune, actually delete the refs" };
        var json = new Option<bool>("--json") { Description = "Emit for a machine" };
        var shelved = new Command(
            "shelved",
            "What is shelved on origin, read from each `TEMP-SHELVED:` commit's trailers. --prune drops a shelf only when it "
            + "carries no snapshot and its PR merged or its base is already in the default branch. A snapshot is never pruned.")
        {
            repo, every, root, refresh, prune, apply, json,
        };
        shelved.SetAction((parsed, cancellationToken) => GuardedAsync(() => ShelvedAsync(
            parsed.GetValue(repo), parsed.GetValue(every), parsed.GetValue(root), parsed.GetValue(refresh), parsed.GetValue(prune), parsed.GetValue(apply), parsed.GetValue(json), cancellationToken), cancellationToken));
        return shelved;
    }

    private sealed record ShelvedRow(Shelf Shelf, string? Prune = null, bool? Pruned = null)
    {
        public JsonObject ToJson()
        {
            var data = Shelf.ToJson();
            if (Prune is not null)
            {
                data["prune"] = Prune;
            }

            if (Pruned is { } pruned)
            {
                data["pruned"] = pruned;
            }

            return data;
        }
    }

    private async Task<int> ShelvedAsync(string? repo, bool all, string? root, bool refresh, bool prune, bool apply, bool json, CancellationToken cancellationToken)
    {
        var chosen = all
            ? await clones(codeRoot(root), refresh, json, cancellationToken).ConfigureAwait(false)
            : [await oneClone(repo).ConfigureAwait(false)];
        var rows = new List<ShelvedRow>();
        foreach (var clone in chosen)
        {
            rows.AddRange(await RowsAsync(clone, prune, apply, cancellationToken).ConfigureAwait(false));
        }

        var code = rows.Any(row => row.Pruned == false) ? 1 : 0; // a drop that was asked for and failed
        if (json)
        {
            await output.WriteLineAsync(PyJson.Dumps(new JsonArray([.. rows.Select(row => (JsonNode)row.ToJson())])).AsMemory(), cancellationToken).ConfigureAwait(false);
            return code;
        }

        if (rows.Count == 0)
        {
            await output.WriteLineAsync("✓ Nothing shelved.".AsMemory(), cancellationToken).ConfigureAwait(false);
            return 0;
        }

        List<(string Heading, bool Right)> columns = [("clone", false), ("shelf", false), ("branch", false), ("PR", false), ("snapshot", false), ("at", false)];
        if (prune)
        {
            columns.Add(("prune", false));
        }

        var table = new TextTable("Shelved" + (prune && !apply ? " (prune dry run — pass --apply)" : string.Empty), columns);
        foreach (var row in rows)
        {
            var shelf = row.Shelf;
            List<string> cells = [Name(shelf.Repo), WithoutHeads(shelf.Ref), shelf.Trailers.Branch, shelf.Trailers.Pr, shelf.Snapshot ? "✓" : string.Empty, shelf.Trailers.At];
            if (prune)
            {
                cells.Add((row.Pruned == true ? "dropped " : string.Empty) + (row.Prune ?? string.Empty));
            }

            _ = table.Add([.. cells]);
        }

        await output.WriteAsync(table.Render().AsMemory(), cancellationToken).ConfigureAwait(false);
        return code;
    }

    private async Task<List<ShelvedRow>> RowsAsync(string clone, bool prune, bool apply, CancellationToken cancellationToken)
    {
        var shelves = await store.ListAsync(clone).ConfigureAwait(false);
        if (shelves.Count == 0)
        {
            return [];
        }

        if (!prune)
        {
            return [.. shelves.Select(shelf => new ShelvedRow(shelf))];
        }

        var merged = new Dictionary<int, string>();
        foreach (var pull in (await new PullRequests(git, github).HeadsAsync(clone, cancellationToken: cancellationToken).ConfigureAwait(false)).Values)
        {
            if (pull.Merged)
            {
                merged[pull.Number] = pull.HeadOid;
            }
        }

        var head = await git.OutAsync(clone, "symbolic-ref", "--short", $"refs/remotes/{ShelfStore.Remote}/HEAD").ConfigureAwait(false);
        var prefix = $"{ShelfStore.Remote}/";
        var defaultBranch = head.StartsWith(prefix, StringComparison.Ordinal) ? head[prefix.Length..] : head;
        if (defaultBranch.Length == 0)
        {
            defaultBranch = "main";
        }

        var rows = new List<ShelvedRow>();
        foreach (var shelf in shelves)
        {
            var row = new ShelvedRow(shelf, await store.PrunableAsync(shelf, merged, defaultBranch).ConfigureAwait(false));
            if (row.Prune is { Length: > 0 } && apply)
            {
                var dropped = await store.DropAsync(shelf).ConfigureAwait(false);
                row = row with { Pruned = dropped.Ok, Prune = dropped.Ok ? row.Prune : $"failed: {dropped.Detail}" };
            }

            rows.Add(row);
        }

        return rows;
    }

    private string Absolute(string path) => PathResolution.Resolve(Path.Combine(host.WorkingDirectory, host.ExpandHome(path)));

    // origin could not say which shelves it holds: fail rather than act as if it holds none.
    private async Task<int> GuardedAsync(Func<Task<int>> run, CancellationToken cancellationToken)
    {
        try
        {
            return await run().ConfigureAwait(false);
        }
        catch (ShelfStoreException failure)
        {
            await error.WriteLineAsync($"✗ {failure.Message}".AsMemory(), cancellationToken).ConfigureAwait(false);
            return 1;
        }
    }

    private async Task<int> RefuseAsync(string message, CancellationToken cancellationToken)
    {
        await error.WriteLineAsync(message.AsMemory(), cancellationToken).ConfigureAwait(false);
        return 2;
    }
}
