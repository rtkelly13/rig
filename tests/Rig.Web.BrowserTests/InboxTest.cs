using System.Text.Json.Nodes;
using Microsoft.Playwright;
using Rig.Core.ReviewQueue;

namespace Rig.Web.BrowserTests;

/// <summary>
/// A browser test over the triage inbox seeded with three items: two PRs in owner/repo and an issue in owner/other.
/// </summary>
public abstract class InboxTest(BrowserFixture fixture) : BrowserTest(fixture)
{
    internal override IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? Estate => Estates.SharedLibrary;

    /// <inheritdoc />
    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        Save(
            new QueueItem("owner/repo", 1, "pr", "Fix the first issue", "OPEN", "2026-01-01T00:00:00Z", "2026-01-02T00:00:00Z", new string('a', 40), 1, 0, 6,
                Body: "## Why this change\n\nRestore the first workflow.\n\n- Keep the context\n- Preserve drafts\n\n<script>window.injected = true</script>"),
            new QueueItem("owner/repo", 2, "pr", "<script> must stay text", "OPEN", "2026-01-02T00:00:00Z", "2026-01-02T00:00:00Z", new string('b', 40), 2, 1, 40),
            new QueueItem("owner/other", 3, "issue", "Triage the bug", "OPEN", "2026-01-03T00:00:00Z", "2026-01-03T00:00:00Z"));
        await CannedGitHubAsync();
    }

    /// <summary>The stored item numbered <paramref name="number"/>.</summary>
    protected QueueItem Item(int number) => Site.Queue.Read().First(entry => entry.Item.Number == number).Item;

    /// <summary>Observes <paramref name="items"/>, as a refresh would.</summary>
    protected void Save(params QueueItem[] items) => Site.Queue.Update(writer => writer.Save(items));

    /// <summary>
    /// Keeps the inbox's GitHub reads local. Watching an item observes nothing new, so it answers with the queue as it
    /// stands; a review is recorded in the shared database, which the next read shows, instead of asking GitHub for a
    /// live head.
    /// </summary>
    private async Task CannedGitHubAsync()
    {
        await Page.RouteAsync("**/api/triage/watch", async route => await route.FulfillAsync(new() { Body = await Site.TriageAsync(Token), ContentType = "application/json" }));
        await Page.RouteAsync("**/api/triage/interact", async route =>
        {
            var body = JsonNode.Parse(route.Request.PostData ?? "{}")!;
            var action = (string?)body["action"];
            if (action is "reviewed" or "changes_requested")
            {
                var request = new QueueInteraction((string)body["repo"]!, (int)body["number"]!, action, (string)body["head"]!, (string?)body["note"] ?? "");
                Site.Queue.Update(writer => writer.Interact(request));
                await route.FulfillAsync(new() { Body = await Site.TriageAsync(Token), ContentType = "application/json" });
                return;
            }

            await route.ContinueAsync();
        });
    }
}
