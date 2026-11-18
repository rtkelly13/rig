using System.Runtime.InteropServices;
using SkiaSharp;

namespace Rig.Image;

/// <summary>One labelled cell of a contact sheet.</summary>
/// <param name="Image">The cell's picture.</param>
/// <param name="Label">The caption under it; empty for none.</param>
/// <param name="Highlight">Whether to outline the cell in the accent colour.</param>
internal sealed record SheetPanel(RgbImage Image, string Label = "", bool Highlight = false);

/// <summary>
/// The contact and comparison sheets of the original imaging module, composed on the same layout. Panels are nearest-neighbour resized so pixel
/// boundaries stay legible. Captions use SkiaSharp's default typeface where the Python used Pillow's bitmap font, so glyph pixels differ.
/// </summary>
internal static class Sheets
{
    private static readonly (byte R, byte G, byte B) Background = (11, 15, 23);
    private static readonly SKColor LabelColour = new(148, 163, 184);
    private static readonly SKColor AccentColour = new(224, 64, 251);

    /// <summary>Lays <paramref name="panels"/> out in a grid, <paramref name="columns"/> wide (all in one row when null).</summary>
    public static RgbImage ContactSheet(IReadOnlyList<SheetPanel> panels, int cell = 240, int? columns = null, int gap = 12, int labelHeight = 22)
    {
        if (panels.Count == 0)
        {
            return Fill(cell, cell);
        }

        var perRow = columns ?? panels.Count;
        var rows = (panels.Count + perRow - 1) / perRow;
        var width = (perRow * cell) + ((perRow + 1) * gap);
        var height = (rows * (cell + labelHeight)) + ((rows + 1) * gap);
        var rgb = Fill(width, height);
        var pixels = rgb.Pixels.ToArray();
        for (var index = 0; index < panels.Count; index++)
        {
            var (row, column) = Math.DivRem(index, perRow);
            Paste(pixels, width, Nearest(panels[index].Image, cell), cell, gap + (column * (cell + gap)), gap + (row * (cell + labelHeight + gap)));
        }

        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        var rgba = new byte[width * height * 4];
        for (var i = 0; i < width * height; i++)
        {
            rgba[i * 4] = pixels[i * 3];
            rgba[(i * 4) + 1] = pixels[(i * 3) + 1];
            rgba[(i * 4) + 2] = pixels[(i * 3) + 2];
            rgba[(i * 4) + 3] = 255;
        }

        Marshal.Copy(rgba, 0, bitmap.GetPixels(), rgba.Length);
        using (var canvas = new SKCanvas(bitmap))
        {
            Annotate(canvas, panels, cell, perRow, gap, labelHeight);
        }

        var drawn = new byte[width * height * 3];
        Marshal.Copy(bitmap.GetPixels(), rgba, 0, rgba.Length);
        for (var i = 0; i < width * height; i++)
        {
            drawn[i * 3] = rgba[i * 4];
            drawn[(i * 3) + 1] = rgba[(i * 4) + 1];
            drawn[(i * 3) + 2] = rgba[(i * 4) + 2];
        }

        return new RgbImage(width, height, drawn);
    }

    /// <summary>The 3-panel diagnostic sheet: reference, render and, when given, the diff heatmap labelled with its scores.</summary>
    public static RgbImage ComparisonSheet(RgbImage reference, RgbImage render, RgbImage? diffHeatmap = null, double? rmsePct = null, double? ssim = null, int cell = 500)
    {
        var panels = new List<SheetPanel> { new(reference, "Reference", true), new(render, "Rendered Candidate") };
        if (diffHeatmap is not null)
        {
            var parts = new List<string>();
            if (rmsePct is { } rmse)
            {
                parts.Add($"RMSE: {rmse.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}%");
            }

            if (ssim is { } structure)
            {
                parts.Add($"SSIM: {structure.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)}");
            }

            panels.Add(new SheetPanel(diffHeatmap, parts.Count == 0 ? "Diff Heatmap" : $"Diff Heatmap ({string.Join(", ", parts)})"));
        }

        return ContactSheet(panels, cell, panels.Count, 16, 26);
    }

    /// <summary>An RGB image as an opaque raster, ready to encode.</summary>
    public static Raster ToRaster(RgbImage image)
    {
        var source = image.Pixels;
        var rgba = new byte[image.Width * image.Height * 4];
        for (var i = 0; i < image.Width * image.Height; i++)
        {
            rgba[i * 4] = source[i * 3];
            rgba[(i * 4) + 1] = source[(i * 3) + 1];
            rgba[(i * 4) + 2] = source[(i * 3) + 2];
            rgba[(i * 4) + 3] = 255;
        }

        return new Raster(image.Width, image.Height, rgba);
    }

    /// <summary>The PNG bytes of an RGB image.</summary>
    public static byte[] EncodePng(RgbImage image) => RasterPipeline.EncodePng(ToRaster(image));

    private static RgbImage Fill(int width, int height)
    {
        var data = new byte[width * height * 3];
        for (var i = 0; i < data.Length; i += 3)
        {
            data[i] = Background.R;
            data[i + 1] = Background.G;
            data[i + 2] = Background.B;
        }

        return new RgbImage(width, height, data);
    }

    // Pillow's NEAREST samples the source pixel whose centre the destination pixel's centre falls in.
    private static byte[] Nearest(RgbImage source, int size)
    {
        var data = new byte[size * size * 3];
        var pixels = source.Pixels;
        for (var y = 0; y < size; y++)
        {
            var sy = Math.Min(source.Height - 1, (int)((y + 0.5) * source.Height / size));
            for (var x = 0; x < size; x++)
            {
                var sx = Math.Min(source.Width - 1, (int)((x + 0.5) * source.Width / size));
                pixels.Slice(((sy * source.Width) + sx) * 3, 3).CopyTo(data.AsSpan(((y * size) + x) * 3));
            }
        }

        return data;
    }

    private static void Paste(byte[] sheet, int sheetWidth, byte[] panel, int size, int left, int top)
    {
        for (var y = 0; y < size; y++)
        {
            panel.AsSpan(y * size * 3, size * 3).CopyTo(sheet.AsSpan((((top + y) * sheetWidth) + left) * 3));
        }
    }

    private static void Annotate(SKCanvas canvas, IReadOnlyList<SheetPanel> panels, int cell, int perRow, int gap, int labelHeight)
    {
        using var outline = new SKPaint { Color = AccentColour, Style = SKPaintStyle.Stroke, StrokeWidth = 2, IsAntialias = false };
        using var typeface = SKTypeface.Default;
        using var font = new SKFont(typeface, 11);
        using var text = new SKPaint { Color = LabelColour, IsAntialias = true };
        for (var index = 0; index < panels.Count; index++)
        {
            var (row, column) = Math.DivRem(index, perRow);
            var x = gap + (column * (cell + gap));
            var y = gap + (row * (cell + labelHeight + gap));
            if (panels[index].Highlight)
            {
                canvas.DrawRect(x - 1, y - 1, cell + 2, cell + 2, outline);
            }

            if (panels[index].Label.Length > 0)
            {
                canvas.DrawText(panels[index].Label, x + 2, y + cell + 5 + 11, SKTextAlign.Left, font, text);
            }
        }
    }
}
