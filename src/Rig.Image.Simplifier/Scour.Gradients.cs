using System.Text.RegularExpressions;

namespace Rig.Image.Simplifier;

// scour's gradient passes: a gradient used by one other gradient folds into it, and identical gradients become one.
internal static partial class Scour
{
    private static readonly string[] GradientBucketAttributes =
        ["gradientUnits", "spreadMethod", "gradientTransform", "x1", "y1", "x2", "y2", "cx", "cy", "fx", "fy", "r"];

    private static readonly string[] GradientStopBucketAttributes = ["offset", "stop-color", "stop-opacity", "style"];

    private static bool IsGradient(DomElement element) => element.QualifiedName is "linearGradient" or "radialGradient" && element.Namespace == SvgNamespace;

    private static int CollapseSinglyReferencedGradients(DomElement root)
    {
        var count = 0;
        var identified = FindElementsWithId(root);
        foreach (var (id, nodes) in FindReferencedElements(root))
        {
            if (nodes.Count != 1 || !identified.TryGetValue(id, out var element) || !IsGradient(element))
            {
                continue;
            }

            var reference = nodes.Single();
            if (IsGradient(reference))
            {
                FoldGradient(element, reference);
                element.Remove();
                count++;
            }
        }

        return count;
    }

    // The gradient `element`, used only by `reference`, hands over its stops and any attribute `reference` leaves unset.
    private static void FoldGradient(DomElement element, DomElement reference)
    {
        if (reference.DescendantsNamed("stop").Count == 0)
        {
            foreach (var stop in element.DescendantsNamed("stop"))
            {
                reference.Adopt(stop);
            }
        }

        AdoptUnsetAttributes(element, reference, ["gradientUnits", "spreadMethod", "gradientTransform"]);
        if (element.QualifiedName == "radialGradient" && reference.QualifiedName == "radialGradient")
        {
            AdoptUnsetAttributes(element, reference, ["fx", "fy", "cx", "cy", "r"]);
        }

        if (element.QualifiedName == "linearGradient" && reference.QualifiedName == "linearGradient")
        {
            AdoptUnsetAttributes(element, reference, ["x1", "y1", "x2", "y2"]);
        }

        var targetHref = element.GetAttributeNS(XlinkNamespace, "href");
        if (targetHref.Length > 0)
        {
            // The reference must point where the folded gradient pointed, to keep the image the same.
            reference.SetAttributeNS(XlinkNamespace, "href", targetHref);
        }
        else if (reference.FindAttributeNS(XlinkNamespace, "href") is not null)
        {
            reference.RemoveAttributeNS(XlinkNamespace, "href");
        }
        else
        {
            // minidom raises NotFoundErr for a missing attribute, which the simplifier treats as "leave the document alone".
            throw new FormatException("The referencing gradient has no xlink:href to remove.");
        }
    }

    private static void AdoptUnsetAttributes(DomElement from, DomElement to, string[] names)
    {
        foreach (var name in names)
        {
            if (to.GetAttribute(name).Length == 0 && from.GetAttribute(name).Length > 0)
            {
                to.SetAttribute(name, from.GetAttribute(name));
            }
        }
    }

    // Two gradients with the same key are duplicates: the key is every attribute that decides how a gradient paints, and its stops.
    private static string GradientBucketKey(DomElement gradient)
    {
        var keys = GradientBucketAttributes.Select(gradient.GetAttribute).ToList();
        keys.Add(gradient.GetAttributeNS(XlinkNamespace, "href"));
        foreach (var stop in gradient.DescendantsNamed("stop"))
        {
            keys.AddRange(GradientStopBucketAttributes.Select(stop.GetAttribute));
        }

        return string.Join('\x1e', keys);
    }

    private static int RemoveDuplicateGradients(DomElement root)
    {
        var referenced = FindReferencedElements(root);
        var previous = -1;
        var count = 0;
        while (previous != count)
        {
            previous = count;
            foreach (var group in new[] { root.DescendantsNamed("linearGradient"), root.DescendantsNamed("radialGradient") })
            {
                var buckets = new OrderedDictionary<string, List<DomElement>>(StringComparer.Ordinal);
                foreach (var gradient in group)
                {
                    var key = GradientBucketKey(gradient);
                    if (!buckets.TryGetValue(key, out var bucket))
                    {
                        bucket = [];
                        buckets[key] = bucket;
                    }

                    bucket.Add(gradient);
                }

                foreach (var bucket in buckets.Values.Where(b => b.Count >= 2))
                {
                    MergeBucket(bucket, referenced);
                    count += bucket.Count - 1;
                }
            }
        }

        return count;
    }

    private static void MergeBucket(List<DomElement> bucket, OrderedDictionary<string, HashSet<DomElement>> referenced)
    {
        var master = bucket[0];
        var duplicates = bucket.Skip(1).ToList();
        var duplicateIds = duplicates.Select(d => d.GetAttribute("id")).ToList();
        var masterId = master.GetAttribute("id");
        if (masterId.Length == 0)
        {
            // A master with no id is no use to the elements that refer to the duplicates, so one that has an id takes its place.
            for (var i = 0; i < duplicateIds.Count; i++)
            {
                if (duplicateIds[i].Length > 0)
                {
                    masterId = duplicateIds[i];
                    duplicates[i] = master;
                    duplicateIds[i] = string.Empty;
                    break;
                }
            }
        }

        RemapDuplicates(masterId, duplicateIds, duplicates, referenced);
    }

    private static void RemapDuplicates(string masterId, List<string> duplicateIds, List<DomElement> duplicates, OrderedDictionary<string, HashSet<DomElement>> referenced)
    {
        Regex? pattern = null;
        for (var i = 0; i < duplicates.Count; i++)
        {
            var duplicate = duplicates[i];
            var id = duplicateIds[i];

            // A duplicate with no parent was already remapped to another master.
            if (duplicate.Parent is null)
            {
                continue;
            }

            if (referenced.TryGetValue(id, out var references))
            {
                // Matches url(#id), url('#id') and url("#id") for any of the duplicate ids.
                pattern ??= new Regex("url\\(['\"]?#(?:" + string.Join('|', duplicateIds) + ")['\"]?\\)", RegexOptions.CultureInvariant);
                var replacement = "url(#" + masterId + ")";
                foreach (var element in references)
                {
                    RemapReferences(element, pattern, replacement, id, masterId);
                }
            }

            duplicate.Remove();
        }

        if (masterId.Length == 0)
        {
            return;
        }

        if (!referenced.TryGetValue(masterId, out var masterReferences))
        {
            masterReferences = [];
        }

        foreach (var id in duplicateIds)
        {
            if (referenced.Remove(id, out var moved))
            {
                masterReferences.UnionWith(moved);
            }
        }

        referenced[masterId] = masterReferences;
    }

    private static void RemapReferences(DomElement element, Regex pattern, string replacement, string duplicateId, string masterId)
    {
        foreach (var attribute in new[] { "fill", "stroke" })
        {
            var value = element.GetAttribute(attribute);
            if (pattern.IsMatch(value))
            {
                element.SetAttribute(attribute, pattern.Replace(value, _ => replacement));
            }
        }

        if (element.GetAttributeNS(XlinkNamespace, "href") == "#" + duplicateId)
        {
            element.SetAttributeNS(XlinkNamespace, "href", "#" + masterId);
        }

        var styles = ReadStyle(element);
        foreach (var name in styles.Keys.ToList())
        {
            styles[name] = pattern.Replace(styles[name], _ => replacement);
        }

        WriteStyle(element, styles);
    }
}
