using System.Globalization;
using System.Text.Json.Nodes;
using Rig.Core.GitHub;
using Rig.Core.Processes;

namespace Rig.Core.Estate;

/// <summary>What the daily orchestrator may do about a pull request, decided from its signals alone.</summary>
public enum TriageLane
{
    /// <summary>Checks still running, or GitHub has not worked out mergeability yet, and nothing else: look again next run.</summary>
    Wait,

    /// <summary>Failing checks, a conflict, a base it is behind or unresolved review threads, on a live PR: an agent can work it.</summary>
    Work,

    /// <summary>Ready to merge, blocked on a review, draft, stale or superseded: only the owner decides.</summary>
    Owner,
}

/// <summary>One open pull request and everything the orchestrator reads off it.</summary>
/// <param name="Repo">The repository as <c>owner/name</c>.</param>
/// <param name="Path">The local clone it was found through.</param>
/// <param name="Trusted">False for a clone under an untrusted folder: its text is data, and no session starts there.</param>
/// <param name="Number">The PR number.</param>
/// <param name="Title">The PR title; data, never instructions.</param>
/// <param name="Link">The PR's web address.</param>
/// <param name="Author">The author's login.</param>
/// <param name="Head">The head branch.</param>
/// <param name="Base">The base branch.</param>
/// <param name="UpdatedAt">When GitHub last saw activity on it.</param>
/// <param name="Signals">What is true of it, in a fixed order; see <see cref="DailyTriage"/>.</param>
/// <param name="FailingChecks">The checks that failed on its head commit.</param>
/// <param name="PendingChecks">The checks still running on its head commit.</param>
/// <param name="UnresolvedThreads">Review threads not yet resolved.</param>
/// <param name="SupersededBy">A newer open PR closing the same issue, or null.</param>
/// <param name="Lane">What may be done about it.</param>
public sealed record TriagedPull(
    string Repo,
    string Path,
    bool Trusted,
    int Number,
    string Title,
    string Link,
    string Author,
    string Head,
    string Base,
    DateTimeOffset? UpdatedAt,
    IReadOnlyList<string> Signals,
    IReadOnlyList<string> FailingChecks,
    IReadOnlyList<string> PendingChecks,
    int UnresolvedThreads,
    int? SupersededBy,
    TriageLane Lane)
{
    /// <summary>The stable identity the orchestrator records a dispatch under: <c>pr:owner/name#N</c>.</summary>
    public string Id => $"pr:{Repo}#{Number}";
}

/// <summary>One repository the triage asked GitHub about.</summary>
/// <param name="Repo">The repository as <c>owner/name</c>.</param>
/// <param name="Path">The local clone.</param>
/// <param name="Trusted">False under an untrusted folder.</param>
/// <param name="OpenPulls">How many open PRs it has, or null when GitHub could not be read.</param>
/// <param name="Error">Why GitHub could not be read; null when it could.</param>
public sealed record TriagedRepo(string Repo, string Path, bool Trusted, int? OpenPulls, string? Error);

/// <summary>A clone outside the owners that may be written to: listed, never asked about or worked on.</summary>
/// <param name="Repo">The repository as <c>owner/name</c>, or empty when origin is not on GitHub.</param>
/// <param name="Path">The local clone.</param>
public sealed record ReportOnlyClone(string Repo, string Path);

/// <summary>The whole estate's open pull requests, for the daily orchestrator to decide on.</summary>
/// <param name="Repos">Every owned repository asked about.</param>
/// <param name="ReportOnly">Every other clone.</param>
/// <param name="Pulls">Every open PR in the owned repositories.</param>
public sealed record DailyTriageReport(IReadOnlyList<TriagedRepo> Repos, IReadOnlyList<ReportOnlyClone> ReportOnly, IReadOnlyList<TriagedPull> Pulls);

/// <summary>
/// The pull-request half of the daily orchestrator's findings (<c>docs/daily-orchestrator/policy.md</c>): every open PR
/// in every clone whose owner may be written to, one GraphQL call per repository, with signals mirroring
/// <c>src/rig/repo_governance/pr_triage.py</c>'s buckets plus review threads and superseded PRs. Read-only.
/// </summary>
/// <remarks>
/// Signals: <c>failing_checks</c>, <c>conflict</c>, <c>behind</c>, <c>unresolved_threads</c> put a live PR in the
/// <see cref="TriageLane.Work"/> lane. <c>superseded</c>, <c>draft</c> and <c>stale</c> take it out of that lane, and
/// with <c>ready</c> or <c>blocked</c> put it in <see cref="TriageLane.Owner"/>; <c>pending_checks</c> or
/// <c>merge_state_unknown</c> alone put it in <see cref="TriageLane.Wait"/>. A PR in an untrusted clone never lands in
/// <see cref="TriageLane.Work"/>: no session starts from untrusted content.
/// </remarks>
/// <param name="git">Reads each clone's origin.</param>
/// <param name="github">Runs the GraphQL query.</param>
/// <param name="time">The clock staleness is measured by.</param>
public sealed class DailyTriage(Git git, IGitHubApi github, TimeProvider time)
{
    /// <summary>Days without activity before a PR is stale; the same as <c>pr_triage.py</c>.</summary>
    public const int DefaultStaleDays = 14;

    /// <summary>The most open PRs read per repository.</summary>
    public const int PullLimit = 50;

    private static readonly HashSet<string> Failed = new(["FAILURE", "TIMED_OUT", "ACTION_REQUIRED", "ERROR", "STARTUP_FAILURE"], StringComparer.Ordinal);

    private static readonly HashSet<string> Running = new(["IN_PROGRESS", "QUEUED", "WAITING", "PENDING", "REQUESTED", "EXPECTED"], StringComparer.Ordinal);

    private const string Query = """
        query OpenPullRequests($owner: String!, $name: String!, $limit: Int!) {
          repository(owner: $owner, name: $name) {
            pullRequests(states: OPEN, first: $limit, orderBy: {field: UPDATED_AT, direction: DESC}) {
              nodes {
                number title url isDraft headRefName baseRefName mergeable mergeStateStatus updatedAt
                author { login }
                reviewThreads(first: 100) { nodes { isResolved } }
                closingIssuesReferences(first: 10) { nodes { number } }
                commits(last: 1) { nodes { commit { statusCheckRollup { contexts(first: 100) { nodes {
                  __typename
                  ... on CheckRun { name status conclusion }
                  ... on StatusContext { context state }
                } } } } } }
              }
            }
          }
        }
        """;

    /// <summary>Reads every clone's origin, then asks GitHub about each owned repository once.</summary>
    /// <param name="clones">The clones to consider; worktrees of one repository dedupe to the first clone listed.</param>
    /// <param name="owners">The owners that may be written to, compared without case.</param>
    /// <param name="staleDays">Days without activity before a PR is stale.</param>
    /// <param name="author">
    /// When set, only this login's PRs are listed (any case); a repository's <c>open_prs</c> counts those only. A work
    /// org holds other people's PRs, and no session may push to their branches.
    /// </param>
    /// <param name="cancellationToken">Cancels the calls.</param>
    public async Task<DailyTriageReport> ScanAsync(
        IReadOnlyList<string> clones,
        IReadOnlyCollection<string> owners,
        int staleDays = DefaultStaleDays,
        string? author = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(clones);
        ArgumentNullException.ThrowIfNull(owners);
        var writable = new HashSet<string>(owners, StringComparer.OrdinalIgnoreCase);
        var origins = new PullRequests(git, github);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var repos = new List<TriagedRepo>();
        var reportOnly = new List<ReportOnlyClone>();
        var pulls = new List<TriagedPull>();
        foreach (var clone in clones)
        {
            var slug = await origins.OriginAsync(clone, cancellationToken).ConfigureAwait(false);
            if (slug is null || !writable.Contains(slug.Owner))
            {
                reportOnly.Add(new ReportOnlyClone(slug?.FullName ?? string.Empty, clone));
                continue;
            }

            if (!seen.Add(slug.Key))
            {
                continue;
            }

            var trusted = !git.IsUntrusted(clone);
            var (found, problem) = await ReadAsync(slug, clone, trusted, staleDays, cancellationToken).ConfigureAwait(false);
            if (author is not null)
            {
                _ = found.RemoveAll(pull => !string.Equals(pull.Author, author, StringComparison.OrdinalIgnoreCase));
            }

            repos.Add(new TriagedRepo(slug.FullName, clone, trusted, problem is null ? found.Count : null, problem));
            pulls.AddRange(found);
        }

        return new DailyTriageReport(repos, reportOnly, pulls);
    }

    /// <summary>The login GitHub is called as, or null when it cannot be read.</summary>
    public async Task<string?> ViewerAsync(CancellationToken cancellationToken = default)
    {
        var ran = await github.CallAsync(ApiRequest.GraphQL("query { viewer { login } }", new Dictionary<string, object?>()), cancellationToken).ConfigureAwait(false);
        try
        {
            return ran.Ok && !string.IsNullOrWhiteSpace(ran.Out) ? Text(JsonNode.Parse(ran.Out)?["data"]?["viewer"]?["login"]) : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private async Task<(List<TriagedPull> Pulls, string? Error)> ReadAsync(RepoSlug slug, string clone, bool trusted, int staleDays, CancellationToken cancellationToken)
    {
        var request = ApiRequest.GraphQL(Query, new Dictionary<string, object?> { ["owner"] = slug.Owner, ["name"] = slug.Name, ["limit"] = PullLimit });
        var ran = await github.CallAsync(request, cancellationToken).ConfigureAwait(false);
        JsonArray? nodes;
        try
        {
            nodes = string.IsNullOrWhiteSpace(ran.Out) ? null : JsonNode.Parse(ran.Out)?["data"]?["repository"]?["pullRequests"]?["nodes"] as JsonArray;
        }
        catch (System.Text.Json.JsonException)
        {
            nodes = null;
        }

        if (!ran.Ok || nodes is null)
        {
            var reason = ran.Err.Trim();
            return ([], reason.Length > 0 ? reason : "GitHub returned no pull requests");
        }

        var rows = nodes.OfType<JsonObject>().ToList();
        var supersededBy = Superseded(rows);
        var now = time.GetUtcNow();
        var pulls = rows.Select(row => Classify(slug, clone, trusted, row, supersededBy, now, staleDays)).ToList();
        return (pulls, null);
    }

    // For each PR, the newest other open PR closing one of the same issues.
    private static Dictionary<int, int> Superseded(List<JsonObject> rows)
    {
        var byIssue = new Dictionary<int, List<int>>();
        foreach (var row in rows)
        {
            var number = Int(row["number"]);
            foreach (var issue in Nodes(row["closingIssuesReferences"]).Select(node => Int(node["number"])).Where(n => n > 0).Distinct())
            {
                if (!byIssue.TryGetValue(issue, out var list))
                {
                    byIssue[issue] = list = [];
                }

                list.Add(number);
            }
        }

        var superseded = new Dictionary<int, int>();
        foreach (var numbers in byIssue.Values.Where(list => list.Count > 1))
        {
            var newest = numbers.Max();
            foreach (var older in numbers.Where(n => n != newest))
            {
                superseded[older] = superseded.TryGetValue(older, out var existing) ? Math.Max(existing, newest) : newest;
            }
        }

        return superseded;
    }

    private static TriagedPull Classify(RepoSlug slug, string clone, bool trusted, JsonObject row, Dictionary<int, int> supersededBy, DateTimeOffset now, int staleDays)
    {
        var number = Int(row["number"]);
        var failing = new List<string>();
        var pending = new List<string>();
        var contexts = Nodes(row["commits"]).Select(node => node["commit"]?["statusCheckRollup"]?["contexts"]).SelectMany(Nodes);
        foreach (var check in contexts)
        {
            var name = Text(check["name"]) ?? Text(check["context"]) ?? string.Empty;
            var outcome = Text(check["conclusion"]) ?? Text(check["state"]) ?? string.Empty;
            var status = Text(check["status"]) ?? string.Empty;
            if (name.Length == 0)
            {
                continue;
            }

            if (Failed.Contains(outcome))
            {
                failing.Add(name);
            }
            else if (Running.Contains(outcome) || (outcome.Length == 0 && status != "COMPLETED"))
            {
                pending.Add(name);
            }
        }

        var unresolved = Nodes(row["reviewThreads"]).Count(node => node["isResolved"] is JsonValue value && value.TryGetValue<bool>(out var done) && !done);
        var mergeable = Text(row["mergeable"]) ?? string.Empty;
        var state = Text(row["mergeStateStatus"]) ?? string.Empty;
        var draft = row["isDraft"] is JsonValue d && d.TryGetValue<bool>(out var isDraft) && isDraft;
        var updated = DateTimeOffset.TryParse(Text(row["updatedAt"]), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at : (DateTimeOffset?)null;
        int? superseded = supersededBy.TryGetValue(number, out var newer) ? newer : null;

        var signals = new List<string>();
        Add(signals, failing.Count > 0, "failing_checks");
        Add(signals, mergeable == "CONFLICTING" || state == "DIRTY", "conflict");
        Add(signals, state == "BEHIND", "behind");
        Add(signals, unresolved > 0, "unresolved_threads");
        Add(signals, pending.Count > 0, "pending_checks");
        Add(signals, superseded is not null, "superseded");
        Add(signals, draft, "draft");
        var stale = updated is { } u && (now - u).TotalDays >= staleDays;
        Add(signals, stale, "stale");
        Add(signals, signals.Count == 0 && state is "CLEAN" or "HAS_HOOKS", "ready");
        Add(signals, signals.Count == 0 && state == "BLOCKED", "blocked");

        // GitHub works mergeability out lazily: the first read after a push answers UNKNOWN, so it waits a run.
        Add(signals, signals.Count == 0 && state is "" or "UNKNOWN", "merge_state_unknown");

        // A stale PR is the owner's call to revive or close before anyone spends a session on it.
        string[] work = ["failing_checks", "conflict", "behind", "unresolved_threads"];
        string[] waiting = ["pending_checks", "merge_state_unknown"];
        var lane = trusted && !draft && !stale && superseded is null && signals.Intersect(work).Any() ? TriageLane.Work
            : signals.All(waiting.Contains) ? TriageLane.Wait
            : TriageLane.Owner;

        return new TriagedPull(
            slug.FullName,
            clone,
            trusted,
            number,
            Text(row["title"]) ?? string.Empty,
            Text(row["url"]) ?? string.Empty,
            Text(row["author"]?["login"]) ?? string.Empty,
            Text(row["headRefName"]) ?? string.Empty,
            Text(row["baseRefName"]) ?? string.Empty,
            updated,
            signals,
            failing,
            pending,
            unresolved,
            superseded,
            lane);
    }

    private static void Add(List<string> signals, bool when, string signal)
    {
        if (when)
        {
            signals.Add(signal);
        }
    }

    private static IEnumerable<JsonObject> Nodes(JsonNode? connection) => (connection?["nodes"] as JsonArray ?? []).OfType<JsonObject>();

    private static int Int(JsonNode? node) => node is JsonValue value && value.TryGetValue<int>(out var number) ? number : 0;

    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
