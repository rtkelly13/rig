using System.Text.Json;
using System.Xml.Linq;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Rig.Core.CommandSurface;
using Rig.Core.Processes;

namespace Rig.CommandOwnership;

internal static class Discovery
{
    internal static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
    private static readonly string[] AllPlatforms = ["linux", "macos", "windows"];

    internal readonly record struct PythonCommand(string Id, string Path, string Owner, string[] Platforms, string? Alias);
    internal readonly record struct PythonTree(PythonCommand[] Commands, string[] Surfaces);
    internal readonly record struct CsCommand(string Path, bool Action, string[] Aliases, bool Forwarded);
    internal readonly record struct Forwarding(string Name, string[] Launch);
    internal readonly record struct Dispatch(string Path, bool Forwarded);
    internal readonly record struct RawAlias(string Path, string Target);
    internal readonly record struct CsTree(CsCommand[] Commands, Forwarding[] Forwarding, string[] Bare, Dispatch[] Dispatch, string[] Jobs)
    {
        public RawAlias[] RawAliases { get; init; } = [];
        public string[] AgentTargets { get; init; } = [];
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
        var python = Decode<PythonTree>(Capture(proc, root, "uv", "run", "--no-sync", "python", "tools/command-ownership/python_tree.py", root));
        var trees = new Dictionary<string, CsTree>(StringComparer.Ordinal);
        // The Windows-only registration branch is expanded without calling any native driver.
        var portable = Decode<CsTree>(Capture(proc, root, "dotnet", dll, "--export-command-tree"));
        var probes = portable.Forwarding.SelectMany(route => Children(route, python).Select(child => (route.Name + child.Path[Prefix(route).Length..]).Split(' '))).ToArray();
        var temporary = Path.Join(root, "temp", "command-ownership-probes.json");
        Directory.CreateDirectory(Path.GetDirectoryName(temporary)!);
        File.WriteAllText(temporary, JsonSerializer.Serialize(probes));
        portable = Decode<CsTree>(Capture(proc, root, "dotnet", dll, "--export-command-tree", "--probes", temporary));
        trees.Add("linux", portable);
        trees.Add("macos", portable);
        trees.Add("windows", Decode<CsTree>(Capture(proc, root, "dotnet", dll, "--export-command-tree", "windows", "--probes", temporary)));
        var rows = Commands(python, trees).ToList();
        rows.AddRange(python.Surfaces.Select(id => EntrySurface(id)));
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
                if (project is not ("src/Rig.Cli/Rig.Cli.csproj" or "tools/command-ownership/CommandOwnership.csproj"))
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

    internal static IEnumerable<CommandOwner> Commands(PythonTree python, IReadOnlyDictionary<string, CsTree> trees)
    {
        foreach (var alias in trees.Values.SelectMany(t => t.RawAliases).Distinct())
        {
            yield return new CommandOwner($"cs-raw-alias:{alias.Path}", alias.Target, $"cs:{alias.Target}", AllPlatforms, $"cs:{alias.Target}");
        }

        foreach (var target in trees.Values.SelectMany(t => t.AgentTargets).Distinct(StringComparer.Ordinal))
        {
            yield return Surface($"agent-target:{target}");
        }

        var commands = trees.SelectMany(t => t.Value.Commands.Where(c => !c.Forwarded).Select(c => (Platform: t.Key, Command: c)))
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

        foreach (var command in python.Commands)
        {
            var capability = Canonical(command.Path);
            yield return command.Alias is { } target
                ? new CommandOwner(command.Id, target, $"cs:{target}", command.Platforms, $"cs:{target}")
                : new CommandOwner(command.Id, capability, command.Owner, command.Platforms);
        }

        // Expand actual fallback rows into exact Python leaves, checking the production routing decision at each one.
        foreach (var tree in trees)
        {
            foreach (var job in tree.Value.Jobs)
            {
                yield return Surface($"cs-job:{tree.Key}:{job}");
            }

            foreach (var route in tree.Value.Forwarding)
            {
                var prefix = Prefix(route);
                foreach (var child in Children(route, python))
                {
                    var path = route.Name + child.Path[prefix.Length..];
                    var decision = tree.Value.Dispatch.Single(d => d.Path == path);
                    if (decision.Forwarded)
                    {
                        if (tree.Value.Commands.Any(c => c.Path == path && c.Action))
                        {
                            throw new InvalidOperationException($"Stale fallback for migrated command: {tree.Key}:{path}");
                        }

                        yield return Surface($"fallback:{tree.Key}:{path}={child.Owner}");
                    }
                }
            }
        }
    }

    private static string Prefix(Forwarding route) => route.Launch switch
    {
        ["desktop-manager", .. var rest] => string.Join(' ', rest.Prepend("desktop-manager")),
        ["python", "-m", "rig.triage.cli"] => "triage",
        ["python", "-m", "rig.c4_okf.cli"] => "c4",
        _ => throw new InvalidOperationException($"Unsupported forwarding discovery: {string.Join(' ', route.Launch)}"),
    };

    private static PythonCommand[] Children(Forwarding route, PythonTree python)
    {
        var prefix = Prefix(route);
        var children = python.Commands.Where(c => c.Path == prefix || c.Path.StartsWith(prefix + " ", StringComparison.Ordinal)).ToArray();
        return children.Length > 0 ? children : throw new InvalidOperationException($"Fallback has no inspected command tree: {route.Name}");
    }

    internal static string Canonical(string path) => path.StartsWith("desktop-manager ", StringComparison.Ordinal)
        ? path["desktop-manager ".Length..] switch
        {
            "help" => "desktop help",
            var desktop when desktop.StartsWith("workspace ", StringComparison.Ordinal) => "desktop " + desktop,
            var other => other,
        }
        : path;

    internal static CommandOwner Surface(string id) => new(id, $"surface:{id}", id, AllPlatforms);

    internal static CommandOwner EntrySurface(string id)
    {
        const string target = "=rig.csharp_cli:entry";
        if (id.StartsWith("script:", StringComparison.Ordinal) && id.EndsWith(target, StringComparison.Ordinal))
        {
            var command = id["script:".Length..^target.Length];
            if (command != "rig")
            {
                return new CommandOwner(id, command, $"cs:{command}", AllPlatforms, $"cs:{command}");
            }
        }

        return Surface(id);
    }

    // Parse C# syntax instead of grep: catches new mains and top-level apps, including alternate executable projects.
    internal static IEnumerable<CommandOwner> Sources(string root, IEnumerable<string> files)
    {
        foreach (var file in files.Where(f => !f.StartsWith("tests/", StringComparison.Ordinal) && !f.StartsWith("tools/image-drift/tests/", StringComparison.Ordinal)))
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

                foreach (var creation in parsed.GetRoot().DescendantNodes().OfType<ObjectCreationExpressionSyntax>().Where(c => c.Type.ToString().EndsWith("RootCommand", StringComparison.Ordinal)))
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
