using Rig.Core.C4;

namespace Rig.Core.Tests.C4;

public sealed class MermaidTests
{
    [Fact]
    public void Generator_diagram_wraps_in_fences()
    {
        var lines = new[] { "C4Context", "Person(p, \"Person\")" };
        var rendered = MermaidGenerator.Diagram(lines);
        Assert.Equal("```mermaid\nC4Context\nPerson(p, \"Person\")\n```\n", rendered);
    }

    [Fact]
    public void Generator_quote_replaces_double_quotes()
    {
        Assert.Equal("hello 'world'", MermaidGenerator.Quote("hello \"world\""));
    }

    [Fact]
    public void Generator_container_renders_internal_and_external_and_links()
    {
        var internalNoLink = MermaidGenerator.Container("api", "Api", "net10.0", "12 files", null, external: false);
        Assert.Equal("Container(api, \"Api\", \"net10.0\", \"12 files\")", internalNoLink);

        var externalWithLink = MermaidGenerator.Container("ext", "Ext", "svc", "1 file", "components/ext", external: true);
        Assert.Equal("Container_Ext(ext, \"Ext\", \"svc\", \"1 file\", $link=\"/components/ext.md\")", externalWithLink);
    }

    [Fact]
    public void Generator_component_handles_pluralization_and_ellipsis()
    {
        var single = MermaidGenerator.Component("c1", "Comp 1", ["TypeA"], "code/c1");
        Assert.Contains("1 type", single, StringComparison.Ordinal);
        Assert.Contains("TypeA", single, StringComparison.Ordinal);
        Assert.DoesNotContain("…", single, StringComparison.Ordinal);

        var many = MermaidGenerator.Component("c2", "Comp 2", ["T1", "T2", "T3", "T4", "T5"], "code/c2");
        Assert.Contains("5 types", many, StringComparison.Ordinal);
        Assert.Contains("…", many, StringComparison.Ordinal);
    }

    [Fact]
    public void Generator_relationship_handles_technology()
    {
        Assert.Equal("Rel(a, b, \"calls\")", MermaidGenerator.Relationship("a", "b", "calls"));
        Assert.Equal("Rel(a, b, \"calls\", \"gRPC\")", MermaidGenerator.Relationship("a", "b", "calls", "gRPC"));
    }

    [Fact]
    public void Generator_boundary_renders_opener()
    {
        Assert.Equal("System_Boundary(b, \"Label\") {", MermaidGenerator.Boundary("System_Boundary", "b", "Label"));
    }

    [Fact]
    public void Reader_parses_c4_elements_boundaries_and_relationships()
    {
        const string source = """
            C4Container
            System_Boundary(b1, "System") {
                Container(api, "API", "net10.0", "Web service", $link="api.md")
                Container_Ext(db, "DB", "Postgres", "Storage")
            }
            Rel(api, db, "reads", "SQL")
            """;

        var diagram = MermaidReader.Parse(source);
        Assert.Equal("C4Container", diagram.Keyword);
        Assert.Equal(3, diagram.Elements.Count);

        var b1 = diagram.Element("b1");
        Assert.NotNull(b1);
        Assert.Equal(ElementKind.Boundary, b1.Kind);
        Assert.Null(b1.Parent);

        var api = diagram.Element("api");
        Assert.NotNull(api);
        Assert.Equal(ElementKind.Container, api.Kind);
        Assert.False(api.External);
        Assert.Equal("b1", api.Parent);
        Assert.Equal("api.md", api.Link);

        var db = diagram.Element("db");
        Assert.NotNull(db);
        Assert.True(db.External);
        Assert.Equal("b1", db.Parent);

        Assert.Single(diagram.Relationships);
        var rel = diagram.Relationships[0];
        Assert.Equal("api", rel.Source);
        Assert.Equal("db", rel.Target);
        Assert.Equal("reads", rel.Label);

        Assert.Equal(["b1", "api", "db"], diagram.Aliases);
    }

    [Fact]
    public void Reader_parses_clicks_in_non_c4_diagram()
    {
        const string source = """
            classDiagram
            class Foo
            class Bar
            click Foo href "foo.md"
            """;

        var diagram = MermaidReader.Parse(source);
        Assert.Equal("classDiagram", diagram.Keyword);
        Assert.Equal(2, diagram.Elements.Count);
        var foo = diagram.Element("Foo");
        Assert.NotNull(foo);
        Assert.Equal("foo.md", foo.Link);
    }

    [Fact]
    public void Reader_handles_empty_or_whitespace()
    {
        var diagram = MermaidReader.Parse("");
        Assert.Empty(diagram.Keyword);
        Assert.Empty(diagram.Elements);
        Assert.Empty(diagram.Relationships);
    }
}
