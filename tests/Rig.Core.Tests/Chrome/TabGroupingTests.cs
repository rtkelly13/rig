using Rig.Core.Chrome;
using Rig.Core.GitHub;
using Rig.Core.Workspace;

namespace Rig.Core.Tests.Chrome;

/// <summary>The repo-grouping planner as a pure function of Chrome windows: every rule, by fixture. Replaces <c>test_chrome_grouping</c>.</summary>
public sealed class TabGroupingTests
{
    private const string Sep = "·";

    private static readonly UrlOwners RigPorts = new([new("rig", "rtkelly13/rig", new(8090, 8099))], []);

    private static readonly UrlOwners DsVercel = new(
        [],
        [
            new("design-system", "rtkelly-labs/design-system", ["design-system", "design-system-storybook"]),
            new("rig", "rtkelly13/rig", ["design-system-docs"]),
        ]);

    private int _ids = 100;

    private sealed record T(string Title, TabGroup? Group = null, bool Pinned = false, string? Url = null);

    private static string Pr(int number, string repo = "acme-corp/data-platform") =>
        $"Some change by rtkelly13 {Sep} Pull Request #{number} {Sep} {repo}";

    private static TabGroup Group(int id, string title) => new(new RuntimeId([id]), title);

    private ChromeWindow Window(int? desktop = 1, long handle = 1000, params T[] specs) =>
        new(
            handle,
            "w",
            "Profile 4",
            "Ryan",
            handle,
            false,
            desktop,
            [
                .. specs.Select((spec, index) => new ChromeTab(new RuntimeId([42, (int)handle, _ids++]), handle, index, spec.Title, spec.Title, index == 0, spec.Pinned, true, false)
                {
                    Group = spec.Group,
                    Url = spec.Url is null ? null : new Uri(spec.Url),
                }),
            ]);

    private ChromeWindow Window(params T[] specs) => Window(1, 1000, specs);

    private static List<(int Index, string Title, bool New)> Moves(GroupPlan plan) =>
        [.. plan.Moves.Select(move => (move.Tab.Index, move.Title, move.New))];

    private static GroupPlan Plan(params ChromeWindow[] windows) => TabGrouping.Plan(windows, 1);

    [Theory]
    [InlineData("Some change by rtkelly13 · Pull Request #12 · acme-corp/data-platform", "acme-corp/data-platform")]
    [InlineData("Fix it · Issue #4 · rtkelly13/shared-utilities", "rtkelly13/shared-utilities")]
    [InlineData("Pull requests · acme-corp/infrastructure · GitHub", "acme-corp/infrastructure")]
    [InlineData("PR checks · acme-corp/data-platform@1a2b3c4", "acme-corp/data-platform")]
    [InlineData("acme-corp/rights-application: The rights app", "acme-corp/rights-application")]
    [InlineData("GitHub - astral-sh/uv: An extremely fast package manager", "astral-sh/uv")]
    [InlineData("src/app.py at main · owner/repo.name", "owner/repo.name")]
    [InlineData("Inbox (3) - someone@example.com - Gmail", null)]
    [InlineData("A · B", null)]
    public void Repo_comes_from_githubs_title_shapes(string title, string? repo) =>
        Assert.Equal(repo, TabGrouping.RepoOf(title)?.FullName);

    [Fact]
    public void First_ungrouped_tab_creates_the_group_and_the_rest_join() =>
        Assert.Equal(
            [(0, "data-platform", true), (2, "data-platform", false), (3, "data-platform", false)],
            Moves(Plan(Window(new T(Pr(1)), new T("Gmail"), new T(Pr(2)), new T(Pr(3))))));

    [Fact]
    public void A_lone_tab_does_not_start_a_group_below_min_size()
    {
        Assert.Empty(Moves(Plan(Window(new T(Pr(1)), new T(Pr(2, "acme-corp/infrastructure"))))));
        Assert.Equal([(0, "data-platform", true)], Moves(TabGrouping.Plan([Window(new T(Pr(1)))], 1, new GroupOptions(MinSize: 1))));
    }

    [Fact]
    public void An_existing_group_with_the_title_is_joined_even_by_one_tab()
    {
        var plan = Plan(Window(new T(Pr(1), Group(9, "data-platform")), new T(Pr(2))));
        Assert.Equal([(1, "data-platform", false)], Moves(plan));
        Assert.Equal(1, plan.AlreadyGrouped);
    }

    [Fact]
    public void Tabs_in_another_group_and_pinned_tabs_never_move()
    {
        var plan = Plan(Window(new T(Pr(1), Group(9, "today")), new T(Pr(2), Pinned: true), new T(Pr(3)), new T(Pr(4))));
        Assert.Equal([(2, "data-platform", true), (3, "data-platform", false)], Moves(plan));
        Assert.Equal(1, plan.Elsewhere);
    }

    [Fact]
    public void Same_repo_name_under_two_owners_titles_both_with_the_owner() =>
        Assert.Equal(
            [(0, "a/tools", true), (1, "b/tools", true), (2, "a/tools", false), (3, "b/tools", false)],
            Moves(Plan(Window(new T(Pr(1, "a/tools")), new T(Pr(2, "b/tools")), new T(Pr(3, "a/tools")), new T(Pr(4, "b/tools"))))));

    [Fact]
    public void Owner_titles_apply_across_the_profile_not_just_the_window()
    {
        var plan = Plan(
            Window(1, 1000, new T(Pr(1, "a/tools")), new T(Pr(2, "a/tools"))),
            Window(1, 2000, new T(Pr(3, "b/tools")), new T(Pr(4, "b/tools"))));
        Assert.Equal(["a/tools", "b/tools"], plan.Moves.Select(move => move.Title).Distinct().Order());
    }

    [Fact]
    public void A_repo_spread_over_windows_gathers_in_the_window_holding_most_of_it()
    {
        var plan = Plan(Window(1, 1000, new T(Pr(1))), Window(1, 2000, new T(Pr(2)), new T(Pr(3))));
        Assert.Equal(
            [(2, 0, true, 2), (2, 1, false, 2), (1, 0, false, 2)],
            plan.Moves.Select(move => (move.WindowNumber, move.Tab.Index, move.New, move.HomeNumber)));
        Assert.Equal([false, false, true], plan.Moves.Select(move => move.Crosses));
    }

    [Fact]
    public void A_tie_goes_to_the_earlier_window() =>
        Assert.Equal(
            [(1, true, 1), (2, false, 1)],
            Plan(Window(1, 1000, new T(Pr(1))), Window(1, 2000, new T(Pr(2)))).Moves.Select(move => (move.WindowNumber, move.New, move.HomeNumber)));

    [Fact]
    public void An_existing_site_group_is_home_and_loose_tabs_elsewhere_join_it()
    {
        var plan = Plan(
            Window(1, 1000, new T("A : PROJ-1"), new T("B : PROJ-2")),
            Window(1, 2000, new T("C : PROJ-3", Group(9, "YouTrack"))));
        Assert.Equal([(1, false, 2), (1, false, 2)], plan.Moves.Select(move => (move.WindowNumber, move.New, move.HomeNumber)));
        Assert.Equal(1, plan.AlreadyGrouped);
    }

    [Fact]
    public void Duplicate_site_groups_merge_into_the_biggest_after_ungrouping_the_rest()
    {
        TabGroup big = Group(8, "YouTrack"), small = Group(9, "YouTrack");
        var plan = Plan(
            Window(1, 1000, new T("A : PROJ-1", small), new T("B : PROJ-2", small), new T("E : PROJ-5")),
            Window(1, 2000, new T("C : PROJ-3", big), new T("D : PROJ-4", big), new T("F : PROJ-6", big)));
        Assert.Equal([(small, 1, 2)], plan.Ungroups.Select(ungroup => (ungroup.Group, ungroup.WindowNumber, ungroup.Tabs.Count)));
        Assert.Equal([(1, 0, 2), (1, 1, 2), (1, 2, 2)], plan.Moves.Select(move => (move.WindowNumber, move.Tab.Index, move.HomeNumber)));
        Assert.Equal([1], plan.WindowNumbers());
        Assert.Equal([1], plan.UngroupWindowNumbers());
    }

    [Fact]
    public void Every_repo_gathers_in_the_window_holding_the_most_github_tabs()
    {
        const string infra = "acme-corp/infrastructure";
        var stray = Group(9, "data-platform");
        var plan = Plan(
            Window(1, 1000, new T(Pr(1), stray), new T(Pr(2))),
            Window(1, 2000, new T(Pr(3, infra)), new T(Pr(4, infra)), new T(Pr(5, infra)), new T(Pr(6))));
        Assert.Equal([(stray, 1)], plan.Ungroups.Select(ungroup => (ungroup.Group, ungroup.WindowNumber)));
        Assert.Equal(
            [
                ("data-platform", 1, 0, false),
                ("data-platform", 1, 1, false),
                ("data-platform", 2, 3, true),
                ("infrastructure", 2, 0, true),
                ("infrastructure", 2, 1, false),
                ("infrastructure", 2, 2, false),
            ],
            plan.Moves.Select(move => (move.Title, move.WindowNumber, move.Tab.Index, move.New)).Order());
        Assert.Equal([2], plan.Moves.Select(move => move.HomeNumber).Distinct());
    }

    [Fact]
    public void A_repo_absent_from_the_review_window_is_created_there_by_a_tab_moved_in()
    {
        const string infra = "acme-corp/infrastructure";
        var plan = Plan(
            Window(1, 1000, new T(Pr(1, infra)), new T(Pr(2, infra)), new T(Pr(3, infra))),
            Window(1, 2000, new T(Pr(4)), new T(Pr(5))));
        Assert.Equal(
            [(2, 0, true, true), (2, 1, false, true)],
            plan.Moves.Where(move => move.Title == "data-platform").Select(move => (move.WindowNumber, move.Tab.Index, move.New, move.Crosses)));
        var lines = TabPlanText.Groups(plan);
        Assert.StartsWith("  new in w1 data-platform", lines[^3], StringComparison.Ordinal);
    }

    [Fact]
    public void Windows_on_other_desktops_are_gathered_by_default()
    {
        var plan = Plan(Window(1, 1000, new T(Pr(1)), new T(Pr(2))), Window(2, 2000, new T(Pr(3))));
        Assert.Equal([(1, true, 1), (1, false, 1), (2, false, 1)], plan.Moves.Select(move => (move.WindowNumber, move.New, move.HomeNumber)));
        Assert.Empty(plan.Skipped);
    }

    [Fact]
    public void A_duplicate_holding_other_tabs_blocks_the_key()
    {
        TabGroup first = Group(8, "data-platform"), second = Group(9, "data-platform");
        var plan = Plan(
            Window(1, 1000, new T(Pr(1), first), new T(Pr(2), first)),
            Window(1, 2000, new T(Pr(3), second), new T("Inbox - me@x.com - Gmail", second)));
        Assert.Empty(plan.Moves);
        Assert.Empty(plan.Ungroups);
        Assert.Equal(["data-platform"], plan.Blocked.Select(block => block.Title));
        Assert.Equal("2 groups share its title, one holds other tabs", plan.Blocked[0].Reason);
    }

    [Fact]
    public void A_group_on_another_desktop_blocks_the_key()
    {
        var plan = TabGrouping.Plan(
            [Window(1, 1000, new T(Pr(1)), new T(Pr(2))), Window(2, 2000, new T(Pr(3), Group(9, "data-platform")))],
            1,
            new GroupOptions(AllDesktops: false));
        Assert.Empty(plan.Moves);
        Assert.Equal([new BlockedKey("Ryan", "data-platform", "its group is on desktop 2")], plan.Blocked);
        Assert.Equal(
            ["  Skipped window 2 [Ryan]: on desktop 2, not the current one", "  Left data-platform [Ryan]: its group is on desktop 2"],
            TabPlanText.Groups(plan).Take(2));
    }

    [Fact]
    public void A_lone_group_left_on_another_desktop_is_not_reported()
    {
        var plan = TabGrouping.Plan(
            [Window(1, 1000, new T("Gmail")), Window(2, 2000, new T(Pr(3), Group(9, "data-platform")))],
            1,
            new GroupOptions(AllDesktops: false));
        Assert.Empty(plan.Blocked);
        Assert.Equal(0, plan.AlreadyGrouped);
    }

    [Fact]
    public void Groups_on_other_desktops_block_the_key_even_without_loose_tabs()
    {
        var plan = TabGrouping.Plan(
            [
                Window(1, 1000, new T(Pr(3), Group(7, "data-platform"))),
                Window(3, 2000, new T(Pr(1), Group(8, "data-platform"))),
                Window(2, 3000, new T(Pr(2), Group(9, "data-platform"))),
            ],
            1,
            new GroupOptions(AllDesktops: false));
        Assert.Equal([new BlockedKey("Ryan", "data-platform", "its group is on desktop 2, 3")], plan.Blocked);
        Assert.Equal(1, plan.AlreadyGrouped);
    }

    [Fact]
    public void Groups_are_per_profile()
    {
        var other = Window(1, 2000, new T(Pr(3)), new T(Pr(4))) with { ProfileDir = "Default", ProfileName = "Work" };
        var plan = Plan(Window(1, 1000, new T(Pr(1)), new T(Pr(2))), other);
        Assert.Equal(
            [(1, true, 1), (1, false, 1), (2, true, 2), (2, false, 2)],
            plan.Moves.Select(move => (move.WindowNumber, move.New, move.HomeNumber)));
    }

    [Fact]
    public void A_profile_without_a_name_is_shown_by_its_key()
    {
        var plan = Plan(Window(new T(Pr(1)), new T(Pr(2))) with { ProfileName = null });
        Assert.Equal("Profile 4", plan.Moves[0].Profile);
    }

    [Fact]
    public void Windows_that_create_groups_run_before_windows_that_only_join() =>
        Assert.Equal([2, 1], Plan(Window(1, 1000, new T(Pr(1))), Window(1, 2000, new T(Pr(2)), new T(Pr(3)))).WindowNumbers());

    [Theory]
    [InlineData("Inbox (5) - ryan@example.com - example.com Mail", "Google")]
    [InlineData("example.com – Calendar - Week of 28 September 2026", "Google")]
    [InlineData("Budget - Google Sheets", "Google")]
    [InlineData("worktrees - Google Search", "Google")]
    [InlineData("Gemini", "AI")]
    [InlineData("Draft the retro - Gemini", "AI")]
    [InlineData("Cross-Language Profiling - Google Gemini", "AI")]
    [InlineData("Q3 plan - Asana", "Asana")]
    [InlineData("● Q3 plan - Asana", "Asana")]
    [InlineData("Home - Asana", "Asana")]
    [InlineData("Rename six link relationships : PROJ-3302", "YouTrack")]
    [InlineData("Issues | YouTrack", "YouTrack")]
    [InlineData("Link direction brief - Claude", "Claude")]
    [InlineData("Claude", "Claude")]
    [InlineData("Google Meet", null)]
    [InlineData("Meet - abc-defg-hij", null)]
    [InlineData("Zoom Meeting", null)]
    [InlineData("Standup | Microsoft Teams", null)]
    [InlineData("Huddle with Sam - Slack", null)]
    [InlineData("Slack", null)]
    [InlineData("Registrations Retro - Miro", null)]
    public void Site_comes_from_the_title_and_calls_never_group(string title, string? site) =>
        Assert.Equal(site, TabGrouping.SiteOf(title));

    [Theory]
    [InlineData("Linking Triage", "claude.ai", "Claude")]
    [InlineData("Rights App Merge Path", "www.claude.ai", "Claude")]
    [InlineData("Q3 roadmap", "app.asana.com", "Asana")]
    [InlineData("Budget", "docs.google.com", "Google")]
    [InlineData("Draft the retro", "gemini.google.com", "AI")]
    [InlineData("Search", "www.google.com", "Google")]
    [InlineData("Some page", "notgoogle.com", null)]
    [InlineData("Board", "acme.youtrack.cloud", "YouTrack")]
    [InlineData("Standup", "meet.google.com", null)]
    [InlineData("Some page", "example.com", null)]
    [InlineData("Some page", null, null)]
    public void A_bare_title_takes_its_site_from_the_host(string title, string? host, string? site) =>
        Assert.Equal(site, TabGrouping.SiteOf(title, host));

    [Fact]
    public void Tab_key_reads_the_title_alone_without_hosts_and_sites_can_be_replaced()
    {
        var window = Window(new T("Linking Triage"), new T("Some page", Url: "https://claude.ai/x"));
        Assert.Null(TabGrouping.TabKey(window.Tabs[0]));
        Assert.Equal("Claude", TabGrouping.TabKey(window.Tabs[1]));
        var hosts = new Dictionary<string, string>(StringComparer.Ordinal) { ["Linking Triage"] = "claude.ai" };
        Assert.Equal("Claude", TabGrouping.TabKey(window.Tabs[0] with { Url = new Uri("triage", UriKind.Relative) }, hosts));
        Assert.Empty(TabGrouping.Plan([Window(new T("A", Url: "https://claude.ai/a"), new T("B", Url: "https://claude.ai/b"))], 1, new GroupOptions(Sites: SiteHosts.None)).Moves);
    }

    [Fact]
    public void Key_of_prefers_the_repo_then_the_site()
    {
        Assert.Equal("acme-corp/data-platform", TabGrouping.KeyOf(Pr(1)));
        Assert.Equal("Claude", TabGrouping.KeyOf("Linking Triage", "claude.ai"));
        Assert.Null(TabGrouping.KeyOf("Some page"));
    }

    [Fact]
    public void History_hosts_group_bare_claude_artifact_titles()
    {
        var hosts = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal)
        {
            ["Profile 4"] = new Dictionary<string, string>(StringComparer.Ordinal) { ["Linking Triage"] = "claude.ai", ["Linking by Entity"] = "claude.ai" },
        };
        var plan = TabGrouping.Plan([Window(new T("Linking Triage"), new T("Linking by Entity"), new T("Somewhere else"))], 1, new GroupOptions(Hosts: hosts));
        Assert.Equal([(0, "Claude", true), (1, "Claude", false)], Moves(plan));
    }

    [Fact]
    public void Site_tabs_group_like_repos_and_call_tabs_stay_put()
    {
        var plan = Plan(
            Window(1, 1000, new T("Google Meet"), new T("Inbox - me@x.com - Gmail"), new T("A : PROJ-1")),
            Window(1, 2000, new T("Budget - Google Sheets"), new T("B : PROJ-2"), new T("Meet - abc-defg-hij")));
        Assert.Equal(
            [("Google", 1, true), ("Google", 2, false), ("YouTrack", 1, true), ("YouTrack", 2, false)],
            plan.Moves.Select(move => (move.Title, move.WindowNumber, move.New)).Order());
    }

    [Fact]
    public void With_this_desktop_only_windows_on_other_desktops_are_skipped()
    {
        var plan = TabGrouping.Plan(
            [Window(2, 1000, new T(Pr(1)), new T(Pr(2))), Window(null, 2000, new T(Pr(3)), new T(Pr(4)))],
            1,
            new GroupOptions(AllDesktops: false));
        Assert.Empty(plan.Moves);
        Assert.Equal(
            [new SkippedWindow(1, "Ryan", "on desktop 2, not the current one"), new SkippedWindow(2, "Ryan", "on an unknown desktop, not the current one")],
            plan.Skipped);
    }

    [Fact]
    public void Render_lists_moves_per_window_then_a_summary()
    {
        var lines = TabPlanText.Groups(Plan(Window(new T(Pr(1)), new T(Pr(2)))));
        Assert.Equal("Window 1 [Ryan] (2)", lines[0]);
        Assert.StartsWith("  new group data-platform", lines[1], StringComparison.Ordinal);
        Assert.StartsWith("  join      data-platform", lines[2], StringComparison.Ordinal);
        Assert.EndsWith("tab   2  | " + TabPlanText.Clip(Pr(2)), lines[2], StringComparison.Ordinal);
        Assert.Equal(
            "2 tabs to move (0 across windows), 1 new groups, 0 duplicates merged, 0 already grouped, 0 left in other groups",
            lines[^1]);
    }

    [Fact]
    public void Render_shows_ungroups_and_moves_to_other_windows()
    {
        TabGroup small = Group(8, "data-platform"), big = Group(9, "data-platform");
        var lines = TabPlanText.Groups(Plan(Window(1, 1000, new T(Pr(1), small)), Window(1, 2000, new T(Pr(3), big), new T(Pr(4), big))));
        Assert.Equal(
            ["Ungroup first (1)", "  ungroup   data-platform            1 tabs in window 1", "Window 1 [Ryan] (1)"],
            lines.Take(3));
        Assert.StartsWith("  to w2     data-platform", lines[3], StringComparison.Ordinal);
    }

    [Fact]
    public void Local_app_on_a_claimed_port_joins_its_repo_group()
    {
        var plan = TabGrouping.Plan(
            [
                Window(
                    new T(Pr(1, "rtkelly13/rig"), new TabGroup(new RuntimeId([42, 1, 1]), "rig")),
                    new T("Rig / Triage", Url: "http://127.0.0.1:8094/triage/inbox"),
                    new T("Rig / Triage", Url: "http://localhost:8094/triage/issues/5")),
            ],
            1,
            new GroupOptions(Owners: RigPorts));
        Assert.Equal([(1, "rig", false), (2, "rig", false)], Moves(plan));
    }

    [Theory]
    [InlineData("http://127.0.0.1:3000/a")]
    [InlineData("http://localhost/a")]
    [InlineData("https://example.com:8094/a")]
    public void Unclaimed_or_remote_ports_do_not_group(string address) =>
        Assert.Empty(TabGrouping.Plan([Window(new T("App", Url: address), new T("App", Url: address))], 1, new GroupOptions(Owners: RigPorts)).Moves);

    [Fact]
    public void Github_url_names_the_repo_a_title_does_not() =>
        Assert.Equal(
            ["Iceberg.NET", "Iceberg.NET"],
            Plan(Window(new T("General", Url: "https://github.com/rtkelly-labs/Iceberg.NET/settings"), new T(Pr(9, "rtkelly-labs/Iceberg.NET")))).Moves.Select(move => move.Title));

    [Theory]
    [InlineData("https://github.com/users/rtkelly13/projects/1/views/1")]
    [InlineData("https://github.com/search?q=x")]
    [InlineData("https://github.com/rtkelly13")]
    public void Github_pages_without_a_repo_group_as_dev(string address) =>
        Assert.Equal(["Dev", "Dev"], Plan(Window(new T("Page", Url: address), new T("Page", Url: address))).Moves.Select(move => move.Title));

    [Theory]
    [InlineData("design-system-storybook.vercel.app")]
    [InlineData("design-system-storybook-cl1lgrpyn-rtkelly13s-projects.vercel.app")]
    [InlineData("design-system-storybook-diffra.vercel.app")]
    [InlineData("design-system-git-feat-manifesto-lan-326cca-rtkelly13s-projects.vercel.app")]
    public void Vercel_hosts_join_the_repo_their_project_name_declares(string host) =>
        Assert.Equal(
            ["design-system", "design-system"],
            TabGrouping.Plan([Window(new T("Storybook", Url: $"https://{host}/"), new T(Pr(1, "rtkelly-labs/design-system")))], 1, new GroupOptions(Owners: DsVercel))
                .Moves.Select(move => move.Title));

    [Fact]
    public void Longest_declared_vercel_name_wins()
    {
        Assert.Equal("rtkelly13/rig", DsVercel.SlugFor(new Uri("https://design-system-docs-abc.vercel.app/")));
        Assert.Equal("rtkelly-labs/design-system", DsVercel.SlugFor(new Uri("https://design-system-storybook-abc.vercel.app/")));
        Assert.Null(DsVercel.SlugFor(new Uri("https://design-systemx.vercel.app/")));
        Assert.Null(DsVercel.SlugFor(new Uri("https://ds-review-eta.vercel.app/")));
    }

    [Theory]
    [InlineData("www.npmjs.com", "Dev")]
    [InlineData("docs.renovatebot.com", "Dev")]
    [InlineData("vercel.com", "Dev")]
    [InlineData("chatgpt.com", "AI")]
    [InlineData("gemini.google.com", "AI")]
    [InlineData("docs.google.com", "Google")]
    [InlineData("www.plex.tv", null)]
    [InlineData("meet.google.com", null)]
    public void Configured_site_groups_take_the_longest_domain(string host, string? site) =>
        Assert.Equal(site, TabGrouping.SiteOf("Some page", host));

    [Fact]
    public void Repo_of_url_ignores_relative_and_missing_urls()
    {
        Assert.Null(TabGrouping.RepoOfUrl(null));
        Assert.Null(TabGrouping.RepoOfUrl(new Uri("/a/b", UriKind.Relative)));
        Assert.Null(TabGrouping.RepoOfUrl(new Uri("http://localhost:8094/")));
        Assert.Equal(new RepoSlug("o", "r"), TabGrouping.RepoOfUrl(new Uri("http://[::1]:8094/"), new UrlOwners([new("r", "o/r", new(8090, 8099))], [])));
    }
}
