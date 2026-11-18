using System.Text;

namespace Rig.Image.Simplifier;

/// <summary>Scour's own XML serialiser: attributes in scour's order, no indentation, a newline between element children, text trimmed.</summary>
internal static class ScourWriter
{
    private static readonly HashSet<string> TextContentElements = new(StringComparer.Ordinal)
    {
        "text", "tspan", "tref", "textPath", "altGlyph", "flowDiv", "flowPara", "flowSpan", "flowTref", "flowLine",
    };

    private static readonly Dictionary<string, int> AttributeOrder = BuildAttributeOrder();

    /// <summary>Serialises the element and everything below it.</summary>
    public static string Write(DomElement root)
    {
        var output = new StringBuilder();
        WriteElement(output, root, false);
        return output.ToString();
    }

    private static Dictionary<string, int> BuildAttributeOrder()
    {
        string[] known =
        [
            "id", "xml:id", "class", "transform", "x", "y", "z", "width", "height", "x1", "x2", "y1", "y2", "dx", "dy", "rotate",
            "startOffset", "method", "spacing", "cx", "cy", "r", "rx", "ry", "fx", "fy", "d", "points",
            .. ScourTables.SvgAttributes,
            "style",
        ];
        var order = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < known.Length; i++)
        {
            order[known[i]] = i;
        }

        return order;
    }

    private static void WriteElement(StringBuilder output, DomElement element, bool preserveWhitespace)
    {
        output.Append('<').Append(element.QualifiedName);
        var attributes = element.Attributes
            .Select(a => (Attribute: a, Order: AttributeOrder.GetValueOrDefault(a.QualifiedName, int.MaxValue)))
            .OrderBy(a => a.Order)
            .ThenBy(a => a.Attribute.QualifiedName, StringComparer.Ordinal)
            .Select(a => a.Attribute);
        foreach (var attribute in attributes)
        {
            var (quote, escapes) = ChooseQuote(attribute.Value);
            var value = MakeWellFormed(attribute.Value, escapes);
            if (attribute.QualifiedName == "style")
            {
                var declarations = value.Split(';');
                Array.Sort(declarations, StringComparer.Ordinal);
                value = string.Join(';', declarations);
            }

            output.Append(' ').Append(attribute.QualifiedName).Append('=').Append(quote).Append(value).Append(quote);
            if (attribute.QualifiedName == "xml:space")
            {
                if (value == "preserve")
                {
                    preserveWhitespace = true;
                }
                else if (value == "default")
                {
                    preserveWhitespace = false;
                }
            }
        }

        if (element.Children.Count == 0)
        {
            output.Append("/>");
            return;
        }

        output.Append('>');
        var inTextElement = TextContentElements.Contains(element.QualifiedName);
        var onNewLine = false;
        foreach (var child in element.Children)
        {
            switch (child)
            {
                case DomElement below:
                    // Inside text content a newline or indent is a visible space, so none is added.
                    if (preserveWhitespace || inTextElement)
                    {
                        WriteElement(output, below, preserveWhitespace);
                    }
                    else
                    {
                        output.Append('\n');
                        WriteElement(output, below, preserveWhitespace);
                        onNewLine = true;
                    }

                    break;
                case DomText { IsCData: true } cdata:
                    output.Append("<![CDATA[").Append(cdata.Value).Append("]]>");
                    break;
                case DomText text:
                    output.Append(MakeWellFormed(TextContent(element, text, inTextElement, preserveWhitespace), TextEscapes));
                    break;
            }
        }

        if (onNewLine)
        {
            output.Append('\n');
        }

        output.Append("</").Append(element.QualifiedName).Append('>');
    }

    private static string TextContent(DomElement element, DomText text, bool inTextElement, bool preserveWhitespace)
    {
        var content = text.Value;
        if (preserveWhitespace)
        {
            return content;
        }

        if (!inTextElement)
        {
            return content.Trim();
        }

        content = content.Replace("\n", string.Empty, StringComparison.Ordinal).Replace('\t', ' ');
        if (ReferenceEquals(text, element.Children[0]))
        {
            content = content.TrimStart();
        }
        else if (ReferenceEquals(text, element.Children[^1]))
        {
            content = content.TrimEnd();
        }

        while (content.Contains("  ", StringComparison.Ordinal))
        {
            content = content.Replace("  ", " ", StringComparison.Ordinal);
        }

        return content;
    }

    private static readonly Dictionary<char, string> TextEscapes = new() { ['<'] = "&lt;", ['>'] = "&gt;", ['&'] = "&amp;" };
    private static readonly Dictionary<char, string> QuoteEscapes = new() { ['<'] = "&lt;", ['>'] = "&gt;", ['&'] = "&amp;", ['"'] = "&quot;" };
    private static readonly Dictionary<char, string> ApostropheEscapes = new() { ['<'] = "&lt;", ['>'] = "&gt;", ['&'] = "&amp;", ['\''] = "&apos;" };

    private static (char Quote, Dictionary<char, string> Escapes) ChooseQuote(string value)
    {
        var doubles = value.Count(c => c == '"');
        if (doubles == 0 || doubles <= value.Count(c => c == '\''))
        {
            return ('"', QuoteEscapes);
        }

        return ('\'', ApostropheEscapes);
    }

    private static string MakeWellFormed(string text, Dictionary<char, string> escapes)
    {
        if (!text.Any(escapes.ContainsKey))
        {
            return text;
        }

        var output = new StringBuilder(text.Length + 8);
        foreach (var c in text)
        {
            output.Append(escapes.TryGetValue(c, out var replacement) ? replacement : c);
        }

        return output.ToString();
    }
}
