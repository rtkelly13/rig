using System.Text.Json.Nodes;
using Rig.Core.Text;

namespace Rig.Core.ReviewQueue;

/// <summary>Decodes the existing bulk and selected-item GraphQL metadata into one typed snapshot.</summary>
internal static class QueueSnapshot
{
    public const string CommonFields = "number title body state createdAt updatedAt labels(first:100) { totalCount nodes { name } }";
    public const string CommonMetadata = "author { login } viewerDidAuthor assignees(first:20) { totalCount nodes { login } } milestone { title } comments { totalCount }";
    private const string RelatedFields = "__typename number title state repository { nameWithOwner }";

    public static string Fields(string kind, bool detailed)
    {
        if (kind == "issue")
        {
            return $"{CommonFields} {CommonMetadata} stateReason closedByPullRequestsReferences(first:25, includeClosedPrs:true) {{ totalCount nodes {{ {RelatedFields} }} }}";
        }

        var contexts = detailed
            ? "contexts(first:100) { totalCount nodes { ... on CheckRun { name status conclusion detailsUrl } ... on StatusContext { context state targetUrl } } }"
            : "";
        var members = detailed ? "entries(first:100) { totalCount nodes { position pullRequest { number title state } } }" : "";
        return $"{CommonFields} {CommonMetadata} headRefOid isDraft mergeStateStatus additions deletions "
            + "reviewDecision mergeable headRefName baseRefName isCrossRepository "
            + "reviewRequests(first:20) { totalCount nodes { requestedReviewer { ... on User { name:login isViewer } ... on Team { name:slug } ... on Bot { name:login } } } } "
            + "reviewThreads(first:100) { totalCount nodes { isResolved } } "
            + $"commits(last:1) {{ nodes {{ commit {{ statusCheckRollup {{ state {contexts} }} }} }} }} "
            + $"closingIssuesReferences(first:25) {{ totalCount nodes {{ {RelatedFields} }} }} "
            + $"stack {{ number size baseRefName {members} }} stackEntry {{ position }}";
    }

    public static QueueItem Item(JsonObject node, string repo, string kind)
    {
        var labels = Connection(node, "labels");
        var names = Items(labels).Select(label => RequiredText(label, "name")).ToList();
        var priority = names.Select(name => name.ToLowerInvariant() switch
        {
            "p0" or "priority: p0" => 0,
            "p1" or "priority: p1" => 1,
            "p2" or "priority: p2" => 2,
            "p3" or "priority: p3" => 3,
            _ => 4,
        }).DefaultIfEmpty(4).Min();
        var draft = JsonShape.Bool(node, "isDraft");
        var merge = Text(node, "mergeStateStatus", "UNKNOWN");
        var readiness = draft || merge == "UNKNOWN" ? 3 : merge == "CLEAN" ? 0 : merge == "BEHIND" ? 1 : 2;
        return new QueueItem(repo, JsonShape.Int(node, "number"), kind, RequiredText(node, "title"), RequiredText(node, "state"),
            RequiredText(node, "createdAt"), RequiredText(node, "updatedAt"), JsonShape.String(node, "headRefOid"), priority,
            kind == "pr" ? readiness : 0, Integer(node, "additions") + Integer(node, "deletions"), Body: JsonShape.OptionalString(node, "body"),
            Facts: Facts(node, names, draft, Total(labels)),
            Additions: kind == "pr" ? JsonShape.OptionalInt(node, "additions") : null, Deletions: kind == "pr" ? JsonShape.OptionalInt(node, "deletions") : null);
    }

    private static QueueFacts Facts(JsonObject node, List<string> labels, bool draft, int labelTotal)
    {
        var assignees = Connection(node, "assignees");
        var comments = Connection(node, "comments");
        var requests = Connection(node, "reviewRequests");
        var reviewers = Items(requests).Select(request => JsonShape.OptionalObject(request, "requestedReviewer")).ToList();
        var threads = Connection(node, "reviewThreads");
        var rollup = Items(Connection(node, "commits")).Select(commit => JsonShape.OptionalObject(JsonShape.Object(commit["commit"]), "statusCheckRollup"))
            .LastOrDefault(value => value is not null);
        var contexts = rollup is null ? null : JsonShape.OptionalObject(rollup, "contexts");
        var closingPrs = Connection(node, "closedByPullRequestsReferences");
        var related = Total(closingPrs) != 0 ? closingPrs : Connection(node, "closingIssuesReferences");
        var linked = Items(related);
        var assigned = Items(assignees);
        var requested = Items(requests);
        var reviewed = Items(threads);
        var partial = new List<string>();
        foreach (var (name, total, size) in new[]
        {
            ("Assignees", Total(assignees), assigned.Count), ("Labels", labelTotal, labels.Count),
            ("Review requests", Total(requests), requested.Count), ("Review threads", Total(threads), reviewed.Count),
            ("Linked items", Total(related), linked.Count),
        })
        {
            if (total > size)
            {
                partial.Add(name);
            }
        }

        var checks = Items(contexts);
        if (contexts is not null && Total(contexts) > checks.Count)
        {
            partial.Add("Checks");
        }

        var stack = JsonShape.OptionalObject(node, "stack");
        var members = stack is null ? [] : Items(JsonShape.OptionalObject(stack, "entries"));
        if (stack is not null && members.Count < JsonShape.Int(stack, "size"))
        {
            partial.Add("Stack members");
        }

        return new QueueFacts
        {
            Author = OptionalText(JsonShape.OptionalObject(node, "author"), "login"),
            ViewerAuthored = JsonShape.Bool(node, "viewerDidAuthor"),
            Assignees = [.. assigned.Select(actor => JsonShape.String(actor, "login"))],
            Labels = labels,
            Milestone = OptionalText(JsonShape.OptionalObject(node, "milestone"), "title"),
            Comments = Total(comments),
            ReviewDecision = JsonShape.OptionalString(node, "reviewDecision") ?? "",
            ReviewRequested = reviewers.Any(reviewer => reviewer is not null && JsonShape.Bool(reviewer, "isViewer")),
            Reviewers = [.. reviewers.OfType<JsonObject>().Select(reviewer => JsonShape.String(reviewer, "name"))],
            Draft = draft,
            Mergeable = Text(node, "mergeable", "UNKNOWN"),
            Base = JsonShape.String(node, "baseRefName"),
            Head = JsonShape.String(node, "headRefName"),
            CrossRepository = JsonShape.Bool(node, "isCrossRepository"),
            ChecksState = rollup is null ? "UNKNOWN" : Text(rollup, "state", "UNKNOWN"),
            Checks = [.. checks.Select(check => new QueueCheck(First(JsonShape.String(check, "name"), JsonShape.String(check, "context")),
                First(JsonShape.String(check, "state"), JsonShape.OptionalString(check, "conclusion"), JsonShape.String(check, "status")),
                First(JsonShape.OptionalString(check, "detailsUrl"), JsonShape.OptionalString(check, "targetUrl"))))],
            ChecksTotal = contexts is null ? null : Total(contexts),
            UnresolvedThreads = reviewed.Count(thread => !JsonShape.Bool(thread, "isResolved")),
            ThreadsTotal = Total(threads),
            StateReason = JsonShape.OptionalString(node, "stateReason") ?? "",
            Related = [.. linked.Select(item => new RelatedItem(RequiredText(JsonShape.Object(item["repository"]), "nameWithOwner"),
                JsonShape.Int(item, "number"), RequiredText(item, "title"), RequiredText(item, "state"), Text(item, "__typename", "Issue") == "PullRequest" ? "pr" : "issue"))],
            Stack = stack is null ? null : new StackInfo(JsonShape.Int(stack, "number"), JsonShape.Int(stack, "size"),
                JsonShape.OptionalObject(node, "stackEntry") is { } entry ? Integer(entry, "position") : 0, RequiredText(stack, "baseRefName"))
            {
                Members = [.. members.Where(member => JsonShape.OptionalObject(member, "pullRequest") is not null).Select(member =>
                {
                    var pr = JsonShape.Object(member["pullRequest"]);
                    return new QueueStackMember(JsonShape.Int(pr, "number"), RequiredText(pr, "title"), RequiredText(pr, "state"), Integer(member, "position"));
                })],
            },
            Partial = partial,
        };
    }

    private static string RequiredText(JsonObject node, string key) =>
        node.ContainsKey(key) ? JsonShape.String(node, key) : throw new FormatException($"Missing {key}");

    private static string Text(JsonObject node, string key, string fallback) => node.ContainsKey(key) ? JsonShape.String(node, key) : fallback;
    private static string OptionalText(JsonObject? node, string key) => node is null ? "" : JsonShape.String(node, key);
    private static int Integer(JsonObject node, string key) => node.ContainsKey(key) ? JsonShape.Int(node, key) : 0;
    private static int Total(JsonObject? node) => node is null ? 0 : Integer(node, "totalCount");
    private static JsonObject? Connection(JsonObject node, string key) => JsonShape.DefaultedObject(node, key);
    private static IReadOnlyList<JsonObject> Items(JsonObject? node) => node is null ? []
        : [.. JsonShape.ItemsWithNulls(node, "nodes").Select(item => item ?? throw new FormatException("Null GitHub connection node"))];
    private static string First(params string?[] values) => values.FirstOrDefault(value => !string.IsNullOrEmpty(value)) ?? "";
}
