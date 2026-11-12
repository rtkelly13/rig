#:package Microsoft.Build.Locator@1.9.1
#:package Microsoft.CodeAnalysis.CSharp.Workspaces@5.0.0
#:package Microsoft.CodeAnalysis.Workspaces.MSBuild@5.0.0
#:property PublishAot=false

// The call graph behind `rig c4 derive dotnet --callgraph`: what each type declares, and which member calls which.
//
//   dotnet run callgraph.cs -- <solution.sln|.slnx> <out-dir>
//
// Opens the solution as MSBuild sees it, so every name is bound by the compiler rather than guessed from text,
// and writes one <Project>.callgraph.txt per project, sorted so two runs over one commit give identical files:
//
//   T:<Ns>.<Type>|<kind>|<accessibility>|<path>:<line>
//   M:<Ns>.<Type>.<Member>/(<parameter types>)|<kind>|<accessibility>|<line>
//   E:<caller member> | <callee member>         a call, a `new`, or a method passed as a delegate
//   X:<interface member> | <implementing member>  where a call through an interface lands
//
// A nested type is written as if it sat in its namespace (`Ns.Inner`, not `Ns.Outer.Inner`), as the text scan
// names it. Only members declared in the solution appear; a call into the BCL or a package is not an edge.
// Code under obj/ (source generators, global usings) is left out: nobody reads it to understand the design.
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

if (args.Length != 2)
{
    Console.Error.WriteLine("usage: dotnet run callgraph.cs -- <solution> <out-dir>");
    return 2;
}

MSBuildLocator.RegisterDefaults();
return await CallGraph.Run(Path.GetFullPath(args[0]), Path.GetFullPath(args[1]));

static class CallGraph
{
    public static async Task<int> Run(string solutionPath, string outDir)
    {
        using var workspace = Microsoft.CodeAnalysis.MSBuild.MSBuildWorkspace.Create();
        workspace.RegisterWorkspaceFailedHandler(e =>
        {
            if (e.Diagnostic.Kind == WorkspaceDiagnosticKind.Failure)
                Console.Error.WriteLine($"warning: {e.Diagnostic.Message}");
        });
        var solution = await workspace.OpenSolutionAsync(solutionPath);
        var root = Path.GetDirectoryName(solutionPath)!;
        Directory.CreateDirectory(outDir);
        var written = 0;
        foreach (var project in solution.Projects.OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            if (project.Language != LanguageNames.CSharp) continue;
            var compilation = await project.GetCompilationAsync();
            if (compilation is null) continue;
            var lines = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var tree in compilation.SyntaxTrees)
            {
                if (!Own(tree.FilePath)) continue;
                var model = compilation.GetSemanticModel(tree);
                new Walker(model, root, lines).Visit(await tree.GetRootAsync());
            }
            var name = Path.GetFileNameWithoutExtension(project.FilePath) ?? project.Name;
            await File.WriteAllLinesAsync(Path.Combine(outDir, $"{name}.callgraph.txt"), lines);
            Console.WriteLine($"{name}: {lines.Count(l => l.StartsWith("T:"))} types, " +
                $"{lines.Count(l => l.StartsWith("M:"))} members, {lines.Count(l => l.StartsWith("E:"))} calls");
            written++;
        }
        return written > 0 ? 0 : 1;
    }

    static bool Own(string path) =>
        path.Length > 0 && !path.Replace('\\', '/').Contains("/obj/", StringComparison.OrdinalIgnoreCase);

    public static bool InSource(ISymbol symbol) =>
        symbol.Locations.Any(l => l.IsInSource && Own(l.SourceTree!.FilePath));

    public static string TypeId(INamedTypeSymbol type)
    {
        var ns = type.ContainingNamespace is { IsGlobalNamespace: false } n ? n.ToDisplayString() + "." : "";
        return ns + type.Name;
    }

    /// <summary>The member a symbol belongs to: an accessor is its property, a local function its host.</summary>
    public static ISymbol Owner(ISymbol symbol)
    {
        var s = symbol;
        while (s is IMethodSymbol { MethodKind: MethodKind.LocalFunction
               or MethodKind.AnonymousFunction } && s.ContainingSymbol is { } outer)
            s = outer;
        if (s is IMethodSymbol { AssociatedSymbol: { } property }) s = property;
        return s.OriginalDefinition;
    }

    public static string MemberId(ISymbol member)
    {
        var type = TypeId(member.ContainingType);
        var name = member switch
        {
            IMethodSymbol { MethodKind: MethodKind.Constructor } => ".ctor",
            IMethodSymbol { MethodKind: MethodKind.StaticConstructor } => ".cctor",
            _ => member.Name,
        };
        var parameters = member switch
        {
            IMethodSymbol m => m.Parameters,
            IPropertySymbol p => p.Parameters,
            _ => [],
        };
        var shown = string.Join(", ", parameters.Select(p => p.Type.ToDisplayString(Short)));
        return $"{type}.{name}/({shown})";
    }

    static readonly SymbolDisplayFormat Short = new(
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameOnly,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes
            | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    public static string Kind(INamedTypeSymbol type) => type switch
    {
        { IsRecord: true, TypeKind: TypeKind.Struct } => "record struct",
        { IsRecord: true } => "record",
        _ => type.TypeKind.ToString().ToLowerInvariant(),
    };

    public static string Access(ISymbol symbol) =>
        symbol.DeclaredAccessibility.ToString().ToLowerInvariant();

    public static string Rel(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');
}

sealed class Walker(SemanticModel model, string root, SortedSet<string> lines)
    : CSharpSyntaxWalker
{
    public override void VisitClassDeclaration(ClassDeclarationSyntax node) { Declare(node); base.VisitClassDeclaration(node); }
    public override void VisitStructDeclaration(StructDeclarationSyntax node) { Declare(node); base.VisitStructDeclaration(node); }
    public override void VisitInterfaceDeclaration(InterfaceDeclarationSyntax node) { Declare(node); base.VisitInterfaceDeclaration(node); }
    public override void VisitRecordDeclaration(RecordDeclarationSyntax node) { Declare(node); base.VisitRecordDeclaration(node); }
    public override void VisitEnumDeclaration(EnumDeclarationSyntax node) { Declare(node); base.VisitEnumDeclaration(node); }

    void Declare(BaseTypeDeclarationSyntax node)
    {
        if (model.GetDeclaredSymbol(node) is not { } type) return;
        var id = CallGraph.TypeId(type);
        var line = node.Identifier.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        lines.Add($"T:{id}|{CallGraph.Kind(type)}|{CallGraph.Access(type)}|{CallGraph.Rel(root, node.SyntaxTree.FilePath)}:{line}");
        foreach (var member in type.GetMembers())
        {
            if (member.IsImplicitlyDeclared || !member.Locations.Any(l => l.SourceTree == node.SyntaxTree)) continue;
            var kind = member switch
            {
                IMethodSymbol { MethodKind: MethodKind.Constructor
                    or MethodKind.StaticConstructor } => "ctor",
                IMethodSymbol { MethodKind: MethodKind.Ordinary } => "method",
                IMethodSymbol { MethodKind: MethodKind.UserDefinedOperator
                    or MethodKind.Conversion } => "operator",
                IPropertySymbol => "property",
                IFieldSymbol { ContainingType.TypeKind: TypeKind.Enum } => "value",
                IFieldSymbol f when f.IsConst => "const",
                IFieldSymbol => "field",
                IEventSymbol => "event",
                _ => null,
            };
            if (kind is null) continue;
            var at = member.Locations.First(l => l.SourceTree == node.SyntaxTree).GetLineSpan().StartLinePosition.Line + 1;
            lines.Add($"M:{CallGraph.MemberId(member)}|{kind}|{CallGraph.Access(member)}|{at}");
            if (member is IFieldSymbol) continue;
            foreach (var iface in type.AllInterfaces)
            foreach (var slot in iface.GetMembers())
            {
                if (!CallGraph.InSource(slot)) continue;
                if (SymbolEqualityComparer.Default.Equals(type.FindImplementationForInterfaceMember(slot), member))
                    lines.Add($"X:{CallGraph.MemberId(slot.OriginalDefinition)} | {CallGraph.MemberId(member)}");
            }
            if (member is IMethodSymbol { OverriddenMethod: { } over } && CallGraph.InSource(over))
                lines.Add($"X:{CallGraph.MemberId(over.OriginalDefinition)} | {CallGraph.MemberId(member)}");
        }
    }

    public override void VisitInvocationExpression(InvocationExpressionSyntax node)
    {
        Edge(node, model.GetSymbolInfo(node).Symbol);
        base.VisitInvocationExpression(node);
    }

    public override void VisitObjectCreationExpression(ObjectCreationExpressionSyntax node)
    {
        Edge(node, model.GetSymbolInfo(node).Symbol);
        base.VisitObjectCreationExpression(node);
    }

    public override void VisitImplicitObjectCreationExpression(ImplicitObjectCreationExpressionSyntax node)
    {
        Edge(node, model.GetSymbolInfo(node).Symbol);
        base.VisitImplicitObjectCreationExpression(node);
    }

    public override void VisitIdentifierName(IdentifierNameSyntax node)
    {
        // A method group passed as a delegate (`items.Select(Parse)`) is a call that happens later.
        var invoked = node.Parent is InvocationExpressionSyntax
            || node.Parent is MemberAccessExpressionSyntax { Parent: InvocationExpressionSyntax } access
               && access.Name == node;
        if (!invoked && model.GetSymbolInfo(node).Symbol is IMethodSymbol method
            && method.MethodKind == MethodKind.Ordinary)
            Edge(node, method);
        base.VisitIdentifierName(node);
    }

    void Edge(SyntaxNode at, ISymbol? callee)
    {
        if (callee is not IMethodSymbol method) return;
        if (method.ReducedFrom is { } extension) method = extension;
        var target = CallGraph.Owner(method);
        if (target.ContainingType is null || !CallGraph.InSource(target)) return;
        if (model.GetEnclosingSymbol(at.SpanStart) is not { } enclosing) return;
        var source = CallGraph.Owner(enclosing);
        if (source.ContainingType is null) return;
        // A field initialiser runs in the constructor.
        if (source is IFieldSymbol) return;
        var from = CallGraph.MemberId(source);
        var to = CallGraph.MemberId(target);
        if (from != to) lines.Add($"E:{from} | {to}");
    }
}
