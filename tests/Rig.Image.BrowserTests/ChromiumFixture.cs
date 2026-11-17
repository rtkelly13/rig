namespace Rig.Image.BrowserTests;

/// <summary>One Chromium for the whole class, started once.</summary>
public sealed class ChromiumFixture : IAsyncLifetime
{
    private string _scratch = string.Empty;

    /// <summary>The running renderer.</summary>
    public ChromiumRenderer Renderer { get; private set; } = null!;

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        _scratch = Path.Combine(Path.GetTempPath(), "rig-tests", Guid.NewGuid().ToString("N"));
        Renderer = await ChromiumRenderer.LaunchAsync(null, _scratch);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await Renderer.DisposeAsync();
        if (Directory.Exists(_scratch))
        {
            Directory.Delete(_scratch, recursive: true);
        }
    }
}
