using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Rig.Core.ReviewQueue;
using static Microsoft.Playwright.Assertions;

namespace Rig.Web.BrowserTests;

/// <summary>URL navigation, the inbox's durable attention states and what its pages show of an item.</summary>
public sealed partial class InboxAttentionTests(BrowserFixture fixture) : InboxTest(fixture)
{
    [Fact]
    public async Task The_browser_talks_to_the_csharp_host()
    {
        // /api/status is answered only by the C# host (the recorded routes have no such route), on the port it bound.
        var status = await Page.APIRequest.GetAsync(Origin + "/api/status");

        Assert.True(status.Ok);
        Assert.Equal(new Uri(Origin).Port, (await status.JsonAsync())!.Value.GetProperty("port").GetInt32());
    }

    [Fact]
    public async Task Deep_links_filters_reload_and_history_are_one_document()
    {
        await Page.GotoAsync(Origin + "/triage/inbox/owner/repo/1?repo=owner%2Frepo");
        await Expect(Page.Heading("Fix the first issue")).ToBeVisibleAsync();
        await Page.EvaluateAsync("window.rigDocumentSentinel = 42");
        await Page.Button("Rig tools").ClickAsync();
        await Page.Modal("Rig tools").Link("UI references").ClickAsync();
        await Expect(Page).ToHaveURLAsync(Origin + "/triage/references");
        await Expect(Page.Heading("Triage interface catalogue", exact: false)).ToBeVisibleAsync();
        Assert.Equal(42, await Page.EvaluateAsync<int>("window.rigDocumentSentinel"));
        await Page.GoBackAsync();
        await Expect(Page.Heading("Fix the first issue")).ToBeVisibleAsync();
        await Page.Button("Queue filters").ClickAsync();
        await Expect(Page.Combobox("Repository")).ToHaveTextAsync("owner/repo");
        await Page.Keyboard.PressAsync("Escape");
        await Page.Textbox("Search queue", exact: false).FillAsync("first");
        await Expect(Page).ToHaveURLAsync(Origin + "/triage/inbox/owner/repo/1?repo=owner%2Frepo&q=first");
        await Page.ReloadAsync();
        await Expect(Page.Textbox("Search queue")).ToHaveValueAsync("first");
        await Expect(Page.Heading("Fix the first issue")).ToBeVisibleAsync();
        await Page.EvaluateAsync("window.rigDocumentSentinel = 43");
        await Page.Button("Rig tools").ClickAsync();
        await Page.Modal("Rig tools", exact: true).Link("Estate").ClickAsync();
        await Expect(Page.Heading("Estate Managed Repositories", exact: false)).ToBeVisibleAsync();
        Assert.Equal(43, await Page.EvaluateAsync<int>("window.rigDocumentSentinel"));
        await Page.GoBackAsync();
        await Expect(Page.Heading("Fix the first issue")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task Archive_read_snooze_and_head_change_restore_attention()
    {
        await Page.GotoAsync(Origin + "/triage/inbox/owner/repo/1");
        await Expect(Page.Button("Archive")).ToBeEnabledAsync();
        await Expect(Page.Button("Mark unread")).ToBeEnabledAsync();
        await Page.Button("Mark unread").ClickAsync();
        await Expect(Page.Button("Mark read")).ToBeEnabledAsync();
        await Page.Button("Mark read").ClickAsync();
        await Page.Button("Archive").ClickAsync();
        await Expect(Page.Button("Move to inbox")).ToBeVisibleAsync();
        Assert.True(Site.Queue.Read().First(entry => entry.Item.Number == 1).Archived);
        await Page.Link("Done").ClickAsync();
        await Expect(Page.Locator(".queue-row")).ToHaveCountAsync(1);
        await Page.Locator(".queue-row").ClickAsync();
        await Page.Button("Move to inbox").ClickAsync();
        await Expect(Page.Button("Archive")).ToBeVisibleAsync();
        await Page.Button("Snooze").First.ClickAsync();
        await Expect(Page.Button("Resume").First).ToBeVisibleAsync();
        await Page.Link("Snoozed").ClickAsync();
        await Expect(Page.Locator(".queue-row")).ToHaveCountAsync(1);
        await Page.Locator(".queue-row").ClickAsync();
        await Page.Button("Resume").First.ClickAsync();
        await Page.GetByRole(AriaRole.Button, new() { NameRegex = Reviewed() }).ClickAsync();
        await Expect(Page.Heading("Waiting for the next change")).ToBeVisibleAsync();
        await Page.Link("Waiting").ClickAsync();
        await Expect(Page.Locator(".queue-row")).ToHaveCountAsync(1);
        Save(Item(1) with { Head = new string('c', 40) });
        await Page.GotoAsync(Origin + "/triage/inbox/owner/repo/1");
        await Expect(Page.Heading("A new commit needs your review")).ToBeVisibleAsync();
        await Expect(Page.Locator(".queue-row")).ToHaveCountAsync(2);
    }

    [Fact]
    public async Task Drafts_survive_navigation_keyboard_and_mobile_layout()
    {
        await Page.GotoAsync(Origin + "/triage/inbox/owner/repo/1");
        var notes = Page.Textbox("Review notes", exact: true);
        await notes.FillAsync("Keep this draft while inspecting another PR");
        await notes.PressAsync("j");
        await Expect(Page).ToHaveURLAsync(Origin + "/triage/inbox/owner/repo/1");
        await notes.FillAsync("Draft retained");
        await Page.Locator("h1").ClickAsync();
        await Page.Keyboard.PressAsync("j");
        await Expect(Page.Heading("<script> must stay text")).ToBeVisibleAsync();
        await Page.Keyboard.PressAsync("k");
        await Expect(notes).ToHaveValueAsync("Draft retained");
        await Page.ReloadAsync();
        await Expect(notes).ToHaveValueAsync("Draft retained");
        await Page.ViewportAsync(390, 844);
        await Expect(Page.Heading("Fix the first issue")).ToBeVisibleAsync();
        Assert.True(await Page.FitsWidthAsync());
        Assert.True(await Page.Locator(".detail-pane").EvaluateAsync<bool>("element => element.scrollWidth <= element.clientWidth"));
        await Page.Button("Rig tools").ClickAsync();
        await Page.Modal("Rig tools").Link("UI references").ClickAsync();
        await Expect(Page.Heading("Triage interface catalogue", exact: false)).ToBeVisibleAsync();
        Assert.True(await Page.FitsWidthAsync());
    }

    [Fact]
    public async Task Unfetched_metadata_is_not_presented_as_passing_or_missing_ownership()
    {
        await Page.GotoAsync(Origin + "/triage/inbox/owner/repo/1");
        var signals = Page.GetByLabel("Action signals");
        await Expect(signals.Text("Signals not fetched")).ToBeVisibleAsync();
        await Expect(signals.Locator("[data-tone=\"success\"]")).ToHaveCountAsync(0);
        await Page.Link("Issues").ClickAsync();
        await Page.Locator(".queue-row").ClickAsync();
        await Expect(signals.Text("Signals not fetched")).ToBeVisibleAsync();
        await Expect(signals.Text("Assign owner")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task Description_and_feed_are_separate_and_links_resolve_to_github()
    {
        var first = Item(1);
        Save(first with { Body = "[Related issue](/owner/repo/issues/2)\n\n![Diagram](/owner/repo/raw/main/a.png)\n\n[Unsafe](javascript:alert(1))" });
        Site.Queue.Update(writer => writer.ImportActivity(first,
        [
            new QueueActivity("comment/1", "2026-01-02T00:00:00Z", "reviewer-one", "github:commented", first.Head, "[Context](#discussion_r123)"),
            new QueueActivity("review/2", "2026-01-03T00:00:00Z", "reviewer-two", "github:changes_requested", first.Head, "Please add validation."),
        ]));
        await Page.GotoAsync(Origin + "/triage/inbox/owner/repo/1");
        var description = Page.GetByRole(AriaRole.Region, new() { Name = "PR description", Exact = true });
        await Expect(description.Link("Related issue")).ToHaveAttributeAsync("href", "https://github.com/owner/repo/issues/2");
        await Expect(description.Link("Diagram")).ToHaveAttributeAsync("href", "https://github.com/owner/repo/raw/main/a.png");
        await Expect(description.Link("Unsafe")).ToHaveAttributeAsync("href", "");
        var feed = Page.GetByRole(AriaRole.Region, new() { Name = "PR feed 2", Exact = true });
        await Expect(feed.Locator(".event-header > strong")).ToHaveTextAsync(["reviewer-one", "reviewer-two"]);
        await Expect(feed.Link("Context")).ToHaveAttributeAsync("href", "https://github.com/owner/repo/pull/1#discussion_r123");
        await description.Button("Collapse").ClickAsync();
        await Expect(description.Link("Related issue")).Not.ToBeVisibleAsync();
        await Expect(feed).ToBeVisibleAsync();
    }

    [Fact]
    public async Task A_note_keeps_edits_typed_while_the_request_is_in_flight()
    {
        await Page.GotoAsync(Origin + "/triage/inbox/owner/repo/1");
        var field = Page.Textbox("Review notes", exact: true);
        await Expect(field).ToBeVisibleAsync();
        await Expect(Page.Button("Save note")).ToBeDisabledAsync();
        await field.FillAsync("Submitted note");
        await Page.RouteAsync("**/api/triage/interact", async route =>
        {
            var response = await route.FetchAsync();
            await field.FillAsync("Newer draft typed while saving");
            await route.FulfillAsync(new() { Response = response });
        });
        await Page.Button("Save note").ClickAsync();
        await Expect(Page.Text("Note saved.", exact: true)).ToBeVisibleAsync();
        await Expect(field).ToHaveValueAsync("Newer draft typed while saving");
        await Page.ReloadAsync();
        await Expect(field).ToHaveValueAsync("Newer draft typed while saving");
    }

    [GeneratedRegex("^reviewed$", RegexOptions.IgnoreCase)]
    private static partial Regex Reviewed();
}
