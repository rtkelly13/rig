using System.Text.RegularExpressions;
using Rig.Core.Vault;

namespace Rig.Core.C4;

/// <summary>OKF <c>generated</c> or <c>verified</c>: who did it and when.</summary>
/// <param name="By">The actor, such as <c>human:ryan</c> or <c>producer/1.0</c>.</param>
/// <param name="At">The ISO date or timestamp as written.</param>
public sealed record Actor(string? By = null, string? At = null);

/// <summary>A markdown link in a concept's notes.</summary>
/// <param name="Text">The link text.</param>
/// <param name="Href">The link target as written.</param>
/// <param name="Target">The concept id it resolves to inside the bundle, or null.</param>
public sealed record BodyLink(string Text, string Href, string? Target);

/// <summary>One OKF concept: a markdown file with frontmatter, a Mermaid block and notes.</summary>
public sealed record Concept
{
    /// <summary>Its id: its path inside the bundle without <c>.md</c>.</summary>
    public required string Id { get; init; }

    /// <summary>Its absolute path on disk.</summary>
    public required string Path { get; init; }

    /// <summary>Its OKF <c>type</c>, or null.</summary>
    public string? Type { get; init; }

    /// <summary>Its title; the file stem title-cased when the frontmatter has none.</summary>
    public required string Title { get; init; }

    /// <summary>Its description, or null.</summary>
    public string? Description { get; init; }

    /// <summary>Its resource, or null.</summary>
    public string? Resource { get; init; }

    /// <summary>Its tags; empty when none.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>Who generated it and when, or null.</summary>
    public Actor? Generated { get; init; }

    /// <summary>Who verified it and when, or null.</summary>
    public Actor? Verified { get; init; }

    /// <summary>Its first Mermaid block, or null.</summary>
    public Diagram? Diagram { get; init; }

    /// <summary>The text after the diagram, trimmed.</summary>
    public string Notes { get; init; } = string.Empty;

    /// <summary>Every markdown link in the notes.</summary>
    public IReadOnlyList<BodyLink> BodyLinks { get; init; } = [];

    /// <summary>Why the frontmatter could not be read, or null.</summary>
    public string? FrontmatterError { get; init; }

    /// <summary>The C4 level named by <see cref="Type"/>, or null when the type is not a level.</summary>
    public int? Level => OkfBundle.Levels.TryGetValue(Type ?? string.Empty, out var level) ? level : null;

    /// <summary>Whether the type is a C4 level or a supplementary C4 view.</summary>
    public bool IsC4 => OkfBundle.Levels.ContainsKey(Type ?? string.Empty) || OkfBundle.Supplementary.Contains(Type ?? string.Empty);

    /// <summary>Each <c>## Heading</c> section in the notes, keyed by the alias of the element it names.</summary>
    public IReadOnlyDictionary<string, string> ElementNotes()
    {
        var sections = new Dictionary<string, string>(StringComparer.Ordinal);
        if (Diagram is null)
        {
            return sections;
        }

        var headings = OkfBundle.Heading().Matches(Notes);
        for (var index = 0; index < headings.Count; index++)
        {
            var heading = headings[index];
            var end = index + 1 < headings.Count ? headings[index + 1].Index : Notes.Length;
            var text = heading.Groups["text"].Value.Trim().Trim('`');
            var body = UnicodeText.Strip(Notes[(heading.Index + heading.Length)..end]);
            foreach (var element in Diagram.Elements)
            {
                if (UnicodeText.CaseFold(text) == UnicodeText.CaseFold(element.Alias) || UnicodeText.CaseFold(text) == UnicodeText.CaseFold(element.Label))
                {
                    sections[element.Alias] = body;
                }
            }
        }

        return sections;
    }
}

/// <summary>An OKF bundle: a folder of concepts.</summary>
/// <param name="Root">The bundle's absolute root.</param>
/// <param name="Concepts">Each concept keyed by id, in file order.</param>
public sealed record Bundle(string Root, IReadOnlyDictionary<string, Concept> Concepts)
{
    /// <summary>The concept called <paramref name="id"/>, or null.</summary>
    public Concept? Get(string? id) => id is not null && Concepts.TryGetValue(id, out var concept) ? concept : null;
}

/// <summary>
/// Reads an OKF bundle of C4 concepts: one markdown file per concept, a Mermaid diagram on top, notes beneath.
/// OKF rules this follows: a concept is any <c>.md</c> file except the reserved <c>index.md</c> and <c>log.md</c>; its
/// id is its path inside the bundle without <c>.md</c>; <c>type</c> is the only required frontmatter key; links between
/// concepts are plain markdown links, either relative to the file or absolute from the bundle root (<c>/a/b.md</c>).
/// The C4 layer on top is a <c>type</c> convention plus Mermaid's own <c>$link</c> on an element.
/// </summary>
public static partial class OkfBundle
{
    /// <summary>Files that are navigation or logs, never concepts.</summary>
    public static readonly IReadOnlySet<string> Reserved = new HashSet<string>(StringComparer.Ordinal) { "index.md", "log.md" };

    /// <summary>C4 levels by OKF type. Dynamic and deployment views sit beside the hierarchy, not in it.</summary>
    public static readonly IReadOnlyDictionary<string, int> Levels = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["C4 Context"] = 1,
        ["C4 Container"] = 2,
        ["C4 Component"] = 3,
        ["C4 Code"] = 4,
    };

    /// <summary>Supplementary C4 views that are not levels.</summary>
    public static readonly IReadOnlySet<string> Supplementary = new HashSet<string>(StringComparer.Ordinal)
    {
        "C4 Landscape", "C4 Dynamic", "C4 Deployment",
    };

    /// <summary>The Mermaid keyword each C4 type should open with.</summary>
    public static readonly IReadOnlyDictionary<string, string> DiagramKeywords = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["C4 Context"] = "C4Context",
        ["C4 Container"] = "C4Container",
        ["C4 Component"] = "C4Component",
        ["C4 Dynamic"] = "C4Dynamic",
        ["C4 Deployment"] = "C4Deployment",
        ["C4 Landscape"] = "C4Context",
    };

    internal static bool IsExternalOrAnchor(string href) =>
        href.Length == 0 || href.StartsWith('#', StringComparison.Ordinal) || External().IsMatch(href);

    /// <summary>
    /// OKF link to concept id: <c>/x.md</c> is bundle-absolute, anything else is relative to the linking file.
    /// Null for an external URL, an anchor, an escaping path, or an empty href.
    /// </summary>
    public static string? ResolveHref(string fromId, string href)
    {
        ArgumentNullException.ThrowIfNull(fromId);
        ArgumentNullException.ThrowIfNull(href);
        if (IsExternalOrAnchor(href))
        {
            return null;
        }

        var path = href.Split('#')[0];
        var joined = path.StartsWith('/', StringComparison.Ordinal)
            ? path.TrimStart('/')
            : PosixJoin(PosixDirName(fromId), path);
        var normalised = PosixNormalise(joined);
        return normalised.StartsWith("..", StringComparison.Ordinal) || normalised == ".."
            ? null
            : normalised.EndsWith(".md", StringComparison.Ordinal) ? normalised[..^3] : normalised;
    }

    /// <summary>
    /// Paths compared one folder name at a time: <c>a/b.md</c> before <c>a-b.md</c>,
    /// where comparing whole strings would put <c>a-b.md</c> first because <c>-</c> sorts below <c>/</c>.
    /// </summary>
    private static readonly Comparer<string> FolderByFolder = Comparer<string>.Create((left, right) =>
    {
        var one = left.Split(Path.DirectorySeparatorChar);
        var two = right.Split(Path.DirectorySeparatorChar);
        for (var index = 0; index < Math.Min(one.Length, two.Length); index++)
        {
            var order = string.CompareOrdinal(one[index], two[index]);
            if (order != 0)
            {
                return order;
            }
        }

        return one.Length.CompareTo(two.Length);
    });

    /// <summary>Every concept under <paramref name="root"/>, keyed by concept id. Hidden directories are skipped.</summary>
    public static Bundle Load(string root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var resolved = Path.GetFullPath(root);
        var concepts = new Dictionary<string, Concept>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(resolved, "*.md", SearchOption.AllDirectories).Order(FolderByFolder))
        {
            var relative = Path.GetRelativePath(resolved, path).Replace(Path.DirectorySeparatorChar, '/');
            var parts = relative.Split('/');
            if (Reserved.Contains(parts[^1]) || parts.Any(part => part.StartsWith('.', StringComparison.Ordinal)))
            {
                continue;
            }

            var concept = LoadConcept(resolved, path);
            concepts[concept.Id] = concept;
        }

        return new Bundle(resolved, concepts);
    }

    /// <summary>Read one concept at <paramref name="path"/> inside the bundle rooted at <paramref name="root"/>.</summary>
    public static Concept LoadConcept(string root, string path)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(path);
        var relative = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
        var conceptId = relative.EndsWith(".md", StringComparison.Ordinal) ? relative[..^3] : relative;
        var text = NormaliseNewlines(File.ReadAllText(path));
        var split = SplitFrontmatter(text);
        var (meta, error) = DecodeFrontmatter(split.Frontmatter);
        var fence = Fence().Match(split.Body);
        var diagram = fence.Success ? MermaidReader.Parse(fence.Groups["code"].Value) : null;
        var notes = fence.Success
            ? UnicodeText.Strip(split.Body[..fence.Index] + split.Body[(fence.Index + fence.Length)..])
            : UnicodeText.Strip(split.Body);
        var bodyLinks = MarkdownLink().Matches(notes)
            .Select(match => new BodyLink(match.Groups["text"].Value, match.Groups["href"].Value, ResolveHref(conceptId, match.Groups["href"].Value)))
            .ToList();
        return new Concept
        {
            Id = conceptId,
            Path = Path.GetFullPath(path),
            Type = meta.Type,
            Title = meta.Title ?? TitleCase(Path.GetFileNameWithoutExtension(path).Replace('-', ' ')),
            Description = meta.Description,
            Resource = meta.Resource,
            Tags = meta.Tags ?? [],
            Generated = meta.Generated,
            Verified = meta.Verified,
            Diagram = diagram,
            Notes = notes,
            BodyLinks = bodyLinks,
            FrontmatterError = error,
        };
    }

    /// <summary>Split a concept's text into its frontmatter and its body.</summary>
    /// <param name="text">The concept's text, with <c>\n</c> newlines.</param>
    public static (string? Frontmatter, string Body) SplitFrontmatter(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!text.StartsWith("---", StringComparison.Ordinal))
        {
            return (null, text);
        }

        var lines = SplitKeepingEnds(text);
        for (var index = 1; index < lines.Count; index++)
        {
            if (lines[index].TrimEnd() == "---")
            {
                return (string.Concat(lines.Skip(1).Take(index - 1)), string.Concat(lines.Skip(index + 1)));
            }
        }

        return (null, text);
    }

    /// <summary>Text with every line ending as <c>\n</c>.</summary>
    public static string NormaliseNewlines(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
    }

    /// <summary>The lines of text already normalised to <c>\n</c>: a trailing newline adds no line.</summary>
    public static string[] SplitLines(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0)
        {
            return [];
        }

        var lines = text.Split('\n');
        return text.EndsWith('\n', StringComparison.Ordinal) ? lines[..^1] : lines;
    }

    /// <summary>The lines of text already normalised to <c>\n</c>, each keeping its newline.</summary>
    private static List<string> SplitKeepingEnds(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var lines = new List<string>();
        var start = 0;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] == '\n')
            {
                lines.Add(text[start..(index + 1)]);
                start = index + 1;
            }
        }

        if (start < text.Length)
        {
            lines.Add(text[start..]);
        }

        return lines;
    }

    private sealed record Frontmatter(string? Type, string? Title, string? Description, string? Resource, IReadOnlyList<string>? Tags, Actor? Generated, Actor? Verified);

    private static (Frontmatter Meta, string? Error) DecodeFrontmatter(string? raw)
    {
        var empty = new Frontmatter(null, null, null, null, null, null, null);
        if (raw is null)
        {
            return (empty, "no YAML frontmatter");
        }

        YamlValue? value;
        try
        {
            value = VaultYaml.Load(raw);
        }
        catch (VaultYamlException error)
        {
            return (empty, $"frontmatter is not valid YAML: {error.Message}");
        }

        if (value is not YamlMap mapping)
        {
            return (empty, "frontmatter must be a YAML mapping");
        }

        return (new Frontmatter(
            Text(mapping, "type"),
            Text(mapping, "title"),
            Text(mapping, "description"),
            Text(mapping, "resource"),
            Tags(mapping),
            ActorValue(mapping, "generated"),
            ActorValue(mapping, "verified")), null);
    }

    private static YamlValue? Field(YamlMap mapping, string name) =>
        mapping.Entries.Where(entry => entry.Key is YamlString key && key.Text == name).Select(entry => entry.Value).FirstOrDefault();

    private static string? Text(YamlMap mapping, string name) => Field(mapping, name) as YamlString is { } value ? value.Text : null;

    private static IReadOnlyList<string>? Tags(YamlMap mapping)
    {
        if (Field(mapping, "tags") is not YamlList list || list.Items.Any(item => item is not YamlString))
        {
            return null;
        }

        return [.. list.Items.Cast<YamlString>().Select(item => item.Text)];
    }

    private static Actor? ActorValue(YamlMap mapping, string name)
    {
        if (Field(mapping, name) is not YamlMap actor)
        {
            return null;
        }

        return new Actor(Text(actor, "by"), ActorAt(actor));
    }

    private static string? ActorAt(YamlMap actor) => Field(actor, "at") switch
    {
        YamlString text => text.Text,
        // A YAML date or timestamp arrives rendered exactly as its ISO text.
        YamlScalar scalar => scalar.Rendered,
        _ => null,
    };

    // Title case: a letter starts a word, everything else lowercase. Hyphens and spaces both start words.
    private static string TitleCase(string text)
    {
        var characters = text.ToCharArray();
        var start = true;
        for (var index = 0; index < characters.Length; index++)
        {
            var character = characters[index];
            characters[index] = start ? char.ToUpperInvariant(character) : char.ToLowerInvariant(character);
            start = !char.IsLetter(character);
        }

        return new string(characters);
    }

    private static string PosixDirName(string path)
    {
        var index = path.LastIndexOf('/', StringComparison.Ordinal);
        return index < 0 ? string.Empty : path[..index];
    }

    private static string PosixJoin(string left, string right) =>
        left.Length == 0 ? right.TrimStart('/') : right.StartsWith('/', StringComparison.Ordinal) ? left + right : left + "/" + right;

    internal static string PosixNormalise(string path)
    {
        var parts = new List<string>();
        foreach (var segment in path.Split('/'))
        {
            if (segment.Length == 0 || segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (parts.Count == 0)
                {
                    parts.Add("..");
                }
                else if (parts[^1] == "..")
                {
                    parts.Add("..");
                }
                else
                {
                    parts.RemoveAt(parts.Count - 1);
                }

                continue;
            }

            parts.Add(segment);
        }

        return parts.Count == 0 ? "." : string.Join('/', parts);
    }

    internal static string StripSuffix(string text, string suffix) =>
        text.EndsWith(suffix, StringComparison.Ordinal) ? text[..^suffix.Length] : text;

    [GeneratedRegex(@"^```(?:mermaid|pele)[ \t]*\n(?<code>.*?)^```[ \t]*$", RegexOptions.Multiline | RegexOptions.Singleline)]
    internal static partial Regex Fence();

    [GeneratedRegex(@"^##[ \t]+(?<text>.+?)[ \t]*$", RegexOptions.Multiline)]
    internal static partial Regex Heading();

    [GeneratedRegex(@"(?<!!)\[(?<text>[^\]]*)\]\((?<href>[^)\s]+)(?:\s+""[^""]*"")?\)")]
    internal static partial Regex MarkdownLink();

    [GeneratedRegex(@"^(?:[a-z][a-z0-9+.-]*:|//)", RegexOptions.IgnoreCase)]
    private static partial Regex External();
}
