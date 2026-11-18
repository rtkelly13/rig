using System.Xml;

namespace Rig.Image.Simplifier;

/// <summary>A node of the small document model the simplifier shares between its ElementTree-style and scour-style passes.</summary>
internal abstract class DomNode
{
    /// <summary>The element this node sits in, or null for the root and for a removed node.</summary>
    public DomElement? Parent { get; set; }
}

/// <summary>Character data. A CDATA section stays its own node so scour can write it back as CDATA; ElementTree merges it into the text.</summary>
internal sealed class DomText(string value, bool isCData) : DomNode
{
    /// <summary>The characters.</summary>
    public string Value { get; set; } = value;

    /// <summary>True for a CDATA section.</summary>
    public bool IsCData { get; } = isCData;
}

/// <summary>An attribute, namespace declarations included (as minidom keeps them).</summary>
internal sealed class DomAttribute(string prefix, string localName, string? ns, string value)
{
    /// <summary>The namespace of declarations: <c>xmlns</c> and <c>xmlns:prefix</c>.</summary>
    public const string XmlnsNamespace = "http://www.w3.org/2000/xmlns/";

    /// <summary>The source prefix, empty when there is none.</summary>
    public string Prefix { get; } = prefix;

    /// <summary>The name without the prefix.</summary>
    public string LocalName { get; } = localName;

    /// <summary>The namespace URI, or null for none.</summary>
    public string? Namespace { get; } = ns;

    /// <summary>The value.</summary>
    public string Value { get; set; } = value;

    /// <summary>The name as written, <c>prefix:local</c> or just the local name.</summary>
    public string QualifiedName => Prefix.Length > 0 ? $"{Prefix}:{LocalName}" : LocalName;

    /// <summary>True for <c>xmlns</c> and <c>xmlns:prefix</c>.</summary>
    public bool IsNamespaceDeclaration => Namespace == XmlnsNamespace || (Prefix.Length == 0 && LocalName == "xmlns");
}

/// <summary>An element, with the same lookups minidom gives scour: attributes by qualified name, an empty string for a missing one.</summary>
internal sealed class DomElement(string prefix, string localName, string? ns) : DomNode
{
    /// <summary>The source prefix, empty when there is none.</summary>
    public string Prefix { get; } = prefix;

    /// <summary>The name without the prefix.</summary>
    public string LocalName { get; } = localName;

    /// <summary>The namespace URI, or null for none.</summary>
    public string? Namespace { get; } = ns;

    /// <summary>The attributes in source order.</summary>
    public List<DomAttribute> Attributes { get; } = [];

    /// <summary>The children in order.</summary>
    public List<DomNode> Children { get; } = [];

    /// <summary>The name as written (minidom's nodeName).</summary>
    public string QualifiedName => Prefix.Length > 0 ? $"{Prefix}:{LocalName}" : LocalName;

    /// <summary>The child elements.</summary>
    public IEnumerable<DomElement> ChildElements => Children.OfType<DomElement>();

    /// <summary>This element and every element below it, in document order (ElementTree's <c>iter()</c>).</summary>
    public IEnumerable<DomElement> SelfAndDescendants()
    {
        yield return this;
        foreach (var child in ChildElements)
        {
            foreach (var below in child.SelfAndDescendants())
            {
                yield return below;
            }
        }
    }

    /// <summary>Every element below this one with the given qualified name, in document order (minidom's <c>getElementsByTagName</c>).</summary>
    public List<DomElement> DescendantsNamed(string qualifiedName) =>
        [.. SelfAndDescendants().Skip(1).Where(e => qualifiedName == "*" || e.QualifiedName == qualifiedName)];

    /// <summary>The attribute with this qualified name, or null.</summary>
    public DomAttribute? FindAttribute(string qualifiedName)
    {
        foreach (var attribute in Attributes)
        {
            if (attribute.QualifiedName == qualifiedName)
            {
                return attribute;
            }
        }

        return null;
    }

    /// <summary>True when the attribute is present.</summary>
    public bool HasAttribute(string qualifiedName) => FindAttribute(qualifiedName) is not null;

    /// <summary>The attribute value, or an empty string when absent (minidom's <c>getAttribute</c>).</summary>
    public string GetAttribute(string qualifiedName) => FindAttribute(qualifiedName)?.Value ?? string.Empty;

    /// <summary>Sets an attribute in place, or appends it with no namespace.</summary>
    public void SetAttribute(string qualifiedName, string value)
    {
        var existing = FindAttribute(qualifiedName);
        if (existing is not null)
        {
            existing.Value = value;
            return;
        }

        var colon = qualifiedName.IndexOf(':', StringComparison.Ordinal);
        Attributes.Add(colon < 0
            ? new DomAttribute(string.Empty, qualifiedName, null, value)
            : new DomAttribute(qualifiedName[..colon], qualifiedName[(colon + 1)..], null, value));
    }

    /// <summary>Removes an attribute by qualified name.</summary>
    public void RemoveAttribute(string qualifiedName) => Attributes.RemoveAll(a => a.QualifiedName == qualifiedName);

    /// <summary>Detaches this element from its parent.</summary>
    public void Remove()
    {
        Parent?.Children.Remove(this);
        Parent = null;
    }

    /// <summary>Appends a child.</summary>
    public void Append(DomNode child)
    {
        child.Parent = this;
        Children.Add(child);
    }

    /// <summary>Moves a node (taken from wherever it is) to sit just before a reference child.</summary>
    public void InsertBefore(DomNode node, DomNode reference)
    {
        node.Parent?.Children.Remove(node);
        node.Parent = this;
        Children.Insert(Children.IndexOf(reference), node);
    }
}

/// <summary>Parses XML into the <see cref="DomElement"/> model: comments, processing instructions and the doctype are dropped, as ElementTree drops them.</summary>
internal static class DomParser
{
    /// <summary>
    /// Parses the document element. Throws <see cref="XmlException"/> for anything that is not well formed, which the callers treat
    /// the way Python treats <c>ParseError</c>. A DOCTYPE is skipped, and an entity it declares is not defined, so a reference to it
    /// is an error here where defusedxml refuses the document outright.
    /// </summary>
    public static DomElement Parse(string text)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            IgnoreWhitespace = false,
            XmlResolver = null,
        };

        using var stringReader = new StringReader(text);
        using var reader = XmlReader.Create(stringReader, settings);
        DomElement? root = null;
        DomElement? current = null;
        while (reader.Read())
        {
            switch (reader.NodeType)
            {
                case XmlNodeType.Element:
                {
                    var element = ReadElement(reader);
                    var empty = reader.IsEmptyElement;
                    if (current is null)
                    {
                        root = element;
                    }
                    else
                    {
                        current.Append(element);
                    }

                    if (!empty)
                    {
                        current = element;
                    }

                    break;
                }

                case XmlNodeType.EndElement:
                    current = current?.Parent;
                    break;

                case XmlNodeType.Text:
                case XmlNodeType.Whitespace:
                case XmlNodeType.SignificantWhitespace:
                    AppendText(current, reader.Value, false);
                    break;

                case XmlNodeType.CDATA:
                    AppendText(current, reader.Value, true);
                    break;
            }
        }

        return root ?? throw new XmlException("The document has no root element.");
    }

    private static DomElement ReadElement(XmlReader reader)
    {
        var element = new DomElement(reader.Prefix, reader.LocalName, NullIfEmpty(reader.NamespaceURI));
        if (reader.MoveToFirstAttribute())
        {
            do
            {
                element.Attributes.Add(new DomAttribute(reader.Prefix, reader.LocalName, NullIfEmpty(reader.NamespaceURI), reader.Value));
            }
            while (reader.MoveToNextAttribute());
            reader.MoveToElement();
        }

        return element;
    }

    private static void AppendText(DomElement? parent, string value, bool isCData)
    {
        if (parent is null)
        {
            return;
        }

        // Expat hands minidom adjacent character data as one text node.
        if (!isCData && parent.Children.Count > 0 && parent.Children[^1] is DomText { IsCData: false } previous)
        {
            previous.Value += value;
            return;
        }

        parent.Append(new DomText(value, isCData));
    }

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;
}
