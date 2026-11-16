using Rig.Core.C4;

namespace Rig.Core.Tests.C4;

/// <summary>Deriving C4 views from a .NET repository: projects, namespaces, both pathways, determinism and staleness.</summary>
public sealed class C4DeriverTests : IDisposable
{
    private const string GeneratorProject = """
        <Project><PropertyGroup><TargetFramework>netstandard2.0</TargetFramework>
        <EnforceExtendedAnalyzerRules>true</EnforceExtendedAnalyzerRules></PropertyGroup>
        <ItemGroup><ProjectReference Include="..\Acme.Runtime\Acme.Runtime.csproj" /></ItemGroup></Project>
        """;

    private const string LegacyProject = """
        <Project><PropertyGroup><TargetFramework>netstandard2.0</TargetFramework>
        <IsRoslynComponent>true</IsRoslynComponent></PropertyGroup><ItemGroup>
        <Compile Include="..\Acme.Gen\Models\*.cs" Link="Models\%(Filename)" />
        <ProjectReference Include="..\Acme.Gen\Acme.Gen.csproj" OutputItemType="Analyzer" /></ItemGroup></Project>
        """;

    private const string Context = """
        ---
        type: C4 Context
        title: Acme — system context
        ---
        ```mermaid
        C4Context
        Person(dev, "Developer")
        System(acme, "Acme", $link="containers.md")
        Rel(dev, acme, "uses")
        ```
        """;

    private readonly TempDir _temp = new();
    private readonly Origin _origin = new("abc1234def56", "2026-11-14", "https://github.com/acme/acme");

    public void Dispose() => _temp.Dispose();

    private string Repo()
    {
        var root = _temp.Folder("acme");
        Write(root, new Dictionary<string, string>
        {
            ["src/Acme.Gen/Acme.Gen.csproj"] = GeneratorProject,
            ["src/Acme.Gen/Gen.cs"] = "namespace Acme.Gen;\nusing Acme.Gen.Parsing;\npublic sealed class Gen : IIncrementalGenerator { void Run() { Parser.Parse(); } }\n",
            ["src/Acme.Gen/Parsing/Parser.cs"] =
                "namespace Acme.Gen.Parsing;\n// Marker is only mentioned in this comment\n"
                + "/// <summary>Reads a <see cref=\"T:Acme.Gen.Models.Model\"/> from text. Says Marker.</summary>\n"
                + "[Obsolete]\npublic static class Parser {\n    public static Model Parse() => new Model(\"Old\");\n    public static int Count { get; }\n}\n",
            ["src/Acme.Gen/Models/Model.cs"] = "namespace Acme.Gen.Models;\npublic record Model(string Name);\n",
            ["src/Acme.Gen/obj/Junk.cs"] = "namespace Junk;\nclass Junk {}\n",
            ["src/Acme.Runtime/Acme.Runtime.csproj"] = "<Project><PropertyGroup><TargetFrameworks>netstandard2.0;net8.0</TargetFrameworks></PropertyGroup></Project>",
            ["src/Acme.Runtime/Marker.cs"] = "namespace Acme.Runtime;\npublic sealed class Marker { }\n",
            ["src/Acme.Legacy/Acme.Legacy.csproj"] = LegacyProject,
            ["src/Acme.Legacy/Old.cs"] = "namespace Acme.Legacy;\npublic sealed class Old { Model m; Marker k; }\n",
            ["src/Acme.Legacy/Polyfill.cs"] = "namespace System.Runtime.CompilerServices;\nclass IsExternalInit {}\n",
        });
        var graph = _temp.Folder("callgraph");
        File.WriteAllText(Path.Join(graph, "Acme.Gen.callgraph.txt"),
            "E:Acme.Gen.Gen.Run/() | Acme.Gen.Parsing.Parser.Parse/()\n"
            + "E:Acme.Gen.Parsing.Parser.Parse/() | Acme.Gen.Parsing.Parser.Parse/()\n"
            + "not an edge\n");
        return root;
    }

    private static string Write(string root, IReadOnlyDictionary<string, string> files)
    {
        foreach (var (name, text) in files)
        {
            var path = Path.Join(root, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }

        return root;
    }

    [Fact]
    public void Discover_reads_projects_analyzers_and_linked_globs()
    {
        var repo = Repo();
        var projects = DotnetScan.Discover(repo).ToDictionary(project => project.Name, StringComparer.Ordinal);
        Assert.Equal(["Acme.Gen", "Acme.Legacy", "Acme.Runtime"], projects.Keys.Order(StringComparer.Ordinal));
        Assert.True(projects["Acme.Gen"].IsAnalyzer);
        Assert.True(projects["Acme.Legacy"].IsAnalyzer);
        Assert.False(projects["Acme.Runtime"].IsAnalyzer);
        Assert.Equal(["netstandard2.0", "net8.0"], projects["Acme.Runtime"].Frameworks);
        Assert.True(Assert.Single(projects["Acme.Legacy"].Refs).Analyzer);
        Assert.Equal(["Model.cs", "Old.cs", "Polyfill.cs"],
            DotnetScan.Sources(projects["Acme.Legacy"]).Select(file => Path.GetFileName(file.Path)).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(DotnetScan.Sources(projects["Acme.Gen"]), file => Path.GetFileName(file.Path) == "Junk.cs");
    }

    [Fact]
    public void Read_source_ignores_names_in_comments_and_strings()
    {
        var repo = Repo();
        var parsed = DotnetScan.ReadSource(Path.Join(repo, "src/Acme.Gen/Parsing/Parser.cs"));
        Assert.Equal("Acme.Gen.Parsing", parsed.Namespace);
        Assert.Equal(["Parser"], parsed.Declares);
        Assert.Contains("Model", parsed.Names);
        Assert.DoesNotContain(parsed.Names, name => name is "Marker" or "Old" or "Gen");
        Assert.True(DotnetScan.ReadSource(Path.Join(repo, "src/Acme.Gen/Gen.cs")).IsGenerator);
    }

    [Fact]
    public void Read_source_reads_kind_summary_members_and_size()
    {
        var repo = Repo();
        var parser = DotnetScan.ReadSource(Path.Join(repo, "src/Acme.Gen/Parsing/Parser.cs"));
        var info = Assert.Single(parser.Types);
        Assert.Equal("class", info.Kind);
        Assert.Equal("Reads a Model from text.", info.Summary);
        Assert.Equal(["Parse()", "Count"], info.Members);
        Assert.Equal(8, parser.Lines);
        Assert.Equal("record", Assert.Single(DotnetScan.ReadSource(Path.Join(repo, "src/Acme.Gen/Models/Model.cs")).Types).Kind);
    }

    [Fact]
    public void Derive_writes_every_level_and_shares_linked_code()
    {
        var repo = Repo();
        var derived = C4Deriver.Derive(_origin, repo, _temp.Folder("out"), callgraph: Path.Join(_temp.Path, "callgraph"));
        Assert.Equal([
            "code/gen.md", "code/gen_models.md", "code/gen_parsing.md", "code/legacy.md", "code/runtime.md",
            "components/gen.md", "components/legacy.md", "components/runtime.md", "containers.md",
        ], derived.Files.Keys.Order(StringComparer.Ordinal));
        var containers = derived.Files["containers.md"];
        Assert.Contains("Rel(csc, gen, \"loads into the build\"", containers, StringComparison.Ordinal);
        Assert.Contains("Rel(gen, legacy, \"checks at build\"", containers, StringComparison.Ordinal);
        Assert.Contains("\"1 source file\"", containers, StringComparison.Ordinal);
        Assert.DoesNotContain("IsExternalInit", string.Concat(derived.Files.Values), StringComparison.Ordinal);
        Assert.Contains("$link=\"/code/gen_models.md\"", derived.Files["components/gen.md"], StringComparison.Ordinal);
        Assert.Contains("$link=\"/code/gen_models.md\"", derived.Files["components/legacy.md"], StringComparison.Ordinal);
        var parsing = derived.Files["code/gen_parsing.md"];
        Assert.Contains("class Parser {\n  +Parse()\n  +Count\n}", parsing, StringComparison.Ordinal);
        Assert.Contains("**class** · Reads a Model from text.", parsing, StringComparison.Ordinal);
        Assert.Contains("Parser.cs](https://github.com/acme/acme/blob/abc1234def56/src/Acme.Gen/Parsing/Parser.cs) in src/Acme.Gen/Parsing/ · 8 lines", parsing, StringComparison.Ordinal);
        Assert.Contains("<<record>>", derived.Files["code/gen_models.md"], StringComparison.Ordinal);
        Assert.Contains("| `Parser.cs` |  | 8 |", derived.Files["components/gen.md"], StringComparison.Ordinal);
    }

    [Fact]
    public void Pathways_score_type_references_against_the_call_graph()
    {
        var repo = Repo();
        var derived = C4Deriver.Derive(_origin, repo, _temp.Folder("out"), callgraph: Path.Join(_temp.Path, "callgraph"));
        Assert.Equal(new PathwayScore(1, 1, 0), derived.Pathways["gen"]);
        Assert.Contains("1 type, 1 call", derived.Files["components/gen.md"], StringComparison.Ordinal);
        var blind = C4Deriver.Derive(_origin, repo, _temp.Folder("out"), callgraph: null);
        Assert.Contains("## Derivation", blind.Files["components/gen.md"], StringComparison.Ordinal);
    }

    [Fact]
    public void Derive_is_deterministic_and_check_finds_drift()
    {
        var repo = Repo();
        var output = _temp.Folder("out");
        var first = C4Deriver.Derive(_origin, repo, output, callgraph: null);
        var second = C4Deriver.Derive(_origin, repo, output, callgraph: null);
        Assert.Equal(first.Files.Keys.Order(StringComparer.Ordinal), second.Files.Keys.Order(StringComparer.Ordinal));
        Assert.All(first.Files, pair => Assert.Equal(pair.Value, second.Files[pair.Key]));
        Assert.Equal(first.Files.Keys.Order(StringComparer.Ordinal), C4Deriver.Write(output, first.Files));
        Assert.Empty(C4Deriver.Stale(output, first.Files));
        File.WriteAllText(Path.Join(repo, "src/Acme.Runtime/Extra.cs"), "namespace Acme.Runtime;\nclass Extra {}\n");
        Assert.Contains("code/runtime.md", C4Deriver.Stale(output, C4Deriver.Derive(_origin, repo, output, callgraph: null).Files));
    }

    [Fact]
    public void Check_reports_orphans_and_write_removes_only_derived_ones()
    {
        var repo = Repo();
        var output = _temp.Folder("out");
        var derived = C4Deriver.Derive(_origin, repo, output, callgraph: null);
        _ = C4Deriver.Write(output, derived.Files);
        // A namespace that has since been renamed away left its derived view behind; a hand-written view sits beside it.
        File.WriteAllText(Path.Join(output, "code", "gone.md"), derived.Files["code/runtime.md"]);
        var hand = Path.Join(output, "notes.md");
        File.WriteAllText(hand, "---\ntype: C4 Context\ngenerated: { by: human:ryan, at: 2026-11-04 }\n---\n");

        Assert.Equal(["code/gone.md"], C4Deriver.Stale(output, derived.Files));
        Assert.Equal(["code/gone.md"], C4Deriver.Write(output, derived.Files));
        Assert.False(File.Exists(Path.Join(output, "code", "gone.md")));
        Assert.True(File.Exists(hand));
        Assert.Empty(C4Deriver.Stale(output, derived.Files));
    }

    [Fact]
    public void Derived_views_under_a_parent_pass_the_bundle_checks()
    {
        var repo = Repo();
        var output = _temp.Folder("out");
        File.WriteAllText(Path.Join(output, "system-context.md"), Context);
        var derived = C4Deriver.Derive(_origin, repo, output, new Parent("system-context", "acme"), Path.Join(_temp.Path, "callgraph"));
        _ = C4Deriver.Write(output, derived.Files);
        Assert.Contains("System_Boundary(acme", derived.Files["containers.md"], StringComparison.Ordinal);
        Assert.Contains("Person(dev, \"Developer\")", derived.Files["containers.md"], StringComparison.Ordinal);
        Assert.Contains("title Acme: projects", derived.Files["containers.md"], StringComparison.Ordinal);
        Assert.Contains("System_Boundary(acme, \"Acme\")", derived.Files["containers.md"], StringComparison.Ordinal);
        var findings = C4Composer.Check(OkfBundle.Load(output));
        Assert.DoesNotContain(findings, finding => finding.Severity != Severity.Info);
    }

    [Fact]
    public void Only_edges_between_different_owners_are_counted()
    {
        var path = _temp.File("Acme.Gen.callgraph.txt");
        File.WriteAllText(path,
            "E:Acme.Gen.Gen.Run/() | Acme.Gen.Parsing.Parser.Parse/()\n"
            + "E:Acme.Gen.Parsing.Parser.Parse/() | Acme.Gen.Parsing.Parser.Parse/()\n"
            + "not an edge\n");
        var owners = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Acme.Gen.Gen"] = "gen",
            ["Acme.Gen.Parsing.Parser"] = "parsing",
        };
        var edges = DotnetScan.CallEdges(path, owners);
        Assert.Equal(new CodeEdge("gen", "parsing"), Assert.Single(edges).Key);
        Assert.Equal(1, edges[new CodeEdge("gen", "parsing")]);
    }

    [Fact]
    public void DiscoverProject_starts_from_one_csproj_and_follows_its_references()
    {
        var repo = Repo();
        var projects = DotnetScan.DiscoverProject(Path.Join(repo, "src/Acme.Legacy/Acme.Legacy.csproj"));
        Assert.Equal(["Acme.Gen", "Acme.Legacy", "Acme.Runtime"], projects.Select(project => project.Name));
    }

    [Fact]
    public void DiscoverProject_throws_when_project_file_does_not_exist()
    {
        var missing = Path.Join(Path.GetTempPath(), "NonExistent.csproj");
        var ex = Assert.Throws<FileNotFoundException>(() => DotnetScan.DiscoverProject(missing));
        Assert.Equal(Path.GetFullPath(missing), ex.FileName);
    }

    [Fact]
    public void DotnetScan_validates_null_arguments_and_empty_project_xml()
    {
        Assert.Throws<ArgumentNullException>(() => DotnetScan.Discover(null!));
        Assert.Throws<ArgumentNullException>(() => DotnetScan.DiscoverProject(null!));
        Assert.Throws<ArgumentNullException>(() => DotnetScan.ReadProject(null!));

        var empty = _temp.File("empty.csproj");
        File.WriteAllText(empty, "<Project />");
        var project = DotnetScan.ReadProject(empty);
        Assert.Equal("empty", project.Name);
        Assert.Empty(project.Frameworks);
        Assert.Empty(project.Refs);
        Assert.Empty(project.CompileLinks);
    }

    [Fact]
    public void CallEdges_returns_empty_when_file_does_not_exist()
    {
        Assert.Empty(DotnetScan.CallEdges(_temp.Path + "/missing.txt", new Dictionary<string, string>()));
    }

    [Fact]
    public void Derive_resolves_alias_collisions_and_keeps_same_file_type_dependencies()
    {
        var root = _temp.Folder("collision_repo");
        Write(root, new Dictionary<string, string>
        {
            ["src/Acme.Foo.Bar/Acme.Foo.Bar.csproj"] = "<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>",
            ["src/Acme.Foo.Bar/Types.cs"] = "namespace Acme.Foo.Bar;\npublic class Alpha { Beta b; }\npublic class Beta {}\n",
            ["src/Acme.Foo_Bar/Acme.Foo_Bar.csproj"] = "<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>",
            ["src/Acme.Foo_Bar/Other.cs"] = "namespace Acme.Foo_Bar;\npublic class Other {}\n",
        });

        var derived = C4Deriver.Derive(_origin, root, _temp.Folder("collision_out"));
        Assert.True(derived.Files.ContainsKey("components/foo_bar.md"));
        Assert.True(derived.Files.ContainsKey("components/foo_bar_2.md"));

        var codeView = derived.Files["code/foo_bar.md"];
        Assert.Contains("Alpha --> Beta", codeView, StringComparison.Ordinal);
    }

    [Fact]
    public void DotnetScan_handles_question_mark_globs()
    {
        var root = _temp.Folder("glob_repo");
        Write(root, new Dictionary<string, string>
        {
            ["src/Test/Test.csproj"] = "<Project><ItemGroup><Compile Include=\"Models/Model?.cs\" /></ItemGroup></Project>",
            ["src/Test/Models/Model1.cs"] = "namespace Test.Models; public class Model1 {}",
            ["src/Test/Models/Model2.cs"] = "namespace Test.Models; public class Model2 {}",
        });
        var project = DotnetScan.ReadProject(Path.Join(root, "src/Test/Test.csproj"));
        var sources = DotnetScan.Sources(project);
        Assert.Equal(2, sources.Count);
    }
}
