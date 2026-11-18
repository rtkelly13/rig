using Rig.Core.C4;

namespace Rig.Core.Tests.C4;

/// <summary>The C4-as-OKF model: reading diagrams, resolving zoom links, checks, composition and deterministic JSON.</summary>
public sealed class C4BundleTests : IDisposable
{
    private const string Context = """
        ---
        type: C4 Context
        title: Shop
        generated: { by: test, at: 2026-11-04 }
        ---

        ```mermaid
        C4Context
        Person(user, "User")
        System(shop, "Shop", "Sells things", $link="shop/containers.md")
        System_Ext(pay, "Payments")
        Rel(user, shop, "Buys from")
        Rel(shop, pay, "Charges")
        ```

        ## shop

        The thing we build.
        """;

    private const string Containers = """
        ---
        type: C4 Container
        title: Shop containers
        ---

        ```mermaid
        C4Container
        Person(user, "User")
        System_Ext(pay, "Payments")
        System_Boundary(shop, "Shop") {
          Container(api, "API", "Python", $link="api.md")
          ContainerDb(db, "DB", "Postgres")
        }
        Rel(user, api, "Calls")
        Rel(api, db, "Reads")
        Rel(api, pay, "Charges")
        ```

        See [the context](/context.md) and [the API](api.md).
        """;

    private const string Api = """
        ---
        type: C4 Component
        title: API components
        verified: { by: ryan, at: 2026-11-04 }
        ---

        ```mermaid
        C4Component
        Person(user, "User")
        ContainerDb(db, "DB", "Postgres")
        System_Ext(pay, "Payments")
        Container_Boundary(api, "API") {
          Component(routes, "Routes", "FastAPI")
        }
        ```
        """;

    private readonly TempDir _temp = new();

    public void Dispose() => _temp.Dispose();

    private string Shop()
    {
        var root = _temp.Folder("shop");
        Write(root, new Dictionary<string, string>
        {
            ["context.md"] = Context,
            ["shop/containers.md"] = Containers,
            ["shop/api.md"] = Api,
            ["index.md"] = "# not a concept",
        });
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
    public void Parse_reads_elements_boundaries_links_and_relationships()
    {
        var diagram = MermaidReader.Parse(Between(Containers));
        Assert.Equal("C4Container", diagram.Keyword);
        var api = diagram.Element("api");
        Assert.NotNull(api);
        Assert.Equal((ElementKind.Container, "shop", "api.md"), (api.Kind, api.Parent, api.Link));
        var pay = diagram.Element("pay");
        Assert.NotNull(pay);
        Assert.True(pay.External);
        Assert.Equal([("user", "api"), ("api", "db"), ("api", "pay")],
            diagram.Relationships.Select(rel => (rel.Source, rel.Target)));
    }

    [Fact]
    public void Parse_reads_click_links_from_class_diagrams()
    {
        var diagram = MermaidReader.Parse("classDiagram\nclass Server\nclick Server href \"components.md\"\n");
        var server = diagram.Element("Server");
        Assert.NotNull(server);
        Assert.Equal("components.md", server.Link);
    }

    [Theory]
    [InlineData("shop/containers", "api.md", "shop/api")]
    [InlineData("shop/containers", "/context.md", "context")]
    [InlineData("shop/containers", "../context.md#top", "context")]
    [InlineData("context", "../escape.md", null)]
    [InlineData("context", "https://example.com/x.md", null)]
    [InlineData("context", "#anchor", null)]
    public void Resolve_href(string from, string href, string? expected) =>
        Assert.Equal(expected, OkfBundle.ResolveHref(from, href));

    [Fact]
    public void Load_bundle_skips_reserved_files_and_reads_provenance_and_notes()
    {
        var bundle = OkfBundle.Load(Shop());
        Assert.Equal(["context", "shop/api", "shop/containers"], bundle.Concepts.Keys.Order(StringComparer.Ordinal));
        var context = bundle.Concepts["context"];
        Assert.Equal(1, context.Level);
        Assert.Equal("2026-11-04", context.Generated?.At);
        Assert.Contains("shop", context.ElementNotes());
    }

    [Fact]
    public void Links_classify_zoom_and_mention()
    {
        var links = C4Composer.CollectLinks(OkfBundle.Load(Shop()));
        var kinds = links.Edges.Select(edge => (edge.Source, edge.Target, edge.Kind)).ToHashSet();
        Assert.Contains(("context", "shop/containers", EdgeKind.Zoom), kinds);
        Assert.Contains(("shop/containers", "shop/api", EdgeKind.Zoom), kinds);
        Assert.Contains(("shop/containers", "context", EdgeKind.Mention), kinds);
        Assert.Empty(links.Broken);
    }

    [Fact]
    public void An_upward_link_is_a_zoom_out_not_a_zoom()
    {
        var root = Shop();
        File.WriteAllText(Path.Join(root, "shop", "api.md"),
            Api.Replace("Component(routes, \"Routes\", \"FastAPI\")", "Component(routes, \"Routes\", \"x\", $link=\"containers.md\")", StringComparison.Ordinal));
        var bundle = OkfBundle.Load(root);
        var edge = C4Composer.CollectLinks(bundle).Edges.Single(candidate => candidate.Source == "shop/api");
        Assert.Equal(EdgeKind.ZoomOut, edge.Kind);
        Assert.DoesNotContain(C4Composer.Check(bundle), finding => finding.Code is "zoom-level" or "fan-in");
    }

    [Fact]
    public void A_clean_bundle_has_no_errors_or_warnings()
    {
        var findings = C4Composer.Check(OkfBundle.Load(Shop()));
        Assert.DoesNotContain(findings, finding => finding.Severity != Severity.Info);
        Assert.Equal(["context", "shop/containers"],
            findings.Where(finding => finding.Code == "unverified").Select(finding => finding.Concept));
    }

    [Fact]
    public void Checks_catch_broken_zooms_orphans_and_missing_types()
    {
        var root = Shop();
        File.WriteAllText(Path.Join(root, "context.md"), Context.Replace("shop/containers.md", "shop/missing.md", StringComparison.Ordinal));
        File.WriteAllText(Path.Join(root, "lonely.md"), "---\ntitle: no type\n---\nhi\n");
        var codes = C4Composer.Check(OkfBundle.Load(root)).Select(finding => (finding.Code, finding.Severity, finding.Concept)).ToHashSet();
        Assert.Contains(("zoom-broken", Severity.Error, "context"), codes);
        Assert.Contains(("orphan", Severity.Warning, "shop/containers"), codes);
        Assert.Contains(("okf-type", Severity.Error, "lonely"), codes);
    }

    [Fact]
    public void Refinement_flags_a_parent_relationship_the_child_drops()
    {
        var root = Shop();
        File.WriteAllText(Path.Join(root, "shop", "containers.md"),
            Containers.Replace("System_Ext(pay, \"Payments\")\n", string.Empty, StringComparison.Ordinal));
        Assert.Contains(C4Composer.Check(OkfBundle.Load(root)),
            finding => finding.Code == "rel-unrefined" && finding.Concept == "shop/containers");
    }

    [Fact]
    public void Model_merges_one_alias_across_views_and_flags_label_conflicts()
    {
        var root = Shop();
        var model = C4Composer.Build(OkfBundle.Load(root));
        var user = model.Elements.Single(element => element.Alias == "user");
        Assert.Equal(["context", "shop/api", "shop/containers"], user.Views);
        File.WriteAllText(Path.Join(root, "shop", "containers.md"), Containers.Replace("Person(user, \"User\")", "Person(user, \"Customer\")", StringComparison.Ordinal));
        Assert.Contains(C4Composer.Check(OkfBundle.Load(root)), finding => finding.Code == "alias-conflict");
    }

    [Fact]
    public void Zoom_tree_walks_the_dag_and_marks_repeats()
    {
        // A second container view that also zooms into the API: the API is reached twice, shown once.
        var root = Shop();
        var second = Containers.Replace("title: Shop containers", "title: Back office", StringComparison.Ordinal)
            .Replace("Rel(api, pay", "Rel(api, db", StringComparison.Ordinal);
        File.WriteAllText(Path.Join(root, "shop", "back-office.md"), second);
        File.WriteAllText(Path.Join(root, "context.md"),
            Context.Replace("System_Ext(pay, \"Payments\")", "System(ops, \"Ops\", $link=\"shop/back-office.md\")", StringComparison.Ordinal));
        var api = Flat(C4Composer.ZoomTree(OkfBundle.Load(root))).Where(node => node.Id == "shop/api").ToList();
        Assert.Equal([false, true], api.Select(node => node.Repeat));
    }

    [Fact]
    public void Expand_inlines_the_child_and_replaces_refined_relationships()
    {
        var composed = C4Composer.Expand(OkfBundle.Load(Shop()), "context", "shop");
        Assert.Contains("System_Boundary(shop, \"Shop\") {", composed, StringComparison.Ordinal);
        Assert.Contains("$link=\"/shop/api.md\"", composed, StringComparison.Ordinal);
        Assert.Contains("Rel(user, api, \"Calls\")", composed, StringComparison.Ordinal);
        Assert.DoesNotContain("Rel(user, shop, \"Buys from\")", composed, StringComparison.Ordinal);
        Assert.NotNull(MermaidReader.Parse(composed).Element("api"));
    }

    [Fact]
    public void Expand_draws_a_relationship_inside_the_boundary_once()
    {
        var root = Shop();
        var inner = Containers.Replace("  ContainerDb(db, \"DB\", \"Postgres\")\n}", "  ContainerDb(db, \"DB\", \"Postgres\")\n  Rel(api, db, \"Reads\")\n}", StringComparison.Ordinal);
        File.WriteAllText(Path.Join(root, "shop", "containers.md"),
            inner.Replace("Rel(api, db, \"Reads\")\nRel(api, pay", "Rel(api, pay", StringComparison.Ordinal));
        var composed = C4Composer.Expand(OkfBundle.Load(root), "context", "shop");
        Assert.Equal(1, Count(composed, "Rel(api, db, \"Reads\")"));
        Assert.Equal(1, Count(composed, "Rel(api, pay, \"Charges\")"));
    }

    [Fact]
    public void Expand_replaces_the_whole_block_of_a_boundary_that_zooms()
    {
        // The Python server replaced only the first line of the block, so its answer kept `Container(web, "Web")`
        // and a stray `}` after the new boundary: Mermaid that does not parse. The C# answer drops the whole block.
        var root = Write(_temp.Folder("block"), new Dictionary<string, string>
        {
            ["ctx.md"] = "---\ntype: C4 Container\ntitle: Ctx\n---\n\n```mermaid\nC4Container\nPerson(user, \"User\")\n"
                + "Container_Boundary(shop, \"Shop\", $link=\"sub/cont.md\") {\n    Container(web, \"Web\")\n}\n"
                + "Rel(user, shop, \"Uses\")\n```\n",
            ["sub/cont.md"] = "---\ntype: C4 Container\ntitle: Cont\n---\n\n```mermaid\nC4Container\nPerson(user, \"User\")\n"
                + "Container(api, \"Api\")\nRel(user, api, \"Calls\")\n```\n",
        });
        var composed = C4Composer.Expand(OkfBundle.Load(root), "ctx", "shop");
        Assert.Equal(
            "C4Container\nPerson(user, \"User\")\nSystem_Boundary(shop, \"Shop\") {\n  Container(api, \"Api\")\n}\n  Rel(user, api, \"Calls\")\n",
            composed);
    }

    [Fact]
    public void Check_words_a_malformed_frontmatter_as_the_YAML_library_does()
    {
        // The Python server quoted PyYAML's multi-line parser error, mark and all; the C# message is YamlDotNet's one line.
        var root = Write(_temp.Folder("yaml"), new Dictionary<string, string> { ["bad2.md"] = "---\ntype: [unclosed\n---\nbody\n" });
        var finding = Assert.Single(C4Composer.Check(OkfBundle.Load(root)), found => found.Code == "okf-frontmatter");
        Assert.Equal("frontmatter is not valid YAML: While parsing a flow sequence, did not find expected ',' or ']'.", finding.Message);
    }

    [Fact]
    public void Concepts_load_folder_by_folder_not_by_whole_path_string()
    {
        // `-` sorts below `/`, so a whole-string sort would list a-b before a/b. The Python server's Path order did not.
        var root = Write(_temp.Folder("order"), new Dictionary<string, string>
        {
            ["a-b.md"] = "---\ntype: note\n---\n",
            ["a/b.md"] = "---\ntype: note\n---\n",
            ["a.md"] = "---\ntype: note\n---\n",
        });
        Assert.Equal(["a/b", "a-b", "a"], OkfBundle.Load(root).Concepts.Keys);
    }

    [Fact]
    public void Json_is_deterministic_snake_case_and_lf()
    {
        var model = C4Composer.Build(OkfBundle.Load(Shop()));
        var json = C4Json.Render(model);
        Assert.Equal(json, C4Json.Render(model));
        Assert.Contains("\"zooms_to\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", json, StringComparison.Ordinal);
        Assert.StartsWith("{\n  \"elements\": [", json, StringComparison.Ordinal);
    }

    [Fact]
    public void The_rig_sample_bundle_has_no_errors()
    {
        var findings = C4Composer.Check(OkfBundle.Load(RepoFile("experiments/c4-okf/rig")));
        Assert.DoesNotContain(findings, finding => finding.Severity == Severity.Error);
    }

    [Fact]
    public void The_parquet_sample_bundle_has_no_errors_or_warnings()
    {
        var findings = C4Composer.Check(OkfBundle.Load(RepoFile("experiments/c4-okf/parquet-sourcegenerator")));
        Assert.DoesNotContain(findings, finding => finding.Severity != Severity.Info);
    }

    [Fact]
    public void Expand_exception_constructors_populate_message_and_inner()
    {
        Assert.NotNull(new ExpandException().Message);
        var msgEx = new ExpandException("custom");
        Assert.Equal("custom", msgEx.Message);
        var innerEx = new ExpandException("wrapped", new InvalidOperationException("inner"));
        Assert.Equal("wrapped", innerEx.Message);
        Assert.IsType<InvalidOperationException>(innerEx.InnerException);
    }

    [Fact]
    public void Roots_identifies_top_level_concepts()
    {
        var bundle = OkfBundle.Load(Shop());
        var roots = C4Composer.Roots(bundle);
        Assert.Equal(["context"], roots);
    }

    [Fact]
    public void DotnetScan_helpers_index_types_and_resolve_member_types()
    {
        Assert.Equal("Acme.Gen.Gen", DotnetScan.TypeOf("Acme.Gen.Gen.Run/()"));
        Assert.Equal("System.String", DotnetScan.TypeOf("System.String.Length"));
        Assert.Equal("NoNamespace", DotnetScan.TypeOf("NoNamespace/()"));

        var owners = new Dictionary<string, IReadOnlyList<string>>
        {
            ["owner1"] = ["Acme.TypeA", "Acme.TypeB", "Shared"],
            ["owner2"] = ["Acme.TypeC", "Shared"],
        };
        var index = DotnetScan.TypeIndex(owners);
        Assert.Equal("owner1", index["Acme.TypeA"]);
        Assert.Equal("owner1", index["Acme.TypeB"]);
        Assert.Equal("owner2", index["Acme.TypeC"]);
        Assert.False(index.ContainsKey("Shared")); // Ambiguous type declared in two owners is dropped

        Assert.Equal(0, DotnetScan.PythonLines(""));
        Assert.Equal(1, DotnetScan.PythonLines("one"));
        Assert.Equal(1, DotnetScan.PythonLines("one\n"));
        Assert.Equal(2, DotnetScan.PythonLines("one\ntwo"));

        var proj = new Project("TestProj", "/path/TestProj.csproj", "TestProj", ["net10.0"], false, [], []);
        Assert.Equal(string.Empty, DotnetScan.ComponentKey(proj, "TestProj"));
        Assert.Equal("Sub", DotnetScan.ComponentKey(proj, "TestProj.Sub"));
        Assert.Equal("Other.Namespace", DotnetScan.ComponentKey(proj, "Other.Namespace"));
    }

    [Fact]
    public void DotnetScan_ReferenceEdges_and_Components_coverage()
    {
        var proj = new Project("Acme", "/path/Acme.csproj", "Acme", ["net10.0"], false, [], []);
        var fileA = new SourceFile("/path/A.cs", "Acme.A", ["Acme.A.ClassA"], new HashSet<string> { "ClassB", "Self" }, false, 10, []);
        var fileB = new SourceFile("/path/B.cs", "Acme.B", ["Acme.B.ClassB"], new HashSet<string>(), false, 20, []);

        var components = DotnetScan.Components(proj, [fileA, fileB]);
        Assert.Equal(2, components.Count);
        Assert.Equal(["Acme.A.ClassA"], components[0].Types);

        var owned = new Dictionary<string, IReadOnlyList<SourceFile>>
        {
            ["ownerA"] = [fileA],
            ["ownerB"] = [fileB],
        };
        var index = new Dictionary<string, string>
        {
            ["ClassB"] = "ownerB",
            ["Self"] = "ownerA",
        };
        var edges = DotnetScan.ReferenceEdges(owned, index);
        Assert.Single(edges);
        Assert.Equal(1, edges[new CodeEdge("ownerA", "ownerB")]);
    }

    [Fact]
    public void DotnetScan_ReadSource_parses_doc_summary_cref_and_members()
    {
        var srcPath = Path.Join(_temp.Path, "Sample.cs");
        var longSummary = new string('A', 200);
        var content = $$"""
            namespace Acme.Sample;

            /// <summary>
            /// {{longSummary}}
            /// Second sentence that should be split out.
            /// </summary>
            public sealed class SampleClass
            {
                /// <summary>Does something with <see cref="Acme.OtherClass"/>.</summary>
                public void DoWork() { }
                public Task<string> FetchAsync<T>() => Task.FromResult("");
                public int Property { get; set; }
                public static int Field = 1;
                public event EventHandler? Changed;
                public void M1() {}
                public void M2() {}
                public void M3() {}
                public void M4() {}
                public void M5() {}
                public void M6() {}
                public void M7() {}
                public void M8() {}
                public void M9() {}
                public void M10() {}
                public void M11() {}
                public void M12() {}
                public void M13() {}
            }
            """;
        File.WriteAllText(srcPath, content);
        var source = DotnetScan.ReadSource(srcPath);

        Assert.Equal("Acme.Sample", source.Namespace);
        Assert.Contains("SampleClass", source.Declares);
        Assert.Single(source.Types);
        var typeInfo = source.Types[0];
        Assert.EndsWith("…", typeInfo.Summary, StringComparison.Ordinal);
        Assert.True(typeInfo.Members.Count <= 12);
        Assert.Contains("DoWork()", typeInfo.Members);
        Assert.Contains("FetchAsync()", typeInfo.Members);
    }

    [Fact]
    public void ExpandException_constructors_are_usable()
    {
        var e1 = new ExpandException();
        Assert.NotNull(e1);
        var e2 = new ExpandException("test message");
        Assert.Equal("test message", e2.Message);
        var inner = new InvalidOperationException("inner");
        var e3 = new ExpandException("msg", inner);
        Assert.Equal("msg", e3.Message);
        Assert.Same(inner, e3.InnerException);
    }

    [Fact]
    public void ElementNotes_and_Links_accessors()
    {
        var bundle = OkfBundle.Load(Shop());
        var concept = bundle.Concepts["context"];
        var notes = concept.ElementNotes();
        Assert.True(notes.ContainsKey("shop"));
        Assert.Contains("The thing we build.", notes["shop"], StringComparison.Ordinal);

        var links = C4Composer.CollectLinks(bundle);
        Assert.NotEmpty(links.ZoomsFrom("context"));
        Assert.Empty(links.ZoomsFrom("nonexistent"));
        Assert.NotEmpty(links.Into("shop/containers"));
        Assert.Empty(links.Into("nonexistent"));
    }

    [Fact]
    public void OkfBundle_path_and_string_helpers()
    {
        Assert.Equal(".", OkfBundle.PosixNormalise(""));
        Assert.Equal("..", OkfBundle.PosixNormalise(".."));
        Assert.Equal("../../sub", OkfBundle.PosixNormalise("../../sub"));
        Assert.Equal("a/c", OkfBundle.PosixNormalise("a/b/../c"));
        Assert.Equal("sample", OkfBundle.StripSuffix("sample.md", ".md"));
        Assert.Equal("sample.txt", OkfBundle.StripSuffix("sample.txt", ".md"));
    }

    [Fact]
    public void IsExternalOrAnchor_handles_schemes_and_anchors()
    {
        Assert.True(OkfBundle.IsExternalOrAnchor("mailto:user@example.com"));
        Assert.True(OkfBundle.IsExternalOrAnchor("https://example.com"));
        Assert.True(OkfBundle.IsExternalOrAnchor("HTTPS://example.com"));
        Assert.True(OkfBundle.IsExternalOrAnchor("//cdn.example.com/asset"));
        Assert.True(OkfBundle.IsExternalOrAnchor("#heading"));
        Assert.True(OkfBundle.IsExternalOrAnchor(""));
        Assert.False(OkfBundle.IsExternalOrAnchor("relative/doc.md"));
        Assert.False(OkfBundle.IsExternalOrAnchor("/root/doc.md"));
    }

    [Fact]
    public void RelIndex_skips_leading_index_position()
    {
        var text = "C4Dynamic\nRelIndex(1, api, db, \"Queries\")\n";
        var diagram = MermaidReader.Parse(text);
        Assert.NotNull(diagram);
        Assert.Single(diagram.Relationships);
        var rel = diagram.Relationships[0];
        Assert.Equal("api", rel.Source);
        Assert.Equal("db", rel.Target);
        Assert.Equal("Queries", rel.Label);
    }

    [Fact]
    public void Check_detects_all_finding_variations()
    {
        var root = _temp.Folder("findings_bundle");
        File.WriteAllText(Path.Join(root, "bad_frontmatter.md"), "---\n: invalid : yaml :\n---\nHello\n");
        File.WriteAllText(Path.Join(root, "missing_diagram.md"), "---\ntype: C4 Context\ntitle: Missing\n---\nNo diagram here\n");
        File.WriteAllText(Path.Join(root, "mismatched_kind.md"), "---\ntype: C4 Context\ntitle: Mismatch\n---\n```mermaid\nC4Container\nPerson(p, \"P\")\n```\n");
        File.WriteAllText(Path.Join(root, "code_c4.md"), "---\ntype: C4 Code\ntitle: Code Level\n---\n```mermaid\nC4Component\nComponent(c, \"C\")\n```\n");
        File.WriteAllText(Path.Join(root, "broken_note_link.md"), "---\ntype: note\ntitle: Note\n---\nLinks to [nonexistent](/not-there.md)\n");
        File.WriteAllText(Path.Join(root, "direct_jump.md"), "---\ntype: C4 Context\ntitle: Direct Jump\n---\n```mermaid\nC4Context\nSystem(s, \"S\", $link=\"component.md\")\n```\n");
        File.WriteAllText(Path.Join(root, "component.md"), "---\ntype: C4 Component\ntitle: Comp\n---\n```mermaid\nC4Component\nComponent(c, \"C\")\n```\n");
        File.WriteAllText(Path.Join(root, "parent2.md"), "---\ntype: C4 Context\ntitle: Parent 2\n---\n```mermaid\nC4Context\nSystem(s2, \"S2\", $link=\"component.md\")\n```\n");

        var bundle = OkfBundle.Load(root);
        var findings = C4Composer.Check(bundle);
        Assert.NotEmpty(findings);
        var codes = findings.Select(f => f.Code).ToHashSet();
        Assert.Contains("okf-frontmatter", codes);
        Assert.Contains("diagram-missing", codes);
        Assert.Contains("diagram-kind", codes);
        Assert.Contains("zoom-level", codes);
        Assert.Contains("fan-in", codes);
    }

    [Fact]
    public void Expand_throws_on_non_diagram_or_non_c4_target()
    {
        var root = _temp.Folder("expand_edges");
        File.WriteAllText(Path.Join(root, "not_diagram.md"), "---\ntype: note\ntitle: Note\n---\nText only\n");
        File.WriteAllText(Path.Join(root, "unclosed.md"), "---\ntype: C4 Container\ntitle: Unclosed\n---\n```mermaid\nC4Container\nContainer_Boundary(b, \"B\") {\n  Container(c, \"C\")\n```\n");
        File.WriteAllText(Path.Join(root, "parent.md"), "---\ntype: C4 Context\ntitle: Parent\n---\n```mermaid\nC4Context\nSystem(s, \"S\", $link=\"not_diagram.md\")\nSystem(nolink, \"NoLink\")\nSystem(s2, \"S2\", $link=\"unclosed.md\")\n```\n");
        var bundle = OkfBundle.Load(root);
        Assert.Throws<ExpandException>(() => C4Composer.Expand(bundle, "not_diagram", "s"));
        Assert.Throws<ExpandException>(() => C4Composer.Expand(bundle, "missing_concept", "s"));
        Assert.Throws<ExpandException>(() => C4Composer.Expand(bundle, "parent", "unknown_alias"));
        Assert.Throws<ExpandException>(() => C4Composer.Expand(bundle, "parent", "nolink"));
        Assert.Throws<ExpandException>(() => C4Composer.Expand(bundle, "parent", "s"));
        var expanded2 = C4Composer.Expand(bundle, "parent", "s2");
        Assert.Contains("Container(c, \"C\")", expanded2, StringComparison.Ordinal);
    }

    [Fact]
    public void Expand_with_top_level_child_elements_and_nested_boundaries()
    {
        var root = _temp.Folder("expand_nested");
        var parentMd = """
            ---
            type: C4 Context
            title: Nested Parent
            ---
            ```mermaid
            C4Context
            System(sub, "Subsystem", $link="sub.md")
            ```
            """;
        var childMd = """
            ---
            type: C4 Container
            title: Child Containers
            ---
            ```mermaid
            C4Container
            Container_Boundary(b1, "Outer") {
              Container_Boundary(b2, "Inner") {
                Component(innerComp, "InnerComponent")
              }
            }
            Container(topComp, "TopLevelComponent")
            ```
            """;
        File.WriteAllText(Path.Join(root, "parent.md"), parentMd);
        File.WriteAllText(Path.Join(root, "sub.md"), childMd);

        var bundle = OkfBundle.Load(root);
        var expanded = C4Composer.Expand(bundle, "parent", "sub");
        Assert.Contains("TopLevelComponent", expanded, StringComparison.Ordinal);
        Assert.Contains("InnerComponent", expanded, StringComparison.Ordinal);
    }

    private static string Between(string text)
    {
        var start = text.IndexOf("```mermaid\n", StringComparison.Ordinal) + "```mermaid\n".Length;
        return text[start..text.IndexOf("\n```", start, StringComparison.Ordinal)];
    }

    private static IEnumerable<TreeNode> Flat(IEnumerable<TreeNode> nodes) =>
        nodes.SelectMany(node => Flat(node.Children).Prepend(node));

    private static int Count(string text, string value) =>
        text.Split(value, StringSplitOptions.None).Length - 1;

    private static string RepoFile(string relative)
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            if (File.Exists(Path.Join(folder.FullName, "rig.slnx")))
            {
                return Path.Join(folder.FullName, relative);
            }
        }

        throw new InvalidOperationException("rig.slnx is not above the test output folder.");
    }
}
