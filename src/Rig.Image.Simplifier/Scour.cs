using System.Globalization;
using System.Text.RegularExpressions;

namespace Rig.Image.Simplifier;

/// <summary>
/// The scour 0.38.2 cleanup the Python simplifier runs with its options (descriptive and editor elements removed, comments stripped, no
/// XML prolog, ids kept, <c>group_create</c> off), ported over <c>System.Xml</c> in place of minidom and run in scour's order. The path,
/// point, transform, gradient and group passes live in the sibling <c>Scour.*.cs</c> files. Not ported: <c>createGroupsForCommonAttributes</c>
/// (scour never reaches it with <c>group_create</c> off) and raster embedding. Where Python crashes on malformed numbers this port leaves
/// the document alone or copes; the pull request that introduced it lists those cases.
/// </summary>
internal static partial class Scour
{
    private const string SvgNamespace = "http://www.w3.org/2000/svg";
    private const string XlinkNamespace = "http://www.w3.org/1999/xlink";

    private static readonly HashSet<string> SvgAttributeNames = new(ScourTables.SvgAttributes, StringComparer.Ordinal);

    private static readonly string[] FontProperties =
    [
        "font-family", "font-size", "font-stretch", "font-size-adjust", "font-style", "font-variant", "font-weight", "letter-spacing",
        "line-height", "kerning", "text-align", "text-anchor", "text-decoration", "text-rendering", "unicode-bidi", "word-spacing", "writing-mode",
    ];

    private static readonly string[] ReferencingProperties = ["fill", "stroke", "filter", "clip-path", "mask", "marker-start", "marker-end", "marker-mid"];

    private static readonly string[] LengthElements = ["svg", "image", "rect", "circle", "ellipse", "line", "linearGradient", "radialGradient", "stop", "filter"];

    private static readonly string[] LengthAttributes = ["x", "y", "width", "height", "cx", "cy", "r", "rx", "ry", "x1", "y1", "x2", "y2", "fx", "fy", "offset"];

    private static readonly string[] PrecisionAttributes =
    [
        "opacity", "flood-opacity", "fill-opacity", "stroke-opacity", "stop-opacity", "stroke-miterlimit", "stroke-dashoffset", "letter-spacing",
        "word-spacing", "kerning", "font-size-adjust", "font-size", "stroke-width",
    ];

    private static readonly string[] ShapeElements = ["rect", "circle", "ellipse", "polygon", "line", "polyline", "path", "g", "a"];

    /// <summary>
    /// Cleans an SVG document. Throws <see cref="System.Xml.XmlException"/> when it is not well formed, <see cref="FormatException"/> or
    /// <see cref="OverflowException"/> when a number in it cannot be scoured; the caller returns the input unchanged for those, as the
    /// Python does for Expat, ArithmeticError and ValueError.
    /// </summary>
    public static string Run(string svg)
    {
        var before = new List<string>();
        var after = new List<string>();
        var root = DomParser.Parse(svg, before, after);
        RemoveDescriptiveElements(root);
        RemoveEditorData(root);
        if (root.GetAttribute("xmlns") != SvgNamespace)
        {
            root.SetAttribute("xmlns", SvgNamespace);
        }

        root = RemoveUnusedNamespaceDeclarations(root);
        RepairStyle(root);
        ConvertColors(root);
        while (RemoveUnreferencedElements(root) > 0)
        {
        }

        RemoveEmptyContainers(root);
        while (RemoveDuplicateGradientStops(root) > 0)
        {
        }

        while (CollapseSinglyReferencedGradients(root) > 0)
        {
        }

        RemoveDuplicateGradients(root);
        MergeSiblingGroups(root);

        // The root's children are the first ones considered, whether or not anything refers to them; the root itself never takes attributes.
        var referenced = FindReferencedElements(root);
        foreach (var child in root.ChildElements.ToList())
        {
            MoveCommonAttributesToParent(child, referenced);
        }

        RemoveUnusedAttributesOnParent(root);
        while (RemoveNestedGroups(root) > 0)
        {
        }

        foreach (var polygon in root.DescendantsNamed("polygon"))
        {
            CleanPolygon(polygon);
        }

        foreach (var polyline in root.DescendantsNamed("polyline"))
        {
            CleanPolyline(polyline);
        }

        foreach (var path in root.DescendantsNamed("path"))
        {
            if (path.GetAttribute("d").Length == 0)
            {
                path.Remove();
            }
            else
            {
                try
                {
                    CleanPath(path);
                }
                catch (ArgumentOutOfRangeException e)
                {
                    // A command with fewer numbers than it needs: Python crashes with an IndexError, so the document is left alone.
                    throw new FormatException("Path data has too few numbers for its command.", e);
                }
            }
        }

        ScourLengths(root);
        ReducePrecision(root);
        RemoveDefaultValues(root, []);
        OptimizeTransforms(root);

        // The top level keeps its DOCTYPE and processing instructions, in order, around the root.
        return string.Concat(before.Select(node => node + "\n")) + ScourWriter.Write(root) + "\n" + string.Concat(after.Select(node => node + "\n"));
    }

    private static void RemoveDescriptiveElements(DomElement root)
    {
        foreach (var name in new[] { "title", "desc", "metadata" })
        {
            foreach (var element in root.DescendantsNamed(name))
            {
                element.Remove();
            }
        }
    }

    private static void RemoveEditorData(DomElement root)
    {
        RemoveNamespacedElements(root);
        foreach (var element in root.SelfAndDescendants())
        {
            element.Attributes.RemoveAll(a => a.Namespace is not null && ScourTables.UnwantedNamespaces.Contains(a.Namespace));
        }

        root.Attributes.RemoveAll(a => ScourTables.UnwantedNamespaces.Contains(a.Value));
    }

    private static void RemoveNamespacedElements(DomElement node)
    {
        foreach (var child in node.ChildElements.ToList())
        {
            if (child.Namespace is not null && ScourTables.UnwantedNamespaces.Contains(child.Namespace))
            {
                child.Remove();
            }
        }

        foreach (var child in node.ChildElements)
        {
            RemoveNamespacedElements(child);
        }
    }

    private static DomElement RemoveUnusedNamespaceDeclarations(DomElement root)
    {
        var toRemove = new List<string>();
        var redundantPrefixes = new List<string>();
        foreach (var attribute in root.Attributes.ToList())
        {
            var name = attribute.QualifiedName;
            if (!name.StartsWith("xmlns:", StringComparison.Ordinal))
            {
                continue;
            }

            if (attribute.Value == SvgNamespace)
            {
                redundantPrefixes.Add(name[6..]);
                toRemove.Add(name);
            }
            else if (IsNamespaceUnused(root, name[6..], attribute.Value))
            {
                toRemove.Add(name);
            }
        }

        foreach (var name in toRemove)
        {
            root.RemoveAttribute(name);
        }

        foreach (var prefix in redundantPrefixes)
        {
            root = RemapPrefix(root, prefix);
        }

        return root;
    }

    private static bool IsNamespaceUnused(DomElement root, string prefix, string ns)
    {
        var elements = root.SelfAndDescendants().ToList();
        if (elements.Any(e => e.Namespace == ns))
        {
            return false;
        }

        return !elements.Any(e => e.Attributes.Any(a => a.QualifiedName.StartsWith(prefix, StringComparison.Ordinal)));
    }

    // minidom rebuilds each prefixed element as a plain, namespace-less one; the document ends up with the SVG namespace only by declaration.
    private static DomElement RemapPrefix(DomElement node, string prefix)
    {
        var current = node;
        if (node.Prefix == prefix)
        {
            current = new DomElement(string.Empty, node.LocalName, null);
            foreach (var attribute in node.Attributes)
            {
                current.Attributes.Add(new DomAttribute(attribute.Prefix, attribute.LocalName, attribute.Namespace, attribute.Value));
            }

            foreach (var child in node.Children)
            {
                current.Append(child);
            }

            if (node.Parent is { } parent)
            {
                parent.Children[parent.Children.IndexOf(node)] = current;
                current.Parent = parent;
            }
        }

        foreach (var child in current.ChildElements.ToList())
        {
            RemapPrefix(child, prefix);
        }

        return current;
    }

    private static readonly string[] OpacityZeroUseless =
        ["fill", "fill-opacity", "fill-rule", "stroke", "stroke-linejoin", "stroke-opacity", "stroke-miterlimit", "stroke-linecap", "stroke-dasharray", "stroke-dashoffset"];

    private static readonly string[] StrokeNoneUseless =
        ["stroke-width", "stroke-linejoin", "stroke-miterlimit", "stroke-linecap", "stroke-dasharray", "stroke-dashoffset", "stroke-opacity"];

    private static readonly string[] FillOpacityZeroUseless = ["fill", "fill-rule"];

    private static readonly string[] StrokeOpacityZeroUseless = ["stroke", "stroke-width", "stroke-linejoin", "stroke-linecap", "stroke-dasharray", "stroke-dashoffset"];

    private static readonly string[] StrokeWidthZeroUseless = ["stroke", "stroke-linejoin", "stroke-linecap", "stroke-dasharray", "stroke-dashoffset", "stroke-opacity"];

    private static readonly string[] ContainerElements = ["a", "defs", "glyph", "g", "marker", "mask", "missing-glyph", "pattern", "svg", "switch", "symbol"];

    // scour's repairStyle: style properties that cannot show are deleted, and the rest that have an attribute form become that attribute.
    private static void RepairStyle(DomElement root)
    {
        foreach (var element in root.SelfAndDescendants())
        {
            var styles = ReadStyle(element);
            if (styles.Count == 0)
            {
                continue;
            }

            // A gradient followed by a black fallback colour: the fallback is never used.
            foreach (var property in new[] { "fill", "stroke" })
            {
                if (styles.TryGetValue(property, out var paint))
                {
                    var chunk = paint.Split(") ");
                    if (chunk.Length == 2 && chunk[1] == "rgb(0, 0, 0)"
                        && (chunk[0].StartsWith("url(#", StringComparison.Ordinal) || chunk[0].StartsWith("url(\"#", StringComparison.Ordinal) || chunk[0].StartsWith("url('#", StringComparison.Ordinal)))
                    {
                        styles[property] = chunk[0] + ")";
                    }
                }
            }

            if (styles.TryGetValue("opacity", out var opacity) && SvgLength.ParsePythonFloat(opacity) == 0.0)
            {
                DeleteUnlessInherited(element, styles, OpacityZeroUseless);
            }

            if (styles.TryGetValue("stroke", out var stroke) && stroke == "none")
            {
                DeleteUnlessInherited(element, styles, StrokeNoneUseless);
                if (!StyleInheritedByChild(element, "stroke") && StyleInheritedFromParent(element, "stroke") is null or "none")
                {
                    styles.Remove("stroke");
                }
            }

            if (styles.TryGetValue("fill", out var fill) && fill == "none")
            {
                DeleteUnlessInherited(element, styles, ["fill-rule", "fill-opacity"]);
            }

            if (styles.TryGetValue("fill-opacity", out var fillOpacity) && SvgLength.ParsePythonFloat(fillOpacity) == 0.0)
            {
                DeleteUnlessInherited(element, styles, FillOpacityZeroUseless);
            }

            if (styles.TryGetValue("stroke-opacity", out var strokeOpacity) && SvgLength.ParsePythonFloat(strokeOpacity) == 0.0)
            {
                DeleteUnlessInherited(element, styles, StrokeOpacityZeroUseless);
            }

            if (styles.TryGetValue("stroke-width", out var strokeWidth) && SvgLength.Parse(strokeWidth).Value == 0.0)
            {
                DeleteUnlessInherited(element, styles, StrokeWidthZeroUseless);
            }

            if (!MayContainText(element))
            {
                foreach (var name in FontProperties)
                {
                    styles.Remove(name);
                }
            }

            styles.Remove("-inkscape-font-specification");
            RepairOverflow(element, root, styles);
            foreach (var name in styles.Keys.ToList())
            {
                if (SvgAttributeNames.Contains(name))
                {
                    element.SetAttribute(name, styles[name]);
                    styles.Remove(name);
                }
            }

            WriteStyle(element, styles);
        }
    }

    private static void DeleteUnlessInherited(DomElement element, OrderedDictionary<string, string> styles, string[] names)
    {
        foreach (var name in names)
        {
            if (styles.ContainsKey(name) && !StyleInheritedByChild(element, name))
            {
                styles.Remove(name);
            }
        }
    }

    // The value of a property inherited from the ancestors, looking only at inline styles and presentation attributes, or null at the top.
    private static string? StyleInheritedFromParent(DomElement node, string style)
    {
        var parent = node.Parent;
        while (parent is not null)
        {
            var styles = ReadStyle(parent);
            if (styles.TryGetValue(style, out var value) && value != "inherit")
            {
                return value;
            }

            var attribute = parent.GetAttribute(style);
            if (attribute is not ("" or "inherit"))
            {
                return attribute;
            }

            parent = parent.Parent;
        }

        return null;
    }

    // Whether a descendant could inherit the property: when false it is safe to delete it from this element.
    private static bool StyleInheritedByChild(DomNode node, string style, bool nodeIsChild = false)
    {
        if (node is not DomElement element)
        {
            return false;
        }

        if (nodeIsChild)
        {
            // A child that sets its own value ends the search along that branch.
            if (element.GetAttribute(style) is not ("" or "inherit"))
            {
                return false;
            }

            if (ReadStyle(element).TryGetValue(style, out var value) && value != "inherit")
            {
                return false;
            }
        }
        else if (element.Children.Count == 0)
        {
            return false;
        }

        if (element.Children.Any(child => StyleInheritedByChild(child, style, true)))
        {
            return true;
        }

        // A container's own inherited value means nothing once no child inherits it.
        return !ContainerElements.Contains(element.QualifiedName);
    }

    // overflow only applies to some elements; below the root the default style sheet already hides it, and on the root visible is the initial value.
    private static void RepairOverflow(DomElement element, DomElement root, OrderedDictionary<string, string> styles)
    {
        if (!styles.TryGetValue("overflow", out var overflow))
        {
            return;
        }

        if (element.QualifiedName is not ("svg" or "symbol" or "image" or "foreignObject" or "marker" or "pattern"))
        {
            styles.Remove("overflow");
        }
        else if (element != root)
        {
            if (overflow == "hidden")
            {
                styles.Remove("overflow");
            }
        }
        else if (overflow == "visible")
        {
            styles.Remove("overflow");
        }
    }

    private static bool MayContainText(DomElement element)
    {
        if (element.Namespace != SvgNamespace)
        {
            return true;
        }

        if (element.QualifiedName is "rect" or "circle" or "ellipse" or "line" or "polygon" or "polyline" or "path" or "image" or "stop")
        {
            return false;
        }

        if (element.QualifiedName is "g" or "clipPath" or "marker" or "mask" or "pattern" or "linearGradient" or "radialGradient" or "symbol")
        {
            return element.ChildElements.Any(MayContainText);
        }

        return true;
    }

    private static void ConvertColors(DomElement element)
    {
        var attributes = element.QualifiedName switch
        {
            "stop" => ["stop-color"],
            "solidColor" => ["solid-color"],
            var name when ShapeElements.Contains(name) => new[] { "fill", "stroke" },
            _ => Array.Empty<string>(),
        };

        var styles = ReadStyle(element);
        foreach (var attribute in attributes)
        {
            var old = element.GetAttribute(attribute);
            if (old.Length > 0)
            {
                var converted = ConvertColor(old);
                if (old.Length > converted.Length)
                {
                    element.SetAttribute(attribute, converted);
                }
            }

            if (styles.TryGetValue(attribute, out var styleValue))
            {
                var converted = ConvertColor(styleValue);
                if (styleValue.Length > converted.Length)
                {
                    styles[attribute] = converted;
                }
            }
        }

        WriteStyle(element, styles);
        foreach (var child in element.ChildElements)
        {
            ConvertColors(child);
        }
    }

    /// <summary>scour's <c>convertColor</c>: a colour as <c>#rrggbb</c>, or <c>#rgb</c> when that is exact.</summary>
    internal static string ConvertColor(string value)
    {
        var text = ScourTables.Colors.TryGetValue(value, out var named) ? named : value;
        var percent = PercentColor().Match(text);
        if (percent.Success)
        {
            var channels = Enumerable.Range(1, 3)
                .Select(i => (int)(double.Parse(percent.Groups[i].Value, CultureInfo.InvariantCulture) * 255.0 / 100.0));
            text = "#" + string.Concat(channels.Select(c => c.ToString("x2", CultureInfo.InvariantCulture)));
        }
        else
        {
            var plain = IntegerColor().Match(text);
            if (plain.Success)
            {
                var channels = Enumerable.Range(1, 3).Select(i => int.Parse(plain.Groups[i].Value, CultureInfo.InvariantCulture));
                text = "#" + string.Concat(channels.Select(c => c.ToString("x2", CultureInfo.InvariantCulture)));
            }
        }

        if (text[0] == '#')
        {
            text = text.ToLowerInvariant();
            if (text.Length == 7 && text[1] == text[2] && text[3] == text[4] && text[5] == text[6])
            {
                text = string.Concat("#", text[1].ToString(), text[3].ToString(), text[5].ToString());
            }
        }

        return text;
    }

    [GeneratedRegex(@"^\s*rgb\(\s*([0-9]*\.?[0-9]+)%\s*,\s*([0-9]*\.?[0-9]+)%\s*,\s*([0-9]*\.?[0-9]+)%\s*\)\s*", RegexOptions.CultureInvariant)]
    private static partial Regex PercentColor();

    [GeneratedRegex(@"^\s*rgb\(\s*([0-9]+)\s*,\s*([0-9]+)\s*,\s*([0-9]+)\s*\)\s*", RegexOptions.CultureInvariant)]
    private static partial Regex IntegerColor();

    [GeneratedRegex(@"\s*[\s,]\s*", RegexOptions.CultureInvariant)]
    private static partial Regex CommaOrSpace();

    private static int RemoveUnreferencedElements(DomElement root)
    {
        var count = 0;
        var identified = FindElementsWithId(root);
        var referenced = FindReferencedElements(root);

        foreach (var definitions in root.DescendantsNamed("defs"))
        {
            foreach (var element in UnusedDefinitions(definitions, referenced))
            {
                element.Remove();
                count++;
            }
        }

        foreach (var (id, goner) in identified)
        {
            if (!referenced.ContainsKey(id)
                && goner.QualifiedName is "linearGradient" or "radialGradient" or "pattern"
                && goner.Parent is { QualifiedName: not "defs" })
            {
                goner.Remove();
                count++;
            }
        }

        return count;
    }

    private static List<DomElement> UnusedDefinitions(DomElement definitions, OrderedDictionary<string, HashSet<DomElement>> referenced)
    {
        var unused = new List<DomElement>();
        foreach (var element in definitions.ChildElements)
        {
            var id = element.GetAttribute("id");
            if (id.Length > 0 && referenced.ContainsKey(id))
            {
                continue;
            }

            if (element.QualifiedName == "g" && element.Namespace == SvgNamespace)
            {
                unused.AddRange(UnusedDefinitions(element, referenced));
            }
            else if (element.QualifiedName is not ("font" or "style" or "metadata" or "script" or "title" or "desc"))
            {
                unused.Add(element);
            }
        }

        return unused;
    }

    private static OrderedDictionary<string, DomElement> FindElementsWithId(DomElement root)
    {
        var identified = new OrderedDictionary<string, DomElement>(StringComparer.Ordinal);
        foreach (var element in root.SelfAndDescendants())
        {
            var id = element.GetAttribute("id");
            if (id.Length > 0)
            {
                identified[id] = element;
            }
        }

        return identified;
    }

    /// <summary>Every id that something refers to (by xlink:href, a style or a presentation attribute, or a style sheet), with the elements that do.</summary>
    private static OrderedDictionary<string, HashSet<DomElement>> FindReferencedElements(DomElement root)
    {
        var ids = new OrderedDictionary<string, HashSet<DomElement>>(StringComparer.Ordinal);
        FindReferencedElements(root, ids);
        return ids;
    }

    private static void FindReferencedElements(DomElement node, OrderedDictionary<string, HashSet<DomElement>> ids)
    {
        // A style element's CSS is read as one stretch of text, and its references belong to the element itself.
        if (node.QualifiedName == "style" && node.Namespace == SvgNamespace)
        {
            var stylesheet = string.Concat(node.Children.OfType<DomText>().Select(t => t.Value));
            if (stylesheet.Length > 0)
            {
                foreach (var rule in stylesheet.Split('}'))
                {
                    var bits = rule.Split('{');
                    if (bits.Length != 2)
                    {
                        continue;
                    }

                    foreach (var declaration in bits[1].Trim().Split(';'))
                    {
                        var nibbles = declaration.Trim().Split(':');
                        if (nibbles.Length == 2)
                        {
                            FindReferencingProperty(node, nibbles[0].Trim(), nibbles[1].Trim(), ids);
                        }
                    }
                }
            }

            return;
        }

        var href = node.GetAttributeNS(XlinkNamespace, "href");
        if (href.Length > 1 && href[0] == '#')
        {
            AddReference(ids, href[1..], node);
        }

        foreach (var declaration in node.GetAttribute("style").Split(';'))
        {
            var parts = declaration.Split(':');
            if (parts.Length == 2)
            {
                FindReferencingProperty(node, parts[0].Trim(), parts[1].Trim(), ids);
            }
        }

        foreach (var property in ReferencingProperties)
        {
            var value = node.GetAttribute(property).Trim();
            if (value.Length > 0)
            {
                FindReferencingProperty(node, property, value, ids);
            }
        }

        foreach (var child in node.ChildElements)
        {
            FindReferencedElements(child, ids);
        }
    }

    private static void AddReference(OrderedDictionary<string, HashSet<DomElement>> ids, string id, DomElement node)
    {
        if (!ids.TryGetValue(id, out var nodes))
        {
            nodes = [];
            ids[id] = nodes;
        }

        nodes.Add(node);
    }

    private static void FindReferencingProperty(DomElement node, string property, string value, OrderedDictionary<string, HashSet<DomElement>> ids)
    {
        if (!ReferencingProperties.Contains(property) || value.Length == 0)
        {
            return;
        }

        if (value.Length >= 7 && value.StartsWith("url(#", StringComparison.Ordinal))
        {
            AddReference(ids, PythonSlice(value, 5, value.IndexOf(')', StringComparison.Ordinal)), node);
        }
        else if (value.Length >= 8)
        {
            if (value.StartsWith("url(\"#", StringComparison.Ordinal))
            {
                AddReference(ids, PythonSlice(value, 6, value.IndexOf("\")", StringComparison.Ordinal)), node);
            }
            else if (value.StartsWith("url('#", StringComparison.Ordinal))
            {
                AddReference(ids, PythonSlice(value, 6, value.IndexOf("')", StringComparison.Ordinal)), node);
            }
        }
    }

    // value[start:end] where end is a str.find() result, so -1 means "all but the last character".
    private static string PythonSlice(string value, int start, int end)
    {
        var stop = end < 0 ? value.Length - 1 : end;
        return stop <= start ? string.Empty : value[start..stop];
    }

    private static void RemoveEmptyContainers(DomElement root)
    {
        foreach (var tag in new[] { "defs", "title", "desc", "metadata", "g" })
        {
            foreach (var element in root.DescendantsNamed(tag))
            {
                if (IsEmptyContainer(element))
                {
                    element.Remove();
                }
            }
        }
    }

    private static bool IsEmptyContainer(DomElement element)
    {
        foreach (var child in element.Children)
        {
            if (child is DomElement or DomText { IsCData: true })
            {
                return false;
            }

            if (child is DomText text && !IsAllWhitespace(text.Value))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsAllWhitespace(string value) => value.Length > 0 && value.All(char.IsWhiteSpace);

    // Stops are rewritten to a plain number, and a stop that repeats the previous one at the same offset is dropped.
    private static int RemoveDuplicateGradientStops(DomElement root)
    {
        var count = 0;
        foreach (var type in new[] { "linearGradient", "radialGradient" })
        {
            foreach (var gradient in root.DescendantsNamed(type))
            {
                var stops = new Dictionary<double, (string Color, string Opacity, string Style)>();
                var toRemove = new List<DomElement>();
                foreach (var stop in gradient.DescendantsNamed("stop"))
                {
                    var length = SvgLength.Parse(stop.GetAttribute("offset"));
                    var offset = length.Unit switch
                    {
                        LengthUnit.Percent => length.Value / 100.0,
                        LengthUnit.None => length.Value,
                        _ => 0.0,
                    };
                    stop.SetAttribute("offset", offset == Math.Truncate(offset) ? PythonNumber.Truncate(offset) : PythonRepr(offset));
                    var entry = (stop.GetAttribute("stop-color"), stop.GetAttribute("stop-opacity"), stop.GetAttribute("style"));
                    if (stops.TryGetValue(offset, out var previous) && previous == entry)
                    {
                        toRemove.Add(stop);
                    }

                    stops[offset] = entry;
                }

                foreach (var stop in toRemove)
                {
                    stop.Remove();
                    count++;
                }
            }
        }

        return count;
    }

    private static string PythonRepr(double value) => value.ToString("R", CultureInfo.InvariantCulture).Replace('E', 'e');

    private static int RemoveNestedGroups(DomElement node)
    {
        var count = 0;
        var groups = new List<DomElement>();
        if (node.QualifiedName != "switch")
        {
            foreach (var child in node.ChildElements)
            {
                if (child.QualifiedName == "g" && child.Namespace == SvgNamespace && child.Attributes.Count == 0
                    && !child.ChildElements.Any(g => g.Namespace == SvgNamespace && g.QualifiedName is "title" or "desc"))
                {
                    groups.Add(child);
                }
            }
        }

        foreach (var group in groups)
        {
            while (group.Children.Count > 0)
            {
                node.InsertBefore(group.Children[0], group);
            }

            group.Remove();
            count++;
        }

        foreach (var child in node.ChildElements.ToList())
        {
            count += RemoveNestedGroups(child);
        }

        return count;
    }

    private static void ScourLengths(DomElement root)
    {
        var all = root.SelfAndDescendants().ToList();
        foreach (var type in LengthElements)
        {
            foreach (var element in all.Where(e => e.QualifiedName == type))
            {
                foreach (var attribute in LengthAttributes)
                {
                    var value = element.GetAttribute(attribute);
                    if (value.Length > 0)
                    {
                        element.SetAttribute(attribute, ScourNumbers.Length(value));
                    }
                }
            }
        }

        var viewBox = root.GetAttribute("viewBox");
        if (viewBox.Length > 0)
        {
            root.SetAttribute("viewBox", string.Join(' ', CommaOrSpace().Split(viewBox).Select(ScourNumbers.Unitless)));
        }
    }

    private static void ReducePrecision(DomElement element)
    {
        var styles = ReadStyle(element);
        foreach (var attribute in PrecisionAttributes)
        {
            var value = element.GetAttribute(attribute);
            if (value.Length > 0 && SvgLength.Parse(value).Unit != LengthUnit.Invalid)
            {
                var shorter = ScourNumbers.Length(value);
                if (shorter.Length < value.Length)
                {
                    element.SetAttribute(attribute, shorter);
                }
            }

            if (styles.TryGetValue(attribute, out var styleValue) && SvgLength.Parse(styleValue).Unit != LengthUnit.Invalid)
            {
                var shorter = ScourNumbers.Length(styleValue);
                if (shorter.Length < styleValue.Length)
                {
                    styles[attribute] = shorter;
                }
            }
        }

        WriteStyle(element, styles);
        foreach (var child in element.ChildElements)
        {
            ReducePrecision(child);
        }
    }

    private static void RemoveDefaultValues(DomElement node, HashSet<string> tainted)
    {
        foreach (var attribute in ScourDefaults.All.Where(a => a.Elements.Contains(node.QualifiedName)))
        {
            RemoveDefaultAttribute(node, attribute);
        }

        // A property that is not at its default is inherited by the children, so the same property at its default must stay there.
        foreach (var name in node.Attributes.Select(a => a.QualifiedName).ToList())
        {
            if (!tainted.Contains(name) && ScourTables.DefaultProperties.TryGetValue(name, out var defaultValue))
            {
                if (node.GetAttribute(name) == defaultValue)
                {
                    node.RemoveAttribute(name);
                }
                else
                {
                    Taint(tainted, name);
                }
            }
        }

        var styles = ReadStyle(node);
        foreach (var name in styles.Keys.ToList())
        {
            if (!tainted.Contains(name) && ScourTables.DefaultProperties.TryGetValue(name, out var defaultValue))
            {
                if (styles[name] == defaultValue)
                {
                    styles.Remove(name);
                }
                else
                {
                    Taint(tainted, name);
                }
            }
        }

        WriteStyle(node, styles);
        foreach (var child in node.ChildElements.ToList())
        {
            RemoveDefaultValues(child, [.. tainted]);
        }
    }

    private static void Taint(HashSet<string> tainted, string name)
    {
        tainted.Add(name);
        if (name == "marker")
        {
            tainted.UnionWith(["marker-start", "marker-mid", "marker-end"]);
        }

        if (name is "marker-start" or "marker-mid" or "marker-end")
        {
            tainted.Add("marker");
        }
    }

    private static void RemoveDefaultAttribute(DomElement node, DefaultAttribute attribute)
    {
        if (!node.HasAttribute(attribute.Name))
        {
            return;
        }

        var value = node.GetAttribute(attribute.Name);
        if (attribute.Text is not null)
        {
            if (value == attribute.Text && (attribute.Condition?.Invoke(node) ?? true))
            {
                node.RemoveAttribute(attribute.Name);
            }

            return;
        }

        var length = SvgLength.Parse(value);
        var valueMatches = attribute.Number is null || (length.Value == attribute.Number && length.Unit != LengthUnit.Invalid);
        if (valueMatches && (attribute.Unit is null || length.Unit == attribute.Unit) && (attribute.Condition?.Invoke(node) ?? true))
        {
            node.RemoveAttribute(attribute.Name);
        }
    }

    // scour reads a style attribute into an ordered map and writes it back whether or not it changed, which drops declarations it cannot split.
    private static OrderedDictionary<string, string> ReadStyle(DomElement element)
    {
        var styles = new OrderedDictionary<string, string>(StringComparer.Ordinal);
        var style = element.GetAttribute("style");
        if (style.Length == 0)
        {
            return styles;
        }

        foreach (var declaration in style.Split(';'))
        {
            var parts = declaration.Split(':');
            if (parts.Length == 2)
            {
                styles[parts[0].Trim()] = parts[1].Trim();
            }
        }

        return styles;
    }

    private static void WriteStyle(DomElement element, OrderedDictionary<string, string> styles)
    {
        var fixedStyle = string.Join(';', styles.Select(s => s.Key + ":" + s.Value));
        if (fixedStyle.Length > 0)
        {
            element.SetAttribute("style", fixedStyle);
        }
        else if (element.GetAttribute("style").Length > 0)
        {
            element.RemoveAttribute("style");
        }
    }
}
