using Rig.Core.Chrome;
using Rig.Core.GitHub;
using Rig.Core.Tests.Fakes;
using static Rig.Core.Tests.Chrome.TabBrowser;

namespace Rig.Core.Tests.Chrome;

/// <summary>
/// Per-site title rules and the non-PR duplicate planner, from real titles seen in Chrome history. Replaces
/// <c>test_chrome_dedupe</c>; its rendering test is in <see cref="TabPlanTextTests"/>.
/// </summary>
public sealed class DedupeTests
{
    private static List<string> ClosedTitles(DedupePlan plan) => [.. plan.Closes.Select(close => close.Item.Tab.Title)];

    [Theory]
    [InlineData("Reclaim CI throughput : PROJ-3425", "youtrack", "youtrack:PROJ-3425")]
    [InlineData("[RA-4078] Royalty Shipments missing sourcename - JIRA", "jira", "jira:RA-4078")]
    [InlineData("Flaky build · Issue #12 · acme-corp/playbook", "github-issue", "gh-issue:acme-corp/playbook#12")]
    [InlineData("Flaky build · Issue #12 · acme-corp/playbook · GitHub", "github-issue", "gh-issue:acme-corp/playbook#12")]
    [InlineData(
        "data-platform/README.md at main · acme-corp/data-platform",
        "github-file",
        "gh-blob:acme-corp/data-platform@main:data-platform/README.md")]
    [InlineData("Pull requests · acme-corp/data-platform", "github-list", "gh-list:acme-corp/data-platform:Pull requests")]
    [InlineData("Inbox (13) - someone@example.com - example.com Mail", "gmail", "gmail:someone@example.com:Inbox")]
    [InlineData(
        "example.com – Calendar - Week of 21 September 2026",
        "google-calendar",
        "gcal:example.com:Week of 21 September 2026")]
    [InlineData("Leadership Ladder - Google Sheets", "google-docs", "gdoc:Sheets:Leadership Ladder")]
    [InlineData("Work - Google Drive", "google-drive", "gdrive:Work")]
    [InlineData("● SharedResources - Asana", "asana", "asana:SharedResources")]
    [InlineData(
        "acme-live-cluster - Database Details | Aurora and RDS | eu-west-1",
        "aws",
        "aws:eu-west-1:Aurora and RDS:acme-live-cluster")]
    [InlineData("NuGet Gallery | MySql.Data 26.7.0", "nuget", "nuget:MySql.Data 26.7.0")]
    [InlineData("Rights App CI Clock - Claude", "claude", "title:Rights App CI Clock")]
    [InlineData("Rights App CI Clock", "title", "title:Rights App CI Clock")]
    [InlineData("  Merge Queue Handbook  ", "title", "title:Merge Queue Handbook")]
    public void Rules_key_real_titles(string title, string site, string key)
    {
        var identity = SiteRules.Identify(title);

        Assert.NotNull(identity);
        Assert.Equal((site, key), (identity.Site, identity.Key));
    }

    [Theory]
    [InlineData("Claude")]
    [InlineData("Claude Code")]
    [InlineData("New chat - Claude")]
    [InlineData("New task - Claude")]
    [InlineData("Artifact - Claude")]
    [InlineData("Page not found – Claude")]
    [InlineData("Sign in - Claude")]
    [InlineData("Page not found · GitHub · GitHub")]
    [InlineData("Google Meet")]
    [InlineData("Meet – Data & Platform Standup - Daily")]
    [InlineData("My Drive - Google Drive")]
    [InlineData("Home - Google Drive")]
    [InlineData("parquet encodings - Google Search")]
    [InlineData("Vercel")]
    [InlineData("Dashboard | Aurora and RDS | eu-west-1")]
    [InlineData("CodeDeploy - AWS Developer Tools")]
    [InlineData("New Songs - RightsApp Administration")]
    [InlineData("Runtime Error")]
    [InlineData("Datalake schema")]
    [InlineData("https://github.com/acme-corp/data-platform/pull/778")]
    [InlineData("")]
    public void Ambiguous_titles_are_never_keyed(string title) => Assert.Null(SiteRules.Identify(title));

    [Theory]
    [InlineData("Checkly", "title")]
    [InlineData("CI · acme-corp/playbook@1a2b3c4", "github-run")]
    public void One_word_titles_and_actions_runs_only_report(string title, string site)
    {
        var identity = SiteRules.Identify(title);

        Assert.NotNull(identity);
        Assert.Equal(site, identity.Site);
        Assert.False(identity.Auto);
    }

    [Fact]
    public void Duplicate_artifacts_close_down_to_the_active_tab()
    {
        var browser = new TabBrowser();
        var window = browser.Window(
            new TabSpec("Merge Queue Handbook", Loaded: true),
            new TabSpec("Merge Queue Handbook"),
            new TabSpec("Merge Queue Handbook", Selected: true),
            new TabSpec("One Writer, One Code"),
            new TabSpec("One Writer, One Code", Loaded: true));

        var plan = Dedupe.Plan(browser.Windows);

        Assert.Equal(
            new HashSet<RuntimeId> { window.Tabs[2].RuntimeId, window.Tabs[4].RuntimeId },
            plan.Kept.Select(item => item.Tab.RuntimeId).ToHashSet());
        Assert.Equal(
            new HashSet<RuntimeId> { window.Tabs[0].RuntimeId, window.Tabs[1].RuntimeId, window.Tabs[3].RuntimeId },
            plan.Closes.Select(close => close.Item.Tab.RuntimeId).ToHashSet());
        Assert.All(plan.Closes, close => Assert.Equal(close.Item.Identity.Key, close.Item.Label));
    }

    [Fact]
    public void Duplicates_are_found_across_windows_of_one_profile_only()
    {
        var browser = new TabBrowser();
        const string title = "example.com – Calendar - Week of 21 September 2026";
        browser.Window(new TabSpec(title));
        browser.Window(new TabSpec(title));
        browser.Window([new TabSpec(title)], profile: "Default");

        var plan = Dedupe.Plan(browser.Windows);

        var close = Assert.Single(plan.Closes);
        Assert.Equal("duplicate of tab 1 in window 1", close.Reason);
    }

    [Fact]
    public void Gmail_unread_count_does_not_split_a_duplicate()
    {
        var browser = new TabBrowser();
        browser.Window(
            new TabSpec("Inbox (13) - someone@example.com - example.com Mail", Selected: true),
            new TabSpec("Inbox (2) - someone@example.com - example.com Mail"));

        Assert.Equal(["Inbox (2) - someone@example.com - example.com Mail"], ClosedTitles(Dedupe.Plan(browser.Windows)));
    }

    [Fact]
    public void Claude_suffix_variant_is_reported_while_exact_copies_close()
    {
        var browser = new TabBrowser();
        browser.Window(
            new TabSpec("Acme Review Queue", Selected: true),
            new TabSpec("Acme Review Queue"),
            new TabSpec("Acme Review Queue - Claude"),
            new TabSpec("Acme Review Queue - Claude"));

        var plan = Dedupe.Plan(browser.Windows);

        // One exact copy of each variant closes; the surviving suffixed tab (maybe a cowork session) is only reported.
        Assert.Equal(["Acme Review Queue", "Acme Review Queue - Claude"], ClosedTitles(plan));
        Assert.Equal(
            [(SiteReportKind.Variant, "Acme Review Queue - Claude", "possibly the same page as tab 1 in window 1")],
            plan.Reports.Select(report => (report.Kind, report.Item.Tab.Title, report.Detail)));
    }

    [Fact]
    public void Same_title_on_different_urls_is_only_reported()
    {
        var browser = new TabBrowser();
        browser.Window(
            new TabSpec("Rig / Triage", Selected: true, Url: "http://127.0.0.1:8094/triage/issues/resultful/resultful/5"),
            new TabSpec("Rig / Triage", Url: "http://127.0.0.1:8094/triage/inbox/rtkelly13/rig/397"));

        var plan = Dedupe.Plan(browser.Windows);

        Assert.Empty(plan.Closes);
        Assert.Equal(
            [(SiteReportKind.OtherPage, "same title as tab 1 in window 1, different URL")],
            plan.Reports.Select(report => (report.Kind, report.Detail)));
    }

    [Fact]
    public void Query_and_fragment_do_not_split_a_duplicate()
    {
        var browser = new TabBrowser();
        browser.Window(
            new TabSpec($"Pull requests {Sep} rtkelly13/rig", Selected: true, Url: "https://github.com/rtkelly13/rig/pulls?q=is%3Aopen"),
            new TabSpec($"Pull requests {Sep} rtkelly13/rig", Url: "https://github.com/rtkelly13/rig/pulls/#top"));

        Assert.Equal([$"Pull requests {Sep} rtkelly13/rig"], ClosedTitles(Dedupe.Plan(browser.Windows)));
    }

    [Theory]
    [InlineData("https://example.com/a", "https://example.com/a/", true)]
    [InlineData("https://example.com/a", "http://example.com/a", false)]
    [InlineData("https://example.com/a", "https://example.org/a", false)]
    [InlineData("https://example.com:8443/a", "https://example.com/a", false)]
    [InlineData("https://me@example.com/a", "https://example.com/a", false)]
    [InlineData("a/b?x=1", "a/b/#top", true)]
    [InlineData("a/b", "a/c", false)]
    public void Same_page_compares_scheme_host_and_path(string first, string second, bool same) =>
        Assert.Equal(same, Dedupe.SamePage(new Uri(first, UriKind.RelativeOrAbsolute), new Uri(second, UriKind.RelativeOrAbsolute)));

    [Fact]
    public void One_unknown_url_leaves_the_title_to_decide()
    {
        var browser = new TabBrowser();
        browser.Window(
            new TabSpec("Merge Queue Handbook", Selected: true, Url: "https://example.com/a"),
            new TabSpec("Merge Queue Handbook"));

        Assert.Equal(["Merge Queue Handbook"], ClosedTitles(Dedupe.Plan(browser.Windows)));
        Assert.True(Dedupe.SamePage(null, new Uri("https://example.com/a")));
    }

    [Fact]
    public void Pinned_audible_and_never_titles_are_not_closed()
    {
        var browser = new TabBrowser();
        browser.Window(
            new TabSpec("Merge Queue Handbook"),
            new TabSpec("Merge Queue Handbook", Pinned: true),
            new TabSpec("Link Certainty Carve-out"),
            new TabSpec("Link Certainty Carve-out", Audible: true),
            new TabSpec("Claude Code"),
            new TabSpec("Claude Code"),
            new TabSpec("Google Meet"),
            new TabSpec("Google Meet"));

        var plan = Dedupe.Plan(browser.Windows);

        Assert.Empty(plan.Closes);
        Assert.Equal(
            [
                "audible; would close as duplicate of tab 3 in window 1",
                "pinned; would close as duplicate of tab 1 in window 1",
            ],
            plan.Reports.Where(report => report.Kind == SiteReportKind.Protected).Select(report => report.Detail).Order());
        Assert.Equal(2, plan.Reports.Count);
    }

    [Fact]
    public void Report_only_rules_never_close_a_match()
    {
        var browser = new TabBrowser();
        browser.Window(new TabSpec("Checkly", Selected: true), new TabSpec("Checkly"));

        var plan = Dedupe.Plan(browser.Windows);

        Assert.Empty(plan.Closes);
        Assert.Equal(
            [(SiteReportKind.ReportOnly, "same title key as tab 1 in window 1")],
            plan.Reports.Select(report => (report.Kind, report.Detail)));
    }

    [Fact]
    public void Pr_tabs_are_left_to_the_pr_planner()
    {
        var browser = new TabBrowser();
        browser.Window(new TabSpec(PrTitle(778)), new TabSpec(PrTitle(778)));

        var plan = Dedupe.Plan(browser.Windows);

        Assert.Empty(plan.Closes);
        Assert.Empty(plan.Reports);
        Assert.Empty(plan.Kept);
    }

    [Fact]
    public void Both_plans_apply_in_one_pass_with_one_selection_restore()
    {
        var browser = new TabBrowser();
        var window = browser.Window(
            new TabSpec(PrTitle(744)),
            new TabSpec("Merge Queue Handbook"),
            new TabSpec("Merge Queue Handbook", Selected: true));
        var prPlan = PrPrune.Plan(browser.Windows, Resolution.Empty);
        var sitePlan = Dedupe.Plan(browser.Windows);
        var actions = new FakeChromeTabActions();

        var applied = ClosePlans.Apply([prPlan, sitePlan], browser.Windows, actions);

        Assert.Equal([window.Tabs[1].RuntimeId], actions.Closed.Select(tab => tab.RuntimeId));
        Assert.Equal(["title:Merge Queue Handbook"], applied.Select(result => result.Planned.Item.Label));
        Assert.Single(actions.Restored);
        Assert.Empty(sitePlan.Activations());
    }
}
