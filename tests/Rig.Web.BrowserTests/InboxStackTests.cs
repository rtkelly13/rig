using Microsoft.Playwright;
using Rig.Core.ReviewQueue;
using static Microsoft.Playwright.Assertions;

namespace Rig.Web.BrowserTests;

/// <summary>How the inbox groups stacked PRs, explains the next action and keeps its shell fixed while panels scroll.</summary>
public sealed class InboxStackTests(BrowserFixture fixture) : InboxTest(fixture)
{
    [Fact]
    public async Task Stack_groups_actions_and_issue_context()
    {
        var first = Item(1);
        var second = Item(2);
        var issue = Item(3);
        QueueStackMember[] members = [new(2, second.Title, "OPEN", 1), new(1, first.Title, "OPEN", 2)];
        Save(
            first with
            {
                Facts = new QueueFacts
                {
                    Author = "author",
                    Comments = 3,
                    ReviewRequested = true,
                    ReviewDecision = "CHANGES_REQUESTED",
                    UnresolvedThreads = 1,
                    ThreadsTotal = 1,
                    ChecksState = "FAILURE",
                    Checks = [new QueueCheck("lint", "FAILURE", "https://github.com/check/1")],
                    ChecksTotal = 1,
                    Stack = new StackInfo(20, 2, 2, "main") { Members = members },
                },
            },
            second with
            {
                Facts = new QueueFacts { ChecksState = "SUCCESS", ReviewDecision = "APPROVED", Stack = new StackInfo(20, 2, 1, "main") { Members = members } },
            },
            issue with
            {
                Facts = new QueueFacts { Labels = ["bug"], Milestone = "v1", Related = [new RelatedItem("owner/repo", 1, first.Title, "OPEN", "pr")] },
            });
        Site.Queue.Update(writer => writer.Interact(new QueueInteraction("owner/repo", 1, "reviewed", first.Head)));
        Site.Queue.Update(writer => writer.ImportActivity(first,
        [
            new QueueActivity("human/1", "2099-01-01T00:00:00Z", "reviewer", "github:commented", first.Head, "Please explain this change."),
        ]));
        await Page.GotoAsync(Origin + "/triage/inbox/owner/repo/1");
        var group = Page.Button(Patterns.Stack20());
        await Expect(group).ToHaveAttributeAsync("aria-expanded", "true");
        await Expect(Page.Locator(".queue-row strong").Nth(0)).ToHaveTextAsync(second.Title);
        await Expect(Page.Locator(".queue-row strong").Nth(1)).ToHaveTextAsync(first.Title);
        var actions = Page.GetByLabel("Action signals");
        await Expect(actions.Text("1 failed check")).ToBeVisibleAsync();
        await Expect(actions.Text("Address review changes")).ToBeVisibleAsync();
        await Expect(actions.Text("Read new replies")).ToBeVisibleAsync();
        await Expect(Page.Link("lint")).ToHaveAttributeAsync("href", "https://github.com/check/1");
        await Page.Locator(".stack-context summary").ClickAsync();
        await Expect(Page.Locator(".stack-context li")).ToHaveCountAsync(2);
        await Expect(Page.Locator(".stack-context")).ToContainTextAsync("Part 2 of 2");
        await group.ClickAsync();
        await Expect(Page.Locator(".queue-row")).ToHaveCountAsync(0);
        await Expect(Page.Heading(first.Title)).ToBeVisibleAsync();
        await Expect(Page.Button("Next item")).ToBeDisabledAsync();
        await group.ClickAsync();
        await Page.Button("Previous item").ClickAsync();
        await Expect(Page.Heading(second.Title)).ToBeVisibleAsync();
        await Expect(Page.GetByLabel("Action signals").Text("Checks passed")).ToBeVisibleAsync();
        await Page.Button("Queue filters").ClickAsync();
        await Page.Combobox("Needs action").ClickAsync();
        await Page.Option("Failed checks").ClickAsync();
        await Page.Combobox("Group by").ClickAsync();
        await Page.Option("Next action").ClickAsync();
        await Page.Button("Done").ClickAsync();
        await Expect(Page.Locator(".queue-row")).ToHaveCountAsync(1);
        await Expect(Page.Locator(".queue-group-toggle")).ToContainTextAsync("Failed checks");
        await Expect(Page).ToHaveURLAsync(Origin + "/triage/inbox/owner/repo/2?action=checks-failed&group=action");
        await Page.Link("Issues").ClickAsync();
        await Page.Locator(".queue-row").ClickAsync();
        await Expect(Page.GetByLabel("Action signals").Text("Assign owner")).ToBeVisibleAsync();
        await Expect(Page.GetByLabel("Action signals").Text("Set priority")).ToBeVisibleAsync();
        await Expect(Page.GetByLabel("Action signals").Text("Linked PR in progress")).ToBeVisibleAsync();
        await Page.Locator(".item-context summary").ClickAsync();
        await Expect(Page.Locator(".github-labels")).ToHaveTextAsync("bug");
        await Expect(Page.Locator(".linked-work")).ToContainTextAsync("owner/repo#1");
    }

    [Theory]
    [InlineData("chain", 1)]
    [InlineData("cycle", 0)]
    [InlineData("fork", 0)]
    public async Task Branch_chains_are_explicitly_inferred_and_ignore_cycles_and_forks(string relationship, int groupCount)
    {
        var first = Item(1);
        var second = Item(2);
        Save(
            first with { Facts = new QueueFacts { Head = "feature-top", Base = "feature-base", CrossRepository = relationship == "fork" } },
            second with { Facts = new QueueFacts { Head = "feature-base", Base = relationship == "cycle" ? "feature-top" : "main" } });
        await Page.GotoAsync(Origin + "/triage/inbox/owner/repo/1");
        await Expect(Page.Heading(first.Title)).ToBeVisibleAsync();
        await Expect(Page.Locator(".queue-group-toggle")).ToHaveCountAsync(groupCount);
        if (groupCount > 0)
        {
            await Expect(Page.Locator(".queue-group-description")).ToContainTextAsync("inferred from base branches");
            await Expect(Page.Locator(".queue-row strong").Nth(0)).ToHaveTextAsync(second.Title);
            await Page.Locator(".stack-context summary").ClickAsync();
            await Expect(Page.Locator(".stack-context")).ToContainTextAsync("not a registered GitHub stack");
        }

        await Page.Button("Queue filters").ClickAsync();
        await Page.Combobox("Group by").ClickAsync();
        await Page.Option("Repository").ClickAsync();
        await Page.Button("Done").ClickAsync();
        await Expect(Page.Locator(".queue-group-toggle")).ToHaveCountAsync(1);
        await Expect(Page.Locator(".queue-group-toggle")).ToContainTextAsync("owner/repo");
    }

    [Fact]
    public async Task The_inbox_shell_stays_fixed_while_only_panels_scroll()
    {
        var first = Item(1);
        Save(
            [
                first with { Body = string.Join("\n\n", Enumerable.Range(0, 80).Select(number => $"Paragraph {number}. Context for the review.")) },
                .. Enumerable.Range(4, 31).Select(number => first with { Number = number, Title = $"Queue item {number}" }),
            ]);
        await Page.ViewportAsync(1280, 720);
        await Page.GotoAsync(Origin + "/triage/inbox/owner/repo/1");
        await Expect(Page.Heading("Fix the first issue")).ToBeVisibleAsync();
        var toolbar = await Page.Locator(".workspace-toolbar").BoxAsync();
        var pane = Page.Locator(".detail-pane");
        await pane.HoverAsync();
        await Page.Mouse.WheelAsync(0, 600);
        await Expect(pane).ToHaveJSPropertyAsync("scrollTop", 600);
        await Page.Locator(".queue-list").HoverAsync();
        await Page.Mouse.WheelAsync(0, 600);
        await Expect(Page.Locator(".queue-list")).ToHaveJSPropertyAsync("scrollTop", 600);
        await Page.Locator(".workspace-toolbar").HoverAsync();
        await Page.Mouse.WheelAsync(0, 2000);
        await AssertSameBoxAsync(toolbar, Page.Locator(".workspace-toolbar"));
        foreach (var selector in new[] { ".app-content", ".triage-page" })
        {
            await Page.Locator(selector).EvaluateAsync("element => element.scrollTop = 200");
            await Expect(Page.Locator(selector)).ToHaveJSPropertyAsync("scrollTop", 0);
        }

        await Page.Button("Focus view").ClickAsync();
        await Expect(pane).ToHaveJSPropertyAsync("scrollTop", 600);
        await AssertSameBoxAsync(toolbar, Page.Locator(".workspace-toolbar"));
    }

    [Fact]
    public async Task Scoped_readiness_and_own_pr_actions()
    {
        var first = Item(1);
        Save(
            first with { Readiness = 0, Facts = new QueueFacts { ViewerAuthored = true, ReviewDecision = "APPROVED" } },
            first with { Repo = "owner/other", Number = 4, Readiness = 2 });
        await Page.GotoAsync(Origin + "/repos/owner/repo/triage/inbox/1");
        var signals = Page.GetByLabel("Action signals");
        await Expect(signals.Text("Approved")).ToBeVisibleAsync();
        await Expect(signals.Text("Review this PR")).ToHaveCountAsync(0);
        await Page.Button("Queue filters").ClickAsync();
        await Page.Combobox("Readiness").ClickAsync();
        await Expect(Page.Option("Blocked (0)")).ToBeVisibleAsync();
    }

    private static async Task AssertSameBoxAsync(LocatorBoundingBoxResult expected, ILocator locator)
    {
        var actual = await locator.BoxAsync();
        Assert.Equal((expected.X, expected.Y, expected.Width, expected.Height), (actual.X, actual.Y, actual.Width, actual.Height));
    }
}
