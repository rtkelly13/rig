using System.Runtime.InteropServices;
using SkiaSharp;

namespace Rig.Image;

/// <summary>
/// Every raster operation outside the renderers: decode, drop alpha, resize and encode. SkiaSharp supplies the PNG and JPEG
/// codecs; <see cref="PillowResize"/> does the resampling so the pixels match Pillow's.
/// </summary>
public static class RasterPipeline
{
    /// <summary>The square canvas image_drift compares on.</summary>
    public const int NormalisedSize = 500;

    /// <summary>Decodes a PNG or JPEG to straight (not premultiplied) RGBA, without resizing.</summary>
    public static Raster Decode(ReadOnlySpan<byte> file)
    {
        using var stream = new MemoryStream(file.ToArray());
        using var codec = SKCodec.Create(stream, out var created);
        if (created != SKCodecResult.Success)
        {
            throw new InvalidDataException($"expected a PNG or JPEG image, SkiaSharp returned {created}");
        }

        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        var rgba = new byte[info.Width * info.Height * 4];
        var handle = GCHandle.Alloc(rgba, GCHandleType.Pinned);
        try
        {
            var result = codec.GetPixels(info, handle.AddrOfPinnedObject());
            if (result != SKCodecResult.Success)
            {
                throw new InvalidDataException($"expected the image to decode, SkiaSharp returned {result}");
            }
        }
        finally
        {
            handle.Free();
        }

        return new Raster(info.Width, info.Height, rgba);
    }

    /// <summary>
    /// Pillow's <c>Image.open(p).convert("RGB").resize((500, 500))</c>: alpha is dropped without compositing, then the image is
    /// resampled bicubically unless it is already the right size.
    /// </summary>
    public static RgbImage LoadNormalised(ReadOnlySpan<byte> file) => Normalise(Decode(file));

    /// <summary>The same normalisation for pixels already in memory, such as a rendered SVG.</summary>
    public static RgbImage Normalise(Raster raster)
    {
        ArgumentNullException.ThrowIfNull(raster);
        var pixels = raster.Pixels;
        var rgb = new byte[raster.Width * raster.Height * 3];
        for (var i = 0; i < raster.Width * raster.Height; i++)
        {
            rgb[i * 3] = pixels[i * 4];
            rgb[(i * 3) + 1] = pixels[(i * 4) + 1];
            rgb[(i * 3) + 2] = pixels[(i * 4) + 2];
        }

        return PillowResize.Resize(new RgbImage(raster.Width, raster.Height, rgb), NormalisedSize, NormalisedSize, ResizeFilter.Bicubic);
    }

    /// <summary>Resamples straight-alpha RGBA to a square with Lanczos, as Pillow does for each ICO entry.</summary>
    public static Raster ResizeLanczos(Raster raster, int size) => PillowResize.Resize(raster, size, size, ResizeFilter.Lanczos);

    /// <summary>Encodes straight-alpha RGBA as a PNG.</summary>
    public static byte[] EncodePng(Raster raster)
    {
        ArgumentNullException.ThrowIfNull(raster);
        var info = new SKImageInfo(raster.Width, raster.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var bitmap = new SKBitmap(info);
        var rgba = raster.Pixels.ToArray();
        Marshal.Copy(rgba, 0, bitmap.GetPixels(), rgba.Length);
        using var image = SKImage.FromBitmap(bitmap);
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        return png.ToArray();
    }
}
