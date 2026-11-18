using Microsoft.Playwright;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Processing.Processors.Transforms;
using SkiaSharp;
using Svg.Skia;

namespace ImageStackEval;

/// <summary>Straight-alpha RGBA8, row-major.</summary>
public sealed record Raster(int Width, int Height, byte[] Rgba);

/// <summary>Decode, drop alpha and resize: what image_drift's load_normalised does with Pillow.</summary>
public interface IRasterLibrary
{
    string Name { get; }

    /// <summary>Decode an image file, convert to RGB without compositing, and resize to a square canvas.</summary>
    RgbImage LoadNormalised(byte[] fileBytes, int size);

    /// <summary>The same for pixels already in memory (a rendered SVG).</summary>
    RgbImage Normalise(Raster raster, int size);

    /// <summary>Decode a file losslessly to straight RGBA (no resizing), for pixel comparisons.</summary>
    Raster DecodeRgba(byte[] fileBytes);

    byte[] EncodePng(Raster raster);

    /// <summary>Resize straight-alpha RGBA to a square, as Pillow does for each ICO entry.</summary>
    Raster Resize(Raster raster, int size);
}

public interface ISvgRenderer : IAsyncDisposable
{
    string Name { get; }

    Task<Raster> RenderAsync(string svgPath, int size);
}

public sealed class ImageSharpLibrary(IResampler? resampler = null, string label = "ImageSharp") : IRasterLibrary
{
    private readonly IResampler _resampler = resampler ?? KnownResamplers.Bicubic;

    public string Name => label;

    public RgbImage LoadNormalised(byte[] fileBytes, int size)
    {
        using var image = Image.Load<Rgb24>(fileBytes);
        return Finish(image, size);
    }

    public RgbImage Normalise(Raster raster, int size)
    {
        using var rgba = Image.LoadPixelData<Rgba32>(raster.Rgba, raster.Width, raster.Height);
        using var rgb = rgba.CloneAs<Rgb24>();
        return Finish(rgb, size);
    }

    public Raster DecodeRgba(byte[] fileBytes)
    {
        using var image = Image.Load<Rgba32>(fileBytes);
        var bytes = new byte[image.Width * image.Height * 4];
        image.CopyPixelDataTo(bytes);
        return new Raster(image.Width, image.Height, bytes);
    }

    public Raster Resize(Raster raster, int size)
    {
        using var image = Image.LoadPixelData<Rgba32>(raster.Rgba, raster.Width, raster.Height);
        image.Mutate(c => c.Resize(size, size, _resampler));
        var bytes = new byte[size * size * 4];
        image.CopyPixelDataTo(bytes);
        return new Raster(size, size, bytes);
    }

    public byte[] EncodePng(Raster raster)
    {
        using var image = Image.LoadPixelData<Rgba32>(raster.Rgba, raster.Width, raster.Height);
        using var ms = new MemoryStream();
        image.SaveAsPng(ms);
        return ms.ToArray();
    }

    private RgbImage Finish(Image<Rgb24> image, int size)
    {
        if (image.Width != size || image.Height != size)
        {
            image.Mutate(c => c.Resize(size, size, _resampler));
        }

        var bytes = new byte[size * size * 3];
        image.CopyPixelDataTo(bytes);
        return new RgbImage(size, size, bytes.Select(b => (double)b).ToArray());
    }
}

public sealed class SkiaSharpLibrary(SKSamplingOptions sampling, string label) : IRasterLibrary
{
    public string Name => label;

    public RgbImage LoadNormalised(byte[] fileBytes, int size)
    {
        using var bitmap = SKBitmap.Decode(fileBytes) ?? throw new InvalidOperationException("SkiaSharp could not decode the image");
        return Finish(bitmap, size);
    }

    public RgbImage Normalise(Raster raster, int size)
    {
        using var bitmap = ToBitmap(raster);
        return Finish(bitmap, size);
    }

    public Raster DecodeRgba(byte[] fileBytes)
    {
        using var bitmap = SKBitmap.Decode(fileBytes) ?? throw new InvalidOperationException("SkiaSharp could not decode the image");
        return FromBitmap(bitmap);
    }

    public byte[] EncodePng(Raster raster)
    {
        using var bitmap = ToBitmap(raster);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    public Raster Resize(Raster raster, int size)
    {
        using var bitmap = ToBitmap(raster);
        using var resized = bitmap.Resize(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Unpremul), sampling)
            ?? throw new InvalidOperationException("SkiaSharp resize failed");
        return FromBitmap(resized);
    }

    public static Raster FromBitmap(SKBitmap bitmap)
    {
        // SKBitmap.Pixels returns unpremultiplied colours whatever the bitmap's alpha type.
        SKColor[] pixels = bitmap.Pixels;
        var bytes = new byte[pixels.Length * 4];
        for (int i = 0; i < pixels.Length; i++)
        {
            bytes[i * 4] = pixels[i].Red;
            bytes[(i * 4) + 1] = pixels[i].Green;
            bytes[(i * 4) + 2] = pixels[i].Blue;
            bytes[(i * 4) + 3] = pixels[i].Alpha;
        }

        return new Raster(bitmap.Width, bitmap.Height, bytes);
    }

    private static SKBitmap ToBitmap(Raster raster)
    {
        var bitmap = new SKBitmap(new SKImageInfo(raster.Width, raster.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        var pixels = new SKColor[raster.Width * raster.Height];
        for (int i = 0; i < pixels.Length; i++)
        {
            pixels[i] = new SKColor(raster.Rgba[i * 4], raster.Rgba[(i * 4) + 1], raster.Rgba[(i * 4) + 2], raster.Rgba[(i * 4) + 3]);
        }

        bitmap.Pixels = pixels;
        return bitmap;
    }

    private RgbImage Finish(SKBitmap decoded, int size)
    {
        // Drop alpha without compositing (PIL convert("RGB")), then resize an opaque bitmap.
        Raster raster = FromBitmap(decoded);
        using var opaque = new SKBitmap(new SKImageInfo(raster.Width, raster.Height, SKColorType.Rgba8888, SKAlphaType.Opaque));
        var pixels = new SKColor[raster.Width * raster.Height];
        for (int i = 0; i < pixels.Length; i++)
        {
            pixels[i] = new SKColor(raster.Rgba[i * 4], raster.Rgba[(i * 4) + 1], raster.Rgba[(i * 4) + 2], 255);
        }

        opaque.Pixels = pixels;
        SKBitmap target = opaque;
        SKBitmap? resized = null;
        if (opaque.Width != size || opaque.Height != size)
        {
            resized = opaque.Resize(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Opaque), sampling)
                ?? throw new InvalidOperationException("SkiaSharp resize failed");
            target = resized;
        }

        SKColor[] outPixels = target.Pixels;
        var data = new double[size * size * 3];
        for (int i = 0; i < outPixels.Length; i++)
        {
            data[i * 3] = outPixels[i].Red;
            data[(i * 3) + 1] = outPixels[i].Green;
            data[(i * 3) + 2] = outPixels[i].Blue;
        }

        resized?.Dispose();
        return new RgbImage(size, size, data);
    }
}

/// <summary>Headless Chromium through Microsoft.Playwright, using image_drift's exact HTML wrapper.</summary>
public sealed class PlaywrightRenderer : ISvgRenderer
{
    private readonly IPlaywright _playwright;
    private readonly IBrowser _browser;
    private readonly string _scratch;

    private PlaywrightRenderer(IPlaywright playwright, IBrowser browser, string scratch)
    {
        _playwright = playwright;
        _browser = browser;
        _scratch = scratch;
    }

    public string Name => "Chromium via Microsoft.Playwright";

    public static async Task<PlaywrightRenderer> LaunchAsync(string chromiumPath, string scratch)
    {
        Directory.CreateDirectory(scratch);
        IPlaywright playwright = await Playwright.CreateAsync();
        IBrowser browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { ExecutablePath = chromiumPath });
        return new PlaywrightRenderer(playwright, browser, scratch);
    }

    public async Task<Raster> RenderAsync(string svgPath, int size)
    {
        string html = Path.Combine(_scratch, $"_{Guid.NewGuid():N}.html");
        string svg = Path.GetFullPath(svgPath);
        await File.WriteAllTextAsync(html, $$"""

            <!DOCTYPE html>
            <html>
            <body style="margin:0; padding:0; background:transparent; display:flex;
                         justify-content:center; align-items:center;">
                <img src="file://{{svg}}" style="width:{{size}}px; height:{{size}}px;" />
            </body>
            </html>

            """);
        try
        {
            IPage page = await _browser.NewPageAsync(new BrowserNewPageOptions { ViewportSize = new ViewportSize { Width = size, Height = size } });
            await page.GotoAsync($"file://{html}");
            byte[] png = await page.ScreenshotAsync(new PageScreenshotOptions { OmitBackground = true });
            await page.CloseAsync();
            return new ImageSharpLibrary().DecodeRgba(png);
        }
        finally
        {
            File.Delete(html);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _browser.DisposeAsync();
        _playwright.Dispose();
    }
}

/// <summary>Svg.Skia (MIT) drawing onto a SkiaSharp canvas; no browser.</summary>
public sealed class SvgSkiaRenderer : ISvgRenderer
{
    public string Name => "Svg.Skia";

    public Task<Raster> RenderAsync(string svgPath, int size)
    {
        using var svg = new SKSvg();
        SKPicture picture = svg.Load(svgPath) ?? throw new InvalidOperationException($"Svg.Skia could not load {svgPath}");
        using var bitmap = new SKBitmap(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            SKRect bounds = picture.CullRect;
            // <img style="width:Npx;height:Npx"> scales the viewBox to fit the square, preserving aspect (meet).
            float scale = Math.Min(size / bounds.Width, size / bounds.Height);
            canvas.Translate((size - (bounds.Width * scale)) / 2f, (size - (bounds.Height * scale)) / 2f);
            canvas.Scale(scale);
            canvas.Translate(-bounds.Left, -bounds.Top);
            canvas.DrawPicture(picture);
        }

        return Task.FromResult(SkiaSharpLibrary.FromBitmap(bitmap));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>A stack is a renderer plus the raster library that decodes, resizes and encodes around it.</summary>
public sealed record Stack(string Name, ISvgRenderer Renderer, IRasterLibrary Library);
