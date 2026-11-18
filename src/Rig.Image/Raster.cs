namespace Rig.Image;

/// <summary>A straight-alpha RGBA8 image, row-major: what a renderer hands back and the ICO and PNG writers take.</summary>
public sealed class Raster
{
    private readonly byte[] _rgba;

    /// <summary>Wraps <paramref name="rgba"/>, which must be exactly <c>width * height * 4</c> bytes; the bytes are not copied.</summary>
    public Raster(int width, int height, byte[] rgba)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentNullException.ThrowIfNull(rgba);
        if (rgba.Length != checked(width * height * 4))
        {
            throw new ArgumentException($"expected {width * height * 4} RGBA bytes for {width}x{height}, found {rgba.Length}", nameof(rgba));
        }

        Width = width;
        Height = height;
        _rgba = rgba;
    }

    /// <summary>Pixels across.</summary>
    public int Width { get; }

    /// <summary>Pixels down.</summary>
    public int Height { get; }

    /// <summary>Red, green, blue, alpha per pixel, straight (not premultiplied).</summary>
    public ReadOnlySpan<byte> Pixels => _rgba;
}

/// <summary>An opaque RGB8 image, row-major: what the drift metrics compare after Pillow's <c>convert("RGB")</c>.</summary>
public sealed class RgbImage
{
    private readonly byte[] _rgb;

    /// <summary>Wraps <paramref name="rgb"/>, which must be exactly <c>width * height * 3</c> bytes; the bytes are not copied.</summary>
    public RgbImage(int width, int height, byte[] rgb)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentNullException.ThrowIfNull(rgb);
        if (rgb.Length != checked(width * height * 3))
        {
            throw new ArgumentException($"expected {width * height * 3} RGB bytes for {width}x{height}, found {rgb.Length}", nameof(rgb));
        }

        Width = width;
        Height = height;
        _rgb = rgb;
    }

    /// <summary>Pixels across.</summary>
    public int Width { get; }

    /// <summary>Pixels down.</summary>
    public int Height { get; }

    /// <summary>Red, green, blue per pixel.</summary>
    public ReadOnlySpan<byte> Pixels => _rgb;

    /// <summary>PIL crop semantics: left and top inclusive, right and bottom exclusive.</summary>
    public RgbImage Crop(int left, int top, int right, int bottom)
    {
        var width = right - left;
        var height = bottom - top;
        var data = new byte[width * height * 3];
        for (var y = 0; y < height; y++)
        {
            _rgb.AsSpan((((top + y) * Width) + left) * 3, width * 3).CopyTo(data.AsSpan(y * width * 3));
        }

        return new RgbImage(width, height, data);
    }
}
