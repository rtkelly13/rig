namespace Rig.Core.Chrome;

/// <summary>Why a duplicate site tab is reported instead of closed.</summary>
public enum SiteReportKind
{
    /// <summary>A different title under an exact-only key: maybe the same page.</summary>
    Variant,

    /// <summary>Its rule only ever reports.</summary>
    ReportOnly,

    /// <summary>Pinned or audible.</summary>
    Protected,

    /// <summary>Same title, but the known URLs name different pages.</summary>
    OtherPage,
}

/// <summary>A tab a site rule identified, with where it sits.</summary>
/// <param name="Tab">The tab.</param>
/// <param name="Identity">What its title names.</param>
/// <param name="Profile">The profile scope.</param>
/// <param name="WindowAge">Its window's age rank.</param>
/// <param name="WindowNumber">Its window's 1-based number within the profile.</param>
public sealed record SiteTab(ChromeTab Tab, TabIdentity Identity, string Profile, long WindowAge, int WindowNumber) : IPlacedTab
{
    /// <inheritdoc/>
    public string Label => Identity.Key;
}

/// <summary>A duplicate site tab to close.</summary>
/// <param name="Item">The tab.</param>
/// <param name="Reason">Why, for people.</param>
/// <param name="Keeper">The tab kept for the same page.</param>
public sealed record SiteClose(SiteTab Item, string Reason, ChromeTab? Keeper = null) : IPlannedTabClose
{
    IPlacedTab IPlannedTabClose.Item => Item;
}

/// <summary>A duplicate site tab left open and reported.</summary>
/// <param name="Item">The tab.</param>
/// <param name="Kind">Why it is reported.</param>
/// <param name="Detail">What a person should know.</param>
public sealed record SiteReport(SiteTab Item, SiteReportKind Kind, string Detail);

/// <summary>What the site rules decided.</summary>
/// <param name="Closes">The tabs to close.</param>
/// <param name="Reports">The tabs reported instead.</param>
/// <param name="Kept">One kept tab per duplicated key; singletons are not listed.</param>
public sealed record DedupePlan(IReadOnlyList<SiteClose> Closes, IReadOnlyList<SiteReport> Reports, IReadOnlyList<SiteTab> Kept) : IClosePlan
{
    IReadOnlyList<IPlannedTabClose> IClosePlan.Closes => Closes;

    /// <summary>Window to the tab to select in place of a closed active tab.</summary>
    public IReadOnlyDictionary<long, ChromeTab> Activations() => ClosePlans.Activations(Closes);
}

/// <summary>
/// Decides which non-PR duplicate tabs to close, using <see cref="SiteRules"/>. Pure, like <see cref="PrPrune"/>, and
/// shares its keeper choice and executor. Ported from <c>desktop_manager.chrome.dedupe</c>.
/// </summary>
/// <remarks>
/// Per profile, tabs whose titles one rule keys alike are one page: the keeper stays and the rest close. Never closed:
/// tabs no rule keys; a title variant under an exact-only rule; any match of a report-only rule; a title match whose
/// known URLs name different pages; pinned and audible tabs; and GitHub PR tabs, which <see cref="PrPrune"/> owns.
/// </remarks>
public static class Dedupe
{
    /// <summary>Every keyed, non-PR tab, oldest window first and left to right.</summary>
    public static IReadOnlyList<SiteTab> SiteTabs(IEnumerable<ChromeWindow> windows) =>
    [
        .. from numbered in PrPrune.NumberedWindows(windows)
           from tab in numbered.Window.Tabs
           where PrPrune.RefFromTitle(tab.Title) is null
           let identity = SiteRules.Identify(tab.Title)
           where identity is not null
           select new SiteTab(tab, identity, numbered.Window.ProfileKey, numbered.Window.AgeRank, numbered.Number),
    ];

    /// <summary>Applies the site rules to every profile's tabs. Pure; nothing is closed here.</summary>
    public static DedupePlan Plan(IEnumerable<ChromeWindow> windows)
    {
        var groups = SiteTabs(windows)
            .GroupBy(item => (item.Profile, item.Identity.Key))
            .OrderBy(group => group.Key.Profile, StringComparer.Ordinal)
            .ThenBy(group => group.Key.Key, StringComparer.Ordinal);

        var closes = new List<SiteClose>();
        var reports = new List<SiteReport>();
        var kept = new List<SiteTab>();
        foreach (var group in groups)
        {
            var members = group.ToList();
            if (members.Count < 2)
            {
                continue;
            }

            var keeper = ClosePlans.Keeper(members);
            kept.Add(keeper);

            // An exact-only key closes within each exact title; across titles it only reports.
            foreach (var variant in members.GroupBy(item => item.Identity.ExactOnly ? item.Tab.Title : string.Empty, StringComparer.Ordinal))
            {
                var local = variant.Contains(keeper) ? keeper : ClosePlans.Keeper(variant);
                if (!ReferenceEquals(local, keeper))
                {
                    reports.Add(new SiteReport(local, SiteReportKind.Variant, $"possibly the same page as tab {ClosePlans.Describe(keeper)}"));
                }

                foreach (var item in variant.Where(item => !ReferenceEquals(item, local)))
                {
                    Decide(item, local, closes, reports);
                }
            }
        }

        return new DedupePlan(closes, reports, kept);
    }

    /// <summary>
    /// Whether two URLs can name one page: the same scheme, host and path, ignoring a trailing slash. The query and
    /// fragment are views of a page, so they do not split a duplicate; an unknown URL leaves the decision to the title.
    /// </summary>
    public static bool SamePage(Uri? url, Uri? other) =>
        url is null || other is null || string.Equals(PageOf(url), PageOf(other), StringComparison.Ordinal);

    private static string PageOf(Uri url) =>
        url.IsAbsoluteUri
            ? $"{url.Scheme}://{url.UserInfo}@{url.Authority}{url.AbsolutePath.TrimEnd('/')}"
            : url.OriginalString.Split('?', '#')[0].TrimEnd('/');

    private static void Decide(SiteTab item, SiteTab keeper, List<SiteClose> closes, List<SiteReport> reports)
    {
        var where = ClosePlans.Describe(keeper);
        if (!SamePage(item.Tab.Url, keeper.Tab.Url))
        {
            reports.Add(new SiteReport(item, SiteReportKind.OtherPage, $"same title as tab {where}, different URL"));
        }
        else if (!item.Identity.Auto)
        {
            reports.Add(new SiteReport(item, SiteReportKind.ReportOnly, $"same {item.Identity.Site} key as tab {where}"));
        }
        else if (item.Tab.Pinned || item.Tab.Audible)
        {
            var guard = item.Tab.Pinned ? "pinned" : "audible";
            reports.Add(new SiteReport(item, SiteReportKind.Protected, $"{guard}; would close as duplicate of tab {where}"));
        }
        else
        {
            closes.Add(new SiteClose(item, $"duplicate of tab {where}", keeper.Tab));
        }
    }
}
