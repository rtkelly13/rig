using System.Globalization;
using System.Text;

namespace Rig.Core.Chrome;

/// <summary>
/// The <c>chrome-tabs</c> plans as plain lines. Pure, so what each command prints is testable. A line that starts with
/// a space is detail; any other line is a heading.
/// </summary>
public static class TabPlanText
{
    private static readonly (CloseKind Kind, string Heading)[] CloseHeadings =
    [
        (CloseKind.Merged, "Close: merged"),
        (CloseKind.Closed, "Close: closed without merging"),
        (CloseKind.Duplicate, "Close: duplicate PR tabs"),
        (CloseKind.Stack, "Close: superseded by a lower stack member"),
        (CloseKind.Queue, "Close: queued behind a lower queue member"),
    ];

    private static readonly (ReportKind Kind, string Heading)[] ReportHeadings =
    [
        (ReportKind.Unknown, "Report only: unknown state (never closed)"),
        (ReportKind.Protected, "Report only: pinned or audible (never closed)"),
    ];

    private static readonly (SiteReportKind Kind, string Heading)[] SiteReportHeadings =
    [
        (SiteReportKind.Variant, "Report only: title variant of a kept tab (never closed)"),
        (SiteReportKind.ReportOnly, "Report only: same title, rule too weak to close on"),
        (SiteReportKind.Protected, "Report only: pinned or audible duplicate (never closed)"),
        (SiteReportKind.OtherPage, "Report only: same title, different URL (never closed)"),
    ];

    /// <summary>The PR plan, grouped by reason in a fixed order, then a summary.</summary>
    public static IReadOnlyList<string> Prune(PrunePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return
        [
            .. CloseHeadings.SelectMany(heading => Section(
                heading.Heading,
                [.. plan.Closes.Where(close => close.Kind == heading.Kind).Select(close => PlanLine(close.Item, close.Reason))])),
            .. ReportHeadings.SelectMany(heading => Section(
                heading.Heading,
                [.. plan.Reports.Where(report => report.Kind == heading.Kind).Select(report => PlanLine(report.Item, report.Detail))])),
            $"{plan.Closes.Count} to close, {plan.Kept.Count} PR tabs kept, {plan.Reports.Count} reported, {plan.Prs.Count} PRs seen",
        ];
    }

    /// <summary>The site-dedupe plan: closes, then reports by kind, then a summary.</summary>
    public static IReadOnlyList<string> Dedupe(DedupePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return
        [
            .. Section("Close: duplicate pages", [.. plan.Closes.Select(close => PlanLine(close.Item, close.Reason))]),
            .. SiteReportHeadings.SelectMany(heading => Section(
                heading.Heading,
                [.. plan.Reports.Where(report => report.Kind == heading.Kind).Select(report => PlanLine(report.Item, report.Detail))])),
            $"{plan.Closes.Count} to close, {plan.Kept.Count} duplicated pages kept, {plan.Reports.Count} reported",
        ];
    }

    /// <summary>The memory-budget plan: its closes, then the memory before and after against the budget.</summary>
    public static IReadOnlyList<string> Memory(MemoryPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var verdict = plan.OverBudget
            ? "still over budget: every remaining tab is protected or held for review"
            : "within budget";
        return
        [
            .. Section(
                $"Close: over the {TabRank.FormatSize(plan.LimitMb)} memory budget",
                [.. plan.Closes.Select(close => PlanLine(close.Item, close.Reason))]),
            $"Memory: {TabRank.FormatSize(plan.BeforeMb)} -> {TabRank.FormatSize(plan.AfterMb)} of {TabRank.FormatSize(plan.LimitMb)}, {verdict}",
        ];
    }

    /// <summary>One line per tab, keep-worthiest first: rank, score or protection, memory, key, title, reasons.</summary>
    public static IReadOnlyList<string> Rank(IEnumerable<RankedTab> ranked)
    {
        ArgumentNullException.ThrowIfNull(ranked);
        return
        [
            .. ranked.Select((item, index) =>
            {
                var standing = item.Protected.Length > 0 ? "keep" : TabRank.Signed(item.Score);
                var memory = item.Tab.MemoryMb is null ? "-" : TabRank.FormatSize(item.MemoryMb);
                var why = string.Join("; ", item.Reasons.Concat(item.Flags)) is { Length: > 0 } joined ? joined : "no signals";
                var where = $"{item.Profile} w{item.WindowNumber} t{item.Tab.Index + 1}";
                return string.Create(
                    CultureInfo.InvariantCulture,
                    $"  {index + 1,3} {standing,4} {memory,8}  {Clip(item.Label, 48),-48}  [{where}]  {why}");
            }),
        ];
    }

    /// <summary>The grouping plan, one block per window in the order they run, then a summary.</summary>
    public static IReadOnlyList<string> Groups(GroupPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var created = plan.Moves.Count(move => move.New);
        var crossing = plan.Moves.Count(move => move.Crosses);
        return
        [
            .. plan.Ungroups.Count > 0
                ? (List<string>)
                [
                    Invariant($"Ungroup first ({plan.Ungroups.Count})"),
                    .. plan.Ungroups.Select(ungroup =>
                        Invariant($"  {"ungroup",-9} {ungroup.Group.Title,-24} {ungroup.Tabs.Count} tabs in window {ungroup.WindowNumber}")),
                ]
                : [],
            .. plan.WindowNumbers().SelectMany(number =>
            {
                var moves = plan.Moves.Where(move => move.WindowNumber == number).ToList();
                return (List<string>)
                [
                    Invariant($"Window {number} [{moves[0].Profile}] ({moves.Count})"),
                    .. moves.Select(move =>
                        Invariant($"  {Verb(move),-9} {move.Title,-24} tab {move.Tab.Index + 1,3}  | {Clip(move.Tab.Title)}")),
                ];
            }),
            .. plan.Skipped.Select(skip => Invariant($"  Skipped window {skip.WindowNumber} [{skip.Profile}]: {skip.Reason}")),
            .. plan.Blocked.Select(block => $"  Left {block.Title} [{block.Profile}]: {block.Reason}"),
            Invariant(
                $"{plan.Moves.Count} tabs to move ({crossing} across windows), {created} new groups, {plan.Ungroups.Count} duplicates merged, {plan.AlreadyGrouped} already grouped, {plan.Elsewhere} left in other groups"),
        ];
    }

    private static string Verb(GroupMove move) => move switch
    {
        { New: true, Crosses: true } => Invariant($"new in w{move.HomeNumber}"),
        { New: true } => "new group",
        { Crosses: true } => Invariant($"to w{move.HomeNumber}"),
        _ => "join",
    };

    private static string Invariant(FormattableString text) => FormattableString.Invariant(text);

    /// <summary>Each profile's saved groups as its bookmarks bar shows them, then what consolidating deletes.</summary>
    public static IReadOnlyList<string> SavedGroups(ConsolidationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return
        [
            .. plan.Groups.Select(group => group.Profile).Distinct().SelectMany(profile =>
            {
                var groups = plan.Groups.Where(group => group.Profile == profile).ToList();
                return (List<string>)
                [
                    $"Saved groups [{profile}] ({groups.Count})",
                    .. groups.Select(group =>
                        string.Create(CultureInfo.InvariantCulture, $"  {group.Position + 1,3}  {(group.Opened ? "open" : "closed"),-6}  {group.Title}")),
                ];
            }),
            .. plan.Deletes.Count > 0
                ? (List<string>)
                [
                    $"Delete: closed duplicates ({plan.Total})",
                    .. plan.Deletes.Select(delete =>
                    {
                        var kept = delete.OpenCopies > 0 ? "the open copy" : "the leftmost closed copy";
                        return $"  {delete.Count} x {delete.Title,-24} [{delete.Profile}] of {delete.OpenCopies + delete.ClosedCopies} saved, keeping {kept}";
                    }),
                ]
                : [],
            .. plan.OpenDuplicates.Select(duplicate =>
                $"  Report only: {duplicate.Title} [{duplicate.Profile}] is open {duplicate.Count} times (never deleted)"),
            $"{plan.Total} saved groups to delete, {plan.Groups.Count} seen",
        ];
    }

    /// <summary>One planned tab: its label, where it sits, why, and its title cut to fit.</summary>
    public static string PlanLine(IPlacedTab item, string why)
    {
        ArgumentNullException.ThrowIfNull(item);
        var where = $"{item.Profile}, window {item.WindowNumber}, tab {item.Tab.Index + 1}";
        return $"  {item.Label}  [{where}]  {why}  | {Clip(item.Tab.Title)}";
    }

    /// <summary><paramref name="title"/> cut to <paramref name="width"/> characters, ending in an ellipsis when cut.</summary>
    public static string Clip(string title, int width = 70)
    {
        ArgumentNullException.ThrowIfNull(title);
        var runes = title.EnumerateRunes().ToList();
        if (runes.Count <= width)
        {
            return title;
        }

        var cut = new StringBuilder();
        foreach (var rune in runes.Take(width - 1))
        {
            cut.Append(rune.ToString());
        }

        return cut.Append('…').ToString();
    }

    private static IEnumerable<string> Section(string heading, IReadOnlyCollection<string> body) =>
        body.Count > 0 ? [$"{heading} ({body.Count})", .. body] : [];
}
