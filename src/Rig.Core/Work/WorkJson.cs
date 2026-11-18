using System.Text.Json.Nodes;
using Rig.Core.Text;

namespace Rig.Core.WorkItems;

/// <summary>
/// The <c>work list --json</c> schema, Consumers depend on it:
/// fields may be added without a version bump; renaming or removing one bumps <see cref="SchemaVersion"/>.
/// </summary>
public static class WorkJson
{
    /// <summary>The schema version.</summary>
    public const int SchemaVersion = 1;

    /// <summary>The whole payload; <paramref name="items"/> is the filtered view of <c>report.Items</c>.</summary>
    /// <param name="report">The report.</param>
    /// <param name="items">The items that passed the filters.</param>
    public static JsonObject Report(WorkReport report, IReadOnlyList<WorkItem> items)
    {
        return new JsonObject
        {
            ["schema_version"] = SchemaVersion,
            ["generated_at"] = IsoTime.Format(report.GeneratedAt),
            ["code_root"] = report.CodeRoot,
            ["warnings"] = new JsonArray([.. report.Warnings.Select(warning => JsonValue.Create(warning))]),
            ["items"] = new JsonArray([.. items.Select(item => (JsonNode?)Item(item))]),
        };
    }

    /// <summary>The payload as the CLI prints it: two-space indentation, non-ASCII kept.</summary>
    /// <param name="report">The report.</param>
    /// <param name="items">The items that passed the filters.</param>
    public static string Render(WorkReport report, IReadOnlyList<WorkItem> items) =>
        JsonOutput.Format(Report(report, items), keepNonAscii: true);

    /// <summary>One item as the JSON schema spells it.</summary>
    /// <param name="item">The item.</param>
    public static JsonObject Item(WorkItem item)
    {
        return new JsonObject
        {
            ["key"] = item.Key,
            ["repo"] = item.Repo,
            ["repo_path"] = item.RepoPath,
            ["branch"] = item.Branch,
            ["ticket"] = item.Ticket,
            ["is_trunk"] = item.IsTrunk,
            ["state"] = item.State.Wire(),
            ["reasons"] = new JsonArray([.. item.Reasons.Select(reason => JsonValue.Create(reason))]),
            ["last_activity"] = IsoTime.Format(item.LastActivity),
            ["dirty"] = item.Dirty,
            ["unpushed"] = item.Unpushed,
            ["checkouts"] = new JsonArray([.. item.Checkouts.Select(checkout => (JsonNode?)Checkout(checkout))]),
            ["branch_ref"] = item.BranchRef is null ? null : Branch(item.BranchRef),
            ["pr"] = item.Pr is null ? null : Pr(item.Pr),
            ["pr_lookup"] = item.PrLookup.Wire(),
            ["sessions"] = new JsonArray([.. item.Sessions.Select(session => (JsonNode?)Session(session))]),
        };
    }

    private static JsonObject Checkout(Checkout checkout) => new()
    {
        ["path"] = checkout.Path,
        ["is_main_checkout"] = checkout.IsMainCheckout,
        ["dirty"] = checkout.Dirty,
    };

    private static JsonObject Branch(BranchRef branch) => new()
    {
        ["upstream"] = branch.Upstream.Length > 0 ? branch.Upstream : null,
        ["upstream_gone"] = branch.UpstreamGone,
        ["ahead"] = Optional(branch.Ahead),
        ["behind"] = Optional(branch.Behind),
        ["unpushed"] = branch.Unpushed,
        ["last_commit"] = IsoTime.Format(branch.LastCommit),
    };

    private static JsonObject Pr(PrInfo pr) => new()
    {
        ["number"] = pr.Number,
        ["state"] = pr.Status.Wire(),
        ["url"] = pr.Url,
        ["updated_at"] = IsoTime.Format(pr.UpdatedAt),
    };

    private static JsonObject Session(SessionFacts session) => new()
    {
        ["session_id"] = session.SessionId,
        ["title"] = session.Title,
        ["state"] = session.State.Wire(),
        ["last_activity"] = IsoTime.Format(session.LastActivity),
        ["last_cwd"] = session.LastCwd,
        ["start_cwd"] = session.StartCwd,
        ["prompt_count"] = session.PromptCount,
        ["pid"] = Optional(session.Pid),
    };

    private static JsonValue? Optional(int? value) => value is { } number ? JsonValue.Create(number) : null;
}
