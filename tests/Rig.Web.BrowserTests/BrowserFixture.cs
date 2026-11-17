using Microsoft.Playwright;

[assembly: Xunit.AssemblyFixture(typeof(Rig.Web.BrowserTests.BrowserFixture))]

namespace Rig.Web.BrowserTests;

/// <summary>One headless Chromium for the whole run; every test opens its own context in it.</summary>
public sealed class BrowserFixture : IAsyncLifetime
{
    // Each test runs a host and a page; two at a time keeps a small runner inside Playwright's expectation timeouts.
    private readonly SemaphoreSlim _turns = new(2);
    private IPlaywright? _playwright;

    /// <summary>The shared browser.</summary>
    public IBrowser Browser { get; private set; } = null!;

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        _playwright = await Playwright.CreateAsync();
        Browser = await _playwright.Chromium.LaunchAsync();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Browser is not null)
        {
            await Browser.CloseAsync();
        }

        _playwright?.Dispose();
        _turns.Dispose();
    }

    /// <summary>Waits for one of the run's two places to drive a page.</summary>
    public Task TakeTurnAsync(CancellationToken cancellationToken) => _turns.WaitAsync(cancellationToken);

    /// <summary>Gives the place back.</summary>
    public void EndTurn() => _turns.Release();
}
