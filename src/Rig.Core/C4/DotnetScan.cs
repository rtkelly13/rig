using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Rig.Core.C4;

/// <summary>A project a project file references.</summary>
/// <param name="Target">The referenced project file's full path.</param>
/// <param name="Analyzer">Whether it is an analyzer reference (<c>OutputItemType="Analyzer"</c>).</param>
public sealed record ProjectRef(string Target, bool Analyzer);

/// <summary>One <c>.csproj</c>. The root namespace defaults to the project name, as MSBuild does.</summary>
/// <param name="Name">The project file's stem.</param>
/// <param name="Path">Its full path.</param>
/// <param name="RootNamespace">Its <c>RootNamespace</c>, or its name.</param>
/// <param name="Frameworks">Its target frameworks, in declaration order.</param>
/// <param name="IsAnalyzer">Whether it ships as a Roslyn analyzer.</param>
/// <param name="Refs">Its project references.</param>
/// <param name="CompileLinks">Files it compiles in by <c>&lt;Compile Include=…&gt;</c>.</param>
public sealed record Project(string Name, string Path, string RootNamespace, IReadOnlyList<string> Frameworks, bool IsAnalyzer, IReadOnlyList<ProjectRef> Refs, IReadOnlyList<string> CompileLinks)
{
    /// <summary>The folder holding the project file.</summary>
    public string Directory => System.IO.Path.GetDirectoryName(Path) ?? string.Empty;
}

/// <summary>One declared type: its kind, the first sentence of its <c>&lt;summary&gt;</c>, and its public members.</summary>
/// <param name="Name">Its name.</param>
/// <param name="Kind">Its declaration kind, such as <c>class</c> or <c>record</c>.</param>
/// <param name="Summary">The first sentence of its documentation, or an empty string.</param>
/// <param name="Members">Its public members; <c>Name()</c> for a method, <c>Name</c> otherwise.</param>
public sealed record TypeInfo(string Name, string Kind, string Summary, IReadOnlyList<string> Members);

/// <summary>One parsed source file.</summary>
/// <param name="Path">Its full path.</param>
/// <param name="Namespace">Its namespace, or an empty string.</param>
/// <param name="Declares">The type names it declares, sorted.</param>
/// <param name="Names">The capitalised identifiers it uses outside comments, strings and directives.</param>
/// <param name="IsGenerator">Whether it implements <c>IIncrementalGenerator</c>.</param>
/// <param name="Lines">Its line count.</param>
/// <param name="Types">Its declared types, sorted by name.</param>
public sealed record SourceFile(string Path, string Namespace, IReadOnlyList<string> Declares, IReadOnlySet<string> Names, bool IsGenerator, int Lines, IReadOnlyList<TypeInfo> Types);

/// <summary>A namespace's share of one project.</summary>
/// <param name="Key">The namespace relative to the project's root namespace.</param>
/// <param name="Namespace">The full namespace.</param>
/// <param name="Files">The files in it.</param>
public sealed record Component(string Key, string Namespace, IReadOnlyList<SourceFile> Files)
{
    /// <summary>Every type the files declare, sorted.</summary>
    public IReadOnlyList<string> Types => [.. Files.SelectMany(file => file.Declares).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
}

/// <summary>A relationship from one owner (a namespace alias or a neighbouring project) to another.</summary>
public sealed record CodeEdge(string Source, string Target);

/// <summary>
/// Reads the C4 structure of a .NET repository straight from its code, without running .NET.
/// Containers are projects; <c>ProjectReference</c> gives their relationships. Components are namespaces inside a
/// project, with two deliberately separate relationship pathways scored against each other: type references (cheap,
/// sees everything) and the Roslyn call graph's <c>E:</c> edge files (precise where it resolves, blind where it does not).
/// </summary>
public static partial class DotnetScan
{
    // Namespaces a project borrows to back-fill the BCL (IsExternalInit); not part of its design.
    private static readonly string[] Polyfill = ["System.", "Microsoft."];

    private static readonly HashSet<string> SkipDirectories = new(StringComparer.Ordinal)
    {
        "bin", "obj", "temp", ".worktrees", "node_modules",
    };

    private const int SummaryCap = 180;
    private const int MemberCap = 8;

    /// <summary>Every project under <paramref name="roots"/>, plus whatever they reference, transitively. Sorted by name.</summary>
    public static IReadOnlyList<Project> Discover(string repo, IReadOnlyList<string>? roots = null)
    {
        ArgumentNullException.ThrowIfNull(repo);
        var pending = new List<string>();
        foreach (var root in roots ?? ["src"])
        {
            var directory = System.IO.Path.Combine(repo, root);
            if (!Directory.Exists(directory))
            {
                continue;
            }

            pending.AddRange(SortedCsproj(directory).Where(path => !Skipped(path, repo)));
        }

        return ReadClosure(pending);
    }

    /// <summary>The project at <paramref name="csproj"/> plus everything it references, transitively. Sorted by name.</summary>
    public static IReadOnlyList<Project> DiscoverProject(string csproj)
    {
        ArgumentNullException.ThrowIfNull(csproj);
        var fullPath = System.IO.Path.GetFullPath(csproj);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"Project file not found: '{fullPath}'", fullPath);
        }

        return ReadClosure([fullPath]);
    }

    private static IReadOnlyList<Project> ReadClosure(List<string> pending)
    {
        var found = new Dictionary<string, Project>(StringComparer.Ordinal);
        while (pending.Count > 0)
        {
            var path = System.IO.Path.GetFullPath(pending[^1]);
            pending.RemoveAt(pending.Count - 1);
            if (found.ContainsKey(path) || !File.Exists(path))
            {
                continue;
            }

            var project = ReadProject(path);
            found[path] = project;
            pending.AddRange(project.Refs.Select(reference => reference.Target));
        }

        return [.. found.Values.OrderBy(project => project.Name, StringComparer.Ordinal)];
    }

    private static IEnumerable<string> SortedCsproj(string directory) =>
        Directory.EnumerateFiles(directory, "*.csproj", SearchOption.AllDirectories).Order(StringComparer.Ordinal);

    private static bool Skipped(string path, string repo)
    {
        var relative = System.IO.Path.GetRelativePath(repo, path);
        return relative.Split(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar)
            .Any(part => SkipDirectories.Contains(part));
    }

    /// <summary>Read one project file.</summary>
    public static Project ReadProject(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var root = XDocument.Load(path).Root ?? throw new InvalidOperationException($"{path}: empty project file");
        var frameworks = Text(root, "TargetFrameworks") is { Length: > 0 } multiple
            ? multiple
            : Text(root, "TargetFramework") ?? string.Empty;
        var refs = Items(root, "ProjectReference")
            .Select(item => new ProjectRef(MsBuildPath(System.IO.Path.GetDirectoryName(path) ?? string.Empty, item.Include), item.OutputItemType == "Analyzer"))
            .ToList();
        var links = Items(root, "Compile")
            .SelectMany(item => MsBuildGlob(System.IO.Path.GetDirectoryName(path) ?? string.Empty, item.Include))
            .ToList();
        var extended = string.Equals(Text(root, "EnforceExtendedAnalyzerRules"), "true", StringComparison.OrdinalIgnoreCase);
        var roslyn = string.Equals(Text(root, "IsRoslynComponent"), "true", StringComparison.OrdinalIgnoreCase);
        return new Project(
            System.IO.Path.GetFileNameWithoutExtension(path),
            System.IO.Path.GetFullPath(path),
            Text(root, "RootNamespace") ?? System.IO.Path.GetFileNameWithoutExtension(path),
            [.. frameworks.Split(';').Where(framework => framework.Length > 0)],
            extended || roslyn,
            refs,
            links);
    }

    private sealed record ItemAttributes(string Include, string OutputItemType);

    private static IEnumerable<ItemAttributes> Items(XElement root, string tag) =>
        root.Descendants(tag)
            .Select(item => new ItemAttributes(item.Attribute("Include")?.Value ?? string.Empty, item.Attribute("OutputItemType")?.Value ?? string.Empty))
            .Where(item => item.Include.Length > 0);

    private static string? Text(XElement root, string tag)
    {
        var value = root.Descendants(tag).FirstOrDefault()?.Value;
        return value is null ? null : value.Trim();
    }

    /// <summary>An MSBuild path uses backslashes on every platform.</summary>
    private static string MsBuildPath(string basePath, string raw)
    {
        var normalised = raw.Replace('\\', '/');
        var parts = normalised.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var rooted = normalised.StartsWith('/') || (parts.Length > 0 && parts[0].EndsWith(':'));
        var joined = rooted
            ? (normalised.StartsWith('/') ? "/" + string.Join('/', parts) : string.Join(System.IO.Path.DirectorySeparatorChar, parts))
            : System.IO.Path.Combine(new[] { basePath }.Concat(parts).ToArray());
        return System.IO.Path.GetFullPath(joined);
    }

    /// <summary><c>..\Other\Models\*.cs</c> or <c>Shared\**\*.cs</c>: the fixed directory part, then a glob below it.</summary>
    private static IEnumerable<string> MsBuildGlob(string basePath, string raw)
    {
        if (!raw.Contains('*', StringComparison.Ordinal) && !raw.Contains('?', StringComparison.Ordinal))
        {
            return [MsBuildPath(basePath, raw)];
        }

        var parts = raw.Replace('\\', '/').Split('/');
        var fixedCount = Array.FindIndex(parts, part => part.Contains('*', StringComparison.Ordinal) || part.Contains('?', StringComparison.Ordinal));
        var directory = fixedCount <= 0 ? System.IO.Path.GetFullPath(basePath) : MsBuildPath(basePath, string.Join('\\', parts[..fixedCount]));
        var pattern = string.Join('/', parts[fixedCount..]);
        if (!Directory.Exists(directory))
        {
            return [];
        }

        var matcher = Glob(pattern);
        return [.. Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Select(file => System.IO.Path.GetFullPath(file))
            .Where(file => matcher.IsMatch(System.IO.Path.GetRelativePath(directory, file).Replace(System.IO.Path.DirectorySeparatorChar, '/')))
            .Order(StringComparer.Ordinal)];
    }

    private static Regex Glob(string pattern)
    {
        var expression = new System.Text.StringBuilder("^");
        for (var index = 0; index < pattern.Length; index++)
        {
            var character = pattern[index];
            switch (character)
            {
                case '*':
                    if (index + 1 < pattern.Length && pattern[index + 1] == '*')
                    {
                        if (index + 2 < pattern.Length && pattern[index + 2] == '/')
                        {
                            expression.Append("(?:.*/)?");
                            index += 2;
                        }
                        else
                        {
                            expression.Append(".*");
                            index++;
                        }
                    }
                    else
                    {
                        expression.Append("[^/]*");
                    }

                    break;
                case '?':
                    expression.Append("[^/]");
                    break;
                case '/':
                    expression.Append('/');
                    break;
                default:
                    expression.Append(Regex.Escape(character.ToString(CultureInfo.InvariantCulture)));
                    break;
            }
        }

        return new Regex(expression.Append('$').ToString(), RegexOptions.CultureInvariant);
    }

    /// <summary>The project's own <c>.cs</c> files plus any it compiles in by <c>&lt;Compile Include=… Link=…&gt;</c>.</summary>
    public static IReadOnlyList<SourceFile> Sources(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        var paths = new HashSet<string>(StringComparer.Ordinal);
        if (Directory.Exists(project.Directory))
        {
            foreach (var path in Directory.EnumerateFiles(project.Directory, "*.cs", SearchOption.AllDirectories))
            {
                var relative = System.IO.Path.GetRelativePath(project.Directory, path);
                if (!relative.Split(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar).Any(part => SkipDirectories.Contains(part)))
                {
                    paths.Add(System.IO.Path.GetFullPath(path));
                }
            }
        }

        foreach (var link in project.CompileLinks.Where(File.Exists))
        {
            paths.Add(System.IO.Path.GetFullPath(link));
        }

        return [.. paths.Order(StringComparer.Ordinal).Select(ReadSource)];
    }

    /// <summary>Read one source file into the names and types the C4 scan needs.</summary>
    public static SourceFile ReadSource(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var raw = OkfBundle.NormaliseNewlines(File.ReadAllText(path));
        var code = Noise().Replace(raw, " ");
        var namespaceMatch = Namespace().Match(code);
        var kinds = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match match in Declaration().Matches(code))
        {
            kinds.TryAdd(match.Groups["name"].Value, match.Groups["kind"].Value.Split(' ')[0]);
        }

        var docs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match match in Documentation().Matches(raw))
        {
            docs[match.Groups["name"].Value] = Summary(match.Groups["doc"].Value);
        }

        var members = kinds.Count == 1 ? Members(code) : [];
        return new SourceFile(
            System.IO.Path.GetFullPath(path),
            namespaceMatch.Success ? namespaceMatch.Groups["name"].Value : string.Empty,
            [.. kinds.Keys.Order(StringComparer.Ordinal)],
            Identifier().Matches(Directive().Replace(code, " ")).Select(match => match.Value).ToHashSet(StringComparer.Ordinal),
            Generator().IsMatch(code),
            PythonLines(raw),
            [.. kinds.Keys.Order(StringComparer.Ordinal).Select(name => new TypeInfo(name, kinds[name], docs.GetValueOrDefault(name, string.Empty), members))]);
    }

    /// <summary><c>Parquet.SourceGenerator.Emitter.Compound</c> in a project rooted at <c>Parquet.SourceGenerator</c> is <c>Emitter.Compound</c>.</summary>
    public static string ComponentKey(Project project, string @namespace)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(@namespace);
        if (@namespace == project.RootNamespace)
        {
            return string.Empty;
        }

        var prefix = project.RootNamespace + ".";
        return @namespace.StartsWith(prefix, StringComparison.Ordinal) ? @namespace[prefix.Length..] : @namespace;
    }

    /// <summary>Group the files into the project's namespaces, skipping polyfills.</summary>
    public static IReadOnlyList<Component> Components(Project project, IReadOnlyList<SourceFile> files)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(files);
        return [.. files
            .Where(file => file.Declares.Count > 0 && !Polyfill.Any(prefix => file.Namespace.StartsWith(prefix, StringComparison.Ordinal)))
            .GroupBy(file => file.Namespace, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new Component(ComponentKey(project, group.Key), group.Key, [.. group]))];
    }

    /// <summary>Type name to the owner key that declares it. A name declared in two owners is dropped: it is ambiguous.</summary>
    public static IReadOnlyDictionary<string, string> TypeIndex(IReadOnlyDictionary<string, IReadOnlyList<string>> owners)
    {
        ArgumentNullException.ThrowIfNull(owners);
        var seen = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var (owner, types) in owners)
        {
            foreach (var name in types)
            {
                if (!seen.TryGetValue(name, out var ownerSet))
                {
                    ownerSet = new HashSet<string>(StringComparer.Ordinal);
                    seen[name] = ownerSet;
                }

                ownerSet.Add(owner);
            }
        }

        return seen.Where(pair => pair.Value.Count == 1).ToDictionary(pair => pair.Key, pair => pair.Value.First(), StringComparer.Ordinal);
    }

    /// <summary>Pathway 1. <c>(owner, other)</c> counted once per distinct type of <c>other</c> that <c>owner</c> names.</summary>
    public static Dictionary<CodeEdge, int> ReferenceEdges(
        IReadOnlyDictionary<string, IReadOnlyList<SourceFile>> owned,
        IReadOnlyDictionary<string, string> index)
    {
        ArgumentNullException.ThrowIfNull(owned);
        ArgumentNullException.ThrowIfNull(index);
        var edges = new Dictionary<CodeEdge, int>();
        foreach (var (owner, files) in owned)
        {
            var named = files.SelectMany(file => file.Names).ToHashSet(StringComparer.Ordinal);
            foreach (var name in named.Order(StringComparer.Ordinal))
            {
                if (index.TryGetValue(name, out var target) && target != owner)
                {
                    var edge = new CodeEdge(owner, target);
                    edges[edge] = edges.GetValueOrDefault(edge) + 1;
                }
            }
        }

        return edges;
    }

    /// <summary>Pathway 2. <paramref name="ownerOf"/> maps a fully qualified type to its owner; calls inside one owner are dropped.</summary>
    public static Dictionary<CodeEdge, int> CallEdges(string path, IReadOnlyDictionary<string, string> ownerOf)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(ownerOf);
        if (!File.Exists(path))
        {
            return [];
        }
        var graph = new CallGraphFile();
        graph.Read(OkfBundle.SplitLines(OkfBundle.NormaliseNewlines(File.ReadAllText(path))));
        var edges = new Dictionary<CodeEdge, int>();
        foreach (var call in graph.Calls)
        {
            if (ownerOf.TryGetValue(call.Source.Type, out var source) && ownerOf.TryGetValue(call.Target.Type, out var target) && source != target)
            {
                var edge = new CodeEdge(source, target);
                edges[edge] = edges.GetValueOrDefault(edge) + 1;
            }
        }

        return edges;
    }

    /// <summary><c>A.B.Type.Method/(args)</c> is declared on <c>A.B.Type</c>.</summary>
    public static string TypeOf(string member)
    {
        ArgumentNullException.ThrowIfNull(member);
        var head = member.Split('/', 2)[0];
        var index = head.LastIndexOf('.');
        return index < 0 ? head : head[..index];
    }

    /// <summary>Python's <c>len(text.splitlines())</c> for normalised text.</summary>
    internal static int PythonLines(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0)
        {
            return 0;
        }

        var count = text.Count(character => character == '\n');
        return text.EndsWith('\n') ? count : count + 1;
    }

    /// <summary>The first sentence of a <c>&lt;summary&gt;</c>, with crefs read as type names and other tags dropped.</summary>
    private static string Summary(string doc)
    {
        var text = string.Join('\n', doc.Split('\n').Select(line => RemovePrefix(line.Trim(), "///")));
        var found = SummaryTag().Match(text);
        if (!found.Success)
        {
            return string.Empty;
        }

        var plain = Cref().Replace(found.Groups["text"].Value, match => match.Groups["ref"].Value.Split('.').Last());
        plain = string.Join(' ', Tag().Replace(plain, string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var sentence = SentenceSplit().Split(plain).FirstOrDefault() ?? string.Empty;
        return sentence.Length <= SummaryCap ? sentence : sentence[..(SummaryCap - 1)].TrimEnd() + "…";
    }

    private static string RemovePrefix(string text, string prefix) =>
        text.StartsWith(prefix, StringComparison.Ordinal) ? text[prefix.Length..] : text;

    private static IReadOnlyList<string> Members(string code)
    {
        var seen = new List<string>();
        var distinct = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in Member().Matches(code))
        {
            if (match.Groups["type"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(TypeWords.Contains))
            {
                continue;
            }

            var shape = match.Groups["shape"].Value;
            var name = match.Groups["name"].Value + (shape is "(" or "<" ? "()" : string.Empty);
            if (distinct.Add(name))
            {
                seen.Add(name);
            }
        }

        return [.. seen.Take(MemberCap)];
    }

    private static readonly IReadOnlySet<string> TypeWords = new HashSet<string>(StringComparer.Ordinal)
    {
        "class", "struct", "interface", "enum", "record", "delegate", "event", "const", "operator",
    };

    [GeneratedRegex(@"\b(?<kind>class|struct|interface|enum|record(?:\s+struct|\s+class)?)\s+(?<name>[A-Z]\w*)")]
    private static partial Regex Declaration();

    [GeneratedRegex(@"(?<doc>(?:^[ \t]*///[^\n]*\n)+)(?:^[ \t]*\[[^\n]*\n)*^[^\n]*?\b(?:class|struct|interface|enum|record(?:\s+struct|\s+class)?)\s+(?<name>[A-Z]\w*)", RegexOptions.Multiline)]
    private static partial Regex Documentation();

    [GeneratedRegex(@"(?<=[.!?])\s")]
    private static partial Regex SentenceSplit();

    [GeneratedRegex(@"<summary>(?<text>[\s\S]*?)</summary>")]
    private static partial Regex SummaryTag();

    [GeneratedRegex(@"<(?:see|seealso|paramref|typeparamref)\s+(?:cref|name|langword)=""(?:[A-Z]:)?(?<ref>[^""]+)""\s*/>")]
    private static partial Regex Cref();

    [GeneratedRegex(@"</?[a-z][^>]*>")]
    private static partial Regex Tag();

    [GeneratedRegex(@"^[ \t]*public\s+(?:(?:static|override|virtual|abstract|sealed|async|readonly|required|partial|new|unsafe|extern)\s+)*(?<type>[\w.?<>\[\],() ]+?)\s+(?<name>[A-Z]\w*)\s*(?<shape>\(|<|\{|=>|;|=)", RegexOptions.Multiline)]
    private static partial Regex Member();

    [GeneratedRegex(@"^\s*namespace\s+(?<name>[\w.]+)", RegexOptions.Multiline)]
    private static partial Regex Namespace();

    [GeneratedRegex(@"\b[A-Z]\w*\b")]
    private static partial Regex Identifier();

    [GeneratedRegex(@"^\s*(?:global\s+)?(?:namespace|using)\s+(?!var\b)[\w.\s=]+;?\s*$", RegexOptions.Multiline)]
    private static partial Regex Directive();

    [GeneratedRegex(@":\s*(?:[\w.]+\.)?IIncrementalGenerator\b")]
    private static partial Regex Generator();

    // Comments and string literals: raw strings first (they may contain quotes), then verbatim, then plain.
    [GeneratedRegex("\"\"\"[\\s\\S]*?\"\"\"|@\"(?:[^\"]|\"\")*\"|\"(?:\\\\.|[^\"\\\\\\n])*\"|'(?:\\\\.|[^'\\\\\\n])*'|//[^\\n]*|/\\*[\\s\\S]*?\\*/")]
    private static partial Regex Noise();
}
