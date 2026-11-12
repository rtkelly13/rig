using System.Globalization;
using Rig.Core.GitHub;

namespace Rig.Core.Chrome;

/// <summary>One tab with how much it is worth keeping, and why.</summary>
/// <param name="Tab">The tab.</param>
/// <param name="Profile">The profile scope.</param>
/// <param name="WindowAge">Its window's age rank.</param>
/// <param name="WindowNumber">Its window's 1-based number within the profile.</param>
/// <param name="Label">A PR ref, a site key or the title.</param>
/// <param name="Protected">Why it is never closed here, or "" when it can be.</param>
/// <param name="Score">The sum of its factors; higher keeps.</param>
/// <param name="Reasons">Each factor, signed, as <c>"+2 your PR"</c>.</param>
/// <param name="Flags">Informational marks that do not change the score, such as "oversized".</param>
/// <param name="Pr">The PR its title names, if any.</param>
/// <param name="Details">That PR's details, when resolved.</param>
public sealed record RankedTab(
    ChromeTab Tab,
    string Profile,
    long WindowAge,
    int WindowNumber,
    string Label,
    string Protected,
    int Score,
    IReadOnlyList<string> Reasons,
    IReadOnlyList<string> Flags,
    PrRef? Pr = null,
    PrDetails? Details = null) : IPlacedTab
{
    /// <summary>The memory Chrome reports for it, or 0 when unloaded.</summary>
    public double MemoryMb => Tab.MemoryMb ?? 0;
}

/// <summary>A tab the memory budget closes.</summary>
/// <param name="Item">The tab.</param>
/// <param name="Reason">Why, for people.</param>
/// <param name="Keeper">Always null: nothing replaces it.</param>
public sealed record MemoryClose(RankedTab Item, string Reason, ChromeTab? Keeper = null) : IPlannedTabClose
{
    IPlacedTab IPlannedTabClose.Item => Item;
}

/// <summary>What fitting the memory budget closes.</summary>
/// <param name="LimitMb">The budget.</param>
/// <param name="BeforeMb">Reported memory of the tabs that stay open after the other plans, before this one.</param>
/// <param name="AfterMb">The same after this plan.</param>
/// <param name="Closes">The tabs to close.</param>
public sealed record MemoryPlan(double LimitMb, double BeforeMb, double AfterMb, IReadOnlyList<MemoryClose> Closes) : IClosePlan
{
    /// <summary>Even after closing, the tabs exceed the budget.</summary>
    public bool OverBudget => AfterMb > LimitMb;

    IReadOnlyList<IPlannedTabClose> IClosePlan.Closes => Closes;
}

/// <summary>
/// Ranks every tab by how much it is worth keeping, and plans closes that fit a memory budget. Ported from
/// <c>desktop_manager.chrome.rank</c>.
/// </summary>
/// <remarks>
/// <para>
/// A tab is either protected (never closed here: a video call, audible or live media, pinned, the active tab of its
/// window, or a page <see cref="SiteRules"/> cannot identify) or carries an integer score built from named factors, so
/// every rank explains itself.
/// </para>
/// <para>
/// Factors: your review requested +3; your own open PR +2, plus +1 each for failing checks and changes requested; a
/// tracker ticket, issue or Google doc +1; someone else's approved PR not waiting on you −1; someone else's draft −1;
/// closed unmerged −1; merged −2; a weakly identified page −1; a minimised window −1. PRs over
/// <see cref="OversizedLines"/> changed lines are flagged without changing their score.
/// </para>
/// </remarks>
public static class TabRank
{
    /// <summary>Changed lines above which a PR is flagged oversized.</summary>
    public const int OversizedLines = 800;

    private static readonly HashSet<string> WorkSites = new(StringComparer.Ordinal) { "youtrack", "jira", "github-issue", "google-docs" };

    /// <summary>Protected first, then by score (highest first), memory (smallest first), oldest window, leftmost.</summary>
    public static IComparer<RankedTab> KeepOrder { get; } = Comparer<RankedTab>.Create((left, right) =>
    {
        var byProtected = (left.Protected.Length == 0).CompareTo(right.Protected.Length == 0);
        if (byProtected != 0)
        {
            return byProtected;
        }

        var byScore = right.Score.CompareTo(left.Score);
        if (byScore != 0)
        {
            return byScore;
        }

        var byMemory = left.MemoryMb.CompareTo(right.MemoryMb);
        if (byMemory != 0)
        {
            return byMemory;
        }

        var byAge = left.WindowAge.CompareTo(right.WindowAge);
        return byAge != 0 ? byAge : left.Tab.Index.CompareTo(right.Tab.Index);
    });

    /// <summary>Every tab, ranked by <see cref="KeepOrder"/>.</summary>
    public static IReadOnlyList<RankedTab> Rank(IEnumerable<ChromeWindow> windows, Resolution? resolution = null)
    {
        var states = resolution?.States ?? new Dictionary<PrRef, PrState>();
        return
        [
            .. (from numbered in PrPrune.NumberedWindows(windows)
                from tab in numbered.Window.Tabs
                select RankOne(tab, numbered.Window, numbered.Number, states)).Order(KeepOrder),
        ];
    }

    /// <summary>
    /// Closes the lowest-ranked, biggest loaded tabs until the rest fit <paramref name="limitMb"/>. Pure.
    /// <paramref name="alreadyClosing"/> tabs are left out of the total; <paramref name="held"/> tabs (ones another
    /// plan reported for a person to decide) count but never close.
    /// </summary>
    public static MemoryPlan PlanMemory(
        IEnumerable<RankedTab> ranked,
        double limitMb,
        IReadOnlySet<RuntimeId>? alreadyClosing = null,
        IReadOnlySet<RuntimeId>? held = null)
    {
        ArgumentNullException.ThrowIfNull(ranked);
        var staying = ranked.Where(item => alreadyClosing?.Contains(item.Tab.RuntimeId) != true).ToList();
        var before = staying.Sum(item => item.MemoryMb);
        var candidates = staying
            .Where(item => item.Protected.Length == 0 && item.MemoryMb > 0 && held?.Contains(item.Tab.RuntimeId) != true)
            .OrderBy(item => item.Score)
            .ThenByDescending(item => item.MemoryMb)
            .ThenBy(item => item.WindowAge)
            .ThenBy(item => item.Tab.Index);

        var total = before;
        var closes = new List<MemoryClose>();
        foreach (var item in candidates)
        {
            if (total <= limitMb)
            {
                break;
            }

            total -= item.MemoryMb;
            closes.Add(new MemoryClose(item, $"frees {FormatSize(item.MemoryMb)} (score {Signed(item.Score)})"));
        }

        return new MemoryPlan(limitMb, before, total, closes);
    }

    /// <summary><c>"1.50 GB"</c> from 1024 MB up, else <c>"300 MB"</c>.</summary>
    public static string FormatSize(double mb) =>
        mb >= 1024
            ? Math.Round(mb / 1024, 2, MidpointRounding.ToEven).ToString("F2", CultureInfo.InvariantCulture) + " GB"
            : Math.Round(mb, 0, MidpointRounding.ToEven).ToString("F0", CultureInfo.InvariantCulture) + " MB";

    private static string Signed(int score) => score.ToString("+0;-0;+0", CultureInfo.InvariantCulture);

    private static RankedTab RankOne(ChromeTab tab, ChromeWindow window, int number, IReadOnlyDictionary<PrRef, PrState> states)
    {
        var pr = PrPrune.RefFromTitle(tab.Title);
        var identity = pr is null ? SiteRules.Identify(tab.Title) : null;
        var label = pr?.ToString() ?? identity?.Key ?? tab.Title;

        var protection = Protection(tab, identified: pr is not null || identity is not null);
        if (protection.Length > 0)
        {
            return new RankedTab(tab, window.ProfileKey, window.AgeRank, number, label, protection, 0, [protection], [], pr);
        }

        var factors = new List<(int Score, string Why)>();
        var flags = new List<string>();
        PrDetails? details = null;
        if (pr is not null)
        {
            var state = states.GetValueOrDefault(pr) ?? new PrState(PrStatus.Unknown);
            details = state.Details;
            factors.AddRange(PrFactors(state.Status, details));
            if (details is not null && details.ChangedLines > OversizedLines)
            {
                flags.Add($"oversized: +{details.Additions}/-{details.Deletions} in {details.ChangedFiles} files");
            }
        }
        else if (identity is not null)
        {
            if (WorkSites.Contains(identity.Site))
            {
                factors.Add((1, $"work item ({identity.Site})"));
            }

            if (!identity.Auto)
            {
                factors.Add((-1, "weakly identified title"));
            }
        }

        if (window.Minimized)
        {
            factors.Add((-1, "minimised window"));
        }

        return new RankedTab(
            tab,
            window.ProfileKey,
            window.AgeRank,
            number,
            label,
            string.Empty,
            factors.Sum(factor => factor.Score),
            [.. factors.Select(factor => $"{Signed(factor.Score)} {factor.Why}")],
            flags,
            pr,
            details);
    }

    private static string Protection(ChromeTab tab, bool identified) =>
        SiteRules.IsCall(tab.Title) ? "video call"
        : tab.Audible ? "audible or live media"
        : tab.Pinned ? "pinned"
        : tab.Selected ? "active tab"
        : !identified ? "unidentified page"
        : string.Empty;

    private static List<(int Score, string Why)> PrFactors(PrStatus status, PrDetails? details)
    {
        if (status == PrStatus.Merged)
        {
            return [(-2, "merged")];
        }

        if (status == PrStatus.Closed)
        {
            return [(-1, "closed without merging")];
        }

        if (status != PrStatus.Open || details is null)
        {
            return [];
        }

        var factors = new List<(int Score, string Why)>();
        if (details.ReviewRequested)
        {
            factors.Add((3, "your review is requested"));
        }

        if (details.AuthoredByViewer)
        {
            factors.Add((2, "your PR"));
            if (details.ChecksFailing)
            {
                factors.Add((1, "checks failing"));
            }

            if (details.ReviewDecision == "CHANGES_REQUESTED")
            {
                factors.Add((1, "changes requested"));
            }
        }
        else if (!details.ReviewRequested)
        {
            if (details.ReviewDecision == "APPROVED")
            {
                factors.Add((-1, "approved; not waiting on you"));
            }

            if (details.Draft)
            {
                factors.Add((-1, "someone else's draft"));
            }
        }

        return factors;
    }
}
