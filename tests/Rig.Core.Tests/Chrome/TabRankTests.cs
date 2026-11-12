using Rig.Core.Chrome;
using Rig.Core.GitHub;
using static Rig.Core.Tests.Chrome.TabBrowser;

namespace Rig.Core.Tests.Chrome;

/// <summary>
/// Tab ranking, call protection and the memory budget, without a browser. Replaces <c>test_chrome_rank</c>; its
/// rendering test is in <see cref="TabPlanTextTests"/> and the memory parsing it checked in <see cref="ChromeModelTests"/>.
/// </summary>
public sealed class TabRankTests
{
    /// <summary>A tab with reported memory is loaded.</summary>
    private static TabSpec Page(string title, double? mb = null, bool selected = false, bool pinned = false, bool audible = false) =>
        new(title, selected, pinned, Loaded: mb is not null, audible, mb);

    private static Dictionary<string, RankedTab> ByTitle(IEnumerable<RankedTab> ranked) =>
        ranked.ToDictionary(item => item.Tab.Title);

    private static PrRef Pr(int number) => new("acme-corp", "data-platform", number);

    [Theory]
    [InlineData("Meet – Registrations Standup")]
    [InlineData("Meet - Risk log review")]
    [InlineData("Google Meet")]
    [InlineData("Meet")]
    [InlineData("Zoom Meeting")]
    [InlineData("Weekly sync | Microsoft Teams")]
    [InlineData("Huddle: #data-platform - Acme - Slack")]
    [InlineData("Jitsi Meet")]
    public void Call_pages_are_calls_and_never_deduped(string title)
    {
        Assert.True(SiteRules.IsCall(title));
        Assert.Null(SiteRules.Identify(title));
    }

    [Theory]
    [InlineData("Meeting notes - Google Docs")]
    [InlineData("Merge Queue Handbook")]
    public void Ordinary_pages_are_not_calls(string title) => Assert.False(SiteRules.IsCall(title));

    [Fact]
    public void Protection_comes_first_and_calls_are_protected_when_silent()
    {
        var browser = new TabBrowser();
        browser.Window(
            Page("Meet – Registrations Standup", 700),
            Page("Rights App CI Clock", 300, pinned: true),
            Page("Claude Code", 900),
            Page("Merge Queue Handbook", 200, selected: true),
            Page("Bulk Job Docs Caching", 400),
            Page("Link Certainty Carve-out", 100, audible: true));

        var ranked = TabRank.Rank(browser.Windows);
        var byTitle = ByTitle(ranked);

        Assert.Equal("video call", byTitle["Meet – Registrations Standup"].Protected);
        Assert.Equal("pinned", byTitle["Rights App CI Clock"].Protected);
        Assert.Equal("unidentified page", byTitle["Claude Code"].Protected);
        Assert.Equal("active tab", byTitle["Merge Queue Handbook"].Protected);
        Assert.Equal("audible or live media", byTitle["Link Certainty Carve-out"].Protected);
        Assert.Equal(string.Empty, byTitle["Bulk Job Docs Caching"].Protected);
        Assert.Equal(["pinned"], byTitle["Rights App CI Clock"].Reasons);
        Assert.Equal("Bulk Job Docs Caching", ranked[^1].Tab.Title);
        Assert.Equal("Claude Code", byTitle["Claude Code"].Label);
    }

    [Fact]
    public void Pr_signals_order_the_pr_tabs()
    {
        var browser = new TabBrowser();
        browser.Window([.. Enumerable.Range(1, 9).Select(number => new TabSpec(PrTitle(number)))]);
        var states = new Dictionary<PrRef, PrState>
        {
            [Pr(1)] = new(PrStatus.Open, Details: new PrDetails(ReviewRequested: true)),
            [Pr(2)] = new(PrStatus.Open, Details: new PrDetails(AuthoredByViewer: true, Checks: "FAILURE")),
            [Pr(3)] = new(PrStatus.Open, Details: new PrDetails(ReviewDecision: "APPROVED")),
            [Pr(4)] = new(PrStatus.Open, Details: new PrDetails(Draft: true, Additions: TabRank.OversizedLines, Deletions: 1)),
            [Pr(5)] = new(PrStatus.Merged, Details: new PrDetails()),
            [Pr(6)] = new(PrStatus.Open, Details: new PrDetails()),
            [Pr(7)] = new(PrStatus.Closed),
            [Pr(8)] = new(PrStatus.Open, Details: new PrDetails(AuthoredByViewer: true, ReviewDecision: "CHANGES_REQUESTED")),
        };

        var ranked = TabRank.Rank(browser.Windows, new Resolution(states, [], new Dictionary<string, string>()));

        Assert.Equal(
            [(1, 3), (2, 3), (8, 3), (6, 0), (9, 0), (3, -1), (4, -1), (7, -1), (5, -2)],
            ranked.Select(item => (item.Pr?.Number ?? 0, item.Score)));
        var oversized = ranked.Single(item => item.Pr == Pr(4));
        Assert.Equal([$"oversized: +{TabRank.OversizedLines}/-1 in 0 files"], oversized.Flags);
        Assert.Equal(["+2 your PR", "+1 checks failing"], ranked.Single(item => item.Pr == Pr(2)).Reasons);
        Assert.Equal(["+2 your PR", "+1 changes requested"], ranked.Single(item => item.Pr == Pr(8)).Reasons);
        Assert.Equal(["-1 closed without merging"], ranked.Single(item => item.Pr == Pr(7)).Reasons);
        Assert.Empty(ranked.Single(item => item.Pr == Pr(9)).Reasons);
        Assert.Equal("acme-corp/data-platform#4", oversized.Label);
        Assert.NotNull(oversized.Details);
    }

    [Fact]
    public void Ties_break_on_memory_then_window_age_then_position()
    {
        var browser = new TabBrowser();
        var young = browser.Window([Page("Bulk Job Docs Caching", 300), Page("Link Certainty Carve-out", 300)], age: 9);
        var old = browser.Window([Page("Merge Queue Handbook", 300), Page("One Writer, One Code", 100)], age: 1);

        var ranked = TabRank.Rank(browser.Windows);

        Assert.Equal([old.Tabs[1], old.Tabs[0], young.Tabs[0], young.Tabs[1]], ranked.Select(item => item.Tab));
    }

    [Fact]
    public void Work_items_rank_up_and_minimised_windows_rank_down()
    {
        var browser = new TabBrowser();
        browser.Window(Page("Reclaim CI throughput : PROJ-3425"), Page("Merge Queue Handbook"), Page("Checkly"));
        browser.Window([Page("Bulk Job Docs Caching")], minimized: true);

        var scores = ByTitle(TabRank.Rank(browser.Windows)).ToDictionary(pair => pair.Key, pair => pair.Value.Score);

        Assert.Equal(
            new Dictionary<string, int>
            {
                ["Reclaim CI throughput : PROJ-3425"] = 1,
                ["Merge Queue Handbook"] = 0,
                ["Checkly"] = -1,
                ["Bulk Job Docs Caching"] = -1,
            },
            scores);
    }

    [Fact]
    public void Memory_budget_closes_lowest_score_then_biggest_until_it_fits()
    {
        var browser = new TabBrowser();
        browser.Window(
            Page("Meet – Standup", 2000),
            Page("Reclaim CI throughput : PROJ-3425", 900),
            Page("Merge Queue Handbook", 300),
            Page("Bulk Job Docs Caching", 800),
            Page("Link Certainty Carve-out", 100));

        var plan = TabRank.PlanMemory(TabRank.Rank(browser.Windows), 3000);

        Assert.Equal(["Bulk Job Docs Caching", "Merge Queue Handbook"], plan.Closes.Select(close => close.Item.Tab.Title));
        Assert.Equal(["frees 800 MB (score +0)", "frees 300 MB (score +0)"], plan.Closes.Select(close => close.Reason));
        Assert.Equal((4100.0, 3000.0, false), (plan.BeforeMb, plan.AfterMb, plan.OverBudget));
        Assert.All(plan.Closes, close => Assert.Null(close.Keeper));
        Assert.Equal(2, ((IClosePlan)plan).Closes.Count);
    }

    [Fact]
    public void Memory_budget_skips_tabs_other_plans_already_close()
    {
        var browser = new TabBrowser();
        var window = browser.Window(Page("Merge Queue Handbook", 800), Page("Bulk Job Docs Caching", 500));

        var plan = TabRank.PlanMemory(TabRank.Rank(browser.Windows), 600, alreadyClosing: new HashSet<RuntimeId> { window.Tabs[0].RuntimeId });

        Assert.Equal(500, plan.BeforeMb);
        Assert.Empty(plan.Closes);
    }

    [Fact]
    public void Memory_budget_counts_but_never_closes_held_tabs()
    {
        var browser = new TabBrowser();
        var window = browser.Window(Page("Rights App CI Clock - Claude", 900), Page("Merge Queue Handbook", 100));

        var plan = TabRank.PlanMemory(TabRank.Rank(browser.Windows), 500, held: new HashSet<RuntimeId> { window.Tabs[0].RuntimeId });

        Assert.Equal(["Merge Queue Handbook"], plan.Closes.Select(close => close.Item.Tab.Title));
        Assert.True(plan.OverBudget);
    }

    [Fact]
    public void Memory_budget_never_touches_protected_or_unloaded_tabs()
    {
        var browser = new TabBrowser();
        browser.Window(Page("Meet – Standup", 3000), Page("Merge Queue Handbook", 500, audible: true), Page("Bulk Job Docs Caching"));

        var plan = TabRank.PlanMemory(TabRank.Rank(browser.Windows), 1024);

        Assert.Empty(plan.Closes);
        Assert.True(plan.OverBudget);
        Assert.Equal(3500, plan.BeforeMb);
    }

    [Theory]
    [InlineData(3500, "3.42 GB")]
    [InlineData(1024, "1.00 GB")]
    [InlineData(1023.6, "1024 MB")]
    [InlineData(300, "300 MB")]
    [InlineData(2.5, "2 MB")]
    [InlineData(3.5, "4 MB")]
    public void Sizes_read_in_megabytes_or_gigabytes(double mb, string expected) => Assert.Equal(expected, TabRank.FormatSize(mb));
}
