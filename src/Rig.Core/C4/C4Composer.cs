using System.Text.RegularExpressions;

namespace Rig.Core.C4;

/// <summary>How one concept links to another.</summary>
public enum EdgeKind
{
    /// <summary>A refinement one level deeper.</summary>
    Zoom,

    /// <summary>A link back up to a shallower level.</summary>
    ZoomOut,

    /// <summary>A plain mention in the notes.</summary>
    Mention,
}

/// <summary>One link between concepts.</summary>
/// <param name="Source">The linking concept.</param>
/// <param name="Target">The linked concept.</param>
/// <param name="Kind">The link kind.</param>
/// <param name="Alias">The diagram element carrying a zoom link, or null.</param>
/// <param name="Href">The link target as written.</param>
public sealed record Edge(string Source, string Target, EdgeKind Kind, string? Alias, string Href);

/// <summary>A link that should have resolved inside the bundle but did not.</summary>
/// <param name="Source">The linking concept.</param>
/// <param name="Href">The link target as written.</param>
/// <param name="Alias">The diagram element carrying it, or null for a note link.</param>
public sealed record BrokenLink(string Source, string Href, string? Alias);

/// <summary>Every resolved and broken link in a bundle.</summary>
/// <param name="Edges">The links that resolved.</param>
/// <param name="Broken">The links that did not.</param>
public sealed record Links(IReadOnlyList<Edge> Edges, IReadOnlyList<BrokenLink> Broken)
{
    /// <summary>The zoom links leaving <paramref name="conceptId"/>.</summary>
    public IEnumerable<Edge> ZoomsFrom(string conceptId) =>
        Edges.Where(edge => edge.Source == conceptId && edge.Kind == EdgeKind.Zoom);

    /// <summary>Every link arriving at <paramref name="conceptId"/>.</summary>
    public IEnumerable<Edge> Into(string conceptId) => Edges.Where(edge => edge.Target == conceptId);
}

/// <summary>How serious a bundle finding is.</summary>
public enum Severity
{
    /// <summary>A broken bundle.</summary>
    Error,

    /// <summary>A likely mistake.</summary>
    Warning,

    /// <summary>Worth knowing.</summary>
    Info,
}

/// <summary>One bundle-check result.</summary>
/// <param name="Code">Its stable code, such as <c>zoom-broken</c>.</param>
/// <param name="Severity">How serious it is.</param>
/// <param name="Concept">The concept it concerns.</param>
/// <param name="Message">A sentence a reader can act on.</param>
public sealed record Finding(string Code, Severity Severity, string Concept, string Message);

/// <summary>One alias across every view that declares it.</summary>
/// <param name="Alias">The alias.</param>
/// <param name="Labels">Every label it is drawn with, in first-seen order.</param>
/// <param name="Kinds">Every shape kind it is drawn as, in first-seen order.</param>
/// <param name="External">Whether any view draws it as external.</param>
/// <param name="Views">The concepts it appears in, in bundle order.</param>
/// <param name="ZoomsTo">The concepts it zooms into.</param>
public sealed record ModelElement(string Alias, IReadOnlyList<string> Labels, IReadOnlyList<ElementKind> Kinds, bool External, IReadOnlyList<string> Views, IReadOnlyList<string> ZoomsTo);

/// <summary>One relationship across every view that draws it.</summary>
/// <param name="Source">The source alias.</param>
/// <param name="Target">The target alias.</param>
/// <param name="Labels">Every label, in first-seen order.</param>
/// <param name="Views">The concepts that draw it, in bundle order.</param>
public sealed record ModelRelationship(string Source, string Target, IReadOnlyList<string> Labels, IReadOnlyList<string> Views);

/// <summary>The merged model: every alias and relationship across all views.</summary>
/// <param name="Elements">The elements, sorted by alias.</param>
/// <param name="Relationships">The relationships, in first-seen order.</param>
public sealed record C4Model(IReadOnlyList<ModelElement> Elements, IReadOnlyList<ModelRelationship> Relationships);

/// <summary>One node in the zoom tree.</summary>
/// <param name="Id">The concept id.</param>
/// <param name="Title">Its title.</param>
/// <param name="Type">Its OKF type, or null.</param>
/// <param name="Via">The alias in the parent that zooms here, or null at a root.</param>
/// <param name="Repeat">Whether this concept was already shown higher up the walk.</param>
/// <param name="Children">What it zooms into.</param>
public sealed record TreeNode(string Id, string Title, string? Type, string? Via, bool Repeat, IReadOnlyList<TreeNode> Children);

/// <summary>An expansion the bundle cannot satisfy.</summary>
public sealed class ExpandException : Exception
{
    /// <summary>An expansion failure with no reason given.</summary>
    public ExpandException()
    {
    }

    /// <summary>An expansion failure.</summary>
    /// <param name="message">What is wrong, as the CLI prints it.</param>
    public ExpandException(string message)
        : base(message)
    {
    }

    /// <summary>An expansion failure caused by another exception.</summary>
    /// <param name="message">What is wrong.</param>
    /// <param name="innerException">Its cause.</param>
    public ExpandException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// How a bundle's documents compose: zoom edges, the merged element model, bundle checks and expanded views.
/// Composition happens three ways, all derived from plain OKF links and Mermaid aliases:
/// <list type="bullet">
/// <item><b>Zoom</b> — an element's <c>$link</c> names the concept that refines it, making the hierarchy a DAG.</item>
/// <item><b>Identity</b> — an alias used in several diagrams is one element seen in several views.</item>
/// <item><b>Expansion</b> — a parent with one zoomed element replaced by the boundary of its child view.</item>
/// </list>
/// </summary>
public static partial class C4Composer
{
    /// <summary>Classify and collect every link in <paramref name="bundle"/>.</summary>
    public static Links CollectLinks(Bundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        var edges = new List<Edge>();
        var broken = new List<BrokenLink>();
        foreach (var concept in bundle.Concepts.Values)
        {
            if (concept.Diagram is not null)
            {
                foreach (var element in concept.Diagram.Elements)
                {
                    if (string.IsNullOrEmpty(element.Link))
                    {
                        continue;
                    }

                    var target = OkfBundle.ResolveHref(concept.Id, element.Link);
                    if (target is null && !OkfBundle.IsExternalOrAnchor(element.Link))
                    {
                        broken.Add(new BrokenLink(concept.Id, element.Link, element.Alias));
                    }
                    else if (target is not null && bundle.Concepts.ContainsKey(target))
                    {
                        edges.Add(new Edge(concept.Id, target, Direction(bundle, concept.Id, target), element.Alias, element.Link));
                    }
                    else if (target is not null)
                    {
                        broken.Add(new BrokenLink(concept.Id, element.Link, element.Alias));
                    }
                }
            }

            foreach (var link in concept.BodyLinks)
            {
                if (link.Target is null)
                {
                    continue;
                }

                if (bundle.Concepts.ContainsKey(link.Target))
                {
                    edges.Add(new Edge(concept.Id, link.Target, EdgeKind.Mention, null, link.Href));
                }
                else if (link.Href.EndsWith(".md", StringComparison.Ordinal) || link.Href.Contains(".md#", StringComparison.Ordinal))
                {
                    broken.Add(new BrokenLink(concept.Id, link.Href, null));
                }
            }
        }

        return new Links(edges, broken);
    }

    private static EdgeKind Direction(Bundle bundle, string source, string target)
    {
        var from = bundle.Get(source)?.Level;
        var to = bundle.Get(target)?.Level;
        return from is not null && to is not null && to < from ? EdgeKind.ZoomOut : EdgeKind.Zoom;
    }

    /// <summary>Entry points: context-level concepts, then any diagram nothing zooms into.</summary>
    public static IReadOnlyList<string> Roots(Bundle bundle, Links? links = null)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        var collected = links ?? CollectLinks(bundle);
        var zoomed = collected.Edges.Where(edge => edge.Kind == EdgeKind.Zoom).Select(edge => edge.Target).ToHashSet(StringComparer.Ordinal);
        var context = bundle.Concepts.Values.Where(concept => concept.Level == 1).Select(concept => concept.Id).Order(StringComparer.Ordinal);
        var loose = bundle.Concepts.Values
            .Where(concept => concept.IsC4 && concept.Level != 1 && !zoomed.Contains(concept.Id))
            .Select(concept => concept.Id)
            .Order(StringComparer.Ordinal);
        return [.. context.Concat(loose)];
    }

    /// <summary>Check OKF conformance, zoom links, level descent, orphans, fan-in and cross-view consistency.</summary>
    public static IReadOnlyList<Finding> Check(Bundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        var links = CollectLinks(bundle);
        var findings = new List<Finding>();
        foreach (var concept in bundle.Concepts.Values)
        {
            findings.AddRange(OkfFindings(concept));
            findings.AddRange(DiagramFindings(concept));
            if (concept.IsC4 && concept.Verified is null)
            {
                findings.Add(new Finding("unverified", Severity.Info, concept.Id, "no `verified` — nobody has checked it"));
            }
        }

        findings.AddRange(ZoomFindings(bundle, links));
        findings.AddRange(ReachabilityFindings(bundle, links));
        findings.AddRange(RefinementFindings(bundle, links));
        findings.AddRange(IdentityFindings(Build(bundle, links)));
        return [.. findings.OrderBy(finding => finding.Severity).ThenBy(finding => finding.Concept, StringComparer.Ordinal).ThenBy(finding => finding.Code, StringComparer.Ordinal)];
    }

    private static IEnumerable<Finding> OkfFindings(Concept concept)
    {
        if (concept.FrontmatterError is not null)
        {
            return [new Finding("okf-frontmatter", Severity.Error, concept.Id, concept.FrontmatterError)];
        }

        return string.IsNullOrEmpty(concept.Type)
            ? [new Finding("okf-type", Severity.Error, concept.Id, "OKF requires a `type` in the frontmatter")]
            : [];
    }

    private static IEnumerable<Finding> DiagramFindings(Concept concept)
    {
        if (!concept.IsC4)
        {
            return [];
        }

        if (concept.Diagram is null)
        {
            return [new Finding("diagram-missing", Severity.Warning, concept.Id, $"`{concept.Type}` has no mermaid block")];
        }

        var expected = OkfBundle.DiagramKeywords.GetValueOrDefault(concept.Type ?? string.Empty);
        var keyword = concept.Diagram.Keyword;
        if (expected is not null && keyword != expected)
        {
            return [new Finding("diagram-kind", Severity.Warning, concept.Id, $"`type: {concept.Type}` but the diagram is `{keyword}`; expected `{expected}`")];
        }

        return concept.Level == 4 && MermaidReader.C4Keywords.Contains(keyword)
            ? [new Finding("diagram-kind", Severity.Info, concept.Id, "code level is usually a class or ER diagram, not a C4 macro diagram")]
            : [];
    }

    private static List<Finding> ZoomFindings(Bundle bundle, Links links)
    {
        var findings = links.Broken.Select(broken => new Finding(
            broken.Alias is not null ? "zoom-broken" : "link-broken",
            broken.Alias is not null ? Severity.Error : Severity.Warning,
            broken.Source,
            broken.Alias is not null
                ? $"`{broken.Alias}` zooms to `{broken.Href}`, which is not in the bundle"
                : $"note links to `{broken.Href}`, which is not in the bundle")).ToList();
        foreach (var edge in links.Edges.Where(edge => edge.Kind == EdgeKind.Zoom))
        {
            var source = bundle.Get(edge.Source);
            var target = bundle.Get(edge.Target);
            if (source?.Level is null || target?.Level is null)
            {
                continue;
            }

            if (target.Level != source.Level + 1)
            {
                findings.Add(new Finding("zoom-level", Severity.Warning, edge.Source,
                    $"`{edge.Alias}` zooms from {source.Type} to {target.Type}; a zoom normally goes one level deeper"));
            }
        }

        return findings;
    }

    private static List<Finding> ReachabilityFindings(Bundle bundle, Links links)
    {
        var findings = new List<Finding>();
        var zoomSources = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        var anyInbound = new HashSet<string>(StringComparer.Ordinal);
        foreach (var edge in links.Edges)
        {
            anyInbound.Add(edge.Target);
            if (edge.Kind == EdgeKind.Zoom)
            {
                if (!zoomSources.TryGetValue(edge.Target, out var sources))
                {
                    sources = new SortedSet<string>(StringComparer.Ordinal);
                    zoomSources[edge.Target] = sources;
                }

                sources.Add(edge.Source);
            }
        }

        foreach (var concept in bundle.Concepts.Values)
        {
            var sources = zoomSources.GetValueOrDefault(concept.Id);
            if (concept.Level is not null && concept.Level > 1 && (sources is null || sources.Count == 0))
            {
                findings.Add(new Finding("orphan", Severity.Warning, concept.Id,
                    $"no diagram zooms into this {concept.Type}; it is only reachable by search"));
            }
            else if (!concept.IsC4 && !anyInbound.Contains(concept.Id))
            {
                findings.Add(new Finding("orphan", Severity.Info, concept.Id, "no concept links to this note"));
            }

            if (sources is { Count: > 1 })
            {
                findings.Add(new Finding("fan-in", Severity.Info, concept.Id,
                    $"shared by {sources.Count} views: {string.Join(", ", sources)}"));
            }
        }

        return findings;
    }

    /// <summary>A relationship drawn to a zoomed element in the parent should still be visible in the child view.</summary>
    private static IEnumerable<Finding> RefinementFindings(Bundle bundle, Links links)
    {
        foreach (var edge in links.Edges.Where(edge => edge.Kind == EdgeKind.Zoom && edge.Alias is not null))
        {
            var parent = bundle.Get(edge.Source)?.Diagram;
            var child = bundle.Get(edge.Target)?.Diagram;
            if (parent is null || child is null || !MermaidReader.C4Keywords.Contains(child.Keyword))
            {
                continue;
            }

            foreach (var rel in parent.Relationships)
            {
                var draws = rel.Source == edge.Alias || rel.Target == edge.Alias;
                if (!draws)
                {
                    continue;
                }

                var other = rel.Source == edge.Alias ? rel.Target : rel.Source;
                if (!child.Aliases.Contains(other))
                {
                    yield return new Finding("rel-unrefined", Severity.Warning, edge.Target,
                        $"`{edge.Source}` draws {rel.Source} → {rel.Target} ({Label(rel)}), but `{other}` does not appear in this view");
                }
            }
        }
    }

    private static IEnumerable<Finding> IdentityFindings(C4Model model) =>
        model.Elements.Where(element => element.Labels.Count > 1).Select(element => new Finding(
            "alias-conflict", Severity.Warning, element.Views[0],
            $"alias `{element.Alias}` has different labels across views: {string.Join(", ", element.Labels.Select(label => $"“{label}”"))}"));

    /// <summary>The merged model: every alias and relationship across every view.</summary>
    public static C4Model Build(Bundle bundle, Links? links = null)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        var collected = links ?? CollectLinks(bundle);
        var zooms = collected.Edges
            .Where(edge => edge.Kind == EdgeKind.Zoom && edge.Alias is not null)
            .GroupBy(edge => (edge.Source, Alias: edge.Alias!))
            .ToDictionary(group => group.Key, group => group.First().Target);
        var elements = new Dictionary<string, ElementAccumulator>(StringComparer.Ordinal);
        var relationships = new Dictionary<(string Source, string Target), RelationshipAccumulator>();
        foreach (var concept in bundle.Concepts.Values)
        {
            if (concept.Diagram is null)
            {
                continue;
            }

            foreach (var element in concept.Diagram.Elements)
            {
                if (!elements.TryGetValue(element.Alias, out var accumulator))
                {
                    accumulator = new ElementAccumulator { External = element.External };
                    elements[element.Alias] = accumulator;
                }
                else
                {
                    accumulator.External |= element.External;
                }

                accumulator.Labels.Add(element.Label);
                accumulator.Kinds.Add(element.Kind);
                accumulator.Views.Add(concept.Id);
                if (zooms.TryGetValue((concept.Id, element.Alias), out var target))
                {
                    accumulator.ZoomsTo.Add(target);
                }
            }

            foreach (var rel in concept.Diagram.Relationships)
            {
                var key = (rel.Source, rel.Target);
                if (!relationships.TryGetValue(key, out var accumulator))
                {
                    accumulator = new RelationshipAccumulator();
                    relationships[key] = accumulator;
                }

                if (rel.Label.Length > 0)
                {
                    accumulator.Labels.Add(rel.Label);
                }

                accumulator.Views.Add(concept.Id);
            }
        }

        return new C4Model(
            [.. elements.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => new ModelElement(
                pair.Key, pair.Value.Labels.Items, pair.Value.Kinds.Items, pair.Value.External, pair.Value.Views.Items, pair.Value.ZoomsTo.Items))],
            [.. relationships.Select(pair => new ModelRelationship(pair.Key.Source, pair.Key.Target, pair.Value.Labels.Items, pair.Value.Views.Items))]);
    }

    /// <summary>The zoom hierarchy from each root view. A concept reached twice is shown once, then marked.</summary>
    public static IReadOnlyList<TreeNode> ZoomTree(Bundle bundle, Links? links = null)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        var collected = links ?? CollectLinks(bundle);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        TreeNode Walk(string conceptId, string? via, HashSet<string> path)
        {
            var concept = bundle.Get(conceptId) ?? throw new InvalidOperationException($"`{conceptId}` is not in the bundle");
            var repeat = seen.Contains(conceptId) || path.Contains(conceptId);
            seen.Add(conceptId);
            var next = new HashSet<string>(path, StringComparer.Ordinal) { conceptId };
            IReadOnlyList<TreeNode> children = repeat
                ? []
                : [.. collected.ZoomsFrom(conceptId).Select(edge => Walk(edge.Target, edge.Alias, next))];
            return new TreeNode(concept.Id, concept.Title, concept.Type, via, repeat, children);
        }

        return [.. Roots(bundle, collected).Select(root => Walk(root, null, []))];
    }

    /// <summary>
    /// The parent diagram with <paramref name="alias"/> drawn as a boundary holding the elements of the view it zooms
    /// into. Parent relationships to it stay (they now point at the boundary) unless the child view refines them: when
    /// the child draws a relationship between the same outside element and something inside, that replaces it.
    /// </summary>
    public static string Expand(Bundle bundle, string conceptId, string alias)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        var parent = bundle.Get(conceptId);
        if (parent?.Diagram is null)
        {
            throw new ExpandException($"`{conceptId}` is not a diagram in this bundle");
        }

        var element = parent.Diagram.Element(alias);
        if (element is null || string.IsNullOrEmpty(element.Link))
        {
            throw new ExpandException($"`{alias}` is not a zoomable element of `{conceptId}`");
        }

        var parentDiagram = parent.Diagram;
        var target = OkfBundle.ResolveHref(conceptId, element.Link);
        var child = bundle.Get(target);
        if (child?.Diagram is null || !MermaidReader.C4Keywords.Contains(child.Diagram.Keyword))
        {
            throw new ExpandException($"`{alias}` zooms to `{element.Link}`, which is not a C4 diagram");
        }

        var childLines = OkfBundle.SplitLines(child.Diagram.Source);
        var own = child.Diagram.Element(alias);
        List<int> copied;
        HashSet<string> inside;
        if (own is not null && own.Kind == ElementKind.Boundary)
        {
            copied = Inside(childLines, own.Line);
            inside = [.. child.Diagram.Elements
                .Where(candidate => Within(child.Diagram.Elements, candidate.Parent, alias))
                .Select(candidate => candidate.Alias)];
        }
        else
        {
            var top = child.Diagram.Elements
                .Where(candidate => candidate.Parent is null && !parentDiagram.Aliases.Contains(candidate.Alias))
                .ToList();
            copied = [.. top.SelectMany(candidate => Block(childLines, candidate.Line))];
            var copiedLines = copied.ToHashSet();
            inside = [.. child.Diagram.Elements
                .Where(candidate => copiedLines.Contains(candidate.Line))
                .Select(candidate => candidate.Alias)];
        }

        var body = copied.Select(index => childLines[index]).ToList();
        var refined = child.Diagram.Relationships.Where(rel => inside.Contains(rel.Target)).Select(rel => rel.Source)
            .Concat(child.Diagram.Relationships.Where(rel => inside.Contains(rel.Source)).Select(rel => rel.Target))
            .ToHashSet(StringComparer.Ordinal);
        var known = parentDiagram.Aliases.Concat(inside).ToHashSet(StringComparer.Ordinal);
        var lines = OkfBundle.SplitLines(parentDiagram.Source);
        var relAt = parentDiagram.Relationships.ToDictionary(rel => rel.Line);
        var elementBlock = Block(lines, element.Line).ToHashSet();
        var output = new List<string>();
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            if (index == element.Line)
            {
                var indent = line[..(line.Length - line.TrimStart().Length)];
                output.Add($"{indent}System_Boundary({alias}, \"{element.Label}\") {{");
                output.AddRange(body.Select(inner => $"{indent}  {Rebase(inner.Trim(), child.Id)}"));
                output.Add($"{indent}}}");
                continue;
            }

            if (elementBlock.Contains(index))
            {
                continue;
            }

            if (relAt.TryGetValue(index, out var rel) && (rel.Source == alias || rel.Target == alias))
            {
                var other = rel.Source == alias ? rel.Target : rel.Source;
                if (refined.Contains(other))
                {
                    continue;
                }
            }

            output.Add(line);
        }

        // A relationship the child wrote inside a boundary already came across with the body; emit it once.
        var inBody = copied.ToHashSet();
        foreach (var rel in child.Diagram.Relationships.Where(rel => !inBody.Contains(rel.Line)))
        {
            if (known.Contains(rel.Source) && known.Contains(rel.Target) && (inside.Contains(rel.Source) || inside.Contains(rel.Target)))
            {
                output.Add($"  {Rebase(childLines[rel.Line].Trim(), child.Id)}");
            }
        }

        return string.Join('\n', output) + "\n";
    }

    private static string Label(Relationship rel) => rel.Label.Length == 0 ? "unlabelled" : rel.Label;

    private static List<int> Block(IReadOnlyList<string> lines, int start)
    {
        if (start < 0 || start >= lines.Count)
        {
            return [];
        }

        if (!lines[start].TrimEnd().EndsWith('{'))
        {
            return [start];
        }

        var depth = 0;
        for (var index = start; index < lines.Count; index++)
        {
            var stripped = lines[index].Trim();
            depth += stripped.EndsWith('{') ? 1 : stripped == "}" ? -1 : 0;
            if (depth == 0)
            {
                return [.. Enumerable.Range(start, index - start + 1)];
            }
        }

        return [.. Enumerable.Range(start, lines.Count - start)];
    }

    private static List<int> Inside(IReadOnlyList<string> lines, int start)
    {
        var block = Block(lines, start);
        return block.Count <= 2 ? [] : block.GetRange(1, block.Count - 2);
    }

    private static bool Within(IReadOnlyList<Element> elements, string? parent, string boundary)
    {
        var byAlias = elements.GroupBy(element => element.Alias, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        while (parent is not null)
        {
            if (parent == boundary)
            {
                return true;
            }

            parent = byAlias.TryGetValue(parent, out var element) ? element.Parent : null;
        }

        return false;
    }

    private static string Rebase(string line, string fromId) =>
        LinkArgument().Replace(line, match =>
        {
            var target = OkfBundle.ResolveHref(fromId, match.Groups["href"].Value);
            return target is null ? match.Value : $"$link=\"/{target}.md\"";
        });

    private sealed class OrderedValues<T> where T : notnull
    {
        private readonly HashSet<T> _seen = [];
        private readonly List<T> _items = [];

        public IReadOnlyList<T> Items => _items;

        public void Add(T value)
        {
            if (_seen.Add(value))
            {
                _items.Add(value);
            }
        }
    }

    private sealed class ElementAccumulator
    {
        public OrderedValues<string> Labels { get; } = new();

        public OrderedValues<ElementKind> Kinds { get; } = new();

        public bool External { get; set; }

        public OrderedValues<string> Views { get; } = new();

        public OrderedValues<string> ZoomsTo { get; } = new();
    }

    private sealed class RelationshipAccumulator
    {
        public OrderedValues<string> Labels { get; } = new();

        public OrderedValues<string> Views { get; } = new();
    }

    [GeneratedRegex(@"\$link=""(?<href>[^""]*)""")]
    private static partial Regex LinkArgument();
}
