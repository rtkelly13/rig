using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Rig.Web.BrowserTests;

/// <summary>Repository scope shared between the estate and the inbox, and the estate's operations tools.</summary>
public sealed class InboxEstateTests(BrowserFixture fixture) : InboxTest(fixture)
{
    [Fact]
    public async Task Repository_scope_and_estate_graph_connectivity()
    {
        await Page.GotoAsync(Origin + "/estate?repo=owner%2Frepo");
        await Expect(Page.Heading("Estate Managed Repositories")).ToBeVisibleAsync();
        await Expect(Page.Button("Inspect owner/repo")).ToBeVisibleAsync();
        var details = Page.GetByRole(AriaRole.Complementary, new() { Name = "Repository details" });
        await Expect(details).ToContainTextAsync("Used by 1 repository");
        await Expect(details).ToContainTextAsync("managed-library");
        await Page.Button("Inspect owner/other").ClickAsync();
        await Expect(Page).ToHaveURLAsync(Origin + "/estate?repo=owner%2Fother");
        await Expect(details).ToContainTextAsync("Consumes from 1 repository");
        await Expect(details).ToContainTextAsync("^1.0");
        await Expect(details).ToContainTextAsync("package.json");
        await Page.EvaluateAsync("window.rigEstateDocument = 42");
        await details.Link("1 issues to triage").ClickAsync();
        await Expect(Page).ToHaveURLAsync(Origin + "/repos/owner/other/triage/issues");
        Assert.Equal(42, await Page.EvaluateAsync<int>("window.rigEstateDocument"));
        await Expect(Page.Combobox("Queue scope")).ToHaveTextAsync("owner/other");
        await Expect(Page.Locator(".queue-row")).ToHaveCountAsync(1);
        await Page.Locator(".queue-row").ClickAsync();
        await Expect(Page).ToHaveURLAsync(Origin + "/repos/owner/other/triage/issues/3");
        await Page.ReloadAsync();
        await Expect(Page.Heading("Triage the bug")).ToBeVisibleAsync();
        await Page.Link("Inbox").ClickAsync();
        await Expect(Page).ToHaveURLAsync(Origin + "/repos/owner/other/triage/inbox");
        await Expect(Page.Locator(".queue-row")).ToHaveCountAsync(0);
        await Page.Combobox("Queue scope").ClickAsync();
        await Page.Option("All repositories").ClickAsync();
        await Expect(Page).ToHaveURLAsync(Origin + "/triage/inbox");
        await Expect(Page.Locator(".queue-row")).ToHaveCountAsync(2);
        await Page.GotoAsync(Origin + "/repos/owner/repo/triage/inbox/1?layout=focus");
        await Expect(Page.Heading("Fix the first issue")).ToBeVisibleAsync();
        await Expect(Page.Locator(".queue-column")).Not.ToBeVisibleAsync();
        await Page.Button("Next item").ClickAsync();
        await Expect(Page).ToHaveURLAsync(Origin + "/repos/owner/repo/triage/inbox/2?layout=focus");
        await Page.GotoAsync(Origin + "/estate?repo=owner%2Fother");
        await Page.ViewportAsync(390, 844);
        await Expect(Page.Heading("Estate Managed Repositories")).ToBeVisibleAsync();
        Assert.True(await Page.FitsWidthAsync());
        Assert.Equal("clip", await Page.Locator(".app-content").EvaluateAsync<string>("element => getComputedStyle(element).overflowY"));
    }

    [Fact]
    public async Task Estate_keeps_the_existing_operations_tools_in_the_spa()
    {
        await Page.GotoAsync(Origin + "/estate");
        await Expect(Page.Heading("Estate Managed Repositories")).ToBeVisibleAsync();
        // The sidebar carries the current tool's pages; other tools, C4 among them, live in the launcher.
        var sidebar = Page.GetByRole(AriaRole.Navigation, new() { Name = "Rig navigation" });
        await Expect(sidebar.Link("Inbox")).ToHaveCountAsync(0);
        await Expect(sidebar.Link("C4 architecture")).ToHaveCountAsync(0);
        await Page.Button("Rig tools").ClickAsync();
        var tools = Page.Modal("Rig tools");
        await Expect(tools.Link("C4 architecture")).ToHaveAttributeAsync("href", "/c4/");
        await Page.Keyboard.PressAsync("Escape");
        await Expect(tools).Not.ToBeVisibleAsync();
        await Page.EvaluateAsync("window.rigOperationsDocument = 42");
        await sidebar.Link("Operations").ClickAsync();
        await Expect(Page).ToHaveURLAsync(Origin + "/estate/operations");
        await Expect(Page.Link(Patterns.Jobs())).ToHaveAttributeAsync("href", "/ops/jobs");
        await Expect(Page.Button(Patterns.BackupDb())).ToBeVisibleAsync();
        Assert.Equal(42, await Page.EvaluateAsync<int>("window.rigOperationsDocument"));
        await Page.ViewportAsync(390, 844);
        var bounds = await Page.Locator("#view-estate").BoxAsync();
        Assert.True(bounds.Y + bounds.Height <= 844 - 53);
        await Page.GoBackAsync();
        await Expect(Page.Heading("Estate Managed Repositories")).ToBeVisibleAsync();
        Assert.Equal(42, await Page.EvaluateAsync<int>("window.rigOperationsDocument"));
    }
}
