using Microsoft.Playwright;

namespace Rig.Web.BrowserTests;

/// <summary>
/// A test that drives the SPA: a fresh host and a fresh page for each, in the run's one browser, at the desktop size
/// the layout tests assume.
/// </summary>
public abstract class BrowserTest(BrowserFixture fixture) : IAsyncLifetime
{
    private IBrowserContext? _context;
    private bool _hasTurn;

    /// <summary>The estate the host's graph is read from, or null for none.</summary>
    internal virtual IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? Estate => null;

    /// <summary>The host this test runs against.</summary>
    protected RigSite Site { get; private set; } = null!;

    /// <summary>The page the test drives.</summary>
    protected IPage Page { get; private set; } = null!;

    /// <summary>The host's address.</summary>
    protected string Origin => Site.Origin;

    /// <summary>Cancelled when the run is.</summary>
    protected static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <inheritdoc />
    public virtual async ValueTask InitializeAsync()
    {
        await fixture.TakeTurnAsync(Token);
        _hasTurn = true;
        Site = await RigSite.StartAsync(Estate, Token);
        _context = await fixture.Browser.NewContextAsync(new() { ViewportSize = new() { Width = 1440, Height = 1000 } });
        Page = await _context.NewPageAsync();
    }

    /// <inheritdoc />
    public virtual async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        if (_context is not null)
        {
            await _context.CloseAsync();
        }

        if (Site is not null)
        {
            await Site.DisposeAsync();
        }

        if (_hasTurn)
        {
            fixture.EndTurn();
        }
    }
}
