using Rig.Core.Chrome;
using Rig.Core.GitHub;
using Rig.Core.Tests.Fakes;
using static Rig.Core.Tests.Chrome.TabBrowser;

namespace Rig.Core.Tests.Chrome;

/// <summary>
/// The PR-prune planner as a pure function of (tabs, PR states), and the shared executor. Replaces
/// <c>test_chrome_pr_prune</c>, except the rendering test, which moves with the renderer.
/// </summary>
public sealed class PrPruneTests
{
    private static TabSpec Tab(int number, bool selected = false, bool pinned = false, bool loaded = false, bool audible = false, string? title = null) =>
        new(title ?? PrTitle(number), selected, pinned, loaded, audible);

    private static PrRef Ref(int number) => PrRef.Parse($"{Repo}#{number}");

    private static OpenPr Open(int number, string head, string @base = "main", int? queuePosition = null) =>
        new(number, head, @base, "2026-09-01T00:00:00Z", QueuePosition: queuePosition);

    private static Resolution Resolve(Dictionary<int, PrStatus> states, IEnumerable<OpenPr>? open = null, Dictionary<string, string>? failed = null)
    {
        var byRef = states.ToDictionary(pair => Ref(pair.Key), pair => new PrState(pair.Value, pair.Value == PrStatus.Unknown ? "NOT_FOUND" : ""));
        var chains = PrChains.Build(open ?? []).Select(chain => RepoChain.Of("acme-corp", "data-platform", chain)).ToList();
        return new Resolution(byRef, chains, failed ?? []);
    }

    private static List<(int, CloseKind, string)> Closes(PrunePlan plan) =>
        [.. plan.Closes.Select(close => (close.Item.Pr.Number, close.Kind, close.Reason))];

    private static List<(int, ReportKind)> Reports(PrunePlan plan) =>
        [.. plan.Reports.Select(report => (report.Item.Pr.Number, report.Kind))];

    private static OpenPr[] StackOfThree() => [Open(10, "a"), Open(11, "b", "a"), Open(12, "c", "b")];

    [Fact]
    public void Ref_from_title_lowercases_and_ignores_subpages_and_suffixes()
    {
        Assert.Equal(Ref(780), PrPrune.RefFromTitle(PrTitle(780, repo: "Acme-Corp/Data-Platform")));
        Assert.Equal(Ref(780), PrPrune.RefFromTitle(PrTitle(780) + " - Memory usage - 12 MB"));
        Assert.Null(PrPrune.RefFromTitle($"Pull requests {Sep} acme-corp/data-platform"));
        Assert.Null(PrPrune.RefFromTitle("Issue 12 · acme-corp/data-platform"));
    }

    [Fact]
    public void Ref_from_title_takes_the_last_marker_when_a_pr_title_quotes_another() =>
        Assert.Equal(Ref(12), PrPrune.RefFromTitle(PrTitle(12, title: $"Revert {Sep} Pull Request #9 {Sep} other/repo")));

    [Fact]
    public void Ref_from_title_rejects_a_number_too_big_for_an_int() =>
        Assert.Null(PrPrune.RefFromTitle(PrTitle(1).Replace("#1 ", "#99999999999 ", StringComparison.Ordinal)));

    [Fact]
    public void Duplicate_keeps_the_active_tab_over_a_loaded_one()
    {
        var browser = new TabBrowser();
        browser.Window(Tab(1, loaded: true), Tab(1, selected: true), Tab(2));

        var plan = PrPrune.Plan(browser.Windows, Resolve(new() { [1] = PrStatus.Open, [2] = PrStatus.Open }));

        Assert.Equal([(1, CloseKind.Duplicate, "duplicate of tab 2 in window 1")], Closes(plan));
        Assert.Equal(0, plan.Closes[0].Item.Tab.Index);
        Assert.Equal(new HashSet<PrRef> { Ref(1), Ref(2) }, plan.Prs);
        Assert.Equal("acme-corp/data-platform#1", plan.Closes[0].Item.Label);
    }

    [Fact]
    public void Duplicate_keeps_a_loaded_tab_over_the_leftmost()
    {
        var browser = new TabBrowser();
        browser.Window(Tab(1), Tab(1, loaded: true));

        var plan = PrPrune.Plan(browser.Windows, Resolve(new() { [1] = PrStatus.Open }));

        Assert.Equal([0], plan.Closes.Select(close => close.Item.Tab.Index));
    }

    [Fact]
    public void Duplicate_falls_back_to_the_leftmost_tab_in_the_oldest_window()
    {
        var browser = new TabBrowser();
        var young = browser.Window([Tab(1)], age: 50);
        var old = browser.Window([Tab(9), Tab(1)], age: 10);

        var plan = PrPrune.Plan(browser.Windows, Resolve(new() { [1] = PrStatus.Open, [9] = PrStatus.Open }));

        Assert.Equal([young.Handle], plan.Closes.Select(close => close.Item.Tab.WindowHandle));
        Assert.Same(old.Tabs[1], plan.Closes[0].Keeper);
        Assert.Equal("duplicate of tab 2 in window 1", plan.Closes[0].Reason);
    }

    [Fact]
    public void Renamed_title_is_still_a_duplicate()
    {
        var browser = new TabBrowser();
        browser.Window(Tab(1, title: PrTitle(1, "Old name"), loaded: true), Tab(1, title: PrTitle(1, "New name")));

        var plan = PrPrune.Plan(browser.Windows, Resolve(new() { [1] = PrStatus.Open }));

        Assert.Equal([(1, CloseKind.Duplicate, "duplicate of tab 1 in window 1")], Closes(plan));
    }

    [Fact]
    public void Duplicates_are_per_profile()
    {
        var browser = new TabBrowser();
        browser.Window([Tab(1)], profile: "Default");
        browser.Window([Tab(1)], profile: "Profile 4");

        var plan = PrPrune.Plan(browser.Windows, Resolve(new() { [1] = PrStatus.Open }));

        Assert.Empty(plan.Closes);
        Assert.Equal(2, plan.Kept.Count);
    }

    [Fact]
    public void Merged_closes_every_tab_even_the_active_one()
    {
        var browser = new TabBrowser();
        browser.Window(Tab(1, selected: true, loaded: true), Tab(1));

        var plan = PrPrune.Plan(browser.Windows, Resolve(new() { [1] = PrStatus.Merged }));

        Assert.Equal([(1, CloseKind.Merged, "merged"), (1, CloseKind.Merged, "merged")], Closes(plan));
        Assert.Empty(plan.Activations());
    }

    [Fact]
    public void Stack_of_three_with_only_the_top_two_open_as_tabs_keeps_the_middle()
    {
        var browser = new TabBrowser();
        browser.Window(Tab(11), Tab(12));

        var plan = PrPrune.Plan(browser.Windows, Resolve(new() { [11] = PrStatus.Open, [12] = PrStatus.Open }, StackOfThree()));

        Assert.Equal([(12, CloseKind.Stack, "superseded by #11 (stack bottom)")], Closes(plan));
        Assert.Equal([11], plan.Kept.Select(item => item.Pr.Number));
    }

    [Fact]
    public void Stack_keeper_with_several_tabs_is_deduplicated()
    {
        var browser = new TabBrowser();
        var window = browser.Window(Tab(10), Tab(10, selected: true), Tab(12));

        var plan = PrPrune.Plan(browser.Windows, Resolve(new() { [10] = PrStatus.Open, [12] = PrStatus.Open }, StackOfThree()));

        Assert.Equal(
            [(10, CloseKind.Duplicate, "duplicate of tab 2 in window 1"), (12, CloseKind.Stack, "superseded by #10 (stack bottom)")],
            Closes(plan));
        Assert.Same(window.Tabs[1], plan.Closes[1].Keeper);
    }

    [Fact]
    public void Merged_middle_member_breaks_the_stack()
    {
        // 11 merged: it is no longer in the open list, so 12's base is nobody's open head.
        var browser = new TabBrowser();
        browser.Window(Tab(10), Tab(11), Tab(12));

        var plan = PrPrune.Plan(
            browser.Windows,
            Resolve(new() { [10] = PrStatus.Open, [11] = PrStatus.Merged, [12] = PrStatus.Open }, [Open(10, "a"), Open(12, "c", "b")]));

        Assert.Equal([(11, CloseKind.Merged, "merged")], Closes(plan));
        Assert.Equal([10, 12], plan.Kept.Select(item => item.Pr.Number).Order());
    }

    [Fact]
    public void Native_merge_queue_keeps_the_front_member_that_has_a_tab()
    {
        var browser = new TabBrowser();
        browser.Window(Tab(21), Tab(22), Tab(23));
        OpenPr[] queue = [Open(20, "q0", queuePosition: 1), Open(23, "q3", queuePosition: 2), Open(21, "q1", queuePosition: 3), Open(22, "q2", queuePosition: 4)];

        var plan = PrPrune.Plan(browser.Windows, Resolve(new() { [21] = PrStatus.Open, [22] = PrStatus.Open, [23] = PrStatus.Open }, queue));

        Assert.Equal([(21, CloseKind.Queue, "queued behind #23"), (22, CloseKind.Queue, "queued behind #23")], Closes(plan));
    }

    [Fact]
    public void Label_queue_puts_the_holder_first_then_orders_by_label_time()
    {
        var browser = new TabBrowser();
        browser.Window(Tab(31), Tab(32), Tab(33));
        OpenPr[] queue =
        [
            Open(31, "l1") with { Labels = new HashSet<string> { "automerge" }, LabeledAt = new Dictionary<string, string> { ["automerge"] = "2026-09-02T00:00:00Z" } },
            Open(32, "l2") with { Labels = new HashSet<string> { "automerge" }, LabeledAt = new Dictionary<string, string> { ["automerge"] = "2026-09-01T00:00:00Z" } },
            Open(33, "l3") with { Labels = new HashSet<string> { "automerge", "locked-merging" } },
        ];

        var plan = PrPrune.Plan(browser.Windows, Resolve(new() { [31] = PrStatus.Open, [32] = PrStatus.Open, [33] = PrStatus.Open }, queue));

        Assert.Equal([(31, CloseKind.Queue, "queued behind #33"), (32, CloseKind.Queue, "queued behind #33")], Closes(plan));
    }

    [Fact]
    public void Chain_rules_are_skipped_for_a_repo_whose_open_list_failed()
    {
        var browser = new TabBrowser();
        browser.Window(Tab(11), Tab(12));

        var plan = PrPrune.Plan(
            browser.Windows,
            Resolve(new() { [11] = PrStatus.Open, [12] = PrStatus.Open }, StackOfThree(), new() { [Repo] = "timeout" }));

        Assert.Empty(plan.Closes);
    }

    [Fact]
    public void Active_superseded_tab_activates_the_keeper_in_the_same_window()
    {
        var browser = new TabBrowser();
        var window = browser.Window(Tab(11), Tab(12, selected: true));

        var plan = PrPrune.Plan(browser.Windows, Resolve(new() { [11] = PrStatus.Open, [12] = PrStatus.Open }, StackOfThree()));

        Assert.Equal(new Dictionary<long, ChromeTab> { [window.Handle] = window.Tabs[0] }, plan.Activations());
    }

    [Fact]
    public void Active_superseded_tab_in_another_window_leaves_chrome_to_pick()
    {
        var browser = new TabBrowser();
        browser.Window(Tab(11));
        browser.Window(Tab(12, selected: true));

        var plan = PrPrune.Plan(browser.Windows, Resolve(new() { [11] = PrStatus.Open, [12] = PrStatus.Open }, StackOfThree()));

        Assert.Empty(plan.Activations());
    }

    [Fact]
    public void Closed_without_merging_closes_every_tab()
    {
        var browser = new TabBrowser();
        browser.Window(Tab(5), Tab(5));

        var plan = PrPrune.Plan(browser.Windows, Resolve(new() { [5] = PrStatus.Closed }));

        Assert.Equal([(5, CloseKind.Closed, "closed without merging"), (5, CloseKind.Closed, "closed without merging")], Closes(plan));
        Assert.Empty(plan.Reports);
    }

    [Fact]
    public void Unknown_state_is_never_closed_even_as_a_duplicate_or_chain_member()
    {
        var browser = new TabBrowser();
        browser.Window(Tab(6), Tab(6), Tab(11), Tab(12));

        var plan = PrPrune.Plan(
            browser.Windows,
            Resolve(new() { [6] = PrStatus.Unknown, [11] = PrStatus.Open, [12] = PrStatus.Unknown }, StackOfThree()));

        Assert.Empty(plan.Closes);
        Assert.Equal([(6, ReportKind.Unknown), (6, ReportKind.Unknown), (12, ReportKind.Unknown)], Reports(plan));
        Assert.Equal("NOT_FOUND", plan.Reports[0].Detail);
    }

    [Fact]
    public void Unknown_state_without_an_error_says_so()
    {
        var browser = new TabBrowser();
        browser.Window(Tab(6));
        var resolution = new Resolution(new Dictionary<PrRef, PrState> { [Ref(6)] = new(PrStatus.Unknown) }, [], new Dictionary<string, string>());

        var plan = PrPrune.Plan(browser.Windows, resolution);

        Assert.Equal(["unknown state"], plan.Reports.Select(report => report.Detail));
    }

    [Fact]
    public void Unresolved_pr_counts_as_unknown()
    {
        var browser = new TabBrowser();
        browser.Window(Tab(7));

        var plan = PrPrune.Plan(browser.Windows, Resolve([]));

        Assert.Equal([(ReportKind.Unknown, "not resolved")], plan.Reports.Select(report => (report.Kind, report.Detail)));
    }

    [Fact]
    public void Pinned_and_audible_tabs_are_never_closed()
    {
        var browser = new TabBrowser();
        browser.Window(Tab(1, pinned: true), Tab(1, audible: true), Tab(1, selected: true), Tab(2, pinned: true));

        var plan = PrPrune.Plan(browser.Windows, Resolve(new() { [1] = PrStatus.Open, [2] = PrStatus.Merged }));

        Assert.Empty(plan.Closes);
        Assert.Equal(
            [
                (1, "audible; would close as: duplicate of tab 3 in window 1"),
                (1, "pinned; would close as: duplicate of tab 3 in window 1"),
                (2, "pinned; would close as: merged"),
            ],
            plan.Reports.Select(report => (report.Item.Pr.Number, report.Detail)).Order());
        Assert.All(plan.Reports, report => Assert.Equal(ReportKind.Protected, report.Kind));
    }

    [Fact]
    public void Non_pr_tabs_are_ignored()
    {
        var browser = new TabBrowser();
        browser.Window(new TabSpec("Inbox"), new TabSpec($"Pull requests {Sep} acme-corp/data-platform"));

        var plan = PrPrune.Plan(browser.Windows, Resolve([]));

        Assert.Empty(plan.Closes);
        Assert.Empty(plan.Reports);
        Assert.Empty(plan.Kept);
    }

    [Fact]
    public void Apply_closes_by_identity_then_restores_selection_once()
    {
        var browser = new TabBrowser();
        var window = browser.Window(Tab(11), Tab(12, selected: true), Tab(3));
        var plan = PrPrune.Plan(
            browser.Windows,
            Resolve(new() { [3] = PrStatus.Merged, [11] = PrStatus.Open, [12] = PrStatus.Open }, StackOfThree()));
        var actions = new FakeChromeTabActions();

        var applied = ClosePlans.Apply([plan], browser.Windows, actions);

        Assert.Equal([window.Tabs[2].RuntimeId, window.Tabs[1].RuntimeId], actions.Closed.Select(tab => tab.RuntimeId));
        Assert.All(applied, result => Assert.True(result.Closed));
        var restored = Assert.Single(actions.Restored);
        Assert.Equal([window.Handle], restored.Windows);
        Assert.Equal(new Dictionary<long, ChromeTab> { [window.Handle] = window.Tabs[0] }, restored.Fallbacks);
    }

    [Fact]
    public void Apply_finishes_each_window_before_the_next_in_the_given_order()
    {
        var browser = new TabBrowser();
        var first = browser.Window(Tab(3), Tab(11, selected: true));
        var second = browser.Window(Tab(4), Tab(12), Tab(5));
        var untouched = browser.Window(Tab(13));
        var plan = PrPrune.Plan(
            browser.Windows,
            Resolve(new()
            {
                [3] = PrStatus.Merged,
                [4] = PrStatus.Merged,
                [5] = PrStatus.Merged,
                [11] = PrStatus.Open,
                [12] = PrStatus.Open,
                [13] = PrStatus.Open,
            }));
        var actions = new FakeChromeTabActions();

        ClosePlans.Apply(
            [plan],
            [.. browser.Windows, first],
            actions,
            order: [second.Handle, 999, first.Handle, second.Handle],
            afterWindow: (window, closes) => actions.Log.Add(("after", window.Handle, closes.Count)));

        Assert.Equal(
            [
                ("close", second.Handle, 0),
                ("close", second.Handle, 2),
                ("restore", second.Handle, -1),
                ("after", second.Handle, 2),
                ("close", first.Handle, 0),
                ("restore", first.Handle, -1),
                ("after", first.Handle, 1),
                ("after", untouched.Handle, 0),
            ],
            actions.Log);
    }

    [Fact]
    public void Apply_without_order_or_callback_goes_in_window_order()
    {
        var browser = new TabBrowser();
        var first = browser.Window(Tab(3));
        var second = browser.Window(Tab(4));
        var plan = PrPrune.Plan(browser.Windows, Resolve(new() { [3] = PrStatus.Merged, [4] = PrStatus.Merged }));
        var actions = new FakeChromeTabActions();

        var applied = ClosePlans.Apply([plan], browser.Windows, actions);

        Assert.Equal([first.Handle, second.Handle], applied.Select(result => result.Planned.Item.Tab.WindowHandle));
        Assert.Equal("closed", applied[0].Detail);
        Assert.Equal(TimeSpan.Zero, applied[0].Elapsed);
    }

    [Fact]
    public void Unattended_holds_back_active_tabs_and_the_foreground_window()
    {
        var browser = new TabBrowser();
        var background = browser.Window(Tab(1), Tab(1, selected: true), Tab(2));
        var foreground = browser.Window(Tab(2), Tab(3));
        var plan = PrPrune.Plan(browser.Windows, Resolve(new() { [1] = PrStatus.Merged, [2] = PrStatus.Merged, [3] = PrStatus.Merged }));

        var runnable = ClosePlans.HoldUnattended([plan], foreground.Handle);

        Assert.Equal([background.Tabs[0], background.Tabs[2]], runnable.Closes.Select(close => close.Item.Tab));
        Assert.Equal(
            new HashSet<RuntimeId> { background.Tabs[1].RuntimeId, foreground.Tabs[0].RuntimeId, foreground.Tabs[1].RuntimeId },
            runnable.Held.Select(close => close.Item.Tab.RuntimeId).ToHashSet());
    }

    [Fact]
    public void Describe_names_the_position_and_window()
    {
        var browser = new TabBrowser();
        browser.Window(Tab(1));
        browser.Window(Tab(2), Tab(1));

        var item = PrPrune.PrTabs(browser.Windows)[^1];

        Assert.Equal("2 in window 2", ClosePlans.Describe(item));
    }
}
