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
}

/// <summary>
/// <c>rig estate</c>: the subcommands ported from <c>src/rig/estate/cli.py</c>. The group's other subcommands are still
/// Python's, so <see cref="Forwarding.Forwarded"/> hands any line naming one of them over. Read-only throughout:
/// <c>sync</c> fetches, and nothing here deletes.
/// </summary>
/// <param name="host">Where it looks.</param>
/// <param name="proc">Runs git and du.</param>
/// <param name="github">Asks GitHub which head refs have a merged PR, for <c>doctor --merged</c>.</param>
/// <param name="time">The clock the clone cache is aged by and the worktree checks read.</param>
/// <param name="output">Where reports go.</param>
/// <param name="error">Where progress and refusals go, so <c>--json</c> stays clean.</param>
internal sealed class EstateCommand(EstateHost host, IProc proc, IGitHubApi github, TimeProvider time, TextWriter output, TextWriter error)
{
    /// <summary>What <c>rig estate</c> is.</summary>
    public const string Description = "Manage the code assets cloned on this machine.";

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(120);

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
        return top.Length > 0 ? top : host.WorkingDirectory;
    }

    private string ExpandHome(string path) =>
        path == "~" ? host.Home
        : path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal) ? Path.Join(host.Home, path[2..])
        : path;

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
