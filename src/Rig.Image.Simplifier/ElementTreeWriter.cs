using System.Text;

namespace Rig.Image.Simplifier;

/// <summary>
/// Python's <c>ElementTree.tostring(root, encoding="unicode")</c> with the SVG namespace registered as the default one. It is
/// reproduced rather than approximated because the simplifier's output is compared with the Python's byte for byte: declarations
/// first and sorted by prefix, attributes in source order, <c> /&gt;</c> for an empty element, and any namespace nobody registered
/// renamed <c>ns1</c>, <c>ns2</c> in order of first use (so <c>xlink:href</c> comes out <c>ns1:href</c>).
/// </summary>
internal static class ElementTreeWriter
{
    private const string SvgNamespace = "http://www.w3.org/2000/svg";

    private static readonly Dictionary<string, string> RegisteredPrefixes = new(StringComparer.Ordinal)
    {
        ["http://www.w3.org/XML/1998/namespace"] = "xml",
        ["http://www.w3.org/1999/xhtml"] = "html",
        ["http://www.w3.org/1999/02/22-rdf-syntax-ns#"] = "rdf",
        ["http://schemas.xmlsoap.org/wsdl/"] = "wsdl",
        ["http://www.w3.org/2001/XMLSchema"] = "xs",
        ["http://www.w3.org/2001/XMLSchema-instance"] = "xsi",
        ["http://purl.org/dc/elements/1.1/"] = "dc",
        [SvgNamespace] = string.Empty,
    };

    /// <summary>Serialises the element and everything below it.</summary>
    public static string Write(DomElement root)
    {
        var namespaces = new Dictionary<string, string>(StringComparer.Ordinal);
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var element in root.SelfAndDescendants())
        {
            AddName(names, namespaces, element.Namespace, element.LocalName);
            foreach (var attribute in element.Attributes.Where(a => !a.IsNamespaceDeclaration))
            {
                AddName(names, namespaces, attribute.Namespace, attribute.LocalName);
            }
        }

        var output = new StringBuilder();
        WriteElement(output, root, names, namespaces);
        return output.ToString();
    }

    private static string Key(string? ns, string localName) => ns is null ? localName : $"{{{ns}}}{localName}";

    private static void AddName(Dictionary<string, string> names, Dictionary<string, string> namespaces, string? ns, string localName)
    {
        var key = Key(ns, localName);
        if (names.ContainsKey(key))
        {
            return;
        }

        if (ns is null)
        {
            names[key] = localName;
            return;
        }

        if (!namespaces.TryGetValue(ns, out var prefix))
        {
            if (!RegisteredPrefixes.TryGetValue(ns, out prefix))
            {
                prefix = $"ns{namespaces.Count}";
            }

            if (prefix != "xml")
            {
                namespaces[ns] = prefix;
            }
        }

        names[key] = prefix.Length > 0 ? $"{prefix}:{localName}" : localName;
    }

    private static void WriteElement(StringBuilder output, DomElement element, Dictionary<string, string> names, Dictionary<string, string>? declarations)
    {
        var tag = names[Key(element.Namespace, element.LocalName)];
        output.Append('<').Append(tag);
        if (declarations is not null)
        {
            foreach (var declaration in declarations.OrderBy(d => d.Value, StringComparer.Ordinal))
            {
                output.Append(" xmlns");
                if (declaration.Value.Length > 0)
                {
                    output.Append(':').Append(declaration.Value);
                }

                output.Append("=\"").Append(EscapeAttribute(declaration.Key)).Append('"');
            }
        }

        foreach (var attribute in element.Attributes.Where(a => !a.IsNamespaceDeclaration))
        {
            output.Append(' ').Append(names[Key(attribute.Namespace, attribute.LocalName)]).Append("=\"").Append(EscapeAttribute(attribute.Value)).Append('"');
        }

        // The text before the first child element is the element's text; every run after a child element is that child's tail.
        var hasElementChild = element.Children.Any(c => c is DomElement);
        var leading = new StringBuilder();
        foreach (var child in element.Children)
        {
            if (child is DomText text)
            {
                leading.Append(text.Value);
            }
            else
            {
                break;
            }
        }

        if (leading.Length == 0 && !hasElementChild)
        {
            output.Append(" />");
            return;
        }

        output.Append('>').Append(EscapeText(leading.ToString()));
        foreach (var child in element.Children.Skip(element.Children.TakeWhile(c => c is DomText).Count()))
        {
            switch (child)
            {
                case DomElement below:
                    WriteElement(output, below, names, null);
                    break;
                case DomText tail:
                    output.Append(EscapeText(tail.Value));
                    break;
            }
        }

        output.Append("</").Append(tag).Append('>');
    }

    private static string EscapeText(string text) => text.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal);

    private static string EscapeAttribute(string text) =>
        EscapeText(text)
            .Replace("\"", "&quot;", StringComparison.Ordinal)
            .Replace("\r", "&#13;", StringComparison.Ordinal)
            .Replace("\n", "&#10;", StringComparison.Ordinal)
            .Replace("\t", "&#09;", StringComparison.Ordinal);
}
