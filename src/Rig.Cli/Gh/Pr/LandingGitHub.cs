using System.Text.Json;
using System.Text.Json.Serialization;
using Rig.Cli.Gh.Threads;
using Rig.Core.GitHub;
using Rig.Core.Processes;

namespace Rig.Cli.Gh.Pr;

/// <summary>
/// What <c>rig gh pr wait</c> reads from GitHub, all through <see cref="IGitHubApi"/>: the PR's landing state in one
/// GraphQL query; the base branch's active rules from REST (<c>repos/o/r/rules/branches/&lt;base&gt;</c>), because
/// GraphQL's <c>branchProtectionRule</c> only sees classic protection and rulesets are where the estate sets
/// <c>required_review_thread_resolution</c>; and the unresolved threads, the read <c>rig gh threads list</c> does.
/// </summary>
internal sealed class LandingGitHub(IGitHubApi api, ThreadsGitHub threads)
{
    /// <summary>Stop paging checks after this many pages, so a cursor GitHub keeps repeating cannot loop forever.</summary>
    public const int MaxCheckPages = 20;

    public const string LandingQuery = """
        query PullRequestLanding($owner: String!, $name: String!, $number: Int!, $cursor: String) {
          repository(owner: $owner, name: $name) {
            pullRequest(number: $number) {
              state
              isDraft
              mergeable
              mergeStateStatus
              reviewDecision
              url
              baseRefName
              autoMergeRequest { enabledAt mergeMethod }
              mergeQueueEntry { position state }
              baseRef { branchProtectionRule { requiresConversationResolution requiredStatusCheckContexts } }
              commits(last: 1) {
                nodes {
                  commit {
                    statusCheckRollup {
                      contexts(first: 100, after: $cursor) {
                        pageInfo { hasNextPage endCursor }
                        nodes {
                          __typename
                          ... on CheckRun { name status conclusion isRequired(pullRequestNumber: $number) }
                          ... on StatusContext { context state isRequired(pullRequestNumber: $number) }
                        }
                      }
                    }
                  }
                }
              }
            }
          }
        }
        """;

    /// <summary>Check-run conclusions and commit-status states that mean the check will not pass without a rerun or a push.</summary>
    private static readonly HashSet<string> FailedResults = new(StringComparer.Ordinal)
    {
        "FAILURE", "CANCELLED", "TIMED_OUT", "ACTION_REQUIRED", "STARTUP_FAILURE", "ERROR", "STALE",
    };

    /// <summary>Commit-status states that are still running. A check run is pending whenever its status is not COMPLETED.</summary>
    private static readonly HashSet<string> PendingStates = new(StringComparer.Ordinal) { "PENDING", "EXPECTED" };

    /// <summary>The PR's state, merge state, auto-merge, queue entry and every check on its head commit.</summary>
    public async Task<Snapshot> FetchSnapshotAsync(RepoSlug repo, int number, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(repo);
        var pr = await LandingPageAsync(repo, number, null, cancellationToken).ConfigureAwait(false);
        var contexts = ContextsOf(pr);
        IReadOnlyList<WireContext> nodes = [.. contexts.Nodes];
        HashSet<string> seen = new(StringComparer.Ordinal);
        while (contexts.PageInfo.HasNextPage)
        {
            var cursor = contexts.PageInfo.EndCursor;
            if (string.IsNullOrEmpty(cursor) || seen.Count >= MaxCheckPages || !seen.Add(cursor))
            {
                throw new GitHubException($"checks of {repo}#{number}: GitHub repeated a page cursor; the list is incomplete");
            }

            contexts = ContextsOf(await LandingPageAsync(repo, number, cursor, cancellationToken).ConfigureAwait(false));
            nodes = [.. nodes, .. contexts.Nodes];
        }

        var protection = pr.BaseRef?.BranchProtectionRule;
        return new Snapshot(
            pr.State,
            pr.IsDraft,
            pr.Mergeable,
            pr.MergeStateStatus,
            pr.ReviewDecision ?? string.Empty,
            pr.AutoMergeRequest is not null,
            pr.MergeQueueEntry is { } entry ? new QueueEntry(entry.Position, entry.State) : null,
            [.. nodes.Select(CheckOf)],
            pr.BaseRefName,
            pr.AutoMergeRequest?.MergeMethod)
        {
            Url = pr.Url,
            ClassicThreadResolution = protection?.RequiresConversationResolution ?? false,
            ClassicRequiredChecks = protection?.RequiredStatusCheckContexts ?? [],
        };
    }

    /// <summary>The active ruleset rules on <paramref name="baseBranch"/>. Never throws: unreadable rules come back with <see cref="Rules.ThreadResolution"/> null.</summary>
    public async Task<Rules> FetchRulesAsync(RepoSlug repo, string baseBranch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(baseBranch);
        var branch = string.Join('/', baseBranch.Split('/').Select(Uri.EscapeDataString));
        var ran = await api.ForRepo(repo)
            .Get($"rules/branches/{branch}")
            .WithQuery("per_page", 100)
            .SendAsync(cancellationToken)
            .ConfigureAwait(false);
        var rules = ran.Ok ? DecodeRules(ran.Out) : null;
        if (rules is null)
        {
            return new Rules(baseBranch, null, []);
        }

        var parameters = rules.Select(rule => rule.Parameters).OfType<WireRuleParameters>().ToList();
        return new Rules(
            baseBranch,
            parameters.Any(found => found.ThreadResolution),
            [.. parameters.SelectMany(found => found.RequiredStatusChecks ?? []).Select(check => check.Context)]);
    }

    /// <summary>
    /// One poll: the snapshot and, while the PR is open and threads could block it, its unresolved threads.
    /// <paramref name="known"/> is the rules an earlier poll read. They are read again only while they are missing or were
    /// unreadable, or when the PR has been retargeted to another base branch, and come back on the observation for the
    /// caller to pass in next time.
    /// </summary>
    public async Task<Observation> ObserveAsync(RepoSlug repo, int number, Rules? known, CancellationToken cancellationToken)
    {
        var snapshot = await FetchSnapshotAsync(repo, number, cancellationToken).ConfigureAwait(false);
        var rules = known is { ThreadResolution: not null } && known.Base == snapshot.Base
            ? known
            : await FetchRulesAsync(repo, snapshot.Base, cancellationToken).ConfigureAwait(false);

        var required = snapshot.ClassicThreadResolution ? true : rules.ThreadResolution;
        if (snapshot.State != "OPEN" || required == false)
        {
            return new Observation(snapshot, rules, []);
        }

        var all = await threads.FetchThreadsAsync(repo, number, cancellationToken).ConfigureAwait(false);
        return new Observation(snapshot, rules, [.. all.Select(item => item.Thread).Where(thread => !thread.Resolved)]);
    }

    private async Task<WirePullRequest> LandingPageAsync(RepoSlug repo, int number, string? cursor, CancellationToken cancellationToken)
    {
        var variables = new Dictionary<string, object?> { ["owner"] = repo.Owner, ["name"] = repo.Name, ["number"] = number, ["cursor"] = cursor };
        var data = await GraphQLCall.DataAsync<WireData>(api, $"landing state of {repo}#{number}", LandingQuery, variables, cancellationToken)
            .ConfigureAwait(false);
        return data.Repository?.PullRequest ?? throw new GitHubException($"{repo}#{number}: no such pull request (or no access to it)");
    }

    private static WireContexts ContextsOf(WirePullRequest pr) =>
        pr.Commits?.Nodes.FirstOrDefault()?.Commit?.StatusCheckRollup?.Contexts ?? new WireContexts([], new WirePageInfo(false));

    private static Check CheckOf(WireContext node)
    {
        var status = node.Typename == "StatusContext";
        var result = status ? node.State : node.Conclusion ?? node.Status;
        var pending = status ? PendingStates.Contains(result) : node.Status != "COMPLETED";
        var finished = FailedResults.Contains(result) ? CheckState.Failed : CheckState.Passed;
        return new Check(status ? node.Context : node.Name, pending ? CheckState.Pending : finished, node.IsRequired, result);
    }

    private static List<WireRule>? DecodeRules(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<WireRule>>(json, GraphQLCall.Wire);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // -- wire shapes ----------------------------------------------------------------------

    private sealed record WirePageInfo(bool HasNextPage, string? EndCursor = null);

    /// <summary>A check run (<c>name</c>, <c>status</c>, <c>conclusion</c>) or a commit status (<c>context</c>, <c>state</c>).</summary>
    private sealed record WireContext(
        [property: JsonPropertyName("__typename")] string Typename = "",
        string Name = "",
        string Status = "",
        string? Conclusion = null,
        string Context = "",
        string State = "",
        bool IsRequired = false);

    private sealed record WireContexts(List<WireContext> Nodes, WirePageInfo PageInfo);

    private sealed record WireRollup(WireContexts? Contexts = null);

    private sealed record WireCommit(WireRollup? StatusCheckRollup = null);

    private sealed record WireCommitNode(WireCommit? Commit = null);

    private sealed record WireCommits(List<WireCommitNode> Nodes);

    private sealed record WireAutoMerge(string? EnabledAt = null, string? MergeMethod = null);

    private sealed record WireQueueEntry(int Position = 0, string State = "");

    private sealed record WireProtection(bool RequiresConversationResolution = false, List<string>? RequiredStatusCheckContexts = null);

    private sealed record WireBaseRef(WireProtection? BranchProtectionRule = null);

    private sealed record WirePullRequest(
        string State,
        string MergeStateStatus,
        string BaseRefName,
        bool IsDraft = false,
        string Mergeable = "UNKNOWN",
        string? ReviewDecision = null,
        string Url = "",
        WireAutoMerge? AutoMergeRequest = null,
        WireQueueEntry? MergeQueueEntry = null,
        WireBaseRef? BaseRef = null,
        WireCommits? Commits = null);

    private sealed record WireRepository(WirePullRequest? PullRequest = null);

    private sealed record WireData(WireRepository? Repository = null);

    private sealed record WireRequiredCheck(string Context = "");

    private sealed record WireRuleParameters(
        [property: JsonPropertyName("required_review_thread_resolution")] bool ThreadResolution = false,
        [property: JsonPropertyName("required_status_checks")] List<WireRequiredCheck>? RequiredStatusChecks = null);

    private sealed record WireRule(string Type = "", WireRuleParameters? Parameters = null);
}
