using System.Text.RegularExpressions;

namespace Rig.Core.C4;

/// <summary>The shape a Mermaid C4 element declares.</summary>
public enum ElementKind
{
    /// <summary>A person.</summary>
    Person,

    /// <summary>A system.</summary>
    System,

    /// <summary>A container.</summary>
    Container,

    /// <summary>A component.</summary>
    Component,

    /// <summary>A boundary holding other elements.</summary>
    Boundary,

    /// <summary>A deployment node.</summary>
    Node,

    /// <summary>A class in a class or flowchart diagram.</summary>
    Class,
}

/// <summary>One declared Mermaid shape.</summary>
/// <param name="Alias">Its Mermaid alias.</param>
/// <param name="Label">Its drawn label; the alias when none was written.</param>
/// <param name="Kind">The shape kind.</param>
/// <param name="Macro">The macro that declared it, such as <c>Container</c>.</param>
/// <param name="External">Whether the macro ends in <c>_Ext</c>.</param>
/// <param name="Link">Its <c>$link</c> or <c>click</c> target, or null.</param>
/// <param name="Parent">The alias of its enclosing boundary, or null.</param>
/// <param name="Line">Its zero-based line in the diagram source; -1 when only a link declared it.</param>
public sealed record Element(string Alias, string Label, ElementKind Kind, string Macro, bool External, string? Link, string? Parent, int Line);

/// <summary>One relationship drawn between two aliases.</summary>
/// <param name="Source">The relationship's source alias.</param>
/// <param name="Target">The relationship's target alias.</param>
/// <param name="Label">Its label, or an empty string.</param>
/// <param name="Line">Its zero-based line in the diagram source.</param>
public sealed record Relationship(string Source, string Target, string Label, int Line);

/// <summary>The structure of one Mermaid block.</summary>
/// <param name="Keyword">Its first word: <c>C4Context</c>, <c>classDiagram</c>, and so on.</param>
/// <param name="Source">The block's text.</param>
/// <param name="Elements">Every declared shape.</param>
/// <param name="Relationships">Every relationship.</param>
public sealed record Diagram(string Keyword, string Source, IReadOnlyList<Element> Elements, IReadOnlyList<Relationship> Relationships)
{
    /// <summary>The element called <paramref name="alias"/>, or null.</summary>
    public Element? Element(string alias) => Elements.FirstOrDefault(element => element.Alias == alias);

    /// <summary>Every alias the diagram declares, in declaration order.</summary>
    public IReadOnlyList<string> Aliases => [.. Elements.Select(element => element.Alias).Distinct(StringComparer.Ordinal)];
}

/// <summary>
/// Reads the structure out of Mermaid C4 source: elements, boundaries, relationships and their <c>$link</c> targets.
/// This is deliberately a line-level reader, not a Mermaid parser. Pele parses and renders the diagram in the browser;
/// all the bundle checks need is which aliases a diagram declares, how they nest, what they link to, and which
/// relationships it draws.
/// </summary>
public static partial class MermaidReader
{
    /// <summary>The C4 macros that nest at all.</summary>
    public static readonly IReadOnlySet<string> C4Keywords = new HashSet<string>(StringComparer.Ordinal)
    {
        "C4Context", "C4Container", "C4Component", "C4Dynamic", "C4Deployment",
    };

    private static readonly HashSet<string> Boundaries = new(StringComparer.Ordinal)
    {
        "Boundary", "Enterprise_Boundary", "System_Boundary", "Container_Boundary",
    };

    private static readonly HashSet<string> Nodes = new(StringComparer.Ordinal)
    {
        "Deployment_Node", "Node", "Node_L", "Node_R",
    };

    private static readonly HashSet<string> Relationships = new(StringComparer.Ordinal)
    {
        "Rel", "BiRel", "Rel_U", "Rel_Up", "Rel_D", "Rel_Down", "Rel_L", "Rel_Left", "Rel_R", "Rel_Right", "Rel_Back", "RelIndex",
    };

    private static readonly IReadOnlyList<(string Prefix, ElementKind Kind)> ShapePrefixes =
    [
        ("Person", ElementKind.Person),
        ("System", ElementKind.System),
        ("Container", ElementKind.Container),
        ("Component", ElementKind.Component),
    ];

    /// <summary>Read a Mermaid block. C4 macros give full structure; other diagrams only <c>click</c> links and classes.</summary>
    public static Diagram Parse(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var lines = OkfBundle.SplitLines(source);
        var first = lines.Select(line => line.Trim())
            .FirstOrDefault(line => line.Length > 0 && !line.StartsWith("%%", StringComparison.Ordinal));
        var keyword = first?.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
        if (!C4Keywords.Contains(keyword))
        {
            return new Diagram(keyword, source, ParseClicks(lines), []);
        }

        var elements = ParseC4(lines, out var relationships);
        return new Diagram(keyword, source, elements, relationships);
    }

    private static List<Element> ParseC4(string[] lines, out List<Relationship> relationships)
    {
        var elements = new List<Element>();
        relationships = [];
        var stack = new List<string>();
        for (var index = 0; index < lines.Length; index++)
        {
            var stripped = lines[index].Trim();
            if (stripped == "}")
            {
                if (stack.Count > 0)
                {
                    stack.RemoveAt(stack.Count - 1);
                }

                continue;
            }

            var match = Macro().Match(lines[index]);
            if (!match.Success)
            {
                continue;
            }

            var name = match.Groups["name"].Value;
            var args = ParseArgs(match.Groups["args"].Value);
            if (Relationships.Contains(name))
            {
                var offset = string.Equals(name, "RelIndex", StringComparison.Ordinal) ? 1 : 0;
                if (args.Positional.Count >= 2 + offset)
                {
                    relationships.Add(new Relationship(args.Positional[offset], args.Positional[offset + 1],
                        args.Positional.Count > 2 + offset ? args.Positional[2 + offset] : string.Empty, index));
                    continue;
                }
            }

            var kind = ShapeKind(name);
            if (kind is null || args.Positional.Count == 0)
            {
                continue;
            }

            var alias = args.Positional[0];
            elements.Add(new Element(alias, args.Positional.Count > 1 ? args.Positional[1] : alias, kind.Value, name,
                name.EndsWith("_Ext", StringComparison.Ordinal), args.Link, stack.Count > 0 ? stack[^1] : null, index));
            if (match.Groups["open"].Success)
            {
                stack.Add(alias);
            }
        }

        return elements;
    }

    private static List<Element> ParseClicks(string[] lines)
    {
        var links = new Dictionary<string, string>(StringComparer.Ordinal);
        var classes = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < lines.Length; index++)
        {
            var click = Click().Match(lines[index]);
            if (click.Success)
            {
                links.TryAdd(click.Groups["alias"].Value, click.Groups["href"].Value);
                continue;
            }

            var declared = Class().Match(lines[index]);
            if (declared.Success)
            {
                classes.TryAdd(declared.Groups["alias"].Value, index);
            }
        }

        var aliases = classes.Keys.Concat(links.Keys.Where(alias => !classes.ContainsKey(alias)));
        return [.. aliases.Select(alias => new Element(alias, alias, ElementKind.Class, "class", false,
            links.GetValueOrDefault(alias), null, classes.GetValueOrDefault(alias, -1)))];
    }

    private static ElementKind? ShapeKind(string name)
    {
        if (Boundaries.Contains(name))
        {
            return ElementKind.Boundary;
        }

        if (Nodes.Contains(name))
        {
            return ElementKind.Node;
        }

        foreach (var (prefix, kind) in ShapePrefixes)
        {
            if (name.StartsWith(prefix, StringComparison.Ordinal))
            {
                return kind;
            }
        }

        return null;
    }

    private sealed record Args(IReadOnlyList<string> Positional, string? Link);

    private static Args ParseArgs(string raw)
    {
        var positional = new List<string>();
        string? link = null;
        foreach (var part in SplitArgs(raw))
        {
            if (part.StartsWith('$', StringComparison.Ordinal))
            {
                var separator = part.IndexOf('=', StringComparison.Ordinal);
                var key = separator < 0 ? part : part[..separator];
                if (key.Trim() == "$link" && separator >= 0)
                {
                    link = Unquote(part[(separator + 1)..]);
                }

                continue;
            }

            positional.Add(Unquote(part));
        }

        return new Args(positional, link);
    }

    /// <summary>Split macro arguments on commas outside double quotes, stripping surrounding quotes from each.</summary>
    private static List<string> SplitArgs(string raw)
    {
        var parts = new List<string>();
        var current = new System.Text.StringBuilder();
        var quoted = false;
        foreach (var character in raw)
        {
            if (character == '"')
            {
                quoted = !quoted;
                current.Append(character);
            }
            else if (character == ',' && !quoted)
            {
                parts.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(character);
            }
        }

        parts.Add(current.ToString());
        return [.. parts.Select(part => part.Trim()).Where(part => part.Length > 0)];
    }

    private static string Unquote(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"' ? trimmed[1..^1] : trimmed;
    }

    [GeneratedRegex(@"^\s*(?<name>[A-Za-z_]+)\s*\((?<args>.*)\)\s*(?<open>\{)?\s*$")]
    private static partial Regex Macro();

    [GeneratedRegex(@"^\s*click\s+(?<alias>[\w-]+)\s+(?:href\s+)?""(?<href>[^""]+)""")]
    private static partial Regex Click();

    [GeneratedRegex(@"^\s*class\s+(?<alias>[\w-]+)")]
    private static partial Regex Class();
}
