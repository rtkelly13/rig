namespace Rig.Image;

/// <summary>The resampling kernels Pillow names.</summary>
public enum ResizeFilter
{
    /// <summary>Pillow's BICUBIC: a cubic with a = -0.5.</summary>
    Bicubic,

    /// <summary>Pillow's LANCZOS: a windowed sinc over three lobes.</summary>
    Lanczos,
}

/// <summary>
/// Pillow's resampler, bit for bit: fixed-point coefficients at 22 bits, a horizontal pass then a vertical pass with an 8-bit
/// result between them, and premultiplied alpha for RGBA. the original scores depend on these pixels, so a general-purpose
/// resampler that is only close would move every metric.
/// </summary>
public static class PillowResize
{
    private const int PrecisionBits = 22;

    /// <summary>Resamples an opaque RGB image to <paramref name="width"/> by <paramref name="height"/>; the same size is returned as is.</summary>
    public static RgbImage Resize(RgbImage image, int width, int height, ResizeFilter filter)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.Width == width && image.Height == height)
        {
            return image;
        }

        return new RgbImage(width, height, Resample(image.Pixels, image.Width, image.Height, 3, width, height, filter));
    }

    /// <summary>
    /// Resamples a straight-alpha RGBA image: Pillow converts to premultiplied RGBa, resamples, and converts back, so colour does
    /// not bleed in from transparent pixels. The same size is returned as is.
    /// </summary>
    public static Raster Resize(Raster raster, int width, int height, ResizeFilter filter)
    {
        ArgumentNullException.ThrowIfNull(raster);
        if (raster.Width == width && raster.Height == height)
        {
            return raster;
        }

        var premultiplied = Premultiply(raster.Pixels);
        var resized = Resample(premultiplied, raster.Width, raster.Height, 4, width, height, filter);
        return new Raster(width, height, Unpremultiply(resized));
    }

    private static byte[] Resample(ReadOnlySpan<byte> source, int sourceWidth, int sourceHeight, int channels, int width, int height, ResizeFilter filter)
    {
        var current = source;
        var currentWidth = sourceWidth;
        byte[]? horizontal = null;
        if (width != sourceWidth)
        {
            var (bounds, coefficients, size) = Coefficients(sourceWidth, width, filter);
            horizontal = new byte[width * sourceHeight * channels];
            for (var y = 0; y < sourceHeight; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var min = bounds[x * 2];
                    var count = bounds[(x * 2) + 1];
                    for (var c = 0; c < channels; c++)
                    {
                        var sum = 1L << (PrecisionBits - 1);
                        for (var i = 0; i < count; i++)
                        {
                            sum += (long)current[((((y * currentWidth) + min + i)) * channels) + c] * coefficients[(x * size) + i];
                        }

                        horizontal[(((y * width) + x) * channels) + c] = Clip8(sum);
                    }
                }
            }

            current = horizontal;
            currentWidth = width;
        }

        if (height == sourceHeight)
        {
            return horizontal ?? current.ToArray();
        }

        var (verticalBounds, verticalCoefficients, verticalSize) = Coefficients(sourceHeight, height, filter);
        var result = new byte[currentWidth * height * channels];
        for (var y = 0; y < height; y++)
        {
            var min = verticalBounds[y * 2];
            var count = verticalBounds[(y * 2) + 1];
            for (var x = 0; x < currentWidth; x++)
            {
                for (var c = 0; c < channels; c++)
                {
                    var sum = 1L << (PrecisionBits - 1);
                    for (var i = 0; i < count; i++)
                    {
                        sum += (long)current[((((min + i) * currentWidth) + x) * channels) + c] * verticalCoefficients[(y * verticalSize) + i];
                    }

                    result[(((y * currentWidth) + x) * channels) + c] = Clip8(sum);
                }
            }
        }

        return result;
    }

    private static byte Clip8(long sum) => (byte)Math.Clamp(sum >> PrecisionBits, 0, 255);

    /// <summary>Pillow's precompute_coeffs and normalize_coeffs_8bpc for one axis of a whole-image resize.</summary>
    private static (int[] Bounds, int[] Coefficients, int Size) Coefficients(int inSize, int outSize, ResizeFilter filter)
    {
        var scale = (double)inSize / outSize;
        var filterScale = Math.Max(scale, 1.0);
        var support = (filter == ResizeFilter.Bicubic ? 2.0 : 3.0) * filterScale;
        var size = ((int)Math.Ceiling(support) * 2) + 1;
        var bounds = new int[outSize * 2];
        var coefficients = new int[outSize * size];
        var weights = new double[size];
        var inverseScale = 1.0 / filterScale;
        for (var x = 0; x < outSize; x++)
        {
            var center = (x + 0.5) * scale;
            var min = Math.Max((int)(center - support + 0.5), 0);
            var max = Math.Min((int)(center + support + 0.5), inSize) - min;
            var total = 0.0;
            for (var i = 0; i < max; i++)
            {
                weights[i] = Kernel(filter, (i + min - center + 0.5) * inverseScale);
                total += weights[i];
            }

            for (var i = 0; i < max; i++)
            {
                var weight = total != 0.0 ? weights[i] / total : weights[i];
                coefficients[(x * size) + i] = weight < 0
                    ? (int)(-0.5 + (weight * (1 << PrecisionBits)))
                    : (int)(0.5 + (weight * (1 << PrecisionBits)));
            }

            bounds[x * 2] = min;
            bounds[(x * 2) + 1] = max;
        }

        return (bounds, coefficients, size);
    }

    private static double Kernel(ResizeFilter filter, double x)
    {
        if (filter == ResizeFilter.Bicubic)
        {
            const double A = -0.5;
            x = Math.Abs(x);
            if (x < 1.0)
            {
                return (((((A + 2.0) * x) - (A + 3.0)) * x) * x) + 1.0;
            }

            return x < 2.0 ? (((((x - 5.0) * x) + 8.0) * x) - 4.0) * A : 0.0;
        }

        return x is > -3.0 and < 3.0 ? Sinc(x) * Sinc(x / 3.0) : 0.0;
    }

    private static double Sinc(double x) => x == 0.0 ? 1.0 : Math.Sin(x * Math.PI) / (x * Math.PI);

    private static byte[] Premultiply(ReadOnlySpan<byte> rgba)
    {
        var result = new byte[rgba.Length];
        for (var i = 0; i < rgba.Length; i += 4)
        {
            int alpha = rgba[i + 3];
            for (var c = 0; c < 3; c++)
            {
                var product = (rgba[i + c] * alpha) + 128;
                result[i + c] = (byte)((product + (product >> 8)) >> 8);
            }

            result[i + 3] = (byte)alpha;
        }

        return result;
    }

    private static byte[] Unpremultiply(byte[] rgba)
    {
        for (var i = 0; i < rgba.Length; i += 4)
        {
            var alpha = rgba[i + 3];
            if (alpha is 0 or 255)
            {
                continue;
            }

            for (var c = 0; c < 3; c++)
            {
                rgba[i + c] = (byte)Math.Min(255 * rgba[i + c] / alpha, 255);
            }
        }

        return rgba;
    }
}
