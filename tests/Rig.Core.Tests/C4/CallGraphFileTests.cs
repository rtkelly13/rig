using Rig.Core.C4;

namespace Rig.Core.Tests.C4;

/// <summary>The Roslyn callgraph text format: declared types and members, calls, and interface dispatch edges.</summary>
public sealed class CallGraphFileTests : IDisposable
{
    private readonly TempDir _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Reads_types_members_calls_and_implements_edges()
    {
        var graph = new CallGraphFile();
        graph.Read([
            "T:Acme.Runner|class|public|src/Acme/Runner.cs:7",
            "M:Acme.Runner.Run/(int)|method|public|9",
            "M:Acme.Runner.Run/(string)|method|public|10",
            "M:Acme.Runner..ctor/()|ctor|public|5",
            "M:Acme.Runner.Count|property|public|12",
            "M:Acme.Runner.CountValue|field|private|13",
            "E:Acme.Runner.Run/(int) | Acme.Runner.Count",
            "X:Acme.IRunner.Run/(int) | Acme.Runner.Run/(int)",
        ]);

        var type = graph.Types["Acme.Runner"];
        Assert.Equal(("class", "public", "src/Acme/Runner.cs", 7), (type.Kind, type.Access, type.Path, type.Line));
        Assert.Equal("Acme", type.Namespace);
        Assert.Equal("Runner", type.Name);

        var key = new MemberKey("Acme.Runner", "Run");
        Assert.Equal(2, graph.Members[key].Count);
        Assert.True(graph.IsCallable(key));
        Assert.False(graph.IsCallable(new MemberKey("Acme.Runner", "CountValue")));
        Assert.Equal("Run()", graph.Label(key));
        Assert.Equal("Count", graph.Label(new MemberKey("Acme.Runner", "Count")));

        Assert.Equal(
            [new MemberKey("Acme.Runner", "Count"), new MemberKey("Acme.Runner", "CountValue"), new MemberKey("Acme.Runner", "Run"), new MemberKey("Acme.Runner", "new")],
            graph.OfType("Acme.Runner"));
        Assert.Contains(new CallEdge(new MemberKey("Acme.Runner", "Run"), new MemberKey("Acme.Runner", "Count")), graph.Calls);
        Assert.Contains(new CallEdge(new MemberKey("Acme.IRunner", "Run"), new MemberKey("Acme.Runner", "Run")), graph.Implements);
    }

    [Fact]
    public void Load_reads_the_named_projects_and_is_null_when_none_exist()
    {
        var folder = _temp.Folder("graph");
        File.WriteAllText(Path.Join(folder, "Acme.Runner.callgraph.txt"), "T:Acme.Runner|class|public|Runner.cs:1\nE:Acme.Runner.A/() | Acme.Runner.B/()\n");
        File.WriteAllText(Path.Join(folder, "Acme.Other.callgraph.txt"), "T:Acme.Other|class|internal|Other.cs:2\n");

        var graph = CallGraphFile.Load(folder, ["Acme.Runner", "Acme.Other", "Acme.Missing"]);
        Assert.NotNull(graph);
        Assert.Equal(["Acme.Other", "Acme.Runner"], graph.Types.Keys.Order(StringComparer.Ordinal));
        Assert.Single(graph.Calls);
        Assert.Null(CallGraphFile.Load(folder, ["Acme.Missing"]));
    }

    [Theory]
    [InlineData("Acme.Runner.Run/(int)", "Acme.Runner", "Run")]
    [InlineData("Acme.Runner..ctor/()", "Acme.Runner", "new")]
    [InlineData("Acme.Runner..cctor/()", "Acme.Runner", "static new")]
    [InlineData("Acme.Runner.Count", "Acme.Runner", "Count")]
    [InlineData("Bare", "", "Bare")]
    public void Keys_name_a_member_and_its_type(string identifier, string type, string name) =>
        Assert.Equal(new MemberKey(type, name), CallGraphFile.Key(identifier));

    [Fact]
    public void Type_of_drops_the_member_and_signature() =>
        Assert.Equal("Acme.Runner", CallGraphFile.TypeOf("Acme.Runner.Run/(int)"));
}
