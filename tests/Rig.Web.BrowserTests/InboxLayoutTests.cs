using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Rig.Web.BrowserTests;

/// <summary>The inbox workspace's layout: resizable panes, the collapsible rail, focus view and the queue's filters.</summary>
public sealed class InboxLayoutTests(BrowserFixture fixture) : InboxTest(fixture)
{
    private const string Popup = "[data-slot=\"select-popup\"]";

    [Fact]
    public async Task Workspace_layout_controls_and_external_links()
    {
        await Page.GotoAsync(Origin + "/triage/inbox/owner/repo/1");
        await Expect(Page.Heading("Fix the first issue")).ToBeVisibleAsync();
        var title = Page.Locator("#selected-title a");
        await Expect(title).ToHaveAttributeAsync("href", "https://github.com/owner/repo/pull/1");
        await Expect(title).ToHaveAttributeAsync("target", "_blank");
        await Expect(Page.Link("Open PR on GitHub")).ToHaveAttributeAsync("href", "https://github.com/owner/repo/pull/1");
        await Page.Button("Expand sidebar").ClickAsync();
        var initial = await Page.Locator(".detail-pane").BoxAsync();
        await Page.Button("Collapse sidebar").ClickAsync();
        await Expect(Page.Button("Expand sidebar")).ToHaveAttributeAsync("aria-expanded", "false");
        var expanded = await Page.Locator(".detail-pane").BoxAsync();
        Assert.True(expanded.Width > initial.Width);
        var separator = Page.GetByRole(AriaRole.Separator, new() { Name = "Resize item list" });
        await separator.FocusAsync();
        await Page.Keyboard.PressAsync("Home");
        await Expect(separator).ToHaveAttributeAsync("aria-valuenow", "260");
        var wider = await Page.Locator(".detail-pane").BoxAsync();
        Assert.True(wider.Width > expanded.Width);
        var handle = await separator.BoxAsync();
        await Page.Mouse.MoveAsync(handle.X + (handle.Width / 2), handle.Y + 30);
        await Page.Mouse.DownAsync();
        await Page.Mouse.MoveAsync(handle.X + 100, handle.Y + 30);
        await Page.Mouse.UpAsync();
        await Expect(separator).Not.ToHaveAttributeAsync("aria-valuenow", "260");
        var widthAfterDrag = await separator.GetAttributeAsync("aria-valuenow");
        Assert.True(int.Parse(widthAfterDrag!, System.Globalization.CultureInfo.InvariantCulture) > 260);
        await Page.ReloadAsync();
        await Expect(separator).ToHaveAttributeAsync("aria-valuenow", widthAfterDrag!);
        await Expect(Page.Button("Expand sidebar")).ToBeVisibleAsync();
        await Page.Button("Rig tools").ClickAsync();
        await Expect(Page.Modal("Rig tools", exact: true)).ToBeVisibleAsync();
        await Page.Keyboard.PressAsync("j");
        await Expect(Page).ToHaveURLAsync(Origin + "/triage/inbox/owner/repo/1");
        await Page.Keyboard.PressAsync("Escape");
        await Expect(Page.Modal("Rig tools", exact: true)).Not.ToBeVisibleAsync();
        await Expect(Page.Button("Rig tools")).ToBeFocusedAsync();
        foreach (var theme in Dsl.Themes)
        {
            await Page.UseThemeAsync(theme);
            await Page.EvaluateAsync("""
                () => {
                    const probe = document.createElement('div');
                    probe.id = 'surface-probe';
                    probe.style.background = 'var(--ds-surface-raised)';
                    document.body.append(probe);
                }
                """);
            Assert.Equal(
                await Page.Locator("#surface-probe").EvaluateAsync<string>("element => getComputedStyle(element).backgroundColor"),
                await Page.Locator(".detail-pane").EvaluateAsync<string>("element => getComputedStyle(element).backgroundColor"));
            await Page.Locator("#surface-probe").EvaluateAsync("element => element.remove()");
        }
    }

    [Fact]
    public async Task Collapsed_sidebar_labels_support_hover_focus_and_responsive_rail()
    {
        await Page.GotoAsync(Origin + "/triage/inbox/owner/repo/1");
        var inbox = Page.GetByRole(AriaRole.Navigation, new() { Name = "Rig navigation" }).Link("Inbox");
        var tooltip = Page.Locator("[data-slot=\"tooltip\"]").Filter(new() { HasTextRegex = Patterns.InboxOnly() });
        await Page.Button("Expand sidebar").ClickAsync();
        await Page.Button("Collapse sidebar").ClickAsync();
        await inbox.HoverAsync();
        await Expect(tooltip).ToBeVisibleAsync();
        await Expect(tooltip).ToHaveTextAsync("Inbox");
        var bounds = await tooltip.BoxAsync();
        var sidebar = await Page.Locator(".app-sidebar").BoxAsync();
        Assert.True(bounds.X >= sidebar.Width);
        await Page.Mouse.MoveAsync(1200, 20);
        await Expect(tooltip).Not.ToBeVisibleAsync();
        await inbox.FocusAsync();
        await Page.Keyboard.PressAsync("Shift+Tab");
        await Page.Keyboard.PressAsync("Tab");
        await Expect(tooltip).ToBeVisibleAsync();
        await Page.Keyboard.PressAsync("Escape");
        await Expect(tooltip).Not.ToBeVisibleAsync();
        await Expect(inbox).ToBeFocusedAsync();
        await Page.Button("Expand sidebar").ClickAsync();
        await inbox.HoverAsync();
        await Expect(tooltip).ToHaveCountAsync(0);
        await Page.ViewportAsync(820, 900);
        await Expect(Page.Locator(".app-sidebar")).ToHaveAttributeAsync("data-compact", "true");
        await inbox.FocusAsync();
        await Page.Keyboard.PressAsync("Shift+Tab");
        await Page.Keyboard.PressAsync("Tab");
        await Expect(tooltip).ToBeVisibleAsync();
        await Expect(tooltip).ToHaveTextAsync("Inbox");
        await inbox.ClickAsync();
        await Expect(Page).ToHaveURLAsync(Origin + "/triage/inbox");
    }

    [Fact]
    public async Task Description_renders_inline_html_like_dependabot_release_notes()
    {
        await Page.GotoAsync(Origin + "/triage/inbox/owner/repo/1?repo=owner%2Frepo");
        var description = Page.Locator("#description-content");
        await Expect(description.Locator("details > summary")).ToHaveTextAsync("Release notes");
        await Expect(description.GetByText("<details>", new() { Exact = false })).ToHaveCountAsync(0);
        Assert.True(await Page.EvaluateAsync<bool>("window.injected === undefined"));
    }

    [Fact]
    public async Task Review_workspace_ratios_focus_content_and_mobile_navigation()
    {
        await Page.GotoAsync(Origin + "/triage/inbox/owner/repo/1?repo=owner%2Frepo");
        await Expect(Page.Heading("Why this change", exact: false)).ToBeVisibleAsync();
        Assert.True(await Page.EvaluateAsync<bool>("window.injected === undefined"));
        await Expect(Page.Heading("PR feed 0")).ToBeVisibleAsync();
        foreach (var (width, target) in new[] { (1280, 908), (1440, 1048), (1920, 1488) })
        {
            await Page.ViewportAsync(width, 1000);
            var pane = await Page.Locator(".detail-pane").BoxAsync();
            Assert.True(Math.Abs(pane.Width - target) < 2);
            Assert.True(pane.Y <= 112);
        }

        await Page.ViewportAsync(1440, 1000);
        var notes = Page.Textbox("Review notes");
        await notes.FillAsync("Keep this while focusing");
        await Page.Locator(".detail-pane").EvaluateAsync("element => element.scrollTop = 100");
        await Page.Button("Focus view").ClickAsync();
        await Expect(Page).ToHaveURLAsync(Origin + "/triage/inbox/owner/repo/1?repo=owner%2Frepo&layout=focus");
        await Expect(Page.Locator(".queue-column")).Not.ToBeVisibleAsync();
        Assert.Equal(100, await Page.Locator(".detail-pane").EvaluateAsync<int>("element => element.scrollTop"));
        await Expect(notes).ToHaveValueAsync("Keep this while focusing");
        await Page.ReloadAsync();
        await Expect(Page.Locator(".queue-column")).Not.ToBeVisibleAsync();
        await Expect(notes).ToHaveValueAsync("Keep this while focusing");
        await Page.Button("Next item").ClickAsync();
        await Expect(Page.Heading("<script> must stay text")).ToBeVisibleAsync();
        await Page.Button("Previous item").ClickAsync();
        await Expect(notes).ToHaveValueAsync("Keep this while focusing");
        await Page.Button("Split view").ClickAsync();
        await Expect(Page.Locator(".queue-column")).ToBeVisibleAsync();
        await Page.ViewportAsync(390, 844);
        await Expect(Page.Locator(".queue-column")).Not.ToBeVisibleAsync();
        await Expect(Page.Heading("Fix the first issue")).ToBeVisibleAsync();
        Assert.True((await Page.Locator(".detail-pane").BoxAsync()).Y <= 112);
        await Page.Link("Back to inbox").ClickAsync();
        await Expect(Page.Locator(".queue-column")).ToBeVisibleAsync();
        await Page.Button("Queue filters").ClickAsync();
        await Expect(Page.Combobox("Repository")).ToHaveTextAsync("owner/repo");
        await Page.Keyboard.PressAsync("Escape");
        Assert.True(await Page.FitsWidthAsync());
    }

    [Fact]
    public async Task Queue_filters_stay_compact_and_preserve_selection()
    {
        await Page.ViewportAsync(1280, 800);
        await Page.GotoAsync(Origin + "/triage/inbox/owner/repo/1");
        await Expect(Page.Heading("Fix the first issue")).ToBeVisibleAsync();
        var column = await Page.Locator(".queue-column").BoxAsync();
        var listing = await Page.Locator(".queue-list").BoxAsync();
        Assert.True(listing.Y - column.Y <= 88);
        Assert.True(listing.Height >= column.Height * 0.8);
        var trigger = Page.Button("Queue filters");
        await trigger.ClickAsync();
        var dialog = Page.Modal("Queue filters", exact: true);
        await Expect(dialog).ToBeVisibleAsync();
        await dialog.Combobox("Repository").ClickAsync();
        await Page.Option("owner/repo").ClickAsync();
        await dialog.Combobox("Readiness").ClickAsync();
        await Page.Option("Clean (1)").ClickAsync();
        await Expect(Page).ToHaveURLAsync(Origin + "/triage/inbox/owner/repo/1?repo=owner%2Frepo&status=0");
        await Page.Keyboard.PressAsync("j");
        await Expect(Page.Heading("Fix the first issue")).ToBeVisibleAsync();
        await dialog.Button("Done").ClickAsync();
        await Expect(dialog).Not.ToBeVisibleAsync();
        await Expect(trigger).ToBeFocusedAsync();
        await Expect(Page.Locator(".filter-summary")).ToHaveTextAsync("owner/repo, Clean");
        await Page.ReloadAsync();
        await Expect(Page.Locator(".filter-summary")).ToHaveTextAsync("owner/repo, Clean");
        await trigger.ClickAsync();
        await Expect(dialog.Combobox("Readiness")).ToHaveTextAsync("Clean (1)");
        await dialog.GetByRole(AriaRole.Checkbox, new() { Name = "Unread only", Exact = true }).CheckAsync();
        await Expect(Page).ToHaveURLAsync(Origin + "/triage/inbox/owner/repo/1?repo=owner%2Frepo&status=0&read=unread");
        await Page.Keyboard.PressAsync("Escape");
        await Expect(trigger).ToBeFocusedAsync();
        await Page.Button("Clear queue filters").ClickAsync();
        await Expect(Page).ToHaveURLAsync(Origin + "/triage/inbox/owner/repo/1");
        await Page.ViewportAsync(390, 844);
        await Page.Link("Back to inbox").ClickAsync();
        await trigger.ClickAsync();
        await Expect(dialog).ToBeVisibleAsync();
        var bounds = await dialog.BoxAsync();
        Assert.True(bounds.X >= 0 && bounds.X + bounds.Width <= 390);
        Assert.True(await Page.FitsWidthAsync());
    }

    [Fact]
    public async Task Filter_selects_keep_the_parent_open_and_fit_both_themes()
    {
        await Page.GotoAsync(Origin + "/triage/inbox/owner/repo/1");
        await Page.Button("Queue filters").ClickAsync();
        var dialog = Page.Modal("Queue filters", exact: true);
        var repository = dialog.Combobox("Repository");
        var listbox = Page.GetByRole(AriaRole.Listbox, new() { Name = "Repository", Exact = true });
        await Expect(repository).ToHaveTextAsync("All repositories");
        await Expect(dialog.Combobox("Readiness")).ToHaveTextAsync("Any readiness");
        await repository.ClickAsync();
        await Expect(listbox).ToBeVisibleAsync();
        await FocusInPopupAsync();
        await Page.Keyboard.PressAsync("j");
        await Expect(Page).ToHaveURLAsync(Origin + "/triage/inbox/owner/repo/1");
        await Page.Keyboard.PressAsync("Escape");
        await Expect(listbox).Not.ToBeVisibleAsync();
        await Expect(dialog).ToBeVisibleAsync();
        await Expect(repository).ToBeFocusedAsync();
        await repository.PressAsync("ArrowDown");
        await Expect(listbox).ToBeVisibleAsync();
        await Expect(Page.Option("All repositories")).ToBeFocusedAsync();
        await Expect(Page.Option("All repositories")).ToHaveAttributeAsync("data-highlighted", "");
        await PressUntilFocusedAsync("End", Page.Option("owner/repo"));
        await Expect(Page.Option("owner/repo")).ToHaveAttributeAsync("data-highlighted", "");
        await Page.Keyboard.PressAsync("Enter");
        await Expect(repository).ToHaveTextAsync("owner/repo");
        await Expect(dialog).ToBeVisibleAsync();
        await repository.ClickAsync();
        await Page.Option("All repositories").ClickAsync();
        await Expect(Page).ToHaveURLAsync(Origin + "/triage/inbox/owner/repo/1");
        foreach (var theme in Dsl.Themes)
        {
            await Page.UseThemeAsync(theme);
            await repository.ClickAsync();
            var popup = Page.Locator(Popup);
            await Expect(popup).ToBeVisibleAsync();
            var expected = await Page.EvaluateAsync<string>("""
                () => {
                    const probe = document.createElement('div');
                    probe.style.backgroundColor = 'var(--ds-surface-raised)';
                    document.body.append(probe);
                    const value = getComputedStyle(probe).backgroundColor;
                    probe.remove();
                    return value;
                }
                """);
            await Expect(popup).ToHaveCSSAsync("background-color", expected);
            await FocusInPopupAsync();
            await Page.Keyboard.PressAsync("Escape");
            await Expect(popup).Not.ToBeVisibleAsync();
        }

        await Page.Keyboard.PressAsync("Escape");
        await Page.ViewportAsync(390, 844);
        await Page.Link("Back to inbox").ClickAsync();
        await Page.Button("Queue filters").ClickAsync();
        await Page.Combobox("Repository").ClickAsync();
        await Expect(Page.Locator(Popup)).ToBeVisibleAsync();
        var bounds = await Page.Locator(Popup).BoxAsync();
        Assert.True(bounds.X >= 0 && bounds.X + bounds.Width <= 390);
        Assert.True(await Page.FitsWidthAsync());
    }

    /// <summary>
    /// Waits until the open select has taken keyboard focus. A key pressed before then goes to the page behind it,
    /// which is what made the focus assertions after it flaky.
    /// </summary>
    /// <summary>Presses a navigation key until the target is focused: the list moves on its highlighted item, which can lag the open.</summary>
    private async Task PressUntilFocusedAsync(string key, ILocator target)
    {
        for (var attempt = 1; ; attempt++)
        {
            await Page.Keyboard.PressAsync(key);
            try
            {
                await Expect(target).ToBeFocusedAsync(new() { Timeout = 1500 });
                return;
            }
            catch (PlaywrightException) when (attempt < 3)
            {
            }
        }
    }

    private async Task FocusInPopupAsync() => _ = await Page.WaitForFunctionAsync($"() => document.activeElement?.closest('{Popup}') != null");
}
