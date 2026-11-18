using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Rig.NullGuards;

internal static class Codemod
{
    internal static Tally Run(string csproj, bool dryRun)
    {
        var dir = Path.GetDirectoryName(csproj)!;
        var tally = new Tally();
        var libraryApi = IsLibrary(csproj);
        var sources = Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories)
            .Where(path => !Segments(dir, path).Any(segment => segment is "bin" or "obj"))
            .Order(StringComparer.Ordinal)
            .ToList();
        var obj = Path.Join(dir, "obj");
        var generated = Directory.Exists(obj) ? Directory.EnumerateFiles(obj, "*.GlobalUsings.g.cs", SearchOption.AllDirectories) : [];
        var options = new CSharpParseOptions(LanguageVersion.Latest);
        var trees = sources.Select(path => CSharpSyntaxTree.ParseText(File.ReadAllText(path), options, path)).ToList();
        var allTrees = trees.Concat(generated.Select(path => CSharpSyntaxTree.ParseText(File.ReadAllText(path), options, path))).ToList();
        var compilation = CSharpCompilation.Create(
            Path.GetFileNameWithoutExtension(csproj),
            allTrees,
            References(dir),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        foreach (var tree in trees)
        {
            var model = compilation.GetSemanticModel(tree);
            var doomed = new List<ExpressionStatementSyntax>();
            foreach (var statement in tree.GetRoot().DescendantNodes().OfType<ExpressionStatementSyntax>())
            {
                if (statement.Expression is not InvocationExpressionSyntax call || Resolve(model, call) is not { } method || !IsThrowIfNull(method))
                {
                    continue;
                }

                var reason = Classify(statement, call, model, libraryApi);
                if (reason is null)
                {
                    doomed.Add(statement);
                    tally.Remove();
                }
                else
                {
                    tally.Keep(reason);
                }
            }

            if (doomed.Count > 0 && !dryRun)
            {
                File.WriteAllText(tree.FilePath, Edit(tree.GetText().ToString(), doomed, tree));
            }
        }

        return tally;
    }

    private static IMethodSymbol? Resolve(SemanticModel model, InvocationExpressionSyntax call)
    {
        var info = model.GetSymbolInfo(call);
        return info.Symbol as IMethodSymbol ?? info.CandidateSymbols.OfType<IMethodSymbol>().FirstOrDefault();
    }

    private static string[] Segments(string dir, string path) => Path.GetRelativePath(dir, path).Split(Path.DirectorySeparatorChar);

    private static bool IsThrowIfNull(IMethodSymbol method) =>
        method.Name == "ThrowIfNull" && method.ContainingType.ToDisplayString() == "System.ArgumentNullException";

    /// <summary>Null when the guard is safe to delete, otherwise the reason it stays.</summary>
    private static string? Classify(ExpressionStatementSyntax statement, InvocationExpressionSyntax call, SemanticModel model, bool libraryApi)
    {
        var argument = call.ArgumentList.Arguments[0].Expression;
        if (model.GetSymbolInfo(argument).Symbol is not IParameterSymbol parameter)
        {
            return "not a parameter";
        }

        if (parameter.Type.TypeKind == TypeKind.Error)
        {
            return "unresolved type";
        }

        if (parameter.Type.TypeKind == TypeKind.TypeParameter)
        {
            return "generic";
        }

        if (!parameter.Type.IsReferenceType || parameter.RefKind != RefKind.None)
        {
            return "not a plain reference type";
        }

        if (parameter.NullableAnnotation != NullableAnnotation.NotAnnotated)
        {
            return "nullable annotated";
        }

        if (!model.GetNullableContext(statement.SpanStart).AnnotationsEnabled())
        {
            return "nullable disabled";
        }

        if (libraryApi && model.GetEnclosingSymbol(statement.SpanStart) is { } owner && IsPublicApi(owner))
        {
            return "library public API";
        }

        var line = statement.SyntaxTree.GetText().Lines.GetLineFromPosition(statement.SpanStart);
        var hasTrivia = statement.GetLeadingTrivia().Any(t => !t.IsKind(SyntaxKind.WhitespaceTrivia) && !t.IsKind(SyntaxKind.EndOfLineTrivia)) || statement.GetTrailingTrivia().Any(t => t.IsKind(SyntaxKind.SingleLineCommentTrivia) || t.IsKind(SyntaxKind.MultiLineCommentTrivia));
        return statement.Parent is BlockSyntax && !hasTrivia && line.ToString().Trim() == statement.ToString() ? null : "shares its line or carries a comment";
    }

    private static bool IsPublicApi(ISymbol symbol)
    {
        for (var current = symbol; current is not null && current.Kind != SymbolKind.Namespace; current = current.ContainingSymbol)
        {
            if (current.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Protected or Accessibility.ProtectedOrInternal or Accessibility.NotApplicable))
            {
                return false;
            }
        }

        return true;
    }

    // A packed project exposes a library surface unless it is a dotnet tool.
    private static bool IsLibrary(string csproj)
    {
        var props = XDocument.Load(csproj).Descendants("PropertyGroup").Elements().GroupBy(e => e.Name.LocalName, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Last().Value.Trim(), StringComparer.Ordinal);
        var packable = props.GetValueOrDefault("IsPackable") == "true" || props.ContainsKey("PackageId");
        return packable && props.GetValueOrDefault("PackAsTool") != "true";
    }

    private static List<MetadataReference> References(string dir)
    {
        var runtime = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        var built = Directory.Exists(Path.Join(dir, "bin", "Debug", "net10.0")) ? Directory.EnumerateFiles(Path.Join(dir, "bin", "Debug", "net10.0"), "*.dll") : [];
        var shared = Path.Join(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "Microsoft.AspNetCore.App", Path.GetFileName(Path.TrimEndingDirectorySeparator(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory())));
        var web = Directory.Exists(shared) ? Directory.EnumerateFiles(shared, "*.dll") : [];
        return [.. runtime.Concat(built).Concat(web).Where(path => path.EndsWith(".dll", StringComparison.Ordinal)).Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))];
    }

    /// <summary>Deletes each statement's line, then tidies the blank line a removed guard block leaves behind.</summary>
    private static string Edit(string text, List<ExpressionStatementSyntax> doomed, SyntaxTree tree)
    {
        var lines = text.Split('\n').ToList();
        var removed = doomed.Select(s => tree.GetLineSpan(s.Span).StartLinePosition.Line).ToHashSet();
        var kept = new List<string>();
        for (var i = 0; i < lines.Count; i++)
        {
            if (removed.Contains(i))
            {
                continue;
            }

            var afterRemoval = i > 0 && removed.Contains(i - 1);
            var blank = lines[i].Trim().Length == 0;
            var previous = kept.Count == 0 ? string.Empty : kept[^1].Trim();
            if (afterRemoval && blank && (previous.Length == 0 || previous.EndsWith('{')))
            {
                continue;
            }

            kept.Add(lines[i]);
        }

        return string.Join('\n', kept);
    }
}
