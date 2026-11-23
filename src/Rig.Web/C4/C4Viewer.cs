using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Renderers;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Rig.Core.C4;

namespace Rig.Web.C4;

/// <summary>One concept in the bundle's list.</summary>
internal sealed record ConceptSummary(string Id, string Title, string? Type, int? Level, string? Description);

/// <summary>How many findings of each severity the bundle has.</summary>
internal sealed record FindingCounts(int Error, int Warning, int Info);

/// <summary>What <c>/api/c4/bundle</c> answers: the concept list, the zoom tree and every finding.</summary>
internal sealed record BundleView(
    string Root,
    string Name,
    IReadOnlyList<string> Bundles,
    IReadOnlyList<ConceptSummary> Concepts,
    IReadOnlyList<TreeNode> Tree,
    IReadOnlyList<Finding> Findings,
    FindingCounts Counts);

/// <summary>An element of a concept's diagram that zooms into another concept.</summary>
internal sealed record ZoomView(string Alias, string Target, string Title, EdgeKind Kind, bool Expandable);

/// <summary>A link arriving at a concept.</summary>
internal sealed record InboundView(string Source, string Title, string? Alias, EdgeKind Kind);

/// <summary>What <c>/api/c4/concept</c> answers.</summary>
internal sealed record ConceptView(
    string Id,
    string Title,
    string? Type,
    int? Level,
    string? Description,
    string? Resource,
    IReadOnlyList<string> Tags,
    Actor? Generated,
    Actor? Verified,
    string? Diagram,
    string? Keyword,
    string NotesHtml,
    IReadOnlyDictionary<string, string> ElementNotes,
    IReadOnlyList<ZoomView> Zooms,
    IReadOnlyList<InboundView> Inbound,
    IReadOnlyList<Finding> Findings);

/// <summary>What <c>/api/c4/expand</c> answers.</summary>
internal sealed record ExpandView(string Id, string Alias, string Diagram);

/// <summary>What every failed C4 request answers.</summary>
internal sealed record ErrorView(string Error);

/// <summary>
/// The C4 viewer's model: which bundles it can serve and the JSON views of one. The domain (links, checks, the zoom tree,
/// expansion) is <see cref="C4Composer"/>; this adds only what the page shows.
/// </summary>
internal static partial class C4Viewer
{
    // Notes may be agent-written: raw HTML in one is shown as text, never passed through.
    private static readonly MarkdownPipeline Markdown = new MarkdownPipelineBuilder().DisableHtml().UsePipeTables().UsePreciseSourceLocation().Build();

    /// <summary>Compact snake_case JSON with nulls kept, enums as their hyphenated words (<c>zoom-out</c>), text unescaped.</summary>
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower) },
    };

    /// <summary>
    /// Notes to HTML. Raw HTML is escaped, and a link or image whose address is a script (<c>javascript:</c>, <c>vbscript:</c>,
    /// <c>file:</c>, or <c>data:</c> other than a small image) is left as the text it was written as: notes may be agent-written.
    /// </summary>
    public static string RenderMarkdown(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var document = Markdig.Markdown.Parse(text, Markdown);
        foreach (var link in document.Descendants<LinkInline>().Where(link => !IsSafe(link.Url)).ToList())
        {
            link.ReplaceBy(new LiteralInline(text.Substring(link.Span.Start, link.Span.Length)), copyChildren: false);
        }

        using var writer = new StringWriter();
        var renderer = new HtmlRenderer(writer);
        Markdown.Setup(renderer);
        renderer.Render(document);
        return Alignment().Replace(writer.ToString(), "style=\"text-align:$1\"");
    }

    // The same refusals as markdown-it's validateLink.
    private static bool IsSafe(string? url) =>
        url is null || !UnsafeUrl().IsMatch(url) || SafeImage().IsMatch(url);

    [GeneratedRegex(@"^\s*(vbscript|javascript|file|data):", RegexOptions.IgnoreCase)]
    private static partial Regex UnsafeUrl();

    [GeneratedRegex(@"^\s*data:image/(gif|png|jpeg|webp);", RegexOptions.IgnoreCase)]
    private static partial Regex SafeImage();

    // Markdig writes a table cell's alignment as `text-align: left;`; markdown-it, which the viewer's notes were first rendered with, as `text-align:left`.
    [GeneratedRegex("style=\"text-align: (left|right|center);\"")]
    private static partial Regex Alignment();

    /// <summary>
    /// Every bundle the viewer can switch between, by name: <paramref name="overridePath"/> first if set, then the sample
    /// bundle, then the other samples beside it. Only these names are servable, so a query can never point the server
    /// elsewhere. The samples live in the source checkout's <c>experiments/</c>; when they are absent only the override is offered.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Bundles(string sampleBundle, string? overridePath)
    {
        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(overridePath))
        {
            var path = overridePath.StartsWith('~', StringComparison.Ordinal) ? Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), overridePath[1..].TrimStart('/', '\\')) : overridePath;
            found[Path.GetFileName(Path.TrimEndingDirectorySeparator(path))] = path;
        }

        if (sampleBundle.Length > 0 && Directory.Exists(sampleBundle))
        {
            _ = found.TryAdd(Path.GetFileName(sampleBundle), sampleBundle);
        }

        var beside = sampleBundle.Length > 0 ? Path.GetDirectoryName(sampleBundle) : null;
        if (beside is not null && Directory.Exists(beside))
        {
            foreach (var path in Directory.EnumerateDirectories(beside).Order(StringComparer.Ordinal))
            {
                if (Directory.EnumerateFiles(path, "*.md", SearchOption.AllDirectories).Any())
                {
                    _ = found.TryAdd(Path.GetFileName(path), path);
                }
            }
        }

        return found;
    }

    /// <summary>The bundle called <paramref name="name"/>, or the first one when no name is asked for; null when there is none.</summary>
    public static string? BundleRoot(IReadOnlyDictionary<string, string> known, string? name) =>
        name is { Length: > 0 } ? known.GetValueOrDefault(name) : known.Values.FirstOrDefault();

    /// <summary>The list, tree and findings of <paramref name="bundle"/>.</summary>
    public static BundleView View(Bundle bundle, IReadOnlyDictionary<string, string> known)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        ArgumentNullException.ThrowIfNull(known);
        var links = C4Composer.CollectLinks(bundle);
        var findings = C4Composer.Check(bundle);
        var name = Path.GetFileName(bundle.Root);
        return new BundleView(
            name,
            name,
            [.. known.Keys],
            [.. bundle.Concepts.Values.Select(c => new ConceptSummary(c.Id, c.Title, c.Type, c.Level, c.Description))],
            C4Composer.ZoomTree(bundle, links),
            findings,
            new FindingCounts(Count(Severity.Error), Count(Severity.Warning), Count(Severity.Info)));

        int Count(Severity severity) => findings.Count(f => f.Severity == severity);
    }

    /// <summary>One concept with its notes rendered, the zooms leaving it, the links arriving and its findings.</summary>
    public static ConceptView View(Bundle bundle, Concept concept)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        ArgumentNullException.ThrowIfNull(concept);
        var links = C4Composer.CollectLinks(bundle);
        var zooms = links.Edges
            .Where(e => e.Source == concept.Id && e.Kind != EdgeKind.Mention)
            .Select(e => new ZoomView(e.Alias ?? string.Empty, e.Target, bundle.Concepts[e.Target].Title, e.Kind, Expandable(bundle, e.Target)))
            .ToList();
        var inbound = links.Into(concept.Id)
            .Select(e => new InboundView(e.Source, bundle.Concepts[e.Source].Title, e.Alias, e.Kind))
            .ToList();
        return new ConceptView(
            concept.Id,
            concept.Title,
            concept.Type,
            concept.Level,
            concept.Description,
            concept.Resource,
            concept.Tags,
            concept.Generated,
            concept.Verified,
            concept.Diagram?.Source,
            concept.Diagram?.Keyword,
            RenderMarkdown(concept.Notes),
            concept.ElementNotes().ToDictionary(note => note.Key, note => RenderMarkdown(note.Value), StringComparer.Ordinal),
            zooms,
            inbound,
            [.. C4Composer.Check(bundle).Where(f => f.Concept == concept.Id)]);
    }

    private static bool Expandable(Bundle bundle, string target) =>
        bundle.Get(target)?.Diagram is { } diagram && diagram.Keyword.StartsWith("C4", StringComparison.Ordinal);
}
