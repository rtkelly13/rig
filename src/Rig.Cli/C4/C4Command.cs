using System.CommandLine;
using Rig.Core.C4;

namespace Rig.Cli.C4;

/// <summary>The bundle and git facts <c>rig c4</c> works with.</summary>
/// <param name="DefaultBundle">The sample bundle documenting rig itself, used when no path is given.</param>
/// <param name="Git">Runs git in a repository and returns its trimmed output, or null when it fails.</param>
internal sealed record C4Host(string DefaultBundle, Func<string, IReadOnlyList<string>, Task<string>> Git);

/// <summary>
/// <c>rig c4</c>: check, walk and compose a C4 model kept as an OKF bundle, and derive one from .NET code.
/// </summary>
internal sealed class C4Command(C4Host host, TextWriter output, TextWriter error)
{
    /// <summary>The <c>c4</c> command group.</summary>
    public Command Build()
    {
        var group = new Command("c4", "C4 diagrams as an Open Knowledge Format bundle: zoom links, composition and checks. Experimental.");
        group.Subcommands.Add(Check());
        group.Subcommands.Add(Model());
        group.Subcommands.Add(Tree());
        group.Subcommands.Add(Expand());
        group.Subcommands.Add(Derive());
        return group;
    }

    private Command Check()
    {
        var bundle = new Argument<string?>("bundle") { Arity = ArgumentArity.ZeroOrOne, Description = "Bundle root: a directory of OKF markdown concepts." };
        var src = new Option<string?>("--src") { Description = "Bundle root, as an alternative to the positional path." };
        var json = new Option<bool>("--json") { Description = "Print JSON instead of a table." };
        var strict = new Option<bool>("--strict") { Description = "Fail on warnings as well as errors." };
        var quiet = new Option<bool>("--quiet", "-q") { Description = "Hide info findings." };
        var command = new Command("check", "Check OKF conformance, zoom links, level descent, orphans, fan-in and cross-view consistency.") { bundle, src, json, strict, quiet };
        command.SetAction(parsed => CheckBundle(
            parsed.GetValue(bundle) ?? parsed.GetValue(src) ?? host.DefaultBundle,
            parsed.GetValue(json),
            parsed.GetValue(strict),
            parsed.GetValue(quiet)));
        return command;
    }

    private int CheckBundle(string root, bool json, bool strict, bool quiet)
    {
        try
        {
            var bundle = OkfBundle.Load(root);
            var findings = C4Composer.Check(bundle);
            var shown = findings.Where(finding => !(quiet && finding.Severity == Severity.Info)).ToList();
            if (json)
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

            Severity[] failing = strict ? [Severity.Error, Severity.Warning] : [Severity.Error];
            return findings.Any(finding => failing.Contains(finding.Severity)) ? 1 : 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            error.Write($"{exception.Message}\n");
            return 1;
        }
    }

    private Command Model()
    {
        var bundle = new Argument<string?>("bundle") { Arity = ArgumentArity.ZeroOrOne, Description = "Bundle root: a directory of OKF markdown concepts." };
        var src = new Option<string?>("--src") { Description = "Bundle root, as an alternative to the positional path." };
        var command = new Command("model", "Print the merged model as JSON: every alias, its labels, the views it appears in, and what it zooms to.") { bundle, src };
        command.SetAction(parsed =>
        {
            try
            {
                var model = C4Composer.Build(OkfBundle.Load(parsed.GetValue(bundle) ?? parsed.GetValue(src) ?? host.DefaultBundle));
                output.Write(C4Json.Render(model) + "\n");
                return 0;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                error.Write($"{exception.Message}\n");
                return 1;
            }
        });
        return command;
    }

    private Command Tree()
    {
        var bundle = new Argument<string?>("bundle") { Arity = ArgumentArity.ZeroOrOne, Description = "Bundle root: a directory of OKF markdown concepts." };
        var src = new Option<string?>("--src") { Description = "Bundle root, as an alternative to the positional path." };
        var json = new Option<bool>("--json") { Description = "Print JSON instead of a tree." };
        var command = new Command("tree", "Print the zoom hierarchy from each root view. A concept reached twice is shown once, then marked.") { bundle, src, json };
        command.SetAction(parsed =>
        {
            try
            {
                var root = parsed.GetValue(bundle) ?? parsed.GetValue(src) ?? host.DefaultBundle;
                var nodes = C4Composer.ZoomTree(OkfBundle.Load(root));
                if (parsed.GetValue(json))
                {
                    output.Write(C4Json.Render(nodes) + "\n");
                    return 0;
                }

                output.Write(root + "\n");
                foreach (var node in nodes)
                {
                    WriteNode(node, 1);
                }

                return 0;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                error.Write($"{exception.Message}\n");
                return 1;
            }
        });
        return command;
    }

    private void WriteNode(TreeNode node, int depth)
    {
        var indent = new string(' ', depth * 2);
        var via = node.Via is null ? string.Empty : $"{node.Via} → ";
        var repeat = node.Repeat ? " (shown above)" : string.Empty;
        output.Write($"{indent}{via}{node.Title} {node.Type ?? "?"} · {node.Id}{repeat}\n");
        foreach (var child in node.Children)
        {
            WriteNode(child, depth + 1);
        }
    }

    private Command Expand()
    {
        var concept = new Argument<string>("concept") { Description = "Concept id of the parent view, e.g. system-context." };
        var alias = new Argument<string>("alias") { Description = "The element to open up in place." };
        var root = new Option<string?>("--root") { Description = "Bundle root." };
        var src = new Option<string?>("--src") { Description = "Bundle root, as an alternative to --root." };
        var command = new Command("expand", "Print Mermaid for a parent view with one element replaced by the inside of the view it zooms into.") { concept, alias, root, src };
        command.SetAction(parsed =>
        {
            try
            {
                output.Write(C4Composer.Expand(OkfBundle.Load(parsed.GetValue(root) ?? parsed.GetValue(src) ?? host.DefaultBundle),
                    parsed.GetRequiredValue(concept), parsed.GetRequiredValue(alias)));
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
        });
        return command;
    }

    private Command Derive()
    {
        var repo = new Argument<string>("repo") { Description = "A .NET repository; projects are read from its src/." };
        var bundle = new Argument<string>("out") { Description = "Bundle root to write containers.md, components/ and code/ into." };
        var parent = new Option<string?>("--parent") { Description = "CONCEPT:ALIAS of the hand-written view the containers refine." };
        var callgraph = new Option<string?>("--callgraph") { Description = "Directory of <Project>.callgraph.txt edge files, scored as a second pathway." };
        var check = new Option<bool>("--check") { Description = "Write nothing; exit 1 if any derived file is stale." };
        var project = new Option<string?>("--project") { Description = "One .csproj to derive with its references, instead of every project under src/." };
        var dotnet = new Command("dotnet", "Derive containers (projects), components (namespaces) and code (types) from a .NET repository.") { repo, bundle, parent, callgraph, check, project };
        dotnet.SetAction(async (parsed, cancellationToken) => await DeriveDotnet(
            parsed.GetRequiredValue(repo),
            parsed.GetRequiredValue(bundle),
            parsed.GetValue(parent),
            parsed.GetValue(callgraph),
            parsed.GetValue(check),
            parsed.GetValue(project),
            cancellationToken).ConfigureAwait(false));
        return new Command("derive", "Derive C4 views from code into a bundle.") { dotnet };
    }

    private async Task<int> DeriveDotnet(string repo, string root, string? parent, string? callgraph, bool check, string? project, CancellationToken cancellationToken)
    {
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

    private static string SeverityText(Severity severity) => severity switch
    {
        Severity.Error => "error",
        Severity.Warning => "warning",
        _ => "info",
    };
}
