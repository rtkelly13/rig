using System.Globalization;
using System.Text;
using Rig.Core;
using Rig.Image;

namespace Rig.Cli.Image;

/// <summary>Turns the errors an image command expects into one <c>Error:</c> line and the error exit code.</summary>
internal static class ImageGuard
{
    /// <summary>Runs <paramref name="body"/>; a missing input, a bad option or an unavailable renderer prints <c>Error: ...</c> to <paramref name="error"/> and returns 2.</summary>
    public static async Task<int> RunAsync(TextWriter error, Func<Task<int>> body)
    {
        try
        {
            return await body().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is RendererUnavailableException or InvalidDataException or IOException or ArgumentException or FormatException or InvalidOperationException)
        {
            await error.WriteLineAsync($"Error: {exception.Message}").ConfigureAwait(false);
            return (int)ExitCode.Error;
        }
    }
}

/// <summary>Reading and rendering the images the image commands compare.</summary>
internal static class ImageInputs
{
    /// <summary>The path as a full path, or a <see cref="FileNotFoundException"/> that says what was expected.</summary>
    public static string Require(string path, string what)
    {
        var full = Path.GetFullPath(path);
        return File.Exists(full) ? full : throw new FileNotFoundException($"expected {what} at {full}, found none");
    }

    /// <summary>A raster file decoded onto the canvas.</summary>
    public static async Task<RgbImage> LoadAsync(string path, int size, CancellationToken cancellationToken) =>
        RasterPipeline.LoadNormalised(await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false), size);

    /// <summary>An SVG rendered by <paramref name="renderer"/> onto the canvas.</summary>
    public static async Task<RgbImage> RenderAsync(ISvgRenderer renderer, string svgPath, int size, CancellationToken cancellationToken) =>
        RasterPipeline.Normalise(await renderer.RenderAsync(svgPath, size, cancellationToken).ConfigureAwait(false), size);

    /// <summary>Launches the authoritative renderer, renders one SVG onto the canvas and disposes the renderer.</summary>
    public static async Task<RgbImage> RenderOnceAsync(ImageHost host, string? chromium, string svgPath, int size, CancellationToken cancellationToken)
    {
        var renderer = await host.LaunchAuthoritative(chromium).ConfigureAwait(false);
        await using (renderer.ConfigureAwait(false))
        {
            return await RenderAsync(renderer, svgPath, size, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>The named feature's box, or an <see cref="ArgumentException"/> listing the choices.</summary>
    public static FeatureBox Region(ImageProject project, string feature) =>
        project.Regions.FirstOrDefault(box => box.Key == feature)
        ?? throw new ArgumentException($"expected one of {string.Join(", ", project.Regions.Select(box => box.Key))}, found '{feature}'", nameof(feature));

    /// <summary>The image cropped to a feature box.</summary>
    public static RgbImage Crop(RgbImage image, FeatureBox box) => image.Crop(box.X0, box.Y0, box.X1, box.Y1);

    /// <summary>Writes an image as a PNG, creating its folder.</summary>
    public static async Task WritePngAsync(string path, RgbImage image, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await File.WriteAllBytesAsync(path, Sheets.EncodePng(image), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Splits <c>--variants</c> into keys, each of which the project must declare.</summary>
    public static IReadOnlyList<string> VariantKeys(ImageProject project, string? variants)
    {
        if (string.IsNullOrWhiteSpace(variants))
        {
            return [.. project.Variants.Keys];
        }

        var keys = variants.Split(',').Select(key => key.Trim()).Where(key => key.Length > 0).ToList();
        var unknown = keys.Where(key => !project.Variants.ContainsKey(key)).ToList();
        return unknown.Count == 0
            ? keys
            : throw new ArgumentException($"expected variants from {string.Join(", ", project.Variants.Keys)}, found {string.Join(", ", unknown)}", nameof(variants));
    }
}

/// <summary>The plain-text drift reports <c>compare</c>, <c>diagnose</c> and <c>vectorize</c> print.</summary>
internal static class ImageReports
{
    /// <summary>The one-line pixel drift result.</summary>
    public static string Summary(string title, DriftNumbers drift, string suffix = "")
    {
        var text = new StringBuilder();
        text.AppendLine(title);
        text.AppendLine(CultureInfo.InvariantCulture, $"Pixel Drift Result: {drift.RmsePct:0.00}% RMSE | {drift.Ssim * 100.0:0.00}% SSIM ({drift.Ssim:0.000}) | {DriftReport.Psnr(drift.PsnrDb)} PSNR{suffix}");
        return text.ToString();
    }

    /// <summary>The five worst cells of the 5 by 5 grid, with their pixel bounds on a canvas of <paramref name="width"/> by <paramref name="height"/>.</summary>
    public static string WorstCells(DriftNumbers drift, int width, int height, string suffix = "")
    {
        var cellHeight = Math.Max(1, height / 5);
        var cellWidth = Math.Max(1, width / 5);
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"Top Spatial Drift Regions{suffix}");
        var rank = 1;
        foreach (var cell in drift.Grid.OrderByDescending(c => c.Error).Take(5))
        {
            var r0 = cell.Row * cellHeight;
            var r1 = Math.Min(height, (cell.Row + 1) * cellHeight);
            var c0 = cell.Column * cellWidth;
            var c1 = Math.Min(width, (cell.Column + 1) * cellWidth);
            text.AppendLine(CultureInfo.InvariantCulture, $"  #{rank} Row {cell.Row + 1}, Col {cell.Column + 1} (Y:[{r0}:{r1}], X:[{c0}:{c1}])  {cell.Error:0.00}");
            rank++;
        }

        return text.ToString();
    }

    /// <summary>The decomposed drift diagnostics, hotspots and builder guidance.</summary>
    public static string Diagnostics(DriftNumbers drift)
    {
        var text = new StringBuilder();
        text.AppendLine("DECOMPOSED_DRIFT_DIAGNOSTICS");
        text.AppendLine(CultureInfo.InvariantCulture, $"  Total Pixel Drift                {drift.RmsePct:0.00}% RMSE  PSNR: {DriftReport.Psnr(drift.PsnrDb)}, SSIM: {drift.Ssim * 100.0:0.00}%");
        text.AppendLine(CultureInfo.InvariantCulture, $"  Luminance / Geometry Error (Y)   {drift.LuminanceRmsePct:0.00}%  edge contour and vertex shape displacement");
        text.AppendLine(CultureInfo.InvariantCulture, $"  Chrominance / Color Error (CbCr) {drift.ChrominanceRmsePct:0.00}%  Color/Geometry ratio: {drift.ColorToGeometryRatio:0.00}x");
        text.AppendLine(CultureInfo.InvariantCulture, $"  Edge Field Correlation           {drift.EdgeCorrelation:0.0}%  Sobel gradient tangent alignment along contours");
        var bias = drift.BoundaryDilationBias > 0 ? "DILATION (+)" : "EROSION (-)";
        text.AppendLine(CultureInfo.InvariantCulture, $"  Boundary Contour Bias            {drift.BoundaryDilationBias:+0.00;-0.00;+0.00} ({bias})  feature thickness relative to raster ground-truth");
        if (drift.Hotspots.Count > 0)
        {
            text.AppendLine("\nLOCALIZED_ERROR_HOTSPOTS");
            foreach (var hotspot in drift.Hotspots)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"  #{hotspot.Rank} X:[{hotspot.X0}..{hotspot.X1}], Y:[{hotspot.Y0}..{hotspot.Y1}]  {hotspot.ErrorPct:0.00}%  {hotspot.Mode}  {DriftReport.HotspotSummary(hotspot.Mode)}");
            }
        }

        text.AppendLine("\nACTIONABLE_BUILDER_REFINEMENTS");
        foreach (var recommendation in DriftReport.Recommendations(drift))
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"  - {recommendation}");
        }

        return text.ToString();
    }
}
