using System.Globalization;
using System.Text.RegularExpressions;

namespace Rig.Core.C4;

/// <summary>Where derived code came from: the commit pins every derived view to one state of the repository.</summary>
/// <param name="Commit">The abbreviated commit.</param>
/// <param name="Date">The commit's <c>%cs</c> date.</param>
/// <param name="Web">The GitHub repository URL, or null when the remote is not GitHub.</param>
public sealed record Origin(string Commit, string Date, string? Web)
{
    /// <summary>The GitHub URL of <paramref name="path"/> at this commit, or null.</summary>
    public string? Blob(string repo, string path)
    {
        var relative = System.IO.Path.GetRelativePath(repo, path).Replace(System.IO.Path.DirectorySeparatorChar, '/');
        return Web is null ? null : $"{Web}/blob/{Commit}/{relative}";
    }

    /// <summary>The GitHub tree URL at this commit, or null.</summary>
    public string? Tree() => Web is null ? null : $"{Web}/tree/{Commit}";
}

/// <summary>The hand-written view the derived container view refines: its concept id and the zoomed element.</summary>
/// <param name="Concept">The concept id.</param>
/// <param name="Alias">The element the container view replaces.</param>
public sealed record Parent(string Concept, string Alias);

/// <summary>How many relationships each pathway found: on both, on type references only, on the call graph only.</summary>
/// <param name="Both">Relationships found by both pathways.</param>
/// <param name="OnlyRefs">Relationships only type references found.</param>
/// <param name="OnlyCalls">Relationships only the call graph found.</param>
public sealed record PathwayScore(int Both, int OnlyRefs, int OnlyCalls);

/// <summary>A derived bundle: file contents by bundle path, plus the pathway scores for the summary.</summary>
/// <param name="Files">Each derived file's text, keyed by its bundle-relative path.</param>
/// <param name="Pathways">Each project's pathway score, keyed by its container alias.</param>
public sealed record Derived(IReadOnlyDictionary<string, string> Files, IReadOnlyDictionary<string, PathwayScore> Pathways);

/// <summary>
/// Derives C4 levels 2–4 of a .NET repository as OKF concepts, from its code, deterministically.
/// Level 1 (context) stays hand-written: who uses a system and what it talks to is not in its code. The derived views
/// hang beneath it. The context's element <c>$link</c>s to <c>containers.md</c>, and <c>--parent</c> copies that
/// element's neighbours into the container view so the refinement check holds by construction.
/// Every derived concept says so: <c>generated.by</c> names this deriver, <c>generated.at</c> is the commit date (not
/// today, so a re-run over the same commit is byte-identical), <c>resource</c> is the commit on GitHub, and the tag
/// <c>derived</c> marks it as overwritten on every run. <c>--check</c> re-derives and fails when a file on disk differs.
/// </summary>
public sealed partial class C4Deriver
{
    /// <summary>The producer stamp every derived concept carries.</summary>
    public const string Producer = "rig-c4-derive/0.1";

    private const string Compiler = "csc";

    private readonly string _repo;
    private readonly Origin _origin;
    private readonly string? _callgraph;
    private readonly Dictionary<string, ProjectScan> _scans;
    private readonly string _common;
    private readonly Dictionary<string, string> _aliases = new(StringComparer.Ordinal);
    private readonly Dictionary<CodeKey, string> _codeIds = [];

    private C4Deriver(string repo, Origin origin, string? callgraph, IReadOnlyList<Project> projects)
    {
        _repo = repo;
        _origin = origin;
        _callgraph = callgraph;
        _scans = [];
        foreach (var project in projects)
        {
            var files = DotnetScan.Sources(project);
            _scans[project.Path] = new ProjectScan(project, files, DotnetScan.Components(project, files));
        }

        _common = CommonNamespace([.. _scans.Values.Select(scan => scan.Project.RootNamespace)]);
        var usedAliases = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (path, scan) in _scans.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var baseSlug = MermaidGenerator.Slug(Short(scan.Project.Name));
            var alias = baseSlug;
            var counter = 2;
            while (!usedAliases.Add(alias))
            {
                alias = $"{baseSlug}_{counter++}";
            }

            _aliases[path] = alias;
        }

        BuildCodeIds();
    }

    /// <summary>Read where the code came from: the remote, the commit and its date.</summary>
    /// <param name="repo">The repository root.</param>
    /// <param name="git">Runs git in that repository and returns its trimmed output, or null when it fails.</param>
    public static async Task<Origin> ReadOriginAsync(string repo, Func<IReadOnlyList<string>, Task<string>> git)
    {
        var remote = await git(["remote", "get-url", "origin"]).ConfigureAwait(false);
        var match = GitHubRemote().Match(remote);
        var commit = await git(["rev-parse", "--short=12", "HEAD"]).ConfigureAwait(false);
        var date = await git(["log", "-1", "--format=%cs"]).ConfigureAwait(false);
        return new Origin(
            string.IsNullOrEmpty(commit) ? "unknown" : commit,
            string.IsNullOrEmpty(date) ? "unknown" : date,
            match.Success ? $"https://github.com/{match.Groups["slug"].Value}" : null);
    }

    /// <summary>Derive the container, component and code views of <paramref name="repo"/> into <paramref name="root"/>.</summary>
    /// <param name="origin">The commit the views pin to.</param>
    /// <param name="repo">The .NET repository; projects are read from its <c>src/</c> unless <paramref name="project"/> names one.</param>
    /// <param name="root">The bundle root the derived views belong to.</param>
    /// <param name="parent">The hand-written container view to refine, or null.</param>
    /// <param name="callgraph">A folder of <c>&lt;Project&gt;.callgraph.txt</c> edge files, or null.</param>
    /// <param name="project">One project file to derive instead of every project under <c>src/</c>, or null.</param>
    public static Derived Derive(Origin origin, string repo, string root, Parent? parent = null, string? callgraph = null, string? project = null)
    {
        var resolved = System.IO.Path.GetFullPath(repo);
        var projects = project is null
            ? DotnetScan.Discover(resolved)
            : DotnetScan.DiscoverProject(project);
        var deriver = new C4Deriver(resolved, origin, callgraph, projects);
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        var pathways = new Dictionary<string, PathwayScore>(StringComparer.Ordinal);
        files["containers.md"] = deriver.Containers(parent, root);
        foreach (var scan in deriver._scans.Values)
        {
            if (scan.Parts.Count == 0)
            {
                continue;
            }

            var alias = deriver._aliases[scan.Project.Path];
            var view = deriver.ComponentsView(scan);
            files[$"components/{alias}.md"] = view.Text;
            pathways[alias] = view.Score;
            foreach (var part in scan.Parts)
            {
                files.TryAdd($"{deriver.CodeId(part)}.md", deriver.CodeView(part));
            }
        }

        return new Derived(files, pathways);
    }

    /// <summary>
    /// Concepts this deriver wrote on an earlier run that a fresh derivation no longer produces. Ownership is the
    /// <c>generated.by</c> stamp: a hand-written concept is never an orphan.
    /// </summary>
    public static IReadOnlyList<string> Orphans(string root, IReadOnlyDictionary<string, string> files)
    {
        if (!Directory.Exists(root))
        {
            return [];
        }

        var producer = Producer.Split('/', 2)[0] + "/";
        return [.. OkfBundle.Load(root).Concepts.Values
            .Where(concept => concept.Generated is not null && (concept.Generated.By ?? string.Empty).StartsWith(producer, StringComparison.Ordinal))
            .Select(concept => concept.Id + ".md")
            .Where(name => !files.ContainsKey(name))
            .Order(StringComparer.Ordinal)];
    }

    /// <summary>Derived paths whose file on disk is missing or differs from a fresh derivation, plus orphans.</summary>
    public static IReadOnlyList<string> Stale(string root, IReadOnlyDictionary<string, string> files)
    {
        var changed = files
            .Where(pair => !File.Exists(System.IO.Path.Combine(root, pair.Key))
                || OkfBundle.NormaliseNewlines(File.ReadAllText(System.IO.Path.Combine(root, pair.Key))) != pair.Value)
            .Select(pair => pair.Key);
        return [.. changed.Concat(Orphans(root, files)).Order(StringComparer.Ordinal)];
    }

    /// <summary>Write every stale derived file and delete orphans; returns the paths written or removed.</summary>
    public static IReadOnlyList<string> Write(string root, IReadOnlyDictionary<string, string> files)
    {
        var changed = Stale(root, files);
        foreach (var name in changed)
        {
            var path = System.IO.Path.Combine(root, name);
            if (!files.TryGetValue(name, out var text))
            {
                File.Delete(path);
                continue;
            }

            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }

        return changed;
    }

    private sealed record ProjectScan(Project Project, IReadOnlyList<SourceFile> Files, IReadOnlyList<Component> Parts);

    private sealed record CodeKey(string Namespace, string Paths);

    private sealed record ComponentView(string Text, PathwayScore Score);

    private sealed record Scored(IReadOnlyList<CodeEdge> Both, IReadOnlyList<CodeEdge> OnlyRefs, IReadOnlyList<CodeEdge> OnlyCalls);

    private sealed record NeighbourLines(IReadOnlyList<string> Lines, string? Label);

    /// <summary>A name without the prefix every project shares; the shared root itself keeps its last segment.</summary>
    private string Short(string name)
    {
        var prefix = _common + ".";
        if (name.StartsWith(prefix, StringComparison.Ordinal))
        {
            return name[prefix.Length..];
        }

        return name == _common ? name.Split('.').Last() : name;
    }

    private void BuildCodeIds()
    {
        var taken = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (path, scan) in _scans)
        {
            foreach (var part in scan.Parts)
            {
                var key = new CodeKey(part.Namespace, string.Join('\n', part.Files.Select(file => file.Path)));
                if (_codeIds.ContainsKey(key))
                {
                    continue;
                }

                var baseName = MermaidGenerator.Slug(Short(part.Namespace));
                var count = taken.GetValueOrDefault(baseName) + 1;
                taken[baseName] = count;
                _codeIds[key] = count == 1 ? $"code/{baseName}" : $"code/{baseName}--{_aliases[path]}";
            }
        }
    }

    private string CodeId(Component part) => _codeIds[new CodeKey(part.Namespace, string.Join('\n', part.Files.Select(file => file.Path)))];

    /// <summary>One alias per code view: a namespace two projects compile in is one element in both views.</summary>
    private string PartAlias(Component part) => "ns_" + MermaidGenerator.Slug(RemovePrefix(CodeId(part), "code/"));

    /// <summary>The namespace without the prefix every project shares, so shared parts read the same everywhere.</summary>
    private string Label(Component part) => Short(part.Namespace) is { Length: > 0 } label ? label : "global";

    /// <summary>
    /// Projects this one references, and projects that reference it: everything the container view joins it to, so
    /// the component view refines every relationship its parent draws.
    /// </summary>
    private IReadOnlyList<ProjectScan> Neighbours(ProjectScan scan)
    {
        var outgoing = scan.Project.Refs.Select(reference => reference.Target).ToHashSet(StringComparer.Ordinal);
        var incoming = _scans.Values
            .Where(other => other.Project.Refs.Any(reference => reference.Target == scan.Project.Path))
            .Select(other => other.Project.Path)
            .ToHashSet(StringComparer.Ordinal);
        return [.. _scans.Values.Where(other => other.Project.Path != scan.Project.Path && (outgoing.Contains(other.Project.Path) || incoming.Contains(other.Project.Path)))];
    }

    private string ContainerLine(ProjectScan scan, bool link, bool external = false)
    {
        var project = scan.Project;
        var technology = $"{(project.IsAnalyzer ? "analyzer" : "library")} · {string.Join(", ", project.Frameworks)}";
        var target = link && scan.Parts.Count > 0 ? $"components/{_aliases[project.Path]}" : null;
        return MermaidGenerator.Container(_aliases[project.Path], project.Name, technology, N(scan.Files.Count, "source file"), target, external);
    }

    private IReadOnlyList<string> ContainerRels(ProjectScan scan)
    {
        var rels = new List<string>();
        var me = _aliases[scan.Project.Path];
        if (scan.Project.IsAnalyzer)
        {
            rels.Add(MermaidGenerator.Relationship(Compiler, me, "loads into the build", "Roslyn"));
        }

        foreach (var reference in scan.Project.Refs)
        {
            if (!_aliases.TryGetValue(reference.Target, out var other))
            {
                continue;
            }

            rels.Add(reference.Analyzer
                ? MermaidGenerator.Relationship(other, me, "checks at build", "analyzer reference")
                : MermaidGenerator.Relationship(me, other, "references", "ProjectReference"));
        }

        return rels;
    }

    private string Containers(Parent? parent, string root)
    {
        var found = ParentNeighbours(root, parent);
        var name = found.Label ?? System.IO.Path.GetFileName(_repo);
        var lines = new List<string> { "C4Container", $"title {name}: projects" };
        var derivedRels = _scans.Values.SelectMany(ContainerRels).ToList();
        var copiedRels = found.Lines.Where(line => line.StartsWith("Rel", StringComparison.Ordinal) || line.StartsWith("BiRel", StringComparison.Ordinal)).ToList();
        var copiedRelSet = copiedRels.ToHashSet(StringComparer.Ordinal);
        lines.AddRange(found.Lines.Where(line => !copiedRelSet.Contains(line)));
        var refined = derivedRels.SelectMany(Endpoints).ToHashSet(StringComparer.Ordinal);
        copiedRels = [.. copiedRels.Where(rel => !refined.Overlaps(Endpoints(rel)))];
        var declared = found.Lines.Any(line => AfterOpenParen(line).StartsWith(Compiler + ",", StringComparison.Ordinal));
        if (!declared && _scans.Values.Any(scan => scan.Project.IsAnalyzer))
        {
            lines.Add($"System_Ext({Compiler}, \"C# compiler\", \"Roslyn, in the consumer's build\")");
        }

        var boundary = parent?.Alias ?? $"repo_{MermaidGenerator.Slug(name)}";
        lines.Add($"System_Boundary({boundary}, \"{name}\") {{");
        lines.AddRange(_scans.Values.Select(scan => $"  {ContainerLine(scan, link: true)}"));
        lines.Add("}");
        lines.AddRange(copiedRels);
        lines.AddRange(derivedRels);
        var notes = _scans.Values.Select(scan => ContainerNote(scan));
        var head = MermaidGenerator.Frontmatter("C4 Container", $"{name} — projects",
            "Every project under src/ and what it references, read from the .csproj files.", _origin);
        var intro = $"Derived by `rig c4 derive dotnet` from commit `{_origin.Commit}`; edits here are overwritten. "
            + "The people and systems outside the boundary are copied from the parent view.\n";
        return head + MermaidGenerator.Diagram(lines) + "\n" + intro + "\n" + string.Join('\n', notes);
    }

    private ComponentView ComponentsView(ProjectScan scan)
    {
        var project = scan.Project;
        var neighbours = Neighbours(scan);
        var owned = scan.Parts.ToDictionary(part => PartAlias(part), part => part.Files, StringComparer.Ordinal);
        var owners = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var part in scan.Parts)
        {
            owners[PartAlias(part)] = [.. part.Files.SelectMany(file => file.Declares)];
        }
        var mine = owners.Values.SelectMany(types => types).ToHashSet(StringComparer.Ordinal);
        foreach (var neighbour in neighbours)
        {
            owners[_aliases[neighbour.Project.Path]] = [.. neighbour.Files.SelectMany(file => file.Declares).Where(type => !mine.Contains(type))];
        }

        var refs = DotnetScan.ReferenceEdges(owned, DotnetScan.TypeIndex(owners));
        var calls = Calls(scan, neighbours);
        var me = _aliases[project.Path];
        var lines = new List<string> { "C4Component", $"title {project.Name}: namespaces" };
        if (project.IsAnalyzer)
        {
            lines.Add($"System_Ext({Compiler}, \"C# compiler\", \"Roslyn\")");
        }

        lines.AddRange(neighbours.Select(neighbour => ContainerLine(neighbour, link: false, external: true)));
        lines.Add($"Container_Boundary({me}, \"{project.Name}\") {{");
        lines.AddRange(scan.Parts.Select(part => $"  {MermaidGenerator.Component(PartAlias(part), Label(part), part.Types, CodeId(part))}"));
        lines.Add("}");
        lines.AddRange(scan.Parts.Where(IsEntry).Select(part => MermaidGenerator.Relationship(Compiler, PartAlias(part), "runs")));
        lines.AddRange(EdgeRels(refs, calls));
        var scored = Score(refs, calls);
        var notes = scan.Parts.Select(part => ComponentNote(part)).ToList();
        notes.AddRange(neighbours.Where(neighbour => neighbour.Parts.Count > 0)
            .Select(neighbour => $"## {_aliases[neighbour.Project.Path]}\n\nSee [its namespaces](/components/{_aliases[neighbour.Project.Path]}.md).\n"));
        notes.Add(DerivationNote(scored, callsKnown: _callgraph is not null));
        var head = MermaidGenerator.Frontmatter("C4 Component", $"{project.Name} — namespaces",
            "Namespaces as components; relationships from type references, checked against the call graph.", _origin);
        return new ComponentView(head + MermaidGenerator.Diagram(lines) + "\n" + string.Join('\n', notes),
            new PathwayScore(scored.Both.Count, scored.OnlyRefs.Count, scored.OnlyCalls.Count));
    }

    private Dictionary<CodeEdge, int>? Calls(ProjectScan scan, IReadOnlyList<ProjectScan> neighbours)
    {
        if (_callgraph is null)
        {
            return null;
        }

        var path = System.IO.Path.Combine(_callgraph, $"{scan.Project.Name}.callgraph.txt");
        if (!File.Exists(path))
        {
            return null;
        }

        var ownerOf = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in scan.Parts)
        {
            foreach (var type in part.Types)
            {
                ownerOf[$"{part.Namespace}.{type}"] = PartAlias(part);
            }
        }

        foreach (var neighbour in neighbours)
        {
            foreach (var part in neighbour.Parts)
            {
                foreach (var type in part.Types)
                {
                    ownerOf.TryAdd($"{part.Namespace}.{type}", _aliases[neighbour.Project.Path]);
                }
            }
        }

        return DotnetScan.CallEdges(path, ownerOf);
    }

    private string CodeView(Component part)
    {
        var types = part.Types.ToHashSet(StringComparer.Ordinal);
        var declared = part.Files
            .SelectMany(file => file.Types.Select(type => (Type: type, File: file)))
            .OrderBy(pair => pair.Type.Name, StringComparer.Ordinal)
            .ToList();
        var edges = part.Files
            .SelectMany(file => file.Declares.SelectMany(declaration => file.Names
                .Where(name => types.Contains(name) && !string.Equals(declaration, name, StringComparison.Ordinal))
                .Select(name => (Source: declaration, Target: name))))
            .Distinct()
            .OrderBy(edge => edge.Source, StringComparer.Ordinal)
            .ThenBy(edge => edge.Target, StringComparer.Ordinal)
            .ToList();
        var links = new List<(string Type, string Url)>();
        foreach (var file in part.Files)
        {
            var url = _origin.Blob(_repo, file.Path);
            if (url is not null)
            {
                links.AddRange(file.Declares.Select(declaration => (declaration, url)));
            }
        }

        var notes = declared.Select(pair => TypeNote(pair.Type, pair.File, _origin.Blob(_repo, pair.File.Path)));
        var head = MermaidGenerator.Frontmatter("C4 Code", $"{(part.Namespace.Length == 0 ? "global" : part.Namespace)} — types",
            "Types in the namespace and which of them name each other. Each type links to its source.", _origin);
        return head + MermaidGenerator.ClassDiagram(declared, edges, links) + "\n" + string.Join('\n', notes);
    }

    private string ContainerNote(ProjectScan scan)
    {
        var project = scan.Project;
        var linked = scan.Files.Count(file => !IsUnder(file.Path, project.Directory));
        var shared = linked == 0
            ? string.Empty
            : $" {linked} of them {(linked == 1 ? "is" : "are")} compiled in from another project.";
        var role = project.IsAnalyzer ? "Ships as a Roslyn analyzer." : "Ships as a library.";
        var total = scan.Files.Sum(file => file.Lines);
        var frameworks = project.Frameworks.Count == 0 ? "unknown framework" : string.Join(", ", project.Frameworks);
        return $"## {_aliases[project.Path]}\n\n{role} Targets {frameworks}. {N(scan.Files.Count, "source file")}, {N(total, "line")}.{shared}\n\nProject file: {Where(project.Path)}\n";
    }

    private string TypeNote(TypeInfo info, SourceFile file, string? url)
    {
        var lines = new List<string> { $"## {info.Name}", string.Empty, $"**{info.Kind}**" + (info.Summary.Length == 0 ? string.Empty : $" · {info.Summary}") };
        var source = url is null ? $"`{System.IO.Path.GetFileName(file.Path)}`" : $"[{System.IO.Path.GetFileName(file.Path)}]({url})";
        var parent = PosixParent(Relative(file.Path));
        lines.Add(string.Empty);
        lines.Add($"{source} in {parent}/ · {N(file.Lines, "line")}");
        if (info.Members.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add("Public: " + string.Join(", ", info.Members.Select(member => $"`{member}`")));
        }

        return string.Join('\n', lines) + "\n";
    }

    private string ComponentNote(Component part)
    {
        var rows = part.Files.Take(12).Select(file =>
        {
            var other = file.Declares.Where(type => type != System.IO.Path.GetFileNameWithoutExtension(file.Path)).ToList();
            var declares = string.Join(", ", other.Take(3)) + (other.Count > 3 ? " …" : string.Empty);
            return $"| `{System.IO.Path.GetFileName(file.Path)}` | {declares} | {file.Lines.ToString(CultureInfo.InvariantCulture)} |";
        }).ToList();
        var more = part.Files.Count > 12 ? $"\n\nAnd {N(part.Files.Count - 12, "more file")}." : string.Empty;
        var directories = part.Files.Select(file => PosixParent(Relative(file.Path))).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var where = directories.Count == 1 ? $"in {directories[0]}/" : "in " + string.Join(", ", directories.Select(directory => $"{directory}/"));
        var total = part.Files.Sum(file => file.Lines);
        var table = string.Join('\n', ["| File | Also declares | Lines |", "|---|---|---|", .. rows]);
        var head = $"`{part.Namespace}`: {N(part.Types.Count, "type")} in {N(part.Files.Count, "file")}, {N(total, "line")}";
        return $"## {PartAlias(part)}\n\n{head}, {where}\n\n{table}{more}\n";
    }

    private static string DerivationNote(Scored scored, bool callsKnown)
    {
        static string Show(IReadOnlyList<CodeEdge> edges) =>
            edges.Count == 0 ? "none" : string.Join(", ", edges.Select(edge => $"`{edge.Source} → {edge.Target}`"));

        if (!callsKnown)
        {
            return "## Derivation\n\nRelationships come from type references only; no call graph was given.\n";
        }

        return "## Derivation\n\n"
            + "Two pathways, scored against each other:\n\n"
            + $"- **Both** type references and the call graph: {scored.Both.Count} relationships.\n"
            + $"- **Type references only** (mentions, signatures, or calls the graph could not resolve): {Show(scored.OnlyRefs)}.\n"
            + $"- **Call graph only** (a call with no type named, e.g. a static helper): {Show(scored.OnlyCalls)}.\n";
    }

    private static List<string> EdgeRels(Dictionary<CodeEdge, int> refs, Dictionary<CodeEdge, int>? calls)
    {
        var rels = new List<string>();
        foreach (var (edge, count) in refs.OrderBy(pair => pair.Key.Source, StringComparer.Ordinal).ThenBy(pair => pair.Key.Target, StringComparer.Ordinal))
        {
            var called = calls?.GetValueOrDefault(edge) ?? 0;
            var technology = N(count, "type") + (called > 0 ? $", {N(called, "call")}" : string.Empty);
            rels.Add(MermaidGenerator.Relationship(edge.Source, edge.Target, "uses", technology));
        }

        if (calls is not null)
        {
            rels.AddRange(calls.Where(pair => !refs.ContainsKey(pair.Key)).OrderBy(pair => pair.Key.Source, StringComparer.Ordinal).ThenBy(pair => pair.Key.Target, StringComparer.Ordinal)
                .Select(pair => MermaidGenerator.Relationship(pair.Key.Source, pair.Key.Target, "calls", $"{N(pair.Value, "call")}, no type named")));
        }

        return rels;
    }

    private static Scored Score(Dictionary<CodeEdge, int> refs, Dictionary<CodeEdge, int>? calls)
    {
        var seenCalls = calls is null ? new HashSet<CodeEdge>() : [.. calls.Keys];
        return new Scored(
            [.. refs.Keys.Where(seenCalls.Contains).OrderBy(edge => edge.Source, StringComparer.Ordinal).ThenBy(edge => edge.Target, StringComparer.Ordinal)],
            [.. refs.Keys.Where(edge => !seenCalls.Contains(edge)).OrderBy(edge => edge.Source, StringComparer.Ordinal).ThenBy(edge => edge.Target, StringComparer.Ordinal)],
            [.. seenCalls.Where(edge => !refs.ContainsKey(edge)).OrderBy(edge => edge.Source, StringComparer.Ordinal).ThenBy(edge => edge.Target, StringComparer.Ordinal)]);
    }

    private static NeighbourLines ParentNeighbours(string root, Parent? parent)
    {
        if (parent is null || !Directory.Exists(root))
        {
            return new NeighbourLines([], null);
        }

        var diagram = OkfBundle.Load(root).Get(parent.Concept)?.Diagram;
        if (diagram is null)
        {
            return new NeighbourLines([], null);
        }

        var touching = diagram.Relationships.Where(rel => rel.Source == parent.Alias || rel.Target == parent.Alias).ToList();
        var near = touching.Select(rel => rel.Source).Concat(touching.Select(rel => rel.Target)).ToHashSet(StringComparer.Ordinal);
        var source = OkfBundle.SplitLines(diagram.Source);
        // The boundary takes the parent's alias, so the parent's relationships still land on it.
        var label = diagram.Element(parent.Alias)?.Label;
        var copied = diagram.Elements.Where(element => near.Contains(element.Alias) && element.Alias != parent.Alias)
            .Select(element => source[element.Line].Trim())
            .Concat(touching.Select(rel => source[rel.Line].Trim()))
            .ToList();
        return new NeighbourLines(copied, label);
    }

    private static IReadOnlyList<string> Endpoints(string rel)
    {
        var parenthesised = AfterOpenParen(rel);
        return [.. parenthesised.Split(',', 3).Take(2).Select(part => part.Trim())];
    }

    private static string AfterOpenParen(string line)
    {
        var index = line.IndexOf('(', StringComparison.Ordinal);
        return index < 0 ? line : line[(index + 1)..];
    }

    private static bool IsEntry(Component part) => part.Files.Any(file => file.IsGenerator);

    private static string CommonNamespace(IReadOnlyList<string> names)
    {
        var split = names.Where(name => name.Length > 0).Select(name => name.Split('.')).ToList();
        if (split.Count == 0)
        {
            return string.Empty;
        }

        var shared = new List<string>();
        for (var depth = 0; depth < split.Min(parts => parts.Length); depth++)
        {
            var segment = split[0][depth];
            if (split.Any(parts => parts[depth] != segment))
            {
                break;
            }

            shared.Add(segment);
        }

        return string.Join('.', shared);
    }

    private static string N(int count, string noun) =>
        $"{count.ToString(CultureInfo.InvariantCulture)} {noun}{(count == 1 ? string.Empty : "s")}";

    private static string RemovePrefix(string text, string prefix) =>
        text.StartsWith(prefix, StringComparison.Ordinal) ? text[prefix.Length..] : text;

    private string Relative(string path) =>
        IsUnder(path, _repo) ? System.IO.Path.GetRelativePath(_repo, path).Replace(System.IO.Path.DirectorySeparatorChar, '/') : path.Replace(System.IO.Path.DirectorySeparatorChar, '/');

    private string Where(string path)
    {
        var parent = PosixParent(Relative(path));
        return parent == "." ? $"`{System.IO.Path.GetFileName(path)}`" : $"`{System.IO.Path.GetFileName(path)}` in {parent}/";
    }

    private static string PosixParent(string path)
    {
        var index = path.LastIndexOf('/');
        return index < 0 ? "." : path[..index];
    }

    private static bool IsUnder(string path, string directory)
    {
        var relative = System.IO.Path.GetRelativePath(directory, path);
        return !System.IO.Path.IsPathRooted(relative) && relative != ".."
            && !relative.StartsWith($"..{System.IO.Path.DirectorySeparatorChar}", StringComparison.Ordinal);
    }

    [GeneratedRegex(@"^(?:git@github\.com:|https://github\.com/)(?<slug>[^/]+/[^/]+?)(?:\.git)?$")]
    private static partial Regex GitHubRemote();
}
