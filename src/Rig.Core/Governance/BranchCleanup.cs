using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Rig.Core.Processes;

namespace Rig.Core.Governance;

/// <summary>What cleanup thinks of one remote branch.</summary>
/// <param name="Branch">The branch, without <c>origin/</c>.</param>
/// <param name="Status"><c>merged</c>, <c>open-pr</c>, <c>no-pr</c> or <c>protected</c>.</param>
/// <param name="Pr">The PR number that decided it, when there was one.</param>
public sealed record BranchVerdict(string Branch, string Status, long? Pr = null)
{
    /// <summary>Whether cleanup may delete it: only a branch whose PR merged.</summary>
    public bool Deletable => Status == "merged";
}

/// <summary>
/// The cleanup picture for one repo. A <see cref="Skipped"/> plan carries no verdicts, so a caller that ignores the
/// reason still deletes nothing.
/// </summary>
/// <param name="Repo">The checkout's folder name.</param>
/// <param name="Verdicts">One per remote branch.</param>
/// <param name="Slug">The origin's <c>owner/name</c>, to dedupe repeat clones.</param>
/// <param name="Skipped">Why the repo must not be pruned at all; empty when it may be.</param>
/// <param name="Unreadable">Skipped because git or gh could not be read, which a run should report as a failure.</param>
public sealed record CleanupPlan(string Repo, IReadOnlyList<BranchVerdict> Verdicts, string Slug = "", string Skipped = "", bool Unreadable = false)
{
    /// <summary>The verdicts with <paramref name="status"/>.</summary>
    public IReadOnlyList<BranchVerdict> Of(string status) => [.. Verdicts.Where(v => v.Status == status)];

    /// <summary>The branches cleanup may delete.</summary>
    public IReadOnlyList<BranchVerdict> Deletable => [.. Verdicts.Where(v => v.Deletable)];
}

/// <summary>What deleting one remote branch did.</summary>
/// <param name="Branch">The branch.</param>
/// <param name="Deleted">Whether the push deleted it.</param>
/// <param name="Error">git's error when it did not.</param>
public sealed record BranchDeletion(string Branch, bool Deleted, string Error = "");

/// <summary>
/// Deletes remote branches whose work has landed. <c>delete_branch_on_merge</c> covers only branches merged while it
/// is on, so repos accumulate a tail of dead refs. Under squash merge a branch's commits never become ancestors of
/// main, so merged-ness is read from the PR that merged it, through gh, not from the commit graph. A branch no PR ever
/// referenced is reported, never deleted. Ported from <c>rig.repo_governance.branch_cleanup</c>.
/// </summary>
/// <param name="proc">Runs gh.</param>
/// <param name="git">Runs git.</param>
/// <param name="repos">The origin identity of each checkout.</param>
/// <param name="policies">The policy's <c>branch_cleanup</c> rules.</param>
public sealed class BranchCleanup(IProc proc, Git git, GovernedRepos repos, RepoPolicies policies)
{
    // gh pr list caps at 1000; the default 30 silently understates what is safe to delete on an old repo.
    private const int PrPageLimit = 500;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(120);

    private static readonly HashSet<string> PushPermissions = ["ADMIN", "MAINTAIN", "WRITE"];

    /// <summary>Whether <paramref name="permission"/> allows deleting branches.</summary>
    public static bool CanPrune(string permission) => permission is not null && PushPermissions.Contains(permission.ToUpperInvariant());

    /// <summary>
    /// Each branch's verdict, checked in order: protected, then an open PR (when the rules keep open-PR heads), then a
    /// merged one. A branch with both a merged and an open PR was reused, and the open PR is the live fact.
    /// </summary>
    public static IReadOnlyList<BranchVerdict> Classify(
        IEnumerable<string> branches, IReadOnlyDictionary<string, long> mergedHeads, IReadOnlyDictionary<string, long> openHeads, BranchCleanupRules rules)
    {
        ArgumentNullException.ThrowIfNull(branches);
        ArgumentNullException.ThrowIfNull(mergedHeads);
        ArgumentNullException.ThrowIfNull(openHeads);
        ArgumentNullException.ThrowIfNull(rules);
        return [.. branches.Select(branch =>
            rules.IsProtected(branch) ? new BranchVerdict(branch, "protected")
            : rules.ProtectOpenPrHeads && openHeads.TryGetValue(branch, out var open) ? new BranchVerdict(branch, "open-pr", open)
            : mergedHeads.TryGetValue(branch, out var merged) ? new BranchVerdict(branch, "merged", merged)
            : new BranchVerdict(branch, "no-pr"))];
    }

    /// <summary>
    /// The plan for <paramref name="repoPath"/>. Reads only, though it fetches with <c>--prune</c> so branches already
    /// deleted on the server are not reported. Skipped when you cannot push to the origin (a clone of someone else's
    /// repo has their branches on it), when the policy disables cleanup, or when gh's PR lists cannot be read.
    /// </summary>
    public async Task<CleanupPlan> PlanAsync(string repoPath, CancellationToken cancellationToken = default)
    {
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(repoPath));
        var identity = await repos.IdentityAsync(repoPath, cancellationToken).ConfigureAwait(false);
        if (!CanPrune(identity.Permission))
        {
            return new(name, [], identity.Slug, identity.Resolved ? $"no push access to {identity.Slug}" : "could not resolve origin");
        }

        var rules = policies.BranchCleanup(name);
        if (!rules.Enabled)
        {
            return new(name, [], identity.Slug, "cleanup disabled in policy");
        }

        if (await RemoteBranchesAsync(repoPath, cancellationToken).ConfigureAwait(false) is not { } branches)
        {
            return new(name, [], identity.Slug, "could not fetch or list remote branches with git", Unreadable: true);
        }

        var merged = await PrHeadsAsync(repoPath, "merged", cancellationToken).ConfigureAwait(false);
        var open = await PrHeadsAsync(repoPath, "open", cancellationToken).ConfigureAwait(false);
        return merged is null || open is null
            ? new(name, [], identity.Slug, "could not read pull requests from gh", Unreadable: true)
            : new(name, Classify(branches, merged, open, rules), identity.Slug);
    }

    /// <summary>Deletes <c>origin/<paramref name="branch"/></c>, returning the outcome rather than throwing.</summary>
    public async Task<BranchDeletion> DeleteAsync(string repoPath, string branch, CancellationToken cancellationToken = default)
    {
        var ran = await git.RunAsync(repoPath, ["push", "origin", "--delete", branch], Timeout, cancellationToken: cancellationToken).ConfigureAwait(false);
        return ran.Ok ? new(branch, true) : new(branch, false, ran.Err.Trim());
    }

    // The remote branches without origin/, sorted and deduped, after pruning refs the server no longer has; null when
    // either git call fails, since stale or missing refs are not a plan to act on.
    private async Task<List<string>?> RemoteBranchesAsync(string repoPath, CancellationToken cancellationToken)
    {
        var fetched = await git.RunAsync(repoPath, ["fetch", "--prune", "--quiet"], Timeout, cancellationToken: cancellationToken).ConfigureAwait(false);
        var listed = await git.RunAsync(repoPath, ["branch", "-r", "--format=%(refname:short)"], Timeout, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!fetched.Ok || !listed.Ok)
        {
            return null;
        }

        return [.. listed.Out.Split('\n')
            .Select(line => line.Trim())
            .Where(name => name.Length > 0 && !name.Contains("->", StringComparison.Ordinal) && name != "origin")
            .Select(name => name.StartsWith("origin/", StringComparison.Ordinal) ? name["origin/".Length..] : name)
            .Distinct()
            .Order(StringComparer.Ordinal)];
    }

    // {head ref: highest PR number} for PRs in state; null when gh gave no readable list, which is not the same as
    // empty: reading it as "no PRs" would let a branch with an open PR be classified as merged and deleted. A merged
    // PR from a fork says nothing about origin's branch of the same name, so those are left out.
    private async Task<Dictionary<string, long>?> PrHeadsAsync(string repoPath, string state, CancellationToken cancellationToken)
    {
        var limit = PrPageLimit.ToString(CultureInfo.InvariantCulture);
        var ran = await proc.RunAsync(
            new ProcRequest(["gh", "pr", "list", "--state", state, "--limit", limit, "--json", "number,headRefName,isCrossRepository"]) { WorkingDirectory = repoPath, Timeout = Timeout },
            cancellationToken).ConfigureAwait(false);
        if (!ran.Ok || string.IsNullOrWhiteSpace(ran.Out) || GitHubCalls.Parse(ran.Out) is not JsonArray rows)
        {
            return null;
        }

        var heads = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (row is not JsonObject fields
                || fields["number"] is not { } numberNode || numberNode.GetValueKind() != JsonValueKind.Number || !numberNode.AsValue().TryGetValue<long>(out var number)
                || fields["headRefName"] is not { } headNode || headNode.GetValueKind() != JsonValueKind.String)
            {
                return null;
            }

            if (state == "merged" && fields["isCrossRepository"] is { } fork && fork.GetValueKind() == JsonValueKind.True)
            {
                continue;
            }

            // Keep the highest PR number per head: a reused branch is described by its most recent PR.
            var head = headNode.GetValue<string>();
            if (head.Length > 0 && number > heads.GetValueOrDefault(head))
            {
                heads[head] = number;
            }
        }

        return heads;
    }
}
