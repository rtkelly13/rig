namespace Rig.Image.Simplifier;

// scour's group passes: sibling groups with the same attributes merge, and inherited attributes move up to the parent that can hold them.
// createGroupsForCommonAttributes is not ported: scour only runs it with group_create, which the simplifier leaves off.
internal static partial class Scour
{
    // Most of the inheritable properties from the SVG 1.1 property index and the SVG Tiny 1.2 attribute table.
    private static readonly HashSet<string> InheritableAttributes = new(StringComparer.Ordinal)
    {
        "clip-rule", "display-align", "fill", "fill-opacity", "fill-rule", "font", "font-family", "font-size", "font-size-adjust", "font-stretch",
        "font-style", "font-variant", "font-weight", "letter-spacing", "pointer-events", "shape-rendering", "stroke", "stroke-dasharray",
        "stroke-dashoffset", "stroke-linecap", "stroke-linejoin", "stroke-miterlimit", "stroke-opacity", "stroke-width", "text-anchor",
        "text-decoration", "text-rendering", "visibility", "word-spacing", "writing-mode",
    };

    private static readonly string[] AnimationElements = ["set", "animate", "animateColor", "animateTransform", "animateMotion"];

    private static bool IsSvgGroup(DomNode node) => node is DomElement { QualifiedName: "g", Namespace: SvgNamespace };

    private static Dictionary<string, string> AttributeMap(DomElement element) =>
        element.Attributes.GroupBy(a => a.QualifiedName).ToDictionary(g => g.Key, g => g.Last().Value, StringComparer.Ordinal);

    private static bool SameAttributes(Dictionary<string, string> left, Dictionary<string, string> right) =>
        left.Count == right.Count && left.All(pair => right.TryGetValue(pair.Key, out var value) && value == pair.Value);

    // Merges each run of sibling <g> elements that carry identical attributes (text between them goes along), then recurses.
    private static void MergeSiblingGroups(DomElement element)
    {
        var children = element.Children;
        var i = children.Count - 1;
        while (i >= 0)
        {
            if (!IsSvgGroup(children[i]))
            {
                i--;
                continue;
            }

            var attributes = AttributeMap((DomElement)children[i]);
            if (attributes.Count == 0)
            {
                i--;
                continue;
            }

            var runStart = i;
            var runEnd = i;
            var runElements = 1;
            while (runStart > 0)
            {
                var next = children[runStart - 1];
                if (next is DomElement nextElement)
                {
                    if (!IsSvgGroup(next))
                    {
                        break;
                    }

                    var hasNoMergeTags = nextElement.ChildElements.Any(n => n.QualifiedName is "title" or "desc" && n.Namespace == SvgNamespace);
                    if (!SameAttributes(attributes, AttributeMap(nextElement)) || hasNoMergeTags)
                    {
                        break;
                    }

                    runElements++;
                }

                runStart--;
            }

            // The next round starts before the run.
            i = runStart - 1;
            if (runElements < 2)
            {
                continue;
            }

            // The run may have started on text; its first group is the one the rest merge into.
            while (!IsSvgGroup(children[runStart]))
            {
                runStart++;
            }

            var primary = (DomElement)children[runStart];
            runStart++;
            foreach (var node in children.GetRange(runStart, runEnd + 1 - runStart))
            {
                if (IsSvgGroup(node))
                {
                    foreach (var child in ((DomElement)node).Children.ToList())
                    {
                        primary.Adopt(child);
                    }

                    ((DomElement)node).Remove();
                }
                else
                {
                    primary.Adopt(node);
                }
            }
        }

        foreach (var child in element.ChildElements.ToList())
        {
            MergeSiblingGroups(child);
        }
    }

    // Moves the inheritable attributes that all the children share up to their parent. Children that other elements refer to stay as they are.
    private static void MoveCommonAttributesToParent(DomElement element, OrderedDictionary<string, HashSet<DomElement>> referenced)
    {
        var childElements = new List<DomElement>();
        foreach (var child in element.Children.ToList())
        {
            if (child is DomElement childElement)
            {
                if (!referenced.ContainsKey(childElement.GetAttribute("id")))
                {
                    childElements.Add(childElement);
                    MoveCommonAttributesToParent(childElement, referenced);
                }
            }
            else if (child is DomText { IsCData: false } text && text.Value.Trim().Length > 0)
            {
                // Text among the children: attributes must stay where they are.
                return;
            }
        }

        if (childElements.Count <= 1)
        {
            return;
        }

        var common = new OrderedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var attribute in childElements[0].Attributes.Where(a => InheritableAttributes.Contains(a.QualifiedName)))
        {
            common[attribute.QualifiedName] = attribute.Value;
        }

        foreach (var child in childElements.Skip(1))
        {
            if (AnimationElements.Contains(child.LocalName))
            {
                continue;
            }

            foreach (var name in common.Keys.Where(name => child.GetAttribute(name) != common[name]).ToList())
            {
                common.Remove(name);
            }
        }

        foreach (var (name, value) in common)
        {
            foreach (var child in childElements)
            {
                child.RemoveAttribute(name);
            }

            element.SetAttribute(name, value);
        }
    }

    // A parent's inheritable attribute that no child inherits does nothing, so it goes.
    private static void RemoveUnusedAttributesOnParent(DomElement element)
    {
        var childElements = element.ChildElements.ToList();
        foreach (var child in childElements)
        {
            RemoveUnusedAttributesOnParent(child);
        }

        if (childElements.Count <= 1)
        {
            return;
        }

        var unused = element.Attributes.Where(a => InheritableAttributes.Contains(a.QualifiedName)).Select(a => a.QualifiedName).ToHashSet(StringComparer.Ordinal);
        foreach (var child in childElements)
        {
            unused.RemoveWhere(name => child.GetAttribute(name) is "" or "inherit");
        }

        foreach (var name in unused)
        {
            element.RemoveAttribute(name);
        }
    }
}
