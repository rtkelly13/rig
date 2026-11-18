using System.Globalization;
using System.Xml;

namespace Rig.Image.Simplifier;

/// <summary>
/// The simplifier's pipeline (image_drift's <c>simplify_svg_document</c>): geometric decimation, then the scour cleanup, then
/// viewBox-aware coordinate quantisation. Text that is not well-formed XML is never an error here; each pass leaves it as it is.
/// </summary>
public static class SvgSimplifier
{
    /// <summary>Runs the passes. A null <paramref name="precision"/> is chosen from the size of the viewBox.</summary>
    public static string Simplify(string svg, double tolerance = 0.5, bool useGeometric = true, bool useScour = true, int? precision = null)
    {
        var current = svg;
        if (useGeometric && tolerance > 0.0)
        {
            current = Geometric(current, tolerance);
        }

        if (useScour)
        {
            current = RunScour(current);
        }

        return Quantize(current, precision);
    }

    /// <summary>The number of path commands over every path element, or 0 when the text is not well-formed XML.</summary>
    public static int CountPathNodes(string svg)
    {
        var root = TryParse(svg);
        if (root is null)
        {
            return 0;
        }

        var total = 0;
        foreach (var element in root.SelfAndDescendants())
        {
            if (PathData(element) is { } d)
            {
                total += Rig.Image.Simplifier.PathData.Parse(d).Count;
            }
        }

        return total;
    }

    /// <summary>The decimal places that suit a viewBox of this size.</summary>
    public static int CalculateViewboxPrecision(double width, double height)
    {
        var largest = Math.Max(width, height);
        if (largest <= 32)
        {
            return 3;
        }

        return largest <= 128 ? 2 : 1;
    }

    /// <summary>Writes every path at the precision (or the viewBox's), leaving text that is not well-formed XML as it was.</summary>
    public static string Quantize(string svg, int? precision = null)
    {
        var root = TryParse(svg);
        if (root is null)
        {
            return svg;
        }

        var places = precision;
        if (places is null)
        {
            var (width, height) = ViewboxDimensions(root);
            places = CalculateViewboxPrecision(width, height);
        }

        foreach (var element in root.SelfAndDescendants())
        {
            if (PathData(element) is { } d)
            {
                var commands = Rig.Image.Simplifier.PathData.Parse(d);
                if (commands.Count > 0)
                {
                    element.SetAttribute("d", Rig.Image.Simplifier.PathData.Serialize(commands, places.Value));
                }
            }
        }

        return ElementTreeWriter.Write(root);
    }

    /// <summary>
    /// The viewBox's width and height, else the width and height attributes with everything but digits and dots dropped, else 1000 by 1000.
    /// A viewBox part that is not a number throws <see cref="FormatException"/>, as the Python's uncaught ValueError does.
    /// </summary>
    internal static (double Width, double Height) ViewboxDimensions(DomElement root)
    {
        var viewBox = root.GetAttribute("viewBox");
        if (viewBox.Length > 0)
        {
            var parts = viewBox.Replace(',', ' ').Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(ParseFloat).ToList();
            if (parts.Count >= 4)
            {
                return (parts[2], parts[3]);
            }
        }

        var width = root.HasAttribute("width") ? root.GetAttribute("width") : "1000";
        var height = root.HasAttribute("height") ? root.GetAttribute("height") : "1000";
        if (double.TryParse(DigitsAndDots(width), NumberStyles.Float, CultureInfo.InvariantCulture, out var w)
            && double.TryParse(DigitsAndDots(height), NumberStyles.Float, CultureInfo.InvariantCulture, out var h))
        {
            return (w, h);
        }

        return (1000.0, 1000.0);
    }

    private static string DigitsAndDots(string text) => new(text.Where(c => char.IsAsciiDigit(c) || c == '.').ToArray());

    private static double ParseFloat(string text)
    {
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            return value;
        }

        throw new FormatException($"could not convert string to float: '{text}'");
    }

    private static string Geometric(string svg, double tolerance)
    {
        var root = TryParse(svg);
        if (root is null)
        {
            return svg;
        }

        foreach (var element in root.SelfAndDescendants())
        {
            if (PathData(element) is { } d)
            {
                element.SetAttribute("d", GeometricSimplifier.SimplifyPath(d, tolerance));
            }
        }

        return ElementTreeWriter.Write(root);
    }

    internal static string RunScour(string svg)
    {
        try
        {
            return Scour.Run(svg);
        }
        catch (XmlException)
        {
            return svg;
        }
        catch (FormatException)
        {
            return svg;
        }
        catch (OverflowException)
        {
            return svg;
        }
    }

    private static string? PathData(DomElement element) =>
        element.LocalName.EndsWith("path", StringComparison.Ordinal) && element.HasAttribute("d") ? element.GetAttribute("d") : null;

    private static DomElement? TryParse(string svg)
    {
        try
        {
            return DomParser.Parse(svg);
        }
        catch (XmlException)
        {
            return null;
        }
    }
}
