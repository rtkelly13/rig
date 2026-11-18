using System.Globalization;

namespace Rig.Core.C4;

/// <summary>A member by type and name: <c>Ns.StatsRunner</c> + <c>Run</c>. A constructor is <c>new</c>.</summary>
/// <param name="Type">The declaring type's fully qualified name.</param>
/// <param name="Name">The member's name, or <c>new</c> for a constructor.</param>
public sealed record MemberKey(string Type, string Name);

/// <summary>An edge: <paramref name="Source"/> calls <paramref name="Target"/>; in implements edges, a call to source lands on target.</summary>
/// <param name="Source">The caller, or the abstract member.</param>
/// <param name="Target">The callee, or the implementation.</param>
public sealed record CallEdge(MemberKey Source, MemberKey Target);

/// <summary>A type the Roslyn call graph declares.</summary>
/// <param name="Id">Its fully qualified name; nested types sit in their namespace.</param>
/// <param name="Kind">Its declaration kind.</param>
/// <param name="Access">Its declared accessibility, lower-cased.</param>
/// <param name="Path">Its source path relative to the solution.</param>
/// <param name="Line">Its one-based line.</param>
public sealed record TypeDecl(string Id, string Kind, string Access, string Path, int Line)
{
    /// <summary>Its namespace, or an empty string in the global namespace.</summary>
    public string Namespace => Id.Contains('.', StringComparison.Ordinal) ? Id[..Id.LastIndexOf('.')] : string.Empty;

    /// <summary>Its name without its namespace.</summary>
    public string Name => Id[(Id.LastIndexOf('.') + 1)..];
}

/// <summary>One declared member and the signature it was written with.</summary>
/// <param name="Key">The member it declares.</param>
/// <param name="Kind">Its kind: method, ctor, property, operator, field, const, value or event.</param>
/// <param name="Access">Its declared accessibility, lower-cased.</param>
/// <param name="Line">Its one-based line.</param>
/// <param name="Signature">Its parameter list as written.</param>
public sealed record MemberDecl(MemberKey Key, string Kind, string Access, int Line, string Signature);

/// <summary>
/// The compiler's view of a .NET solution, read from the files the Roslyn callgraph generator writes.
/// One <c>&lt;Project&gt;.callgraph.txt</c> per project, one record per line:
/// <list type="bullet">
/// <item><c>T:&lt;Ns&gt;.&lt;Type&gt;|&lt;kind&gt;|&lt;access&gt;|&lt;path&gt;:&lt;line&gt;</c>: a declared type.</item>
/// <item><c>M:&lt;Ns&gt;.&lt;Type&gt;.&lt;Member&gt;/(&lt;params&gt;)|&lt;kind&gt;|&lt;access&gt;|&lt;line&gt;</c>: a declared member.</item>
/// <item><c>E:&lt;caller&gt; | &lt;callee&gt;</c>: a call, a <c>new</c>, or a method group passed as a delegate.</item>
/// <item><c>X:&lt;interface or base member&gt; | &lt;implementation&gt;</c>: where a call through an abstraction lands.</item>
/// </list>
/// Members are read by name, not by signature: overloads are one node, because a reader follows who calls <c>Run</c>,
/// not which overload.
/// </summary>
public sealed class CallGraphFile
{
    /// <summary>Kinds drawn as nodes. Fields, constants and enum values are listed in the notes, not drawn.</summary>
    public static readonly IReadOnlySet<string> Callable = new HashSet<string>(StringComparer.Ordinal)
    {
        "method", "ctor", "property", "operator",
    };

    /// <summary>Every declared type, keyed by fully qualified name.</summary>
    public Dictionary<string, TypeDecl> Types { get; } = new(StringComparer.Ordinal);

    /// <summary>Every declared member, keyed by type and name.</summary>
    public Dictionary<MemberKey, List<MemberDecl>> Members { get; } = [];

    /// <summary>Every call edge.</summary>
    public HashSet<CallEdge> Calls { get; } = [];

    /// <summary>Every interface or base-member edge.</summary>
    public HashSet<CallEdge> Implements { get; } = [];

    /// <summary>The union of the named projects' files, or null when none of them is there.</summary>
    /// <param name="directory">The folder holding the callgraph files.</param>
    /// <param name="projects">The project names to read.</param>
    public static CallGraphFile? Load(string directory, IEnumerable<string> projects)
    {
        var graph = new CallGraphFile();
        var found = false;
        foreach (var name in projects.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var path = System.IO.Path.Combine(directory, $"{name}.callgraph.txt");
            if (!File.Exists(path))
            {
                continue;
            }

            found = true;
            graph.Read(OkfBundle.SplitLines(OkfBundle.NormaliseNewlines(File.ReadAllText(path))));
        }

        return found ? graph : null;
    }

    /// <summary>Read the records of one callgraph file.</summary>
    public void Read(IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            var separator = line.IndexOf(':', StringComparison.Ordinal);
            var tag = separator < 0 ? line : line[..separator];
            var rest = separator < 0 ? string.Empty : line[(separator + 1)..];
            switch (tag)
            {
                case "T":
                    ReadType(rest);
                    break;
                case "M":
                    ReadMember(rest);
                    break;
                case "E":
                case "X":
                    ReadCall(tag, rest);
                    break;
                default:
                    break;
            }
        }
    }

    private void ReadType(string rest)
    {
        var fields = rest.Split('|');
        if (fields.Length != 4)
        {
            return;
        }

        var where = fields[3];
        var colon = where.LastIndexOf(':');
        if (colon < 0 || !int.TryParse(where[(colon + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var line))
        {
            return;
        }

        Types.TryAdd(fields[0], new TypeDecl(fields[0], fields[1], fields[2], where[..colon], line));
    }

    private void ReadMember(string rest)
    {
        var fields = rest.Split('|');
        if (fields.Length != 4 || !int.TryParse(fields[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var line))
        {
            return;
        }

        var key = Key(fields[0]);
        var slash = fields[0].IndexOf('/', StringComparison.Ordinal);
        var signature = slash < 0 ? string.Empty : fields[0][(slash + 1)..];
        var declaration = new MemberDecl(key, fields[1], fields[2], line, signature);
        if (!Members.TryGetValue(key, out var declarations))
        {
            declarations = [];
            Members[key] = declarations;
        }

        if (!declarations.Contains(declaration))
        {
            declarations.Add(declaration);
        }
    }

    private void ReadCall(string tag, string rest)
    {
        var separator = rest.IndexOf(" | ", StringComparison.Ordinal);
        if (separator < 0)
        {
            return;
        }

        var call = new CallEdge(Key(rest[..separator]), Key(rest[(separator + 3)..]));
        (tag == "E" ? Calls : Implements).Add(call);
    }

    /// <summary>Whether the member is drawn as a callable node.</summary>
    public bool IsCallable(MemberKey key)
    {
        return Members.TryGetValue(key, out var declarations) && declarations.Any(declaration => Callable.Contains(declaration.Kind));
    }

    /// <summary>Every member declared on <paramref name="typeId"/>, sorted.</summary>
    public IReadOnlyList<MemberKey> OfType(string typeId)
    {
        return [.. Members.Keys.Where(key => key.Type == typeId).OrderBy(key => key.Type, StringComparer.Ordinal).ThenBy(key => key.Name, StringComparer.Ordinal)];
    }

    private static readonly IReadOnlySet<string> Called = new HashSet<string>(StringComparer.Ordinal) { "method", "ctor", "operator" };

    /// <summary><c>Run()</c> for something called, <c>Count</c> for a property or field.</summary>
    public string Label(MemberKey key)
    {
        var kinds = Members.TryGetValue(key, out var declarations) ? declarations.Select(declaration => declaration.Kind).ToHashSet(StringComparer.Ordinal) : [];
        return key.Name + (kinds.Overlaps(Called) || kinds.Count == 0 ? "()" : string.Empty);
    }

    /// <summary><c>A.B.Type.Run/(int)</c> is <c>Run</c> on <c>A.B.Type</c>; <c>A.B.Type..ctor/()</c> is <c>new</c>.</summary>
    public static MemberKey Key(string identifier)
    {
        var head = identifier.Split('/', 2)[0];
        if (head.EndsWith("..ctor", StringComparison.Ordinal))
        {
            return new MemberKey(head[..^"..ctor".Length], "new");
        }

        if (head.EndsWith("..cctor", StringComparison.Ordinal))
        {
            return new MemberKey(head[..^"..cctor".Length], "static new");
        }

        var index = head.LastIndexOf('.');
        return index < 0 ? new MemberKey(string.Empty, head) : new MemberKey(head[..index], head[(index + 1)..]);
    }

    /// <summary>The type a member identifier is declared on.</summary>
    public static string TypeOf(string identifier) => Key(identifier).Type;
}
