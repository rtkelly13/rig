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

        var owners = new Dictionary<string, IReadOnlyList<string>>
        {
            ["owner1"] = ["Acme.TypeA", "Acme.TypeB"],
        };
        var index = DotnetScan.TypeIndex(owners);
        Assert.Equal("owner1", index["Acme.TypeA"]);
        Assert.Equal("owner1", index["Acme.TypeB"]);
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
