using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Rig.Core.GitHub;
using Rig.Core.Orca;
using Rig.Core.Processes;
using Rig.Core.Roots;

namespace Rig.Core.Estate;

/// <summary>Rig's daily shelving policy; supplied from config, never from Orca cleanup settings.</summary>
/// <param name="IdleDays">Days without file, commit or sidebar activity.</param>
public sealed record OrcaShelvingPolicy(int IdleDays = 14)
{
    /// <summary>Rejects an accidental zero-day unattended cleanup.</summary>
    public void Validate() => ArgumentOutOfRangeException.ThrowIfLessThan(IdleDays, 1);
}

/// <summary>A durable triage row, including branches without a PR and refusals.</summary>
public sealed record OrcaShelvingRow(
    string WorktreeId, string Repo, string Path, string Branch, string Outcome, string Reason,
    string Shelf = "", string Sha = "", Uri? PrUrl = null, string TraceId = "", string ObservedAt = "");

/// <summary>
/// The daily rig workflow: observe inactivity, plan the existing shelver, verify a recovery shelf, make it discoverable
/// in GitHub, then remove the disk worktree and its Orca entry. See target architecture's desired → plan → apply (#30).
/// </summary>
public sealed class OrcaShelving(Git git, IProc proc, TimeProvider clock, string ledger)
{
    /// <summary>Per-run and per-worktree spans, registered by the CLI's existing trace writer.</summary>
    public static readonly ActivitySource Source = new("Rig.Estate");

    /// <summary>The newest meaningful file or commit change. Null means it could not be proved.</summary>
    public async Task<DateTimeOffset?> ChangedAtAsync(string path, CancellationToken token = default)
    {
        var head = await git.RunAsync(path, ["--no-optional-locks", "show", "-s", "--format=%ct", "HEAD"], cancellationToken: token).ConfigureAwait(false);
        var listed = await git.RunAsync(path, ["--no-optional-locks", "ls-files", "--cached", "--others", "--exclude-standard", "-z"], cancellationToken: token).ConfigureAwait(false);
        if (!head.Ok || !listed.Ok || !long.TryParse(head.Out.Trim(), CultureInfo.InvariantCulture, out var seconds))
        {
            return null;
        }

        var newest = DateTimeOffset.FromUnixTimeSeconds(seconds);
        foreach (var relative in listed.Out.Split('\0', StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            var file = new FileInfo(System.IO.Path.Join(path, relative));
            try
            {
                if (file.LinkTarget is not null || Directory.Exists(file.FullName))
                {
                    return null; // Submodule or link: do not walk out of this tree.
                }

                // A deletion is activity too. Its containing directory records the removal.
                var written = file.Exists ? file.LastWriteTimeUtc : Directory.GetLastWriteTimeUtc(file.DirectoryName!);
                if (written == DateTime.MinValue || written.Year < 1970)
                {
                    return null;
                }

                newest = written > newest ? written : newest;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        return newest;
    }

    /// <summary>Strictly older than the threshold, using the newest signal, with missing and future signals kept.</summary>
    public static bool IsStale(DateTimeOffset now, DateTimeOffset? changed, double? sidebarMs, int idleDays) =>
        changed is { } at && sidebarMs is { } sidebar && double.IsFinite(sidebar) && sidebar > 0
        && at < now.AddDays(-idleDays) && sidebar < now.AddDays(-idleDays).ToUnixTimeMilliseconds();

    /// <summary>One finite sweep. Reports every row immediately so interrupted runs keep their evidence.</summary>
    public async Task<IReadOnlyList<OrcaShelvingRow>> RunAsync(
        OrcaRuntime runtime, string cwd, OrcaShelvingPolicy policy, bool apply,
        Action<OrcaShelvingRow> record, IReadOnlySet<string>? only = null, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(record);
        policy.Validate();
        using var run = Source.StartActivity("estate.orca_shelving");
        var projects = (await runtime.ProjectsAsync(token).ConfigureAwait(false)).ToDictionary(p => p.Id, StringComparer.Ordinal);
        var worktrees = await runtime.WorktreesAsync(token).ConfigureAwait(false);
        var activity = (await runtime.ActivityAsync(token).ConfigureAwait(false)).ToDictionary(a => a.WorktreeId, StringComparer.Ordinal);
        var busy = await Busy.SnapshotAsync(proc, token).ConfigureAwait(false);
        var shelver = new Shelver(git, new TreeRemoval(proc), clock, ledger);
        var store = new ShelfStore(git);
        var ownership = new OrcaWorktrees(git, proc);
        var rows = new List<OrcaShelvingRow>();
        var latest = new Dictionary<string, OrcaShelvingRow>(StringComparer.Ordinal);
        var sink = record;
        record = row =>
        {
            row = row with { ObservedAt = clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture) };
            latest[row.WorktreeId] = row;
            sink(row);
        };
        foreach (var workspace in worktrees.Where(w => !w.IsMain && (only is null || only.Contains(w.RepoId))))
        {
            token.ThrowIfCancellationRequested();
            using var span = Source.StartActivity("estate.orca_shelve_worktree");
            span?.SetTag("worktree.id", workspace.Id);
            span?.SetTag("worktree.path", workspace.Path);
            var row = new OrcaShelvingRow(workspace.Id, projects.GetValueOrDefault(workspace.RepoId)?.DisplayName ?? workspace.RepoId,
                workspace.Path, OrcaWorktrees.ShortBranch(workspace.Branch), "kept", "", TraceId: span?.TraceId.ToString() ?? "");
            try
            {
                row = await ProcessAsync(workspace, row).ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ShelfStoreException or OrcaUnavailableException or ProcException or InvalidDataException or JsonException or FormatException)
            {
                row = latest.GetValueOrDefault(row.WorktreeId, row) with { Outcome = "failed", Reason = e.Message };
                span?.SetStatus(ActivityStatusCode.Error, e.Message);
            }

            span?.SetTag("shelving.outcome", row.Outcome);
            span?.SetTag("shelving.ref", row.Shelf);
            span?.SetTag("shelving.pr", row.PrUrl?.AbsoluteUri);
            record(row);
            rows.Add(latest.GetValueOrDefault(row.WorktreeId, row));
        }

        return rows;

        async Task<OrcaShelvingRow> ProcessAsync(OrcaWorktree workspace, OrcaShelvingRow row)
        {
            if (workspace.HostId != "local" || workspace.Pinned || OrcaWorktrees.IsCurrent(cwd, workspace.Path))
            {
                return row with { Reason = "remote, pinned or current workspace" };
            }

            if (!activity.TryGetValue(workspace.Id, out var live) || live.AgentStates.Any(s => s is not ("done" or "idle")))
            {
                return row with { Reason = "agent active, waiting, or activity unknown" };
            }

            if (workspace.LastActivityMs is not { } last || last >= clock.GetUtcNow().AddDays(-policy.IdleDays).ToUnixTimeMilliseconds())
            {
                return row with { Reason = "recent or unknown sidebar activity" };
            }

            var changed = await ChangedAtAsync(workspace.Path, token).ConfigureAwait(false);
            if (changed is null)
            {
                return row with { Outcome = "needs-decision", Reason = "file or commit activity unreadable (including source links/submodules)" };
            }

            if (!IsStale(clock.GetUtcNow(), changed, workspace.LastActivityMs, policy.IdleDays))
            {
                return row with { Reason = "recent file or commit activity" };
            }

            if (!projects.TryGetValue(workspace.RepoId, out var project) || !await ownership.BelongsToAsync(workspace.Path, project.Path, token).ConfigureAwait(false))
            {
                return row with { Reason = "cannot prove worktree belongs to its registered project" };
            }

            var tree = (await Worktree.ListAsync(git, project.Path, token).ConfigureAwait(false))
                .SingleOrDefault(t => PathResolution.Same(PathResolution.Resolve(t.Path), PathResolution.Resolve(workspace.Path)));
            if (tree is null || tree.IsMain || tree.Detached || tree.Unborn)
            {
                return row with { Reason = "not a registered linked branch worktree" };
            }

            var origin = await git.OutAsync(project.Path, "remote", "get-url", "origin").ConfigureAwait(false);
            var slug = RepoSlug.FromRemote(origin)?.FullName;
            if (slug is null)
            {
                return row with { Reason = "no GitHub origin for recovery PR" };
            }

            var pr = await PullAsync(slug, tree.Branch, token).ConfigureAwait(false);
            // Remote work can progress without touching the local checkout. Never shelve that PR as stale.
            if (pr is not null && pr.State == "OPEN" && (pr.Head != tree.Head || pr.Updated >= clock.GetUtcNow().AddDays(-policy.IdleDays)))
            {
                return row with { Reason = "PR has newer commits or recent discussion/activity", PrUrl = pr.Url };
            }

            var prs = pr is null ? new Dictionary<string, PullRequest>(StringComparer.Ordinal)
                : new Dictionary<string, PullRequest>(StringComparer.Ordinal) { [tree.Branch] = new(pr.Number, pr.State, tree.Branch, pr.Head) };
            var plan = await shelver.PlanAsync(project.Path, tree, await shelver.ShelfUserAsync(project.Path).ConfigureAwait(false),
                await store.RemoteShelvesAsync(project.Path).ConfigureAwait(false), prs,
                new ShelveOptions(TimeSpan.Zero, AlwaysMarker: true), busy).ConfigureAwait(false);
            if (busy.WorkingIn(workspace.Path) is null)
            {
                plan.DecisionNeeded.Add("cannot prove no process is working in the worktree");
            }

            row = row with { Shelf = plan.Ref, PrUrl = pr?.Url };
            if (plan.DecisionNeeded.Count > 0)
            {
                return row with { Outcome = "needs-decision", Reason = string.Join("; ", plan.DecisionNeeded) };
            }

            if (!apply)
            {
                return row with { Outcome = "would-shelve", Reason = pr is { State: "OPEN" } ? "verify shelf, label PR stale/shelved, remove" : "verify shelf, create TEMP draft PR labelled shelved, remove" };
            }

            // Re-check live ownership/activity before the first write, and again just before removal.
            async Task<bool> StillQuietAsync()
            {
                var fresh = (await runtime.WorktreesAsync(token).ConfigureAwait(false)).SingleOrDefault(w => w.Id == workspace.Id);
                var agents = (await runtime.ActivityAsync(token).ConfigureAwait(false)).SingleOrDefault(a => a.WorktreeId == workspace.Id);
                var processes = await Busy.SnapshotAsync(proc, token).ConfigureAwait(false);
                return fresh is not null && !fresh.Pinned && fresh.Path == workspace.Path && fresh.Branch == workspace.Branch
                    && agents is not null && agents.AgentStates.All(s => s is "done" or "idle")
                    && processes.WorkingIn(workspace.Path) == false
                    && IsStale(clock.GetUtcNow(), await ChangedAtAsync(workspace.Path, token).ConfigureAwait(false), fresh.LastActivityMs, policy.IdleDays);
            }

            if (!await StillQuietAsync().ConfigureAwait(false))
            {
                return row with { Reason = "activity changed before shelving" };
            }

            var result = await shelver.ApplyAsync(plan, async (_, sha) =>
            {
                row = row with { Outcome = "shelf-verified", Sha = sha, Reason = "verified on origin; preparing recovery PR" };
                record(row); // Durable before the first GitHub mutation or deletion.
                var currentPr = await PullAsync(slug, tree.Branch, token).ConfigureAwait(false);
                if (currentPr != pr)
                {
                    return new ShelfStep(false, "PR changed since planning");
                }

                await LabelAsync(slug, "shelved", token).ConfigureAwait(false);
                if (currentPr is { State: "OPEN" })
                {
                    await LabelAsync(slug, "stale", token).ConfigureAwait(false);
                    await GhAsync(["pr", "edit", currentPr.Number.ToString(CultureInfo.InvariantCulture), "--repo", slug, "--add-label", "stale,shelved"], token).ConfigureAwait(false);
                    row = row with { PrUrl = currentPr.Url };
                }
                else
                {
                    var branch = plan.Ref["refs/heads/".Length..];
                    var existing = await PullAsync(slug, branch, token).ConfigureAwait(false);
                    if (existing is { State: "OPEN" })
                    {
                        await GhAsync(["pr", "edit", existing.Number.ToString(CultureInfo.InvariantCulture), "--repo", slug, "--add-label", "shelved"], token).ConfigureAwait(false);
                        row = row with { PrUrl = existing.Url };
                    }
                    else if (existing is not null)
                    {
                        return new ShelfStep(false, "recovery PR was closed; a person must decide before cleanup");
                    }
                    else
                    {
                        var body = $"Automatically shelved by rig after {policy.IdleDays} days without file, commit or workspace activity.\n\n"
                            + $"Original branch: `{tree.Branch}`\n\nShelf: `{plan.Ref}`\n\nVerified commit: `{sha}`\n\n"
                            + $"Restore from the clone with `rig estate unshelve {plan.Name}`. This is a recovery snapshot; review and resume the work before merging.\n\nTrace: `{row.TraceId}`";
                        var created = await GhAsync(["pr", "create", "--repo", slug, "--head", branch, "--draft", "--title", $"TEMP: Shelved {tree.Branch}", "--body", body, "--label", "shelved"], token).ConfigureAwait(false);
                        row = row with { PrUrl = new Uri(created.Trim()) };
                    }
                }

                if (!await StillQuietAsync().ConfigureAwait(false))
                {
                    return new ShelfStep(false, "activity changed during recovery PR creation");
                }

                row = row with { Outcome = "removing", Reason = "remote shelf and recovery PR ready; final file comparison follows" };
                record(row);
                return new ShelfStep(true, row.PrUrl?.AbsoluteUri ?? "");
            }).ConfigureAwait(false);
            row = row with { Outcome = result.Outcome, Reason = result.Detail, Sha = result.Sha };
            record(row); // Disk removal is recorded before asking Orca to forget its workspace.
            if (result.Outcome is "shelved" or "removed")
            {
                var removed = await runtime.RemoveWorktreeAsync(workspace.Id, token).ConfigureAwait(false);
                if (!removed && (await runtime.WorktreesAsync(token).ConfigureAwait(false)).Any(w => w.Id == workspace.Id))
                {
                    return row with { Outcome = "failed", Reason = "shelved safely, but Orca has not removed the sidebar entry" };
                }

                row = row with { Reason = $"{row.Reason}; Orca sidebar entry removed" };
            }

            return row;
        }
    }

    private sealed record Pull(int Number, string State, string Head, Uri Url, DateTimeOffset Updated);

    private async Task<Pull?> PullAsync(string slug, string branch, CancellationToken token)
    {
        var text = await GhAsync(["pr", "list", "--repo", slug, "--head", branch, "--state", "all", "--limit", "100", "--json", "number,state,headRefOid,isCrossRepository,url,updatedAt"], token).ConfigureAwait(false);
        var items = JsonNode.Parse(text) as JsonArray ?? throw new InvalidDataException("GitHub PR list is not an array");
        return items.Select(item => item as JsonObject ?? throw new InvalidDataException("Invalid PR"))
            .Where(item => item["isCrossRepository"]?.GetValue<bool>() == false)
            .Select(item => new Pull(item["number"]!.GetValue<int>(), item["state"]!.GetValue<string>(), item["headRefOid"]!.GetValue<string>(),
                new Uri(item["url"]!.GetValue<string>()), DateTimeOffset.Parse(item["updatedAt"]!.GetValue<string>(), CultureInfo.InvariantCulture)))
            .MaxBy(pr => pr.Number);
    }

    private async Task LabelAsync(string slug, string name, CancellationToken token)
    {
        var labels = JsonNode.Parse(await GhAsync(["label", "list", "--repo", slug, "--search", name, "--limit", "100", "--json", "name"], token).ConfigureAwait(false)) as JsonArray
            ?? throw new InvalidDataException("GitHub label list is not an array");
        if (!labels.Any(label => label?["name"]?.GetValue<string>() == name))
        {
            await GhAsync(["label", "create", name, "--repo", slug, "--description", "Work preserved by rig's daily shelving workflow", "--color", "D4C5F9"], token).ConfigureAwait(false);
        }
    }

    private async Task<string> GhAsync(IReadOnlyList<string> args, CancellationToken token)
    {
        var ran = await proc.RunAsync(new ProcRequest(["gh", .. args]) { Timeout = TimeSpan.FromSeconds(90), Environment = new Dictionary<string, string> { ["GH_PROMPT_DISABLED"] = "1" } }, token).ConfigureAwait(false);
        if (!ran.Ok)
        {
            throw new IOException($"gh {args[0]} {args[1]} failed: {ran.Err.Trim()}");
        }

        return ran.Out;
    }
}
