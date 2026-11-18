using Microsoft.Playwright;

namespace Rig.Image;

/// <summary>
/// The authoritative renderer: headless Chromium through Microsoft.Playwright, in image_drift's exact HTML wrapper. The
/// Microsoft.Playwright package version pins the Chromium revision, so a developer and CI install the same browser.
/// </summary>
public sealed class ChromiumRenderer : ISvgRenderer
{
    /// <summary>The Chromium build the pinned Playwright package drives (revision 1248); fixtures and reports carry it as provenance.</summary>
    public const string PinnedBrowserVersion = "156.0.8078.4";

    private readonly IPlaywright _playwright;
    private readonly IBrowser _browser;
    private readonly string _scratch;

    private ChromiumRenderer(IPlaywright playwright, IBrowser browser, string scratch)
    {
        _playwright = playwright;
        _browser = browser;
        _scratch = scratch;
    }

    /// <inheritdoc />
    public string Name => "Chromium";

    /// <summary>The browser's version, for provenance.</summary>
    public string BrowserVersion => _browser.Version;

    /// <summary>Starts the browser. <paramref name="executablePath"/> overrides the one Playwright installed; <paramref name="scratch"/> holds the HTML wrappers.</summary>
    public static async Task<ChromiumRenderer> LaunchAsync(string? executablePath, string scratch)
    {
        ArgumentException.ThrowIfNullOrEmpty(scratch);
        Directory.CreateDirectory(scratch);
        var playwright = await Playwright.CreateAsync().ConfigureAwait(false);
        try
        {
            var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { ExecutablePath = executablePath }).ConfigureAwait(false);
            return new ChromiumRenderer(playwright, browser, scratch);
        }
        catch (PlaywrightException exception)
        {
            playwright.Dispose();
            throw new RendererUnavailableException($"expected Chromium to start, it did not: {exception.Message}", exception);
        }
        catch
        {
            playwright.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<Raster> RenderAsync(string svgPath, int size, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var html = Path.Join(_scratch, $"_{Guid.NewGuid():N}.html");
        var svg = new Uri(Path.GetFullPath(svgPath)).AbsoluteUri;
        await File.WriteAllTextAsync(
            html,
            $$"""

            <!DOCTYPE html>
            <html>
            <body style="margin:0; padding:0; background:transparent; display:flex;
                         justify-content:center; align-items:center;">
                <img src="{{svg}}" style="width:{{size}}px; height:{{size}}px;" />
            </body>
            </html>

            """,
            cancellationToken).ConfigureAwait(false);
        try
        {
            var page = await _browser.NewPageAsync(new BrowserNewPageOptions { ViewportSize = new ViewportSize { Width = size, Height = size } }).ConfigureAwait(false);
            try
            {
                await page.GotoAsync(new Uri(html).AbsoluteUri).ConfigureAwait(false);
                var png = await page.ScreenshotAsync(new PageScreenshotOptions { OmitBackground = true }).ConfigureAwait(false);
                return RasterPipeline.Decode(png);
            }
            finally
            {
                await page.CloseAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            File.Delete(html);
        }
    }

    /// <summary>Screenshots a local HTML page, full page, as a PNG at <paramref name="outputPng"/> (the gallery's <c>--screenshot</c>).</summary>
    public async Task ScreenshotPageAsync(string htmlPath, string outputPng, int width, int height, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPng))!);
        var page = await _browser.NewPageAsync(new BrowserNewPageOptions { ViewportSize = new ViewportSize { Width = width, Height = height } }).ConfigureAwait(false);
        try
        {
            await page.GotoAsync(new Uri(Path.GetFullPath(htmlPath)).AbsoluteUri, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle }).ConfigureAwait(false);
            await page.ScreenshotAsync(new PageScreenshotOptions { Path = outputPng, FullPage = true }).ConfigureAwait(false);
        }
        finally
        {
            await page.CloseAsync().ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _browser.DisposeAsync().ConfigureAwait(false);
        _playwright.Dispose();
    }
}
