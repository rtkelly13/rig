using System.Diagnostics.CodeAnalysis;

[assembly: SuppressMessage(
    "Design",
    "CA1054",
    Justification = "A PR URL is a wire value: it is kept exactly as GitHub spells it and emitted into the work JSON schema.",
    Scope = "namespaceanddescendants",
    Target = "~N:Rig.Core.WorkItems")]
[assembly: SuppressMessage(
    "Design",
    "CA1056",
    Justification = "A PR URL is a wire value: it is kept exactly as GitHub spells it and emitted into the work JSON schema.",
    Scope = "namespaceanddescendants",
    Target = "~N:Rig.Core.WorkItems")]

namespace Rig.Core.WorkItems;

/// <summary>Where a work item is in its life. The precedence is in <see cref="WorkJoin.DeriveState"/>.</summary>
public enum WorkState
{
    /// <summary>Open work: a PR, a running session, uncommitted changes or unpushed commits.</summary>
    Active,

    /// <summary>The newest PR merged; a cleanup candidate.</summary>
    Merged,

    /// <summary>The newest PR was closed without merging.</summary>
    Closed,

    /// <summary>Nothing has happened for longer than the stale window.</summary>
    Stale,

    /// <summary>The repo's default branch. Home: never finished and never stale.</summary>
    Trunk,

    /// <summary>The bucket for sessions outside every known checkout.</summary>
    Unattached,
}

/// <summary>The newest pull request's state.</summary>
public enum PrStatus
{
    /// <summary>Open, ready for review.</summary>
    Open,

    /// <summary>Open as a draft.</summary>
    Draft,

    /// <summary>Merged.</summary>
    Merged,

    /// <summary>Closed without merging.</summary>
    Closed,
}

/// <summary>Whether an empty PR field can be trusted to mean "no PR".</summary>
public enum PrLookup
{
    /// <summary>GitHub answered, from the network or the cache: no PR really means none.</summary>
    Ok,

    /// <summary>Not asked: a trunk branch, a detached head, the unattached bucket, or offline.</summary>
    Skipped,

    /// <summary>Asked and failed: no gh, not authenticated, offline, or not a GitHub remote.</summary>
    Unavailable,
}

/// <summary>How a Claude Code session ended, or whether it is still running.</summary>
public enum WorkSessionState
{
    /// <summary>Running and working.</summary>
    Live,

    /// <summary>Running and idle at the prompt.</summary>
    WaitingOnUser,

    /// <summary>Not running; the process died while a tool call or prompt was in flight.</summary>
    DiedMidTask,

    /// <summary>Not running; the process died after Claude had replied.</summary>
    DiedIdle,

    /// <summary>Not running; Claude Code exited normally.</summary>
    EndedClean,

    /// <summary>The user ran <c>/clear</c>.</summary>
    Cleared,
}

/// <summary>How each enum is spelled in JSON and in the text output.</summary>
public static class WorkWire
{
    /// <summary>Session states that mean a Claude process is attached to the work right now.</summary>
    public static IReadOnlySet<WorkSessionState> RunningStates { get; } =
        new HashSet<WorkSessionState> { WorkSessionState.Live, WorkSessionState.WaitingOnUser };

    /// <summary><paramref name="state"/> as the JSON and CLI spell it.</summary>
    public static string Wire(this WorkState state) => state switch
    {
        WorkState.Active => "active",
        WorkState.Merged => "merged",
        WorkState.Closed => "closed",
        WorkState.Stale => "stale",
        WorkState.Trunk => "trunk",
        _ => "unattached",
    };

    /// <summary><paramref name="status"/> as the JSON and CLI spell it.</summary>
    public static string Wire(this PrStatus status) => status switch
    {
        PrStatus.Open => "open",
        PrStatus.Draft => "draft",
        PrStatus.Merged => "merged",
        _ => "closed",
    };

    /// <summary><paramref name="lookup"/> as the JSON and CLI spell it.</summary>
    public static string Wire(this PrLookup lookup) => lookup switch
    {
        PrLookup.Ok => "ok",
        PrLookup.Skipped => "skipped",
        _ => "unavailable",
    };

    /// <summary><paramref name="state"/> as the JSON and CLI spell it.</summary>
    public static string Wire(this WorkSessionState state) => state switch
    {
        WorkSessionState.Live => "live",
        WorkSessionState.WaitingOnUser => "waiting-on-user",
        WorkSessionState.DiedMidTask => "died-mid-task",
        WorkSessionState.DiedIdle => "died-idle",
        WorkSessionState.EndedClean => "ended-clean",
        _ => "cleared",
    };

    /// <summary>Whether <paramref name="state"/> means a Claude process is running now.</summary>
    public static bool IsRunning(this WorkSessionState state) => RunningStates.Contains(state);
}

/// <summary>One working tree: a repo's main checkout or one of its linked worktrees.</summary>
/// <param name="Path">The checkout's folder.</param>
/// <param name="Branch">The branch checked out; empty for a detached HEAD.</param>
/// <param name="IsMainCheckout">This is the clone's own checkout rather than a linked worktree.</param>
/// <param name="Dirty">Modified, staged or untracked files; null when the status check was skipped or failed.</param>
public sealed record Checkout(string Path, string Branch, bool IsMainCheckout, bool? Dirty = null);

/// <summary>One local branch, from a single <c>git for-each-ref</c> per repo.</summary>
/// <param name="Name">The branch, without <c>refs/heads/</c>.</param>
/// <param name="LastCommit">The tip's commit date.</param>
/// <param name="Upstream">What the branch tracks, without <c>refs/remotes/</c>.</param>
/// <param name="UpstreamGone">The tracked remote branch no longer exists.</param>
/// <param name="Ahead">Commits ahead of the upstream; null without an upstream or when it is gone.</param>
/// <param name="Behind">Commits behind the upstream; null without an upstream or when it is gone.</param>
/// <param name="Unpushed">Commits reachable from the tip that are on no remote-tracking ref.</param>
public sealed record BranchRef(
    string Name,
    DateTimeOffset? LastCommit = null,
    string Upstream = "",
    bool UpstreamGone = false,
    int? Ahead = null,
    int? Behind = null,
    int Unpushed = 0);

/// <summary>Everything git said about one clone.</summary>
/// <param name="Path">The clone's folder.</param>
/// <param name="Name">Display name: the clone's path relative to the code root, with forward slashes.</param>
/// <param name="DefaultBranch">The branch <c>origin/HEAD</c> names, or <c>main</c>/<c>master</c> when it names none.</param>
/// <param name="Checkouts">The main checkout and every non-prunable linked worktree.</param>
public sealed record RepoFacts(string Path, string Name, string DefaultBranch, IReadOnlyList<Checkout> Checkouts)
{
    /// <summary>The local branches, in <c>for-each-ref</c> order.</summary>
    public IReadOnlyList<BranchRef> Branches { get; init; } = [];

    /// <summary><c>owner/name</c> parsed from the origin URL; empty when it is not a GitHub remote.</summary>
    public string GithubSlug { get; init; } = "";
}

/// <summary>A (repo, branch) pair: the identity of a work item.</summary>
/// <param name="RepoPath">The clone's folder.</param>
/// <param name="Branch">The branch, or <c>(detached)</c>.</param>
public readonly record struct BranchKey(string RepoPath, string Branch);

/// <summary>
/// Compares <see cref="BranchKey"/> the way Python compares its <c>(Path, str)</c> key: paths case-insensitively on
/// Windows only, branches always ordinally.
/// </summary>
public sealed class BranchKeyComparer : IEqualityComparer<BranchKey>
{
    /// <summary>The one instance.</summary>
    public static readonly BranchKeyComparer Instance = new();

    private BranchKeyComparer()
    {
    }

    /// <inheritdoc />
    public bool Equals(BranchKey x, BranchKey y) =>
        WorkPaths.Comparer.Equals(x.RepoPath, y.RepoPath) && string.Equals(x.Branch, y.Branch, StringComparison.Ordinal);

    /// <inheritdoc />
    public int GetHashCode(BranchKey obj) => HashCode.Combine(WorkPaths.Comparer.GetHashCode(obj.RepoPath), StringComparer.Ordinal.GetHashCode(obj.Branch));
}

/// <summary>The newest pull request whose head is a work item's branch.</summary>
/// <param name="Number">The PR number.</param>
/// <param name="Status">Open, draft, merged or closed.</param>
/// <param name="Url">GitHub's URL, exactly as it answered.</param>
/// <param name="UpdatedAt">When GitHub last updated it.</param>
public sealed record PrInfo(int Number, PrStatus Status, string Url, DateTimeOffset? UpdatedAt = null);

/// <summary>One Claude Code session, classified, before attribution.</summary>
/// <param name="SessionId">The transcript's session id.</param>
/// <param name="Title">The session's label.</param>
/// <param name="State">Live, waiting, or how it ended.</param>
/// <param name="LastActivity">When it last did anything.</param>
/// <param name="LastCwd">Where it was when it last did anything; attribution uses this.</param>
/// <param name="StartCwd">Where it started; <c>claude --resume</c> must run from here.</param>
/// <param name="PromptCount">How many prompts the user typed.</param>
/// <param name="Pid">The process id, when running.</param>
public sealed record SessionFacts(
    string SessionId,
    string Title,
    WorkSessionState State,
    DateTimeOffset? LastActivity,
    string? LastCwd,
    string? StartCwd,
    int PromptCount,
    int? Pid = null);

/// <summary>Which (repo, branch) a session belongs to; a null repo means the unattached bucket.</summary>
/// <param name="RepoPath">The clone, or null when unattached.</param>
/// <param name="Branch">The branch checked out there now, or empty.</param>
/// <param name="Reason">Why it landed where it did, for the unattached bucket mostly.</param>
public sealed record Attribution(string? RepoPath, string Branch = "", string Reason = "");

/// <summary>One row of the work list: a (repo, branch) pair with its git, GitHub and Claude facts joined.</summary>
/// <param name="Key">Stable identifier: <c>&lt;repo&gt;:&lt;branch&gt;</c>, or <c>unattached</c>.</param>
/// <param name="Repo">The clone's display name; null for unattached.</param>
/// <param name="RepoPath">The clone's folder; null for unattached.</param>
/// <param name="Branch">The branch, <c>(detached)</c>, or null for unattached.</param>
/// <param name="Ticket">The first ticket id in the branch name, upper-cased; null when it names none.</param>
/// <param name="IsTrunk">The branch is the repo's default branch.</param>
/// <param name="State">The derived lifecycle state.</param>
/// <param name="Reasons">The facts that decided <paramref name="State"/>, most important first.</param>
/// <param name="Checkouts">The checkouts holding <paramref name="Branch"/>.</param>
/// <param name="BranchRef">The local branch, or null when detached or without a local ref.</param>
/// <param name="Pr">The newest PR, or null when there is none or it was not asked.</param>
/// <param name="PrLookup">Whether <paramref name="Pr"/> can be trusted to mean "no PR" when null.</param>
/// <param name="Sessions">The attributed sessions, newest first.</param>
/// <param name="LastActivity">The latest of the branch's commit, its newest session and its PR update.</param>
public sealed record WorkItem(
    string Key,
    string? Repo,
    string? RepoPath,
    string? Branch,
    string? Ticket,
    bool IsTrunk,
    WorkState State,
    IReadOnlyList<string> Reasons,
    IReadOnlyList<Checkout> Checkouts,
    BranchRef? BranchRef,
    PrInfo? Pr,
    PrLookup PrLookup,
    IReadOnlyList<SessionFacts> Sessions,
    DateTimeOffset? LastActivity)
{
    /// <summary>Any checkout holding the branch has uncommitted changes.</summary>
    public bool Dirty => Checkouts.Any(checkout => checkout.Dirty == true);

    /// <summary>The branch's unpushed commit count; 0 without a local ref.</summary>
    public int Unpushed => BranchRef?.Unpushed ?? 0;

    /// <summary>The sessions that are running now.</summary>
    public IReadOnlyList<SessionFacts> RunningSessions => [.. Sessions.Where(session => session.State.IsRunning())];
}

/// <summary>One collection of the whole estate, before filtering.</summary>
/// <param name="GeneratedAt">The moment the collection started.</param>
/// <param name="CodeRoot">The code root that was scanned.</param>
/// <param name="Items">Every item, in display order.</param>
public sealed record WorkReport(DateTimeOffset GeneratedAt, string CodeRoot, IReadOnlyList<WorkItem> Items)
{
    /// <summary>Degradations worth knowing about: a repo gh could not answer for, and so on.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
}
