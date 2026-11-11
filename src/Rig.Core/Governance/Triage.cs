using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Rig.Core.Processes;

namespace Rig.Core.Governance;

/// <summary>One open issue, as <c>issue-triage</c> buckets it.</summary>
/// <param name="Repo">The repo's <c>owner/name</c>.</param>
/// <param name="Number">The issue number.</param>
/// <param name="Title">The title.</param>
/// <param name="Link">The issue's URL.</param>
/// <param name="Author">The author's login; empty when GitHub names none.</param>
/// <param name="CreatedAt">When it was opened.</param>
/// <param name="UpdatedAt">When it last changed.</param>
/// <param name="CommentsCount">How many comments it has.</param>
/// <param name="Labels">Its label names.</param>
public sealed record IssueItem(
    string Repo,
    int Number,
    string Title,
    string Link,
    string Author = "",
    DateTimeOffset? CreatedAt = null,
    DateTimeOffset? UpdatedAt = null,
    int CommentsCount = 0,
    IReadOnlyList<string>? Labels = null)
{
    /// <summary>The label names, never null.</summary>
    public IReadOnlyList<string> LabelNames => Labels ?? [];

    /// <summary><c>owner/name#number</c>.</summary>
    public string ShortRef => $"{Repo}#{Number.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>The item as Python's <c>model_dump(mode="json")</c> wrote it.</summary>
    public JsonObject ToJson() => new()
    {
        ["repo"] = Repo,
        ["number"] = Number,
        ["title"] = Title,
        ["url"] = Link,
        ["author"] = Author,
        ["created_at"] = Triage.Timestamp(CreatedAt),
        ["updated_at"] = Triage.Timestamp(UpdatedAt),
        ["comments_count"] = CommentsCount,
        ["labels"] = Triage.Names(LabelNames),
    };
}

/// <summary>The issue buckets, in the order the dashboard prints them.</summary>
public enum IssueBucket
{
    /// <summary>No comments and no <c>type:</c> label.</summary>
    NeedsTriage,

    /// <summary>Labelled a bug.</summary>
    ActiveBugs,

    /// <summary>Labelled a good first issue or help wanted.</summary>
    Community,

    /// <summary>Waiting on a reproduction and idle past the stale window.</summary>
    StaleRepro,

    /// <summary>Already triaged.</summary>
    Other,
}

/// <summary>Issues grouped by <see cref="IssueBucket"/>.</summary>
/// <param name="Total">How many issues were read.</param>
/// <param name="Buckets">Each bucket's issues, in read order.</param>
public sealed record IssueDashboard(int Total, IReadOnlyDictionary<IssueBucket, IReadOnlyList<IssueItem>> Buckets)
{
    /// <summary>The issues in <paramref name="bucket"/>.</summary>
    public IReadOnlyList<IssueItem> this[IssueBucket bucket] => Buckets[bucket];

    /// <summary>The dashboard as Python's <c>model_dump(mode="json")</c> wrote it.</summary>
    public JsonObject ToJson() => new()
    {
        ["total_issues"] = Total,
        ["needs_triage"] = Items(IssueBucket.NeedsTriage),
        ["active_bugs"] = Items(IssueBucket.ActiveBugs),
        ["community"] = Items(IssueBucket.Community),
        ["stale_repro"] = Items(IssueBucket.StaleRepro),
        ["other"] = Items(IssueBucket.Other),
    };

    private JsonArray Items(IssueBucket bucket) => [.. this[bucket].Select(JsonNode (item) => item.ToJson())];
}

/// <summary>One open pull request and how its checks stand, as <c>pr-triage</c> buckets it.</summary>
/// <param name="Repo">The repo's <c>owner/name</c>.</param>
/// <param name="Number">The PR number.</param>
/// <param name="Title">The title.</param>
/// <param name="Link">The PR's URL.</param>
/// <param name="HeadBranch">The head branch.</param>
/// <param name="BaseBranch">The base branch.</param>
/// <param name="Author">The author's login; empty when GitHub names none.</param>
/// <param name="IsDraft">Whether it is a draft.</param>
/// <param name="Mergeable">GitHub's <c>mergeable</c>: MERGEABLE, CONFLICTING or UNKNOWN.</param>
/// <param name="MergeStateStatus">GitHub's <c>mergeStateStatus</c>.</param>
/// <param name="UpdatedAt">When it last changed.</param>
/// <param name="PassedChecks">Checks that passed.</param>
/// <param name="FailedChecks">Checks that failed.</param>
/// <param name="PendingChecks">Checks still queued or running.</param>
/// <param name="MissingRequiredChecks">Required checks the PR has not reported, in the policy's order.</param>
public sealed record PrItem(
    string Repo,
    int Number,
    string Title,
    string Link,
    string HeadBranch,
    string BaseBranch = "main",
    string Author = "",
    bool IsDraft = false,
    string Mergeable = "",
    string MergeStateStatus = "",
    DateTimeOffset? UpdatedAt = null,
    IReadOnlyList<string>? PassedChecks = null,
    IReadOnlyList<string>? FailedChecks = null,
    IReadOnlyList<string>? PendingChecks = null,
    IReadOnlyList<string>? MissingRequiredChecks = null)
{
    /// <summary>Checks that passed, never null.</summary>
    public IReadOnlyList<string> Passed => PassedChecks ?? [];

    /// <summary>Checks that failed, never null.</summary>
    public IReadOnlyList<string> Failed => FailedChecks ?? [];

    /// <summary>Checks still running, never null.</summary>
    public IReadOnlyList<string> Pending => PendingChecks ?? [];

    /// <summary>Required checks not reported, never null.</summary>
    public IReadOnlyList<string> Missing => MissingRequiredChecks ?? [];

    /// <summary><c>owner/name#number</c>.</summary>
    public string ShortRef => $"{Repo}#{Number.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>The item as Python's <c>model_dump(mode="json")</c> wrote it.</summary>
    public JsonObject ToJson() => new()
    {
        ["repo"] = Repo,
        ["number"] = Number,
        ["title"] = Title,
        ["url"] = Link,
        ["head_branch"] = HeadBranch,
        ["base_branch"] = BaseBranch,
        ["author"] = Author,
        ["is_draft"] = IsDraft,
        ["mergeable"] = Mergeable,
        ["merge_state_status"] = MergeStateStatus,
        ["updated_at"] = Triage.Timestamp(UpdatedAt),
        ["passed_checks"] = Triage.Names(Passed),
        ["failed_checks"] = Triage.Names(Failed),
        ["pending_checks"] = Triage.Names(Pending),
        ["missing_required_checks"] = Triage.Names(Missing),
    };
}

/// <summary>The PR buckets, in the order the dashboard prints them.</summary>
public enum PrBucket
{
    /// <summary>GitHub says CLEAN and nothing is failing or pending.</summary>
    Ready,

    /// <summary>Behind its base branch.</summary>
    Behind,

    /// <summary>Draft, conflicting, failing, missing a required check, or blocked by the ruleset.</summary>
    Blocked,

    /// <summary>Checks still running.</summary>
    Pending,

    /// <summary>Idle past the stale window.</summary>
    Stale,

    /// <summary>None of the above; not shown.</summary>
    Other,
}

/// <summary>PRs grouped by <see cref="PrBucket"/>; <see cref="PrBucket.Other"/> is counted in the total but not kept.</summary>
/// <param name="Total">How many PRs were read.</param>
/// <param name="Buckets">Each shown bucket's PRs, in read order.</param>
public sealed record PrDashboard(int Total, IReadOnlyDictionary<PrBucket, IReadOnlyList<PrItem>> Buckets)
{
    /// <summary>The PRs in <paramref name="bucket"/>.</summary>
    public IReadOnlyList<PrItem> this[PrBucket bucket] => Buckets[bucket];

    /// <summary>The dashboard as Python's <c>model_dump(mode="json")</c> wrote it.</summary>
    public JsonObject ToJson() => new()
    {
        ["total_prs"] = Total,
        ["ready"] = Items(PrBucket.Ready),
        ["behind"] = Items(PrBucket.Behind),
        ["blocked"] = Items(PrBucket.Blocked),
        ["pending"] = Items(PrBucket.Pending),
        ["stale"] = Items(PrBucket.Stale),
    };

    private JsonArray Items(PrBucket bucket) => [.. this[bucket].Select(JsonNode (item) => item.ToJson())];
}

/// <summary>What a batch action on one PR did.</summary>
/// <param name="Ok">Whether it worked.</param>
/// <param name="Message">What to print.</param>
public sealed record PrActionResult(bool Ok, string Message);

/// <summary>Whether every review thread on a PR is resolved, and what to say about it.</summary>
/// <param name="IsAligned">True only when every page was read and nothing is unresolved.</param>
/// <param name="Message">What to print.</param>
public sealed record ReviewThreadsResult(bool IsAligned, string Message);

/// <summary>
/// The issue and PR triage dashboards and their batch actions, read through <c>gh</c>. A repo gh cannot list, or answers
/// with something unreadable, contributes nothing. Ported from <c>rig.repo_governance.issue_triage</c> and
/// <c>pr_triage</c>.
/// </summary>
/// <param name="proc">Runs gh.</param>
public sealed class Triage(IProc proc)
{
    /// <summary>Days without activity before an issue waiting on a repro, or a PR, counts as stale.</summary>
    public const int StaleDays = 14;

    /// <summary>The required checks of a repo the policy registers none for: this project's legacy names.</summary>
    public static IReadOnlyList<string> LegacyRequiredChecks { get; } = ["Lint & Typecheck", "Tests & Coverage"];

    private const string IssueFields = "number,title,url,author,createdAt,updatedAt,comments,labels";
    private const string PrFields = "number,title,url,headRefName,baseRefName,author,isDraft,mergeable,mergeStateStatus,updatedAt,statusCheckRollup";

    /// <summary>The bucket <paramref name="issue"/> falls in at <paramref name="now"/>.</summary>
    public static IssueBucket Categorize(IssueItem issue, DateTimeOffset now, int staleDays = StaleDays)
    {
        ArgumentNullException.ThrowIfNull(issue);
        var labels = issue.LabelNames.Select(label => label.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
        if ((labels.Contains("status: needs-repro") || labels.Contains("needs-repro")) && issue.UpdatedAt is { } updated && WholeDays(now - updated) >= staleDays)
        {
            return IssueBucket.StaleRepro;
        }

        if (labels.Contains("type: bug") || labels.Contains("bug"))
        {
            return IssueBucket.ActiveBugs;
        }

        if (labels.Contains("good first issue") || labels.Contains("help wanted"))
        {
            return IssueBucket.Community;
        }

        var typed = labels.Any(label => label.StartsWith("type:", StringComparison.Ordinal) || label.StartsWith("type/", StringComparison.Ordinal));
        return issue.CommentsCount == 0 && !typed ? IssueBucket.NeedsTriage : IssueBucket.Other;
    }

    /// <summary>The bucket <paramref name="pr"/> falls in at <paramref name="now"/>.</summary>
    public static PrBucket Categorize(PrItem pr, DateTimeOffset now, int staleDays = StaleDays)
    {
        ArgumentNullException.ThrowIfNull(pr);
        if (pr.IsDraft || pr.Failed.Count > 0 || pr.Missing.Count > 0 || pr.Mergeable == "CONFLICTING" || pr.MergeStateStatus is "DIRTY" or "BLOCKED")
        {
            return PrBucket.Blocked;
        }

        if (pr.Pending.Count > 0)
        {
            return PrBucket.Pending;
        }

        return pr.MergeStateStatus switch
        {
            "BEHIND" => PrBucket.Behind,
            "CLEAN" => PrBucket.Ready,
            _ when pr.UpdatedAt is { } updated && WholeDays(now - updated) >= staleDays => PrBucket.Stale,
            _ => PrBucket.Other,
        };
    }

    /// <summary>The issues grouped into their buckets at <paramref name="now"/>.</summary>
    public static IssueDashboard Dashboard(IReadOnlyList<IssueItem> issues, DateTimeOffset now, int staleDays = StaleDays)
    {
        ArgumentNullException.ThrowIfNull(issues);
        var buckets = Enum.GetValues<IssueBucket>().ToDictionary(bucket => bucket, _ => new List<IssueItem>());
        foreach (var issue in issues)
        {
            buckets[Categorize(issue, now, staleDays)].Add(issue);
        }

        return new(issues.Count, buckets.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<IssueItem>)pair.Value));
    }

    /// <summary>The PRs grouped into their buckets at <paramref name="now"/>.</summary>
    public static PrDashboard Dashboard(IReadOnlyList<PrItem> prs, DateTimeOffset now, int staleDays = StaleDays)
    {
        ArgumentNullException.ThrowIfNull(prs);
        var buckets = Enum.GetValues<PrBucket>().ToDictionary(bucket => bucket, _ => new List<PrItem>());
        foreach (var pr in prs)
        {
            buckets[Categorize(pr, now, staleDays)].Add(pr);
        }

        return new(prs.Count, buckets.Where(pair => pair.Key != PrBucket.Other).ToDictionary(pair => pair.Key, pair => (IReadOnlyList<PrItem>)pair.Value));
    }

    /// <summary>The repo's open issues (at most 100), optionally only those labelled <paramref name="label"/>.</summary>
    public async Task<IReadOnlyList<IssueItem>> IssuesAsync(string repo, string? label = null, CancellationToken cancellationToken = default)
    {
        List<string> argv = ["gh", "issue", "list", "--repo", repo, "--state", "open", "--limit", "100", "--json", IssueFields];
        if (!string.IsNullOrEmpty(label))
        {
            argv.AddRange(["--label", label]);
        }

        return await ReadRowsAsync(argv, row => new IssueItem(
            repo,
            Required<int>(row, "number"),
            Required<string>(row, "title"),
            Required<string>(row, "url"),
            Login(row),
            Time(row, "createdAt"),
            Time(row, "updatedAt"),
            Required<JsonArray>(row, "comments").Count,
            [.. Required<JsonArray>(row, "labels").Select(node => Required<string>(node, "name"))]),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The repo's open PRs (at most 50), each with its checks sorted against <paramref name="requiredChecks"/>.</summary>
    public async Task<IReadOnlyList<PrItem>> PullRequestsAsync(string repo, IReadOnlyList<string> requiredChecks, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requiredChecks);
        return await ReadRowsAsync(
            ["gh", "pr", "list", "--repo", repo, "--state", "open", "--limit", "50", "--json", PrFields],
            row =>
            {
                List<string> passed = [], failed = [], pending = [];
                var reported = new HashSet<string>(StringComparer.Ordinal);
                foreach (var check in Required<JsonArray>(row, "statusCheckRollup"))
                {
                    var name = Optional(check, "name") is { Length: > 0 } n ? n : Optional(check, "context");
                    if (string.IsNullOrEmpty(name))
                    {
                        continue;
                    }

                    reported.Add(name);
                    var (status, conclusion, state) = (Optional(check, "status"), Optional(check, "conclusion"), Optional(check, "state"));
                    if (conclusion is "SUCCESS" or "NEUTRAL" || state == "SUCCESS")
                    {
                        passed.Add(name);
                    }
                    else if (conclusion is "FAILURE" or "TIMED_OUT" or "ACTION_REQUIRED" || state is "FAILURE" or "ERROR")
                    {
                        failed.Add(name);
                    }
                    else if (status is "IN_PROGRESS" or "QUEUED" or "WAITING" or "PENDING" || state == "PENDING")
                    {
                        pending.Add(name);
                    }
                }

                return new PrItem(
                    repo,
                    Required<int>(row, "number"),
                    Required<string>(row, "title"),
                    Required<string>(row, "url"),
                    Required<string>(row, "headRefName"),
                    Required<string>(row, "baseRefName"),
                    Login(row),
                    Required<bool>(row, "isDraft"),
                    Required<string>(row, "mergeable"),
                    Required<string>(row, "mergeStateStatus"),
                    Time(row, "updatedAt"),
                    passed,
                    failed,
                    pending,
                    [.. requiredChecks.Distinct(StringComparer.Ordinal).Where(check => !reported.Contains(check))]);
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Brings the PR's branch up to date with its base through <c>gh pr update-branch</c>.</summary>
    public async Task<PrActionResult> UpdateBehindAsync(PrItem pr, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pr);
        var ran = await proc.RunAsync(new ProcRequest(["gh", "pr", "update-branch", Number(pr), "--repo", pr.Repo]), cancellationToken).ConfigureAwait(false);
        return ran.Ok
            ? new(true, $"Successfully updated branch for {pr.ShortRef}")
            : new(false, $"Failed to update branch for {pr.ShortRef}: {ran.Err.Trim()}");
    }

    /// <summary>
    /// Squash-merges the PR and deletes its branch, but only once <paramref name="threads"/> says every review thread is
    /// resolved, so repos without native protection are held to it too.
    /// </summary>
    public async Task<PrActionResult> MergeReadyAsync(
        PrItem pr, Func<string, int, CancellationToken, Task<ReviewThreadsResult>> threads, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pr);
        ArgumentNullException.ThrowIfNull(threads);
        var resolved = await threads(pr.Repo, pr.Number, cancellationToken).ConfigureAwait(false);
        if (!resolved.IsAligned)
        {
            return new(false, $"Blocked {pr.ShortRef}: {resolved.Message}");
        }

        var ran = await proc.RunAsync(new ProcRequest(["gh", "pr", "merge", Number(pr), "--repo", pr.Repo, "--squash", "--delete-branch"]), cancellationToken)
            .ConfigureAwait(false);
        return ran.Ok
            ? new(true, $"Successfully merged {pr.ShortRef} and deleted branch")
            : new(false, $"Failed to merge {pr.ShortRef}: {ran.Err.Trim()}");
    }

    /// <summary><paramref name="value"/> as pydantic's JSON mode writes a datetime: ISO 8601, <c>Z</c> for UTC; null stays null.</summary>
    internal static JsonNode? Timestamp(DateTimeOffset? value)
    {
        if (value is not { } time)
        {
            return null;
        }

        var text = time.ToString(time.Ticks % TimeSpan.TicksPerSecond == 0 ? "yyyy-MM-dd'T'HH:mm:ss" : "yyyy-MM-dd'T'HH:mm:ss.ffffff", CultureInfo.InvariantCulture);
        return text + (time.Offset == TimeSpan.Zero ? "Z" : time.ToString("zzz", CultureInfo.InvariantCulture));
    }

    internal static JsonArray Names(IReadOnlyList<string> names) => [.. names.Select(name => (JsonNode?)name)];

    // timedelta.days floors, so a span a second short of a day is 0 days.
    private static long WholeDays(TimeSpan span) => (long)Math.Floor(span.TotalDays);

    private static string Number(PrItem pr) => pr.Number.ToString(CultureInfo.InvariantCulture);

    // gh's rows; a failed run or any row missing a field reads as no rows at all, as pydantic's ValueError did.
    private async Task<IReadOnlyList<T>> ReadRowsAsync<T>(IReadOnlyList<string> argv, Func<JsonNode, T> read, CancellationToken cancellationToken)
    {
        var ran = await proc.RunAsync(new ProcRequest(argv), cancellationToken).ConfigureAwait(false);
        if (!ran.Ok)
        {
            return [];
        }

        try
        {
            return JsonNode.Parse(ran.Out) is JsonArray rows ? [.. rows.Select(row => read(row ?? throw new FormatException("null row")))] : [];
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException)
        {
            return [];
        }
    }

    private static T Required<T>(JsonNode? node, string key) =>
        node is JsonObject row && row[key] is { } value
            ? value is T same ? same : value.GetValue<T>()
            : throw new FormatException($"missing {key}");

    private static string? Optional(JsonNode? node, string key) => node is JsonObject row && row[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static string Login(JsonNode row) => row["author"] is JsonObject author ? Optional(author, "login") ?? string.Empty : string.Empty;

    // gh's ISO timestamps; an empty or unparseable one is no time, as parse_datetime had it.
    private static DateTimeOffset? Time(JsonNode row, string key) =>
        Optional(row, key) is { Length: > 0 } text && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time) ? time : null;
}
