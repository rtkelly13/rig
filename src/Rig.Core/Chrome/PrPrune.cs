using System.Globalization;
using System.Text.RegularExpressions;
using Rig.Core.GitHub;

namespace Rig.Core.Chrome;

/// <summary>Why a PR tab closes.</summary>
public enum CloseKind
{
    /// <summary>The PR merged.</summary>
    Merged,

    /// <summary>The PR closed without merging.</summary>
    Closed,

    /// <summary>Another tab shows the same PR.</summary>
    Duplicate,

    /// <summary>A lower member of its stack has a tab.</summary>
    Stack,

    /// <summary>A member ahead of it in its merge queue has a tab.</summary>
    Queue,
}

/// <summary>Why a PR tab is reported instead of closed.</summary>
public enum ReportKind
{
    /// <summary>Its state could not be resolved.</summary>
    Unknown,

    /// <summary>Pinned or audible, so it stays whatever the rule said.</summary>
    Protected,
}

/// <summary>A tab known to show a PR, with the context the rules need.</summary>
/// <param name="Tab">The tab.</param>
/// <param name="Pr">The PR its title names.</param>
/// <param name="Profile">The profile scope.</param>
/// <param name="WindowAge">Its window's age rank.</param>
/// <param name="WindowNumber">Its window's 1-based number within the profile.</param>
public sealed record PrTab(ChromeTab Tab, PrRef Pr, string Profile, long WindowAge, int WindowNumber) : IPlacedTab
{
    /// <inheritdoc/>
    public string Label => Pr.ToString();
}

/// <summary>A PR tab to close.</summary>
/// <param name="Item">The tab.</param>
/// <param name="Kind">The rule that closes it.</param>
/// <param name="Reason">Why, for people.</param>
/// <param name="Keeper">The tab that stays for this PR, or for its chain's keeper, if any.</param>
public sealed record PlannedClose(PrTab Item, CloseKind Kind, string Reason, ChromeTab? Keeper = null) : IPlannedTabClose
{
    IPlacedTab IPlannedTabClose.Item => Item;
}

/// <summary>A PR tab left open and reported.</summary>
/// <param name="Item">The tab.</param>
/// <param name="Kind">Why it is reported.</param>
/// <param name="Detail">What a person should know.</param>
public sealed record PrReport(PrTab Item, ReportKind Kind, string Detail);

/// <summary>What the PR rules decided across every profile.</summary>
/// <param name="Closes">The tabs to close.</param>
/// <param name="Reports">The tabs reported instead.</param>
/// <param name="Kept">The tabs that stay.</param>
/// <param name="Prs">Every PR seen in a tab, for callers that report coverage.</param>
public sealed record PrunePlan(
    IReadOnlyList<PlannedClose> Closes,
    IReadOnlyList<PrReport> Reports,
    IReadOnlyList<PrTab> Kept,
    IReadOnlySet<PrRef> Prs) : IClosePlan
{
    IReadOnlyList<IPlannedTabClose> IClosePlan.Closes => Closes;

    /// <summary>Window to the tab to select in place of a closed active tab.</summary>
    public IReadOnlyDictionary<long, ChromeTab> Activations() => ClosePlans.Activations(Closes);
}

/// <summary>
/// Decides which GitHub pull-request tabs to close. Pure: no UI Automation, no network. Ported from
/// <c>desktop_manager.chrome.pr_prune</c>.
/// </summary>
/// <remarks>
/// <para>Per Chrome profile, across all of that profile's windows:</para>
/// <list type="number">
/// <item><description>Duplicates: several tabs for one PR keep one.</description></item>
/// <item><description>Merged or closed: every tab of a merged or closed PR closes.</description></item>
/// <item><description>Chains: in a stack or merge queue only the lowest member with a tab keeps it.</description></item>
/// <item><description>Unknown state: reported, never closed.</description></item>
/// </list>
/// <para>
/// Tabs are identified from their titles alone, because that is all UI Automation exposes: GitHub titles every PR page
/// <c>... · Pull Request #n · owner/repo</c>, whichever subpage is showing. Titles change when a PR is renamed and a
/// discarded tab keeps its old one, so tabs are keyed on <c>owner/repo#n</c> only.
/// </para>
/// </remarks>
public static partial class PrPrune
{
    /// <summary>
    /// <c>owner/repo#n</c> from a GitHub PR page title, or null for any other page. Searched rather than anchored, so a
    /// title that still carries a Chrome suffix resolves; the last marker wins when a PR title quotes another.
    /// </summary>
    public static PrRef? RefFromTitle(string title)
    {
        ArgumentNullException.ThrowIfNull(title);
        var matches = PrTitle().Matches(title);
        if (matches.Count == 0)
        {
            return null;
        }

        var match = matches[^1];
        return int.TryParse(match.Groups["number"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            ? new PrRef(match.Groups["owner"].Value, match.Groups["repo"].Value, number)
            : null;
    }

    /// <summary>Windows oldest first, each with its 1-based number within its profile.</summary>
    public static IEnumerable<(ChromeWindow Window, int Number)> NumberedWindows(IEnumerable<ChromeWindow> windows)
    {
        ArgumentNullException.ThrowIfNull(windows);
        var numbering = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var window in windows.OrderBy(window => window.AgeRank).ThenBy(window => window.Handle))
        {
            var number = numbering[window.ProfileKey] = numbering.GetValueOrDefault(window.ProfileKey) + 1;
            yield return (window, number);
        }
    }

    /// <summary>Every tab showing a PR, oldest window first and left to right.</summary>
    public static IReadOnlyList<PrTab> PrTabs(IEnumerable<ChromeWindow> windows) =>
    [
        .. from numbered in NumberedWindows(windows)
           from tab in numbered.Window.Tabs
           let pr = RefFromTitle(tab.Title)
           where pr is not null
           select new PrTab(tab, pr, numbered.Window.ProfileKey, numbered.Window.AgeRank, numbered.Number),
    ];

    /// <summary>Applies the PR rules to every profile's tabs. Pure; nothing is closed here.</summary>
    public static PrunePlan Plan(IEnumerable<ChromeWindow> windows, Resolution resolution)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        var items = PrTabs(windows);
        var chains = resolution.Chains.Where(chain => !resolution.FailedRepos.ContainsKey(chain.Slug)).ToList();

        var closes = new List<PlannedClose>();
        var reports = new List<PrReport>();
        var kept = new List<PrTab>();
        foreach (var profile in items.GroupBy(item => item.Profile, StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            PlanProfile([.. profile], resolution.States, chains, closes, reports, kept);
        }

        return new PrunePlan(closes, reports, kept, items.Select(item => item.Pr).ToHashSet());
    }

    private static void PlanProfile(
        List<PrTab> items,
        IReadOnlyDictionary<PrRef, PrState> states,
        List<RepoChain> chains,
        List<PlannedClose> allCloses,
        List<PrReport> reports,
        List<PrTab> kept)
    {
        var byPr = items.GroupBy(item => item.Pr).ToDictionary(group => group.Key, group => group.ToList());
        var closes = new List<PlannedClose>();
        var openPrs = new HashSet<PrRef>();

        foreach (var pr in byPr.Keys.Order())
        {
            var state = states.GetValueOrDefault(pr) ?? new PrState(PrStatus.Unknown, "not resolved");
            switch (state.Status)
            {
                case PrStatus.Unknown:
                    var detail = state.Error.Length > 0 ? state.Error : "unknown state";
                    reports.AddRange(byPr[pr].Select(item => new PrReport(item, ReportKind.Unknown, detail)));
                    break;
                case PrStatus.Closed:
                    closes.AddRange(byPr[pr].Select(item => new PlannedClose(item, CloseKind.Closed, "closed without merging")));
                    break;
                case PrStatus.Merged:
                    closes.AddRange(byPr[pr].Select(item => new PlannedClose(item, CloseKind.Merged, "merged")));
                    break;
                default:
                    openPrs.Add(pr);
                    break;
            }
        }

        // Rule 3: PR-level decisions first, so a chain keeper's duplicates are settled by rule 1 below.
        var superseded = new Dictionary<PrRef, (CloseKind Kind, string Reason, PrRef Keeper)>();
        foreach (var chain in chains)
        {
            var withTabs = chain.Members.Where(openPrs.Contains).ToList();
            if (withTabs.Count < 2)
            {
                continue;
            }

            var keeper = withTabs[0];
            foreach (var member in withTabs.Skip(1))
            {
                superseded.TryAdd(
                    member,
                    chain.Kind == ChainKind.Stack
                        ? (CloseKind.Stack, $"superseded by #{keeper.Number} (stack bottom)", keeper)
                        : (CloseKind.Queue, $"queued behind #{keeper.Number}", keeper));
            }
        }

        // Rule 1 over every open PR that survives the chains; the chain keeper's kept tab is found here.
        var keptTab = new Dictionary<PrRef, PrTab>();
        foreach (var pr in openPrs.Where(pr => !superseded.ContainsKey(pr)).Order())
        {
            var group = byPr[pr];
            var keeperItem = ClosePlans.Keeper(group);
            keptTab[pr] = keeperItem;
            kept.Add(keeperItem);
            closes.AddRange(
                group.Where(item => !ReferenceEquals(item, keeperItem))
                    .Select(item => new PlannedClose(item, CloseKind.Duplicate, $"duplicate of tab {ClosePlans.Describe(keeperItem)}", keeperItem.Tab)));
        }

        foreach (var pr in superseded.Keys.Order())
        {
            var by = superseded[pr];
            var keeperTab = keptTab.GetValueOrDefault(by.Keeper)?.Tab;
            closes.AddRange(byPr[pr].Select(item => new PlannedClose(item, by.Kind, by.Reason, keeperTab)));
        }

        // Pinned and audible tabs are never closed, whatever the rule said.
        foreach (var close in closes)
        {
            var tab = close.Item.Tab;
            if (tab.Pinned || tab.Audible)
            {
                var guard = tab.Pinned ? "pinned" : "audible";
                reports.Add(new PrReport(close.Item, ReportKind.Protected, $"{guard}; would close as: {close.Reason}"));
                kept.Add(close.Item);
            }
            else
            {
                allCloses.Add(close);
            }
        }
    }

    [GeneratedRegex(@"· Pull Request #(?<number>\d+) · (?<owner>[\w.-]+)/(?<repo>[\w.-]+)(?=\s|$)")]
    private static partial Regex PrTitle();
}
