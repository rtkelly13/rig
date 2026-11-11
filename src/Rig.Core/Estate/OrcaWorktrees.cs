using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Rig.Core.GitHub;
using Rig.Core.Governance;
using Rig.Core.Orca;
using Rig.Core.Processes;
using Rig.Core.Roots;

namespace Rig.Core.Estate;

/// <summary>Everything <see cref="OrcaWorktrees.Decide"/> reads about one worktree.</summary>
/// <param name="Exists">The folder is there.</param>
/// <param name="IsWorktree">Git knows the folder as the top of a working tree.</param>
/// <param name="IsCurrent">This command runs inside it.</param>
/// <param name="BusyAgents">Agents still working, blocked or waiting in it.</param>
/// <param name="Foreign">The worktree's git is not its registered project's, or the project is not registered.</param>
/// <param name="Dirty">Uncommitted or untracked files.</param>
/// <param name="Pr">The newest PR for the branch, or null when there is none (or HEAD is detached).</param>
/// <param name="PrKnown">False when GitHub could not list the branch's PRs, so "no PR" cannot be trusted.</param>
/// <param name="BeyondMerge">Commits on HEAD that are on no remote and not in the PR's merged head.</param>
/// <param name="InDefault">HEAD is an ancestor of the remote default branch.</param>
/// <param name="IdleDays">Days since Orca last saw activity in it.</param>
/// <param name="LeftoverMatches">For a leftover folder: null when it could not be checked, else whether it matches its branch.</param>
/// <param name="LeftoverDetail">Why a leftover folder does not match.</param>
public sealed record WorktreeFacts(
    bool Exists,
    bool IsWorktree,
    bool IsCurrent = false,
    int BusyAgents = 0,
    bool Foreign = false,
    bool Dirty = false,
    PullRequest? Pr = null,
    bool PrKnown = true,
    int BeyondMerge = 0,
    bool InDefault = false,
    double IdleDays = 0,
    bool? LeftoverMatches = null,
    string LeftoverDetail = "");

/// <summary>A verdict and why.</summary>
/// <param name="Verdict">One of the <see cref="OrcaWorktrees"/> verdicts.</param>
/// <param name="Reason">Why.</param>
public sealed record Judgement(string Verdict, string Reason);

/// <summary>One linked worktree, its verdict, and what <c>--apply</c> did.</summary>
/// <param name="Profile">The Orca profile: <c>personal</c> or <c>work</c>.</param>
/// <param name="Project">The project's display name, else its folder's name.</param>
/// <param name="Path">The worktree's folder, as Orca spells it.</param>
/// <param name="Branch">The branch, without <c>refs/heads/</c>; empty when detached.</param>
/// <param name="Verdict">One of the <see cref="OrcaWorktrees"/> verdicts.</param>
/// <param name="Reason">Why.</param>
/// <param name="Outcome">What <c>--apply</c> did: <c>removed</c>, <c>skipped: ...</c> or <c>failed: ...</c>.</param>
/// <param name="WorktreeId">Orca's id for the worktree reviewed, so apply removes that one and no other at the same path.</param>
public sealed record WorktreeRow(string Profile, string Project, string Path, string Branch, string Verdict, string Reason, string Outcome = "", string WorktreeId = "")
{
    /// <summary><c>--apply</c> acts on it.</summary>
    public bool Removable => OrcaWorktrees.Removable.Contains(Verdict);

    /// <summary>It was removed.</summary>
    public bool Removed => Outcome.StartsWith("removed", StringComparison.Ordinal);

    /// <summary>Its removal failed.</summary>
    public bool Failed => Outcome.StartsWith("failed", StringComparison.Ordinal);
}

/// <summary>Whether a leftover folder matches its branch, and if not, why.</summary>
/// <param name="Matches">Every file git does not ignore matches the branch's last commit.</param>
/// <param name="Detail">Why not.</param>
public sealed record Comparison(bool Matches, string Detail = "");

/// <summary>The newest PR for one branch, or <c>Known = false</c> when GitHub could not say.</summary>
/// <param name="Known">GitHub answered.</param>
/// <param name="Pr">The newest same-repository PR, or null for none.</param>
public sealed record PrAnswer(bool Known, PullRequest? Pr = null);

/// <summary>The newest same-repository PR for a branch of a clone.</summary>
/// <param name="repo">The clone.</param>
/// <param name="branch">The branch.</param>
/// <param name="cancellationToken">Cancels the lookup.</param>
public delegate Task<PrAnswer> PrLookup(string repo, string branch, CancellationToken cancellationToken);

/// <summary>
/// Which of Orca's worktrees are finished, and clearing them out. Ported from <c>src/rig/estate/orca_worktrees.py</c>.
/// </summary>
/// <remarks>
/// <para>
/// Orca lists every worktree of every registered project, and nothing removes one when its work lands, so the sidebar
/// fills with finished branches. <see cref="Decide"/> is pure: it reads <see cref="WorktreeFacts"/>, which
/// <see cref="GatherAsync"/> collects from git, Orca's <c>worktree.ps</c> and GitHub, so every verdict is reproducible
/// from what the report prints.
/// </para>
/// <para>
/// A worktree is finished when its newest PR merged with nothing local since (<c>merged</c>), when it holds nothing
/// outside the default branch and has sat idle (<c>landed</c>), when its folder is gone (<c>missing</c>), or when an
/// interrupted delete left a folder git no longer knows and every file in it matches its branch (<c>leftover</c>).
/// Everything else is kept, and a dirty tree or a running agent is kept whatever else is true. Removal goes through
/// Orca (<c>worktree.rm</c>), so Orca stops the terminals and drops the sidebar entry; only a leftover folder, which git
/// has already let go of, is deleted here, and it is compared with its branch again just before.
/// </para>
/// </remarks>
/// <param name="git">Runs git.</param>
/// <param name="proc">Runs <c>gh</c> for the PR lookup and the tree removal's helpers.</param>
/// <param name="prs">The PR lookup; <see cref="BranchPrAsync"/> by default.</param>
public sealed class OrcaWorktrees(Git git, IProc proc, PrLookup? prs = null)
{
    /// <summary>The newest PR merged, with nothing local since.</summary>
    public const string Merged = "merged";

    /// <summary>Nothing outside the default branch, and idle long enough.</summary>
    public const string Landed = "landed";

    /// <summary>Orca lists it, but its folder is gone.</summary>
    public const string Missing = "missing";

    /// <summary>Git no longer knows the folder, and every file in it matches its branch.</summary>
    public const string Leftover = "leftover";

    /// <summary>This command runs inside it.</summary>
    public const string Current = "current";

    /// <summary>An agent still works or waits in it.</summary>
    public const string Busy = "busy";

    /// <summary>Uncommitted or untracked files.</summary>
    public const string Dirty = "dirty";

    /// <summary>The PR merged, but commits since are local.</summary>
    public const string AfterMerge = "after-merge";

    /// <summary>The newest PR is open.</summary>
    public const string OpenPr = "open-pr";

    /// <summary>The newest PR closed without merging.</summary>
    public const string ClosedPr = "closed-pr";

    /// <summary>Commits outside the default branch, and no PR.</summary>
    public const string Unmerged = "unmerged";

    /// <summary>In the default branch, but touched too recently.</summary>
    public const string Recent = "recent";

    /// <summary>A leftover folder that differs from its branch, or could not be compared.</summary>
    public const string LeftoverDiffers = "leftover-differs";

    /// <summary>Its git is not the registered project's.</summary>
    public const string Foreign = "foreign";

    /// <summary>GitHub could not list the branch's PRs.</summary>
    public const string PrUnknown = "pr-unknown";

    /// <summary>Days a worktree holding nothing beyond the default branch must sit untouched to count as landed.</summary>
    public const double DefaultIdleDays = 2.0;

    private const double MsPerDay = 86_400_000;

    /// <summary>The verdicts <c>--apply</c> acts on; every other verdict keeps the worktree.</summary>
    public static readonly IReadOnlySet<string> Removable = new HashSet<string>(StringComparer.Ordinal) { Merged, Landed, Missing, Leftover };

    /// <summary>Agent states that mean someone is still working in the worktree.</summary>
    public static readonly IReadOnlySet<string> BusyStates = new HashSet<string>(StringComparer.Ordinal) { "working", "blocked", "waiting" };

    private static readonly TimeSpan GhTimeout = TimeSpan.FromSeconds(60);

    private readonly PrLookup _prs = prs ?? ((repo, branch, cancellationToken) => BranchPrAsync(git, proc, repo, branch, cancellationToken));

    /// <summary>The verdict for one worktree and the reason, in the order the safety rules need.</summary>
    public static Judgement Decide(WorktreeFacts facts, double idleDays = DefaultIdleDays)
    {
        ArgumentNullException.ThrowIfNull(facts);
        if (facts.IsCurrent)
        {
            return new(Current, "this command is running inside it");
        }

        if (facts.BusyAgents > 0)
        {
            return new(Busy, $"{facts.BusyAgents} agent(s) still working or waiting");
        }

        if (!facts.Exists)
        {
            return new(Missing, "the folder is gone");
        }

        if (!facts.IsWorktree)
        {
            return facts.LeftoverMatches == true
                ? new(Leftover, "not a git worktree any more; its files match the branch")
                : new(LeftoverDiffers, facts.LeftoverDetail is { Length: > 0 } detail ? detail : "not a git worktree; could not compare it");
        }

        if (facts.Foreign)
        {
            return new(Foreign, "its git is not the registered project's");
        }

        if (facts.Dirty)
        {
            return new(Dirty, "uncommitted or untracked files");
        }

        if (!facts.PrKnown)
        {
            return new(PrUnknown, "GitHub could not list the branch's PRs");
        }

        if (facts.Pr is { Merged: true } merged)
        {
            return facts.BeyondMerge > 0
                ? new(AfterMerge, $"PR #{merged.Number} merged, but {facts.BeyondMerge} commit(s) since are local")
                : new(Merged, $"PR #{merged.Number} merged");
        }

        if (facts.Pr is { State: "OPEN" } open)
        {
            return new(OpenPr, $"PR #{open.Number} open");
        }

        if (facts.Pr is { } closed)
        {
            return new(ClosedPr, $"PR #{closed.Number} closed without merging: decide by hand");
        }

        if (!facts.InDefault)
        {
            return new(Unmerged, "commits not in the default branch, and no PR");
        }

        return facts.IdleDays < idleDays
            ? new(Recent, $"already in the default branch, but active {Fixed(facts.IdleDays, 1)} day(s) ago")
            : new(Landed, $"already in the default branch; idle {Fixed(facts.IdleDays, 0)} day(s)");
    }

    /// <summary>The branch name without <c>refs/heads/</c>; empty for a detached HEAD.</summary>
    public static string ShortBranch(string? reference) =>
        reference is null ? string.Empty
        : reference.StartsWith("refs/heads/", StringComparison.Ordinal) ? reference["refs/heads/".Length..]
        : reference;

    /// <summary>
    /// Whether every file git does not ignore in <paramref name="folder"/> matches <paramref name="branch"/>'s last commit.
    /// </summary>
    /// <remarks>
    /// The folder has no <c>.git</c> any more, so it is compared through the main checkout's object store with a throwaway
    /// index: read the branch's tree into it, refresh it against the folder, then look for changed or untracked files.
    /// Ignored files regenerate and do not count. A file missing from the folder loses nothing, because the branch has it;
    /// an interrupted delete leaves many missing, usually <c>.gitignore</c> among them, so missing files are marked
    /// skip-worktree and git reads their ignore rules from the index instead of the folder.
    /// </remarks>
    public async Task<Comparison> LeftoverMatchesAsync(string folder, string repo, string branch, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(branch);
        ArgumentNullException.ThrowIfNull(repo);
        if (branch.Length == 0)
        {
            return new(false, "detached when it broke; nothing to compare it with");
        }

        var common = (await git.RunAsync(repo, ["rev-parse", "--path-format=absolute", "--git-common-dir"], cancellationToken: cancellationToken).ConfigureAwait(false)).OutOrEmpty;
        if (common.Length == 0)
        {
            return new(false, $"could not find the git folder of {OrcaLayout.Posix(repo)}");
        }

        if (!(await git.RunAsync(repo, ["rev-parse", "--verify", "--quiet", $"refs/heads/{branch}"], cancellationToken: cancellationToken).ConfigureAwait(false)).Ok)
        {
            return new(false, $"branch {branch} no longer exists, so the folder may hold the only copy");
        }

        var scratchRoot = Path.Join(repo, Gitignores.StateDir);
        _ = Directory.CreateDirectory(scratchRoot);
        var scratch = Path.Join(scratchRoot, $"orca-leftover-{Guid.NewGuid():N}");
        _ = Directory.CreateDirectory(scratch);
        Ran changed;
        Ran untracked;
        try
        {
            var environment = new Dictionary<string, string>
            {
                ["GIT_DIR"] = common,
                ["GIT_WORK_TREE"] = folder,
                ["GIT_INDEX_FILE"] = Path.Join(scratch, "index"),
            };

            Task<Ran> Run(IReadOnlyList<string> args, string? stdin = null) =>
                git.RunAsync(folder, args, environment: environment, stdinText: stdin, cancellationToken: cancellationToken);

            if (!(await Run(["read-tree", $"refs/heads/{branch}"]).ConfigureAwait(false)).Ok)
            {
                return new(false, $"could not read {branch}");
            }

            _ = await Run(["update-index", "-q", "--refresh"]).ConfigureAwait(false);
            var missing = await Run(["diff-files", "--name-only", "--diff-filter=D", "-z"]).ConfigureAwait(false);
            if (missing.Ok && missing.Out.Length > 0)
            {
                _ = await Run(["update-index", "-z", "--skip-worktree", "--stdin"], missing.Out).ConfigureAwait(false);
            }

            changed = await Run(["diff-files", "--name-only"]).ConfigureAwait(false);
            untracked = await Run(["ls-files", "--others", "--exclude-standard"]).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                Directory.Delete(scratch, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // A scratch index left under temp/.rig is harmless, and temp/ is disposable.
            }
        }

        if (!(changed.Ok && untracked.Ok))
        {
            return new(false, "git could not compare the folder");
        }

        var differing = (changed.Out + untracked.Out).ReplaceLineEndings("\n").Split('\n').Where(line => line.Trim().Length > 0).ToList();
        return differing.Count > 0
            ? new(false, $"{differing.Count} file(s) differ from {branch}, e.g. {differing[0]}")
            : new(true);
    }

    /// <summary>
    /// The newest PR whose head is <paramref name="branch"/> in <paramref name="repo"/>'s own GitHub repository. Asked per
    /// branch, so a PR outside any recent window is still found, and a fork's PR sharing the branch name is ignored. Any
    /// failure answers <c>Known = false</c>.
    /// </summary>
    public static async Task<PrAnswer> BranchPrAsync(Git git, IProc proc, string repo, string branch, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(git);
        ArgumentNullException.ThrowIfNull(proc);
        var origin = await git.RunAsync(repo, ["remote", "get-url", "origin"], TimeSpan.FromSeconds(10), cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!origin.Ok || RepoSlug.FromRemote(origin.Out.Trim()) is not { } slug)
        {
            return new(false);
        }

        var ran = await proc.RunAsync(
            new ProcRequest(["gh", "pr", "list", "--repo", slug.FullName, "--head", branch, "--state", "all", "--limit", "100", "--json", "number,state,headRefOid,isCrossRepository"])
            {
                Timeout = GhTimeout,
            },
            cancellationToken).ConfigureAwait(false);
        var pulls = ran.Ok ? BranchPulls(ran.Out) : null;
        if (pulls is null)
        {
            return new(false);
        }

        var own = pulls.Where(p => !p.CrossRepository).ToList();
        if (own.Count == 0)
        {
            return new(true);
        }

        var newest = own.MaxBy(p => p.Number)!;
        return new(true, new PullRequest(newest.Number, newest.State, branch, newest.HeadOid));
    }

    /// <summary>Collects <see cref="WorktreeFacts"/> for one linked worktree; reads only, changes nothing.</summary>
    /// <param name="worktree">The worktree.</param>
    /// <param name="repo">Its registered project's checkout, or null when the project is not registered.</param>
    /// <param name="activity">What Orca reports running in it, or null.</param>
    /// <param name="nowMs">Now, in milliseconds since the epoch.</param>
    /// <param name="cwd">Where this command runs.</param>
    /// <param name="cancellationToken">Cancels the probes.</param>
    public async Task<WorktreeFacts> GatherAsync(OrcaWorktree worktree, string? repo, OrcaActivity? activity, double nowMs, string cwd, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(worktree);
        var path = worktree.Path;
        var busy = activity?.AgentStates.Count(state => state is not null && BusyStates.Contains(state)) ?? 0;
        var idle = worktree.LastActivityMs is { } last && last != 0 ? (nowMs - last) / MsPerDay : 0.0;
        var facts = new WorktreeFacts(Directory.Exists(path), false, IsWithin(cwd, path), busy, IdleDays: Math.Max(idle, 0.0));
        if (!facts.Exists || facts.IsCurrent || busy > 0)
        {
            return facts;
        }

        var branch = ShortBranch(worktree.Branch);
        if (!await IsWorktreeAsync(path, cancellationToken).ConfigureAwait(false))
        {
            if (repo is null)
            {
                return facts with { LeftoverMatches = false, LeftoverDetail = "its project is not registered in Orca" };
            }

            var compared = await LeftoverMatchesAsync(path, repo, branch, cancellationToken).ConfigureAwait(false);
            return facts with { LeftoverMatches = compared.Matches, LeftoverDetail = compared.Detail };
        }

        if (repo is null || !await BelongsToAsync(path, repo, cancellationToken).ConfigureAwait(false))
        {
            return facts with { IsWorktree = true, Foreign = true };
        }

        var dirty = await IsDirtyAsync(path, cancellationToken).ConfigureAwait(false);
        var answer = branch.Length > 0 && !dirty ? await _prs(repo, branch, cancellationToken).ConfigureAwait(false) : new PrAnswer(true);
        var pr = answer.Pr;
        return facts with
        {
            IsWorktree = true,
            Dirty = dirty,
            Pr = pr,
            PrKnown = answer.Known,
            BeyondMerge = pr is { Merged: true } ? await BeyondMergeAsync(path, pr.HeadOid).ConfigureAwait(false) : 0,
            InDefault = !dirty && pr is null && answer.Known && await InDefaultAsync(path, cancellationToken).ConfigureAwait(false),
        };
    }

    /// <summary>Every linked worktree in one Orca profile with its verdict, or only those of the <paramref name="only"/> project ids.</summary>
    /// <exception cref="OrcaUnavailableException">Orca could not be asked.</exception>
    public async Task<IReadOnlyList<WorktreeRow>> ReviewAsync(
        OrcaRuntime runtime,
        string profile,
        string cwd,
        double nowMs,
        double idleDays = DefaultIdleDays,
        IReadOnlySet<string>? only = null,
        CancellationToken cancellationToken = default)
    {
        var repos = (await runtime.ProjectsAsync(cancellationToken).ConfigureAwait(false)).Where(r => only is null || only.Contains(r.Id)).ToList();
        if (repos.Count == 0)
        {
            return [];
        }

        var activity = new Dictionary<string, OrcaActivity>(StringComparer.Ordinal);
        foreach (var running in await runtime.ActivityAsync(cancellationToken).ConfigureAwait(false))
        {
            activity[running.WorktreeId] = running;
        }

        var paths = new Dictionary<string, string>(StringComparer.Ordinal);
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var repo in repos)
        {
            paths[repo.Id] = repo.Path;
            names[repo.Id] = repo.DisplayName is { Length: > 0 } shown ? shown : OrcaLayout.FileName(repo.Path);
        }

        var rows = new List<WorktreeRow>();
        foreach (var worktree in await runtime.WorktreesAsync(cancellationToken).ConfigureAwait(false))
        {
            if (worktree.IsMain || !names.TryGetValue(worktree.RepoId, out var project))
            {
                continue;
            }

            var facts = await GatherAsync(worktree, paths[worktree.RepoId], activity.GetValueOrDefault(worktree.Id), nowMs, cwd, cancellationToken).ConfigureAwait(false);
            var judgement = Decide(facts, idleDays);
            rows.Add(new(profile, project, worktree.Path, ShortBranch(worktree.Branch), judgement.Verdict, judgement.Reason, WorktreeId: worktree.Id));
        }

        return rows;
    }

    /// <summary>
    /// The ids of the projects <paramref name="wanted"/> names in this profile: a path to a project's checkout or to one
    /// of its worktrees (or anything inside them), or a name, the checkout's folder name or Orca's display name, ignoring
    /// case.
    /// </summary>
    /// <exception cref="OrcaUnavailableException">Orca could not be asked.</exception>
    public static async Task<IReadOnlySet<string>> SelectAsync(OrcaRuntime runtime, string wanted, string home, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(wanted);
        var repos = await runtime.ProjectsAsync(cancellationToken).ConfigureAwait(false);
        var roots = repos.Select(r => (r.Id, r.Path)).ToList();
        roots.AddRange((await runtime.WorktreesAsync(cancellationToken).ConfigureAwait(false)).Select(w => (w.RepoId, w.Path)));
        var candidate = ExpandUser(wanted, home);
        if (Directory.Exists(candidate) || File.Exists(candidate))
        {
            var here = PathResolution.Resolve(candidate);
            return roots.Where(root => IsWithin(here, root.Path)).Select(root => root.Item1).ToHashSet(StringComparer.Ordinal);
        }

        if (Path.IsPathFullyQualified(candidate))
        {
            // A vanished worktree is still registered: match its path as Orca spells it.
            var spelled = Comparable(candidate);
            return roots.Where(root => Comparable(root.Path) == spelled).Select(root => root.Item1).ToHashSet(StringComparer.Ordinal);
        }

        var key = wanted.Trim();
        return repos
            .Where(r => string.Equals(key, OrcaLayout.FileName(r.Path), StringComparison.OrdinalIgnoreCase) || string.Equals(key, r.DisplayName, StringComparison.OrdinalIgnoreCase))
            .Select(r => r.Id)
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Removes each removable worktree; returns the rows with what happened to each.</summary>
    /// <exception cref="OrcaUnavailableException">Orca could not be asked for its projects or worktrees.</exception>
    public async Task<IReadOnlyList<WorktreeRow>> ApplyAsync(OrcaRuntime runtime, IReadOnlyList<WorktreeRow> rows, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var repoPaths = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var repo in await runtime.ProjectsAsync(cancellationToken).ConfigureAwait(false))
        {
            repoPaths[repo.Id] = repo.Path;
        }

        var byId = new Dictionary<string, OrcaWorktree>(StringComparer.Ordinal);
        foreach (var worktree in await runtime.WorktreesAsync(cancellationToken).ConfigureAwait(false))
        {
            byId[worktree.Id] = worktree;
        }

        var done = new List<WorktreeRow>();
        foreach (var row in rows)
        {
            if (!row.Removable)
            {
                done.Add(row);
                continue;
            }

            if (byId.GetValueOrDefault(row.WorktreeId) is not { } worktree || Comparable(worktree.Path) != Comparable(row.Path))
            {
                done.Add(row with { Outcome = "skipped: Orca no longer lists the worktree reviewed" });
                continue;
            }

            var outcome = await RemoveAsync(runtime, worktree, row, repoPaths.GetValueOrDefault(worktree.RepoId), cancellationToken).ConfigureAwait(false);
            done.Add(row with { Outcome = outcome });
        }

        return done;
    }

    /// <summary><paramref name="path"/> normalised as <c>os.path.normpath</c> would, and casefolded on Windows.</summary>
    internal static string Comparable(string path)
    {
        var normal = Path.IsPathRooted(path) ? Path.GetFullPath(path) : path;
        var trimmed = Path.TrimEndingDirectorySeparator(normal);
        return OperatingSystem.IsWindows() ? trimmed.ToLowerInvariant() : trimmed;
    }

    /// <summary>Whether <paramref name="path"/> is <paramref name="root"/> or inside it, by spelling.</summary>
    internal static bool IsWithin(string path, string root)
    {
        var inner = Comparable(path);
        var outer = Comparable(root);
        return inner == outer || inner.StartsWith(outer.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    // Python's f"{value:.{digits}f}": round half to even on the exact value. F0 is exact; F1 matches outside exact ties.
    private static string Fixed(double value, int digits) =>
        digits == 0
            ? Math.Round(value, MidpointRounding.ToEven).ToString("F0", CultureInfo.InvariantCulture)
            : value.ToString($"F{digits}", CultureInfo.InvariantCulture);

    private static string ExpandUser(string path, string home) =>
        path == "~" ? home
        : path.StartsWith("~/", StringComparison.Ordinal) || (OperatingSystem.IsWindows() && path.StartsWith("~\\", StringComparison.Ordinal)) ? Path.Join(home, path[2..])
        : path;

    private static List<BranchPull>? BranchPulls(string text)
    {
        try
        {
            if (JsonNode.Parse(text) is not JsonArray items)
            {
                return null;
            }

            var pulls = new List<BranchPull>();
            foreach (var item in items)
            {
                if (item is not JsonObject pull || pull["number"] is not JsonValue number || number.GetValueKind() != JsonValueKind.Number || !number.TryGetValue<int>(out var n))
                {
                    return null;
                }

                if (!TryText(pull, "state", out var state) || !TryText(pull, "headRefOid", out var oid))
                {
                    return null;
                }

                var cross = pull["isCrossRepository"]?.GetValueKind() switch
                {
                    null => false,
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    _ => (bool?)null,
                };
                if (cross is null)
                {
                    return null;
                }

                pulls.Add(new(n, state, oid, cross.Value));
            }

            return pulls;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryText(JsonObject pull, string key, out string value)
    {
        value = string.Empty;
        if (!pull.TryGetPropertyValue(key, out var raw))
        {
            return true;
        }

        if (raw is JsonValue text && text.GetValueKind() == JsonValueKind.String)
        {
            value = text.GetValue<string>();
            return true;
        }

        return false;
    }

    private async Task<bool> IsWorktreeAsync(string path, CancellationToken cancellationToken)
    {
        var top = (await git.RunAsync(path, ["rev-parse", "--show-toplevel"], cancellationToken: cancellationToken).ConfigureAwait(false)).OutOrEmpty;
        return top.Length > 0 && Comparable(top) == Comparable(path);
    }

    private async Task<string> CommonDirAsync(string path, CancellationToken cancellationToken)
    {
        var common = (await git.RunAsync(path, ["rev-parse", "--path-format=absolute", "--git-common-dir"], cancellationToken: cancellationToken).ConfigureAwait(false)).OutOrEmpty;
        return common.Length > 0 ? Comparable(common) : string.Empty;
    }

    private async Task<bool> BelongsToAsync(string path, string? repo, CancellationToken cancellationToken)
    {
        if (repo is null)
        {
            return false;
        }

        var mine = await CommonDirAsync(path, cancellationToken).ConfigureAwait(false);
        return mine.Length > 0 && mine == await CommonDirAsync(repo, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> IsDirtyAsync(string path, CancellationToken cancellationToken) =>
        (await git.RunAsync(path, ["status", "--porcelain"], cancellationToken: cancellationToken).ConfigureAwait(false)).OutOrEmpty.Length > 0;

    // Commits on HEAD found on no remote and not in the merged oid; falls back when the oid is unknown here.
    private async Task<int> BeyondMergeAsync(string path, string mergedOid)
    {
        if (mergedOid.Length > 0)
        {
            var count = await git.OutAsync(path, "rev-list", "--count", "HEAD", "--not", "--remotes", mergedOid).ConfigureAwait(false);
            if (count.Length > 0 && count.All(char.IsAsciiDigit) && int.TryParse(count, CultureInfo.InvariantCulture, out var beyond))
            {
                return beyond;
            }

            if (await git.OutAsync(path, "rev-parse", "HEAD").ConfigureAwait(false) == mergedOid)
            {
                return 0;
            }
        }

        return await Exposure.CountAsync(git, path, "rev-list", "--count", "HEAD", "--not", "--remotes").ConfigureAwait(false);
    }

    private async Task<bool> InDefaultAsync(string path, CancellationToken cancellationToken)
    {
        var head = await git.OutAsync(path, "rev-parse", "--abbrev-ref", "origin/HEAD").ConfigureAwait(false);
        var fallback = head.Length > 0 ? head : "origin/main";
        return (await git.RunAsync(path, ["merge-base", "--is-ancestor", "HEAD", fallback], cancellationToken: cancellationToken).ConfigureAwait(false)).Ok;
    }

    // Why the worktree must now be kept, re-checked just before removal; empty when it may go.
    private async Task<string> StillSafeAsync(string path, WorktreeRow row, string? repo, CancellationToken cancellationToken)
    {
        if (row.Verdict == Missing)
        {
            return Directory.Exists(path) || File.Exists(path) ? "its folder is back" : string.Empty;
        }

        if (repo is null)
        {
            return "its project is no longer registered";
        }

        if (row.Verdict == Leftover)
        {
            var compared = await LeftoverMatchesAsync(path, repo, row.Branch, cancellationToken).ConfigureAwait(false);
            return compared.Matches ? string.Empty : $"changed since review: {compared.Detail}";
        }

        if (!await BelongsToAsync(path, repo, cancellationToken).ConfigureAwait(false))
        {
            return "its git is not the registered project's";
        }

        return await IsDirtyAsync(path, cancellationToken).ConfigureAwait(false) ? "changed since review: uncommitted or untracked files" : string.Empty;
    }

    private async Task<string> RemoveAsync(OrcaRuntime runtime, OrcaWorktree worktree, WorktreeRow row, string? repo, CancellationToken cancellationToken)
    {
        var path = worktree.Path;
        if (await StillSafeAsync(path, row, repo, cancellationToken).ConfigureAwait(false) is { Length: > 0 } keep)
        {
            return $"skipped: {keep}";
        }

        if (row.Verdict == Leftover)
        {
            try
            {
                await new TreeRemoval(proc).RemoveAsync(path, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return $"failed: could not delete the folder: {e.Message}";
            }
        }

        if (row.Verdict is Leftover or Missing && repo is not null)
        {
            _ = await git.RunAsync(repo, ["worktree", "prune"], cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        bool confirmed;
        try
        {
            confirmed = await runtime.RemoveWorktreeAsync(worktree.Id, cancellationToken).ConfigureAwait(false);
        }
        catch (OrcaUnavailableException e)
        {
            return await GoneAsync(runtime, worktree.Id, cancellationToken).ConfigureAwait(false)
                ? "removed (git had already dropped it from Orca)"
                : $"failed: {e.Message}";
        }

        return confirmed || await GoneAsync(runtime, worktree.Id, cancellationToken).ConfigureAwait(false)
            ? "removed"
            : "failed: Orca accepted the removal but still lists the worktree";
    }

    // True when Orca no longer lists the worktree; a failed listing proves nothing.
    private static async Task<bool> GoneAsync(OrcaRuntime runtime, string worktreeId, CancellationToken cancellationToken)
    {
        try
        {
            return (await runtime.WorktreesAsync(cancellationToken).ConfigureAwait(false)).All(w => w.Id != worktreeId);
        }
        catch (OrcaUnavailableException)
        {
            return false;
        }
    }

    private sealed record BranchPull(int Number, string State, string HeadOid, bool CrossRepository);
}
