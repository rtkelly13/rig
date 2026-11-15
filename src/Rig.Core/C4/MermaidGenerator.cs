using System.Globalization;
using System.Text.RegularExpressions;

namespace Rig.Core.C4;

/// <summary>
/// Writes the Mermaid blocks rig's derived C4 views use. Generation is pure string building so the same commit derives
/// the same bytes: container and component views are C4 macro diagrams, code views are class diagrams, and every
/// element can carry a bundle-absolute <c>$link</c>.
/// </summary>
public static partial class MermaidGenerator
{
    /// <summary>A Mermaid block holding <paramref name="lines"/>, ending with a newline.</summary>
    public static string Diagram(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        return "```mermaid\n" + string.Join('\n', lines) + "\n```\n";
    }

    /// <summary>A double-quoted Mermaid label with its quotes replaced, as the Python deriver wrote it.</summary>
    public static string Quote(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Replace("\"", "'", StringComparison.Ordinal);
    }

    /// <summary>A container or external container macro line.</summary>
    /// <param name="alias">The Mermaid alias.</param>
    /// <param name="name">The project name.</param>
    /// <param name="technology">Its technology, such as <c>library · net10.0</c>.</param>
    /// <param name="detail">The detail line, such as <c>12 source files</c>.</param>
    /// <param name="link">The bundle-relative view it zooms into without <c>.md</c>, such as <c>components/api</c>, or null.</param>
    /// <param name="external">Whether to write <c>Container_Ext</c>.</param>
    public static string Container(string alias, string name, string technology, string detail, string? link, bool external = false)
    {
        var target = link is null ? string.Empty : $", $link=\"/{link}.md\"";
        return $"{(external ? "Container_Ext" : "Container")}({alias}, \"{name}\", \"{technology}\", \"{detail}\"{target})";
    }

    /// <summary>A component macro line pointing at its code view.</summary>
    public static string Component(string alias, string label, IReadOnlyList<string> types, string codeId)
    {
        ArgumentNullException.ThrowIfNull(types);
        var shown = string.Join(", ", types.Take(4)) + (types.Count > 4 ? " …" : string.Empty);
        var count = $"{types.Count.ToString(CultureInfo.InvariantCulture)} type{(types.Count == 1 ? string.Empty : "s")}";
        return $"Component({alias}, \"{Quote(label)}\", \"{count}\", \"{Quote(shown)}\", $link=\"/{codeId}.md\")";
    }

    /// <summary>A relationship macro line.</summary>
    public static string Relationship(string source, string target, string label, string? technology = null) =>
        technology is null
            ? $"Rel({source}, {target}, \"{label}\")"
            : $"Rel({source}, {target}, \"{label}\", \"{technology}\")";

    /// <summary>A boundary opener; the caller writes its members and the closing brace.</summary>
    public static string Boundary(string macro, string alias, string label) => $"{macro}({alias}, \"{label}\") {{";

    /// <summary>A class diagram: each type, its kind and public members, then which types name each other.</summary>
    /// <param name="types">The declared types in drawing order.</param>
    /// <param name="edges">The type-to-type dependencies.</param>
    /// <param name="links">Each type and the URL its source opens at.</param>
    public static string ClassDiagram(
        IReadOnlyList<(TypeInfo Type, SourceFile File)> types,
        IReadOnlyList<(string Source, string Target)> edges,
        IReadOnlyList<(string Type, string Url)> links)
    {
        ArgumentNullException.ThrowIfNull(types);
        ArgumentNullException.ThrowIfNull(edges);
        ArgumentNullException.ThrowIfNull(links);
        var lines = new List<string> { "classDiagram", "direction LR" };
        foreach (var (type, _) in types)
        {
            var body = new List<string>();
            if (type.Kind != "class")
            {
                body.Add($"  <<{type.Kind}>>");
            }

            body.AddRange(type.Members.Select(member => $"  +{member}"));
            if (body.Count == 0)
            {
                lines.Add($"class {type.Name}");
                continue;
            }

            lines.Add($"class {type.Name} {{");
            lines.AddRange(body);
            lines.Add("}");
        }

        lines.AddRange(edges.Select(edge => $"{edge.Source} --> {edge.Target}"));
        lines.AddRange(links.Select(link => $"click {link.Type} href \"{link.Url}\""));
        return Diagram(lines);
    }

    /// <summary>The frontmatter rig's derivation writes: deterministic provenance and the <c>derived</c> tag.</summary>
    public static string Frontmatter(string kind, string title, string description, Origin origin, string? resource = null)
    {
        ArgumentNullException.ThrowIfNull(origin);
        return string.Join('\n',
        [
            "---",
            $"type: {kind}",
            $"title: {title}",
            $"description: {description}",
            $"resource: {resource ?? origin.Tree() ?? "unknown"}",
            $"generated: {{ by: {C4Deriver.Producer}, at: {origin.Date} }}",
            "tags: [derived]",
            "---",
            string.Empty,
        ]);
    }

    /// <summary>A slug made of lower-case letters, digits and underscores, the same mapping the Python deriver used.</summary>
    public static string Slug(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var slug = NonSlug().Replace(text.ToLowerInvariant(), "_").Trim('_');
        return slug.Length == 0 ? "root" : slug;
    }

    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex NonSlug();
}
