namespace Rig.Image;

/// <summary>
/// Renders an SVG to a straight-alpha RGBA raster on a square canvas, the way a browser lays out
/// <c>&lt;img style="width:Npx;height:Npx"&gt;</c>: the viewBox fits the square, centred, with a transparent background.
/// </summary>
public interface ISvgRenderer : IAsyncDisposable
{
    /// <summary>A short name for reports.</summary>
    string Name { get; }

    /// <summary>Renders <paramref name="svgPath"/> at <paramref name="size"/> by <paramref name="size"/> pixels.</summary>
    Task<Raster> RenderAsync(string svgPath, int size, CancellationToken cancellationToken);
}

/// <summary>A renderer could not start, such as Chromium not being installed.</summary>
public sealed class RendererUnavailableException : Exception
{
    /// <summary>Creates the exception.</summary>
    public RendererUnavailableException()
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    public RendererUnavailableException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and its cause.</summary>
    public RendererUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
