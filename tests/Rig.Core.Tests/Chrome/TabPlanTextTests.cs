using Rig.Core.Chrome;

namespace Rig.Core.Tests.Chrome;

/// <summary>
/// The <c>chrome-tabs</c> plans as lines. Replaces the rendering tests of <c>test_chrome_dedupe</c>,
/// <c>test_chrome_rank</c> and <c>test_chrome_saved_groups</c>; the PR plan's is in <see cref="PrPruneTests"/>.
/// </summary>
public sealed class TabPlanTextTests
{
    private static List<string> Headings(IEnumerable<string> lines) =>
        [.. lines.Where(line => !line.StartsWith(' ')).Select(line => line.Split(" (")[0])];

    [Fact]
    public void Site_plan_lists_closes_then_reports()
    {
        var browser = new TabBrowser();
        browser.Window(new TabSpec("X Handbook", Selected: true), new TabSpec("X Handbook"), new TabSpec("X Handbook - Claude"));

        var lines = TabPlanText.Dedupe(Dedupe.Plan(browser.Windows));

        Assert.Equal(
            ["Close: duplicate pages", "Report only: title variant of a kept tab", "1 to close, 1 duplicated pages kept, 1 reported"],
            Headings(lines));
        Assert.Equal("  title:X Handbook  [Profile 4, window 1, tab 2]", lines[1][..(lines[1].IndexOf(']', StringComparison.Ordinal) + 1)]);
    }

    [Fact]
    public void Memory_plan_says_when_protected_tabs_keep_it_over_budget()
    {
        var browser = new TabBrowser();
        browser.Window(new TabSpec("Meet – Standup", Loaded: true, Mb: 3000), new TabSpec("Merge Queue Handbook", Loaded: true, Audible: true, Mb: 500));

        Assert.Equal(
            ["Memory: 3.42 GB -> 3.42 GB of 1.00 GB, still over budget: every remaining tab is protected or held for review"],
            TabPlanText.Memory(TabRank.PlanMemory(TabRank.Rank(browser.Windows), 1024)));
    }

    [Fact]
    public void Memory_plan_lists_its_closes_then_the_fit()
    {
        var browser = new TabBrowser();
        browser.Window(new TabSpec("Merge Queue Handbook", Loaded: true, Mb: 300), new TabSpec("Bulk Job Docs Caching", Loaded: true, Mb: 800));

        var lines = TabPlanText.Memory(TabRank.PlanMemory(TabRank.Rank(browser.Windows), 512));

        Assert.Equal("Close: over the 512 MB memory budget (1)", lines[0]);
        Assert.Contains("  [Profile 4, window 1, tab 2]  frees 800 MB (score +0)  | Bulk Job Docs Caching", lines[1], StringComparison.Ordinal);
        Assert.Equal("Memory: 1.07 GB -> 300 MB of 512 MB, within budget", lines[2]);
    }

    [Fact]
    public void Rank_lines_show_standing_memory_and_signals()
    {
        var browser = new TabBrowser();
        browser.Window(new TabSpec("Merge Queue Handbook", Selected: true, Loaded: true, Mb: 300), new TabSpec("Checkly"));

        var ranked = TabRank.Rank(browser.Windows);
        var lines = TabPlanText.Rank(ranked);

        Assert.Equal(2, lines.Count);
        Assert.StartsWith($"    1 keep   300 MB  {ranked[0].Label,-48}  [Profile 4 w1 t1]  ", lines[0], StringComparison.Ordinal);
        Assert.Equal($"    2   -1        -  {ranked[1].Label,-48}  [Profile 4 w1 t2]  -1 weakly identified title", lines[1]);
    }

    [Fact]
    public void Rank_line_without_signals_says_so()
    {
        var browser = new TabBrowser();
        browser.Window(new TabSpec("Merge Queue Handbook"));

        Assert.EndsWith("]  no signals", TabPlanText.Rank(TabRank.Rank(browser.Windows))[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Saved_groups_list_the_bar_then_the_deletes()
    {
        SavedGroup[] bar =
        [
            new("Default", "Google", true, 0),
            new("Default", "Google", false, 1),
            new("Default", "YouTrack", false, 2),
            new("Default", "YouTrack", false, 3),
        ];

        Assert.Equal(
            [
                "Saved groups [Default] (4)",
                "    1  open    Google",
                "    2  closed  Google",
                "    3  closed  YouTrack",
                "    4  closed  YouTrack",
                "Delete: closed duplicates (2)",
                "  1 x Google                   [Default] of 2 saved, keeping the open copy",
                "  1 x YouTrack                 [Default] of 2 saved, keeping the leftmost closed copy",
                "2 saved groups to delete, 4 seen",
            ],
            TabPlanText.SavedGroups(SavedGroups.PlanConsolidation(bar)));
    }

    [Fact]
    public void Saved_groups_report_open_duplicates_per_profile()
    {
        SavedGroup[] bars =
        [
            new("Default", "Google", true, 0),
            new("Default", "Google", true, 1),
            new("Profile 4", "Claude", false, 0),
        ];

        Assert.Equal(
            [
                "Saved groups [Default] (2)",
                "    1  open    Google",
                "    2  open    Google",
                "Saved groups [Profile 4] (1)",
                "    1  closed  Claude",
                "  Report only: Google [Default] is open 2 times (never deleted)",
                "0 saved groups to delete, 3 seen",
            ],
            TabPlanText.SavedGroups(SavedGroups.PlanConsolidation(bars)));
    }

    [Theory]
    [InlineData("short", 70, "short")]
    [InlineData("abcdef", 6, "abcdef")]
    [InlineData("abcdefg", 6, "abcde…")]
    [InlineData("ab😀😀😀d", 5, "ab😀😀…")]
    public void Titles_are_cut_by_character_with_an_ellipsis(string title, int width, string expected) =>
        Assert.Equal(expected, TabPlanText.Clip(title, width));
}
