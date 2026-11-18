using System.CommandLine;
using DotMake.CommandLine;
using Rig.Core.C4;

namespace Rig.Cli.C4;

/// <summary>The bundle and git facts <c>rig c4</c> works with.</summary>
/// <param name="DefaultBundle">The sample bundle documenting rig itself, used when no path is given.</param>
/// <param name="Git">Runs git in a repository and returns its trimmed output, or null when it fails.</param>
internal sealed record C4Host(string DefaultBundle, Func<string, IReadOnlyList<string>, Task<string>> Git);

/// <summary>
/// <c>rig c4</c>: check, walk and compose a C4 model kept as an OKF bundle, and derive one from .NET code.
/// </summary>
[CliCommand(
    Name = "c4",
    Description = "C4 diagrams as an Open Knowledge Format bundle: zoom links, composition and checks. Experimental.")]
internal sealed class C4Command(C4Host host, TextWriter output, TextWriter error)
{
    private const string BundleDescription = "Bundle root: a directory of OKF markdown concepts.";
    private const string SrcDescription = "Bundle root, as an alternative to the positional path.";

    /// <summary>The instance DotMake binds onto; rig always builds the wired one above instead.</summary>
    internal C4Command()
        : this(null!, TextWriter.Null, TextWriter.Null)
    {
    }

    /// <summary>The <c>c4</c> command group.</summary>
    public Command Build()
    {
        var group = DeclarativeCommand.Build(this);
        group.Add(DeclarativeCommand.Build(new CheckCommand(host, output, error)));
        group.Add(DeclarativeCommand.Build(new ModelCommand(host, output, error)));
        group.Add(DeclarativeCommand.Build(new TreeCommand(host, output, error)));
        group.Add(DeclarativeCommand.Build(new ExpandCommand(host, output, error)));
        var derive = DeclarativeCommand.Build(new DeriveCommand(error));
        derive.Add(DeclarativeCommand.Build(new DotnetCommand(host, output, error)));
        group.Add(derive);
        return group;
    }

    /// <summary>A group named without one of its verbs is the parser's error.</summary>
    public int Run() => DeclarativeCommand.RequireSubcommand(error);

    private static void WriteNode(TextWriter output, TreeNode node, int depth)
    {
        var indent = new string(' ', depth * 2);
        var via = node.Via is null ? string.Empty : $"{node.Via} → ";
        var repeat = node.Repeat ? " (shown above)" : string.Empty;
        output.Write($"{indent}{via}{node.Title} {node.Type ?? "?"} · {node.Id}{repeat}\n");
        foreach (var child in node.Children)
        {
            WriteNode(output, child, depth + 1);
        }
    }

    private static string SeverityText(Severity severity) => severity switch
    {
        Severity.Error => "error",
        Severity.Warning => "warning",
        _ => "info",
    };

    /// <summary><c>rig c4 check</c>.</summary>
    [CliCommand(Name = "check", Description = "Check OKF conformance, zoom links, level descent, orphans, fan-in and cross-view consistency.")]
    internal sealed class CheckCommand(C4Host host, TextWriter output, TextWriter error)
    {
        /// <summary>The instance DotMake binds onto; rig always builds the wired one above instead.</summary>
        internal CheckCommand()
            : this(null!, TextWriter.Null, TextWriter.Null)
        {
        }

        /// <summary>The bundle root, when given positionally.</summary>
        [CliArgument(Name = "bundle", Required = false, Arity = CliArgumentArity.ZeroOrOne, Description = BundleDescription)]
        public string? Bundle { get; set; }

        /// <summary>The bundle root, as an option.</summary>
        [CliOption(Name = "--src", Required = false, Description = SrcDescription)]
        public string? Src { get; set; }

        /// <summary>Print JSON instead of a table.</summary>
        [CliOption(Name = "--json", Description = "Print JSON instead of a table.")]
        public bool Json { get; set; }

        /// <summary>Fail on warnings as well as errors.</summary>
        [CliOption(Name = "--strict", Description = "Fail on warnings as well as errors.")]
        public bool Strict { get; set; }

        /// <summary>Hide info findings.</summary>
        [CliOption(Name = "--quiet", Aliases = ["-q"], Description = "Hide info findings.")]
        public bool Quiet { get; set; }

        /// <summary>Checks the bundle and prints the findings.</summary>
        public int Run()
        {
            try
            {
                var bundle = OkfBundle.Load(Bundle ?? Src ?? host.DefaultBundle);
                var findings = C4Composer.Check(bundle);
                var shown = findings.Where(finding => !(Quiet && finding.Severity == Severity.Info)).ToList();
                if (Json)
                {
                    output.Write(C4Json.Render(shown) + "\n");
                }
                else
                {
                    var severities = shown.Select(finding => SeverityText(finding.Severity)).ToList();
                    var severityWidth = Math.Max(7, severities.Select(text => text.Length).DefaultIfEmpty(0).Max());
                    var codeWidth = Math.Max(15, shown.Select(finding => finding.Code.Length).DefaultIfEmpty(0).Max());
                    var conceptWidth = shown.Select(finding => finding.Concept.Length).DefaultIfEmpty(0).Max();
                    for (var index = 0; index < shown.Count; index++)
                    {
                        var finding = shown[index];
                        output.Write($"{severities[index].PadRight(severityWidth)} {finding.Code.PadRight(codeWidth)} {finding.Concept.PadRight(conceptWidth)} {finding.Message}\n");
                    }

                    var errors = findings.Count(finding => finding.Severity == Severity.Error);
                    var warnings = findings.Count(finding => finding.Severity == Severity.Warning);
                    var info = findings.Count(finding => finding.Severity == Severity.Info);
                    output.Write($"\n{bundle.Concepts.Count} concepts · {errors} errors · {warnings} warnings · {info} info\n");
                }

                Severity[] failing = Strict ? [Severity.Error, Severity.Warning] : [Severity.Error];
                return findings.Any(finding => failing.Contains(finding.Severity)) ? 1 : 0;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                error.Write($"{exception.Message}\n");
                return 1;
            }
        }
    }

    /// <summary><c>rig c4 model</c>.</summary>
    [CliCommand(Name = "model", Description = "Print the merged model as JSON: every alias, its labels, the views it appears in, and what it zooms to.")]
    internal sealed class ModelCommand(C4Host host, TextWriter output, TextWriter error)
    {
        /// <summary>The instance DotMake binds onto; rig always builds the wired one above instead.</summary>
        internal ModelCommand()
            : this(null!, TextWriter.Null, TextWriter.Null)
        {
        }

        /// <summary>The bundle root, when given positionally.</summary>
        [CliArgument(Name = "bundle", Required = false, Arity = CliArgumentArity.ZeroOrOne, Description = BundleDescription)]
        public string? Bundle { get; set; }

        /// <summary>The bundle root, as an option.</summary>
        [CliOption(Name = "--src", Required = false, Description = SrcDescription)]
        public string? Src { get; set; }

        /// <summary>Prints the merged model.</summary>
        public int Run()
        {
            try
            {
                var model = C4Composer.Build(OkfBundle.Load(Bundle ?? Src ?? host.DefaultBundle));
                output.Write(C4Json.Render(model) + "\n");
                return 0;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                error.Write($"{exception.Message}\n");
                return 1;
            }
        }
    }

    /// <summary><c>rig c4 tree</c>.</summary>
    [CliCommand(Name = "tree", Description = "Print the zoom hierarchy from each root view. A concept reached twice is shown once, then marked.")]
    internal sealed class TreeCommand(C4Host host, TextWriter output, TextWriter error)
    {
        /// <summary>The instance DotMake binds onto; rig always builds the wired one above instead.</summary>
        internal TreeCommand()
            : this(null!, TextWriter.Null, TextWriter.Null)
        {
        }

        /// <summary>The bundle root, when given positionally.</summary>
        [CliArgument(Name = "bundle", Required = false, Arity = CliArgumentArity.ZeroOrOne, Description = BundleDescription)]
        public string? Bundle { get; set; }

        /// <summary>The bundle root, as an option.</summary>
        [CliOption(Name = "--src", Required = false, Description = SrcDescription)]
        public string? Src { get; set; }

        /// <summary>Print JSON instead of a tree.</summary>
        [CliOption(Name = "--json", Description = "Print JSON instead of a tree.")]
        public bool Json { get; set; }

        /// <summary>Prints the zoom hierarchy.</summary>
        public int Run()
        {
            try
            {
                var root = Bundle ?? Src ?? host.DefaultBundle;
                var nodes = C4Composer.ZoomTree(OkfBundle.Load(root));
                if (Json)
                {
                    output.Write(C4Json.Render(nodes) + "\n");
                    return 0;
                }

                output.Write(root + "\n");
                foreach (var node in nodes)
                {
                    WriteNode(output, node, 1);
                }

                return 0;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                error.Write($"{exception.Message}\n");
                return 1;
            }
        }
    }

    /// <summary><c>rig c4 expand</c>.</summary>
    [CliCommand(Name = "expand", Description = "Print Mermaid for a parent view with one element replaced by the inside of the view it zooms into.")]
    internal sealed class ExpandCommand(C4Host host, TextWriter output, TextWriter error)
    {
        /// <summary>The instance DotMake binds onto; rig always builds the wired one above instead.</summary>
        internal ExpandCommand()
            : this(null!, TextWriter.Null, TextWriter.Null)
        {
        }

        /// <summary>Concept id of the parent view.</summary>
        [CliArgument(Name = "concept", Description = "Concept id of the parent view, e.g. system-context.")]
        public string Concept { get; set; } = null!;

        /// <summary>The element to open up in place.</summary>
        [CliArgument(Name = "alias", Description = "The element to open up in place.")]
        public string Alias { get; set; } = null!;

        /// <summary>The bundle root.</summary>
        [CliOption(Name = "--root", Required = false, Description = "Bundle root.")]
        public string? Root { get; set; }

        /// <summary>The bundle root, as an alternative to <c>--root</c>.</summary>
        [CliOption(Name = "--src", Required = false, Description = "Bundle root, as an alternative to --root.")]
        public string? Src { get; set; }

        /// <summary>Prints the expanded view.</summary>
        public int Run()
        {
            try
            {
                output.Write(C4Composer.Expand(OkfBundle.Load(Root ?? Src ?? host.DefaultBundle), Concept, Alias));
                return 0;
            }
            catch (ExpandException exception)
            {
                output.Write($"{exception.Message}\n");
                return 1;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                error.Write($"{exception.Message}\n");
                return 1;
            }
        }
    }

    /// <summary><c>rig c4 derive</c>: a group, named without a verb it is the parser's error.</summary>
    [CliCommand(Name = "derive", Description = "Derive C4 views from code into a bundle.")]
    internal sealed class DeriveCommand(TextWriter error)
    {
        /// <summary>The instance DotMake binds onto; rig always builds the wired one above instead.</summary>
        internal DeriveCommand()
            : this(TextWriter.Null)
        {
        }

        /// <summary>A group named without one of its verbs is the parser's error.</summary>
        public int Run() => DeclarativeCommand.RequireSubcommand(error);
    }

    /// <summary><c>rig c4 derive dotnet</c>.</summary>
    [CliCommand(Name = "dotnet", Description = "Derive containers (projects), components (namespaces) and code (types) from a .NET repository.")]
    internal sealed class DotnetCommand(C4Host host, TextWriter output, TextWriter error)
    {
        /// <summary>The instance DotMake binds onto; rig always builds the wired one above instead.</summary>
        internal DotnetCommand()
            : this(null!, TextWriter.Null, TextWriter.Null)
        {
        }

        /// <summary>A .NET repository; projects are read from its src/.</summary>
        [CliArgument(Name = "repo", Description = "A .NET repository; projects are read from its src/.")]
        public string Repo { get; set; } = null!;

        /// <summary>Bundle root to write into.</summary>
        [CliArgument(Name = "out", Description = "Bundle root to write containers.md, components/ and code/ into.")]
        public string Out { get; set; } = null!;

        /// <summary>CONCEPT:ALIAS of the hand-written view the containers refine.</summary>
        [CliOption(Name = "--parent", Required = false, Description = "CONCEPT:ALIAS of the hand-written view the containers refine.")]
        public string? ParentRef { get; set; }

        /// <summary>Directory of call graph edge files.</summary>
        [CliOption(Name = "--callgraph", Required = false, Description = "Directory of <Project>.callgraph.txt edge files, scored as a second pathway.")]
        public string? Callgraph { get; set; }

        /// <summary>Write nothing; exit 1 if any derived file is stale.</summary>
        [CliOption(Name = "--check", Description = "Write nothing; exit 1 if any derived file is stale.")]
        public bool Check { get; set; }

        /// <summary>One project to derive with its references.</summary>
        [CliOption(Name = "--project", Required = false, Description = "One .csproj to derive with its references, instead of every project under src/.")]
        public string? Project { get; set; }

        /// <summary>Derives the views and writes them, or with <c>--check</c> reports what is stale.</summary>
        public async Task<int> RunAsync(CliContext context)
        {
            var cancellationToken = context.CancellationToken;
            var repo = Repo;
            var root = Out;
            var parent = ParentRef;
            var callgraph = Callgraph;
            var check = Check;
            var project = Project;
            try
            {
                if (!Directory.Exists(repo))
                {
                    await error.WriteAsync($"Repository directory does not exist: {repo}\n").ConfigureAwait(false);
                    return 1;
                }

                Parent? link = null;
                if (!string.IsNullOrEmpty(parent))
                {
                    var separator = parent.IndexOf(':', StringComparison.Ordinal);
                    link = separator < 0 ? new Parent(parent, string.Empty) : new Parent(parent[..separator], parent[(separator + 1)..]);
                }

                var origin = await C4Deriver.ReadOriginAsync(repo, args => host.Git(repo, args)).ConfigureAwait(false);
                var derived = C4Deriver.Derive(origin, repo, root, link, callgraph, project);
                var changed = check ? C4Deriver.Stale(root, derived.Files) : C4Deriver.Write(root, derived.Files);
                foreach (var name in changed)
                {
                    var verb = check ? "stale" : derived.Files.ContainsKey(name) ? "wrote" : "removed";
                    var orphaned = check && !derived.Files.ContainsKey(name) ? " (orphaned)" : string.Empty;
                    await output.WriteAsync($"{verb} {name}{orphaned}\n").ConfigureAwait(false);
                }

                foreach (var (alias, score) in derived.Pathways.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                {
                    await output.WriteAsync($"{alias}: {score.Both} relationships on both pathways · {score.OnlyRefs} references only · {score.OnlyCalls} calls only\n").ConfigureAwait(false);
                }

                await output.WriteAsync($"{derived.Files.Count} derived concepts · {changed.Count} {(check ? "stale" : "changed")}\n").ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                return check && changed.Count > 0 ? 1 : 0;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or System.Xml.XmlException)
            {
                await error.WriteAsync($"{exception.Message}\n").ConfigureAwait(false);
                return 1;
            }
        }
    }
}
