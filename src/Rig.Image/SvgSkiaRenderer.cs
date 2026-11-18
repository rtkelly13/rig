using SkiaSharp;
using Svg.Skia;

namespace Rig.Image;

/// <summary>The fast renderer: Svg.Skia drawing onto a SkiaSharp canvas, no browser. It never decides a promotion or writes a production cut.</summary>
public sealed class SvgSkiaRenderer : ISvgRenderer
{
    /// <inheritdoc />
    public string Name => "Svg.Skia";

    /// <inheritdoc />
    public Task<Raster> RenderAsync(string svgPath, int size, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var svg = new SKSvg();
        var picture = svg.Load(svgPath) ?? throw new InvalidDataException($"{svgPath}: expected an SVG Svg.Skia can load, found one it cannot");
        using var bitmap = new SKBitmap(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            var bounds = picture.CullRect;

            // An <img> with a square box scales the viewBox to fit, preserving aspect, centred.
            var scale = Math.Min(size / bounds.Width, size / bounds.Height);
            canvas.Translate((size - (bounds.Width * scale)) / 2f, (size - (bounds.Height * scale)) / 2f);
            canvas.Scale(scale);
            canvas.Translate(-bounds.Left, -bounds.Top);
            canvas.DrawPicture(picture);
        }

        // SKBitmap.Pixels hands back unpremultiplied colours whatever the bitmap's alpha type.
        var pixels = bitmap.Pixels;
        var rgba = new byte[pixels.Length * 4];
        for (var i = 0; i < pixels.Length; i++)
        {
            rgba[i * 4] = pixels[i].Red;
            rgba[(i * 4) + 1] = pixels[i].Green;
            rgba[(i * 4) + 2] = pixels[i].Blue;
            rgba[(i * 4) + 3] = pixels[i].Alpha;
        }

        return Task.FromResult(new Raster(size, size, rgba));
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
