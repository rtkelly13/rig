using System.Text.Json;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Rig.Core.CommandSurface;
using Rig.Core.Processes;

namespace Rig.CommandOwnership;

internal static class Discovery
{
    internal static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
    private static readonly string[] AllPlatforms = ["linux", "macos", "windows"];
    private static readonly MetadataReference[] CommandReferences =
    [
        MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(System.CommandLine.RootCommand).Assembly.Location),
    ];

    internal readonly record struct CsCommand(string Path, bool Action, string[] Aliases);
    internal readonly record struct RawAlias(string Path, string Target);
    internal readonly record struct CsTree(CsCommand[] Commands, string[] Jobs)
    {
        public RawAlias[] RawAliases { get; init; } = [];
        public string[] AgentTargets { get; init; } = [];
        public string[] McpTools { get; init; } = [];
    }

    internal static string Capture(IProc proc, string root, params string[] argv)
    {
        var ran = proc.Run(new ProcRequest(argv) { WorkingDirectory = root, Timeout = TimeSpan.FromMinutes(2) });
        if (!ran.Ok)
        {
            throw new InvalidOperationException($"Discovery failed ({string.Join(' ', argv)}): {ran.Code}\n{ran.Err}");
        }

        return ran.Out;
    }

    internal static T Decode<T>(string json) => JsonSerializer.Deserialize<T>(json, Json) ?? throw new InvalidOperationException("Empty discovery output");

    internal static CommandOwner[] Read(string root, string dll, IProc proc)
    {
        var trees = new Dictionary<string, CsTree>(StringComparer.Ordinal);
        // The Windows-only registration branch is expanded without calling any native driver.
        trees.Add("linux", Decode<CsTree>(Capture(proc, root, "dotnet", dll, "--export-command-tree")));
        trees.Add("macos", Decode<CsTree>(Capture(proc, root, "dotnet", dll, "--export-command-tree", "macos")));
        trees.Add("windows", Decode<CsTree>(Capture(proc, root, "dotnet", dll, "--export-command-tree", "windows")));
        var rows = Commands(trees).ToList();
        var files = Capture(proc, root, "git", "ls-files", "--cached", "--others", "--exclude-standard", "-z").Split('\0', StringSplitOptions.RemoveEmptyEntries);
        rows.AddRange(Sources(root, files));
        var index = Capture(proc, root, "git", "ls-files", "--stage", "-z");
        foreach (var executable in index.Split('\0', StringSplitOptions.RemoveEmptyEntries).Where(entry => entry.StartsWith("100755 ", StringComparison.Ordinal)))
        {
            rows.Add(Surface($"executable-file:{executable[(executable.IndexOf('\t', StringComparison.Ordinal) + 1)..]}"));
        }

        foreach (var project in files.Where(f => f.EndsWith(".csproj", StringComparison.Ordinal) && !f.StartsWith("tests/", StringComparison.Ordinal)))
        {
            // Evaluate imports and SDK defaults too: OutputType may be inherited rather than present in project XML.
            var outputType = Capture(proc, root, "dotnet", "msbuild", project, "-nologo", "-getProperty:OutputType").Trim();
            if (outputType is "Exe" or "WinExe")
            {
                if (project == "src/Rig.Launcher/Rig.Launcher.csproj")
                {
                    // rigw has no command tree of its own: verify its sole action is the whole-line handoff
                    // to the production CLI entry point whose tree we inspected above.
                    CheckLauncher(File.ReadAllText(Path.Join(root, "src/Rig.Launcher/Launcher.cs")));
                }
                else if (project is not ("src/Rig.Cli/Rig.Cli.csproj" or "tools/command-ownership/CommandOwnership.csproj"))
                {
                    throw new InvalidOperationException($"Unsupported executable project: {project}; add its runtime discovery adapter");
                }

                rows.Add(Surface($"executable-project:{project}:{outputType}"));
            }
            else if (outputType != "Library")
            {
                throw new InvalidOperationException($"Unsupported evaluated project output type: {project}:{outputType}");
            }
        }
        var duplicate = rows.GroupBy(r => r.Id, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException($"Repeated executable registration: {duplicate.Key}");
        }

        return rows.OrderBy(r => r.Id, StringComparer.Ordinal).ToArray();
    }

    internal static void CheckLauncher(string source)
    {
        var syntax = CSharpSyntaxTree.ParseText(source).GetRoot();
        var members = syntax.DescendantNodes().OfType<MemberDeclarationSyntax>()
            .Where(member => member is not BaseNamespaceDeclarationSyntax).ToArray();
        if (members is not [ClassDeclarationSyntax type, MethodDeclarationSyntax method]
            || type.Identifier.ValueText != "Launcher"
            || syntax.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>().SingleOrDefault()?.Name.ToString() != "Rig.Launcher"
            || syntax.DescendantNodes().OfType<UsingDirectiveSyntax>().Any(usingDirective => usingDirective.Name?.ToString() != "System.Diagnostics.CodeAnalysis" || usingDirective.Alias is not null)
            || method.NormalizeWhitespace().ToFullString() != "private static Task<int> Main(string[] args) => Cli.Program.Main(args);")
        {
            throw new InvalidOperationException("Unsupported rigw launcher: expected only the unchanged-argument handoff to Cli.Program.Main");
        }
    }

    internal static IEnumerable<CommandOwner> Commands(IReadOnlyDictionary<string, CsTree> trees)
    {
        foreach (var alias in trees.Values.SelectMany(t => t.RawAliases).Distinct())
        {
            yield return new CommandOwner($"cs-raw-alias:{alias.Path}", alias.Target, $"cs:{alias.Target}", AllPlatforms, $"cs:{alias.Target}");
        }

        foreach (var target in trees.Values.SelectMany(t => t.AgentTargets).Distinct(StringComparer.Ordinal))
        {
            yield return Surface($"agent-target:{target}");
        }

        // The MCP server's actual tool attributes, read from the attributes, not a registration loop.
        foreach (var tool in trees.Values.SelectMany(t => t.McpTools).Distinct(StringComparer.Ordinal))
        {
            yield return Surface($"mcp:{tool}");
        }

        var commands = trees.SelectMany(t => t.Value.Commands.Select(c => (Platform: t.Key, Command: c)))
            .GroupBy(row => row.Command.Path, StringComparer.Ordinal);
        foreach (var command in commands)
        {
            if (command.GroupBy(c => c.Platform, StringComparer.Ordinal).Any(g => g.Count() > 1))
            {
                throw new InvalidOperationException($"Multiple C# registrations for one path: {command.Key}");
            }

            var platforms = command.Select(c => c.Platform).Order(StringComparer.Ordinal).ToArray();
            yield return new CommandOwner($"cs:{command.Key}", command.Key, $"cs:{command.Key}", platforms);
            foreach (var alias in command.SelectMany(c => c.Command.Aliases).Distinct(StringComparer.Ordinal))
            {
                var words = command.Key.Split(' ');
                words[^1] = alias;
                var path = string.Join(' ', words);
                var aliasPlatforms = command.Where(c => c.Command.Aliases.Contains(alias, StringComparer.Ordinal)).Select(c => c.Platform).Order(StringComparer.Ordinal).ToArray();
                yield return new CommandOwner($"cs-alias:{path}", command.Key, $"cs:{command.Key}", aliasPlatforms, $"cs:{command.Key}");
            }
        }

        foreach (var tree in trees)
        {
            foreach (var job in tree.Value.Jobs)
            {
                yield return Surface($"cs-job:{tree.Key}:{job}");
            }
        }
    }

    internal static CommandOwner Surface(string id) => new(id, $"surface:{id}", id, AllPlatforms);

    // Parse C# syntax instead of grep: catches new mains and top-level apps, including alternate executable projects.
    internal static IEnumerable<CommandOwner> Sources(string root, IEnumerable<string> files)
    {
        foreach (var file in files.Where(f => !f.StartsWith("tests/", StringComparison.Ordinal)))
        {
            var full = Path.Join(root, file);
            if (file.EndsWith(".csproj", StringComparison.Ordinal))
            {
                var project = XDocument.Load(full);
                yield return Surface($"project:{file}:{project.Root?.Attribute("Sdk")?.Value}");
            }
            else if (file.EndsWith(".cs", StringComparison.Ordinal))
            {
                var text = File.ReadAllText(full);
                // File-app directives are consumed by the SDK, not Roslyn's normal syntax parser.
                var parsed = CSharpSyntaxTree.ParseText(string.Join('\n', text.Split('\n').Where(l => !l.StartsWith("#:", StringComparison.Ordinal))));
                foreach (var method in parsed.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().Where(m => m.Identifier.ValueText == "Main" && m.Modifiers.Any(m => m.ValueText == "static")))
                {
                    yield return Surface($"main:{file}:{method.Identifier.ValueText}");
                }

                var semantic = CSharpCompilation.Create("CommandInspection", [parsed], CommandReferences).GetSemanticModel(parsed);
                foreach (var creation in parsed.GetRoot().DescendantNodes().OfType<BaseObjectCreationExpressionSyntax>()
                    .Where(c => semantic.GetTypeInfo(c).Type?.Name == "RootCommand"))
                {
                    var method = creation.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault()?.Identifier.ValueText ?? "top-level";
                    yield return Surface($"command-root:{file}:{method}");
                }

                if (parsed.GetRoot().ChildNodes().OfType<GlobalStatementSyntax>().Any())
                {
                    yield return Surface($"file-app:{file}");
                }
            }
            else if (file.EndsWith("package.json", StringComparison.Ordinal))
            {
                var package = JsonDocument.Parse(File.ReadAllText(full));
                foreach (var property in new[] { "bin", "scripts" })
                {
                    if (package.RootElement.TryGetProperty(property, out var entries))
                    {
                        if (entries.ValueKind == JsonValueKind.Object)
                        {
                            foreach (var entry in entries.EnumerateObject())
                            {
                                yield return Surface($"node:{file}:{property}:{entry.Name}={entry.Value.GetString()}");
                            }
                        }
                        else if (property == "bin" && entries.ValueKind == JsonValueKind.String)
                        {
                            yield return Surface($"node:{file}:bin={entries.GetString()}");
                        }
                        else
                        {
                            throw new InvalidOperationException($"Unsupported Node executable declaration: {file}:{property}");
                        }
                    }
                }
            }
            else if (Path.GetExtension(file) is ".cmd" or ".bat" or ".sh" or ".ps1" or ".plist")
            {
                yield return Surface($"launcher:{file}:{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(full)))}");
            }
            else if (file.StartsWith("scripts/", StringComparison.Ordinal) && Path.GetExtension(file) is ".js" or ".mjs" or ".cjs" or ".ts")
            {
                yield return Surface($"node-file:{file}");
            }
        }
    }
}
