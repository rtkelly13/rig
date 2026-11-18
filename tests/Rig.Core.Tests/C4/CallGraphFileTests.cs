using Rig.Core.C4;

namespace Rig.Core.Tests.C4;

public sealed class CallGraphFileTests : IDisposable
{
    private readonly TempDir _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Key_and_TypeOf_parse_identifiers()
    {
        var ctor = CallGraphFile.Key("Acme.Foo.Bar..ctor/()");
        Assert.Equal("Acme.Foo.Bar", ctor.Type);
        Assert.Equal("new", ctor.Name);

        var cctor = CallGraphFile.Key("Acme.Foo.Bar..cctor/()");
        Assert.Equal("Acme.Foo.Bar", cctor.Type);
        Assert.Equal("static new", cctor.Name);

        var method = CallGraphFile.Key("Acme.Foo.Bar.Run/(int, string)");
        Assert.Equal("Acme.Foo.Bar", method.Type);
        Assert.Equal("Run", method.Name);
        Assert.Equal("Acme.Foo.Bar", CallGraphFile.TypeOf("Acme.Foo.Bar.Run/(int, string)"));

        var globalMethod = CallGraphFile.Key("Standalone/()");
        Assert.Equal(string.Empty, globalMethod.Type);
        Assert.Equal("Standalone", globalMethod.Name);
    }

    [Fact]
    public void TypeDecl_properties_extract_namespace_and_name()
    {
        var decl = new TypeDecl("Acme.Models.User", "class", "public", "src/User.cs", 10);
        Assert.Equal("Acme.Models", decl.Namespace);
        Assert.Equal("User", decl.Name);

        var globalDecl = new TypeDecl("GlobalType", "class", "public", "src/Global.cs", 5);
        Assert.Equal(string.Empty, globalDecl.Namespace);
        Assert.Equal("GlobalType", globalDecl.Name);
    }

    [Fact]
    public void Read_parses_types_members_calls_and_implements()
    {
        var graph = new CallGraphFile();

        var lines = new[]
        {
            "T:Acme.Service|class|public|src/Service.cs:42",
            "T:InvalidTypeLine",
            "T:Acme.BadColon|class|public|src/Service.cs:notanumber",
            "M:Acme.Service.Run/(string)|method|public|45",
            "M:Acme.Service.Count|property|public|48",
            "M:Acme.Service.Field|field|private|50",
            "M:Acme.Service.BadLine|method|public|notanumber",
            "M:InvalidMemberLine",
            "E:Acme.Service.Run/() | Acme.Service.Count",
            "X:Acme.IService.Run/() | Acme.Service.Run/()",
            "E:InvalidEdgeWithoutSeparator",
            "UNKNOWN:RandomTag",
        };

        graph.Read(lines);

        Assert.True(graph.Types.ContainsKey("Acme.Service"));
        var type = graph.Types["Acme.Service"];
        Assert.Equal("class", type.Kind);
        Assert.Equal(42, type.Line);

        var runKey = new MemberKey("Acme.Service", "Run");
        var countKey = new MemberKey("Acme.Service", "Count");
        var fieldKey = new MemberKey("Acme.Service", "Field");

        Assert.True(graph.IsCallable(runKey));
        Assert.False(graph.IsCallable(fieldKey));
        Assert.False(graph.IsCallable(new MemberKey("Acme.Service", "Missing")));

        var members = graph.OfType("Acme.Service");
        Assert.Equal(3, members.Count);

        Assert.Equal("Run()", graph.Label(runKey));
        Assert.Equal("Count", graph.Label(countKey));
        Assert.Equal("Missing()", graph.Label(new MemberKey("Acme.Service", "Missing")));

        Assert.Single(graph.Calls);
        Assert.Single(graph.Implements);
    }

    [Fact]
    public void Load_reads_and_merges_multiple_project_files()
    {
        var dir = _temp.Folder("graphs");
        File.WriteAllText(Path.Join(dir, "ProjA.callgraph.txt"), "T:ProjA.A|class|public|src/A.cs:1\nE:ProjA.A.Run/() | ProjB.B.Run/()\n");
        File.WriteAllText(Path.Join(dir, "ProjB.callgraph.txt"), "T:ProjB.B|class|public|src/B.cs:1\n");

        Assert.Null(CallGraphFile.Load(dir, ["Nonexistent"]));

        var merged = CallGraphFile.Load(dir, ["ProjA", "ProjB", "Nonexistent"]);
        Assert.NotNull(merged);
        Assert.True(merged.Types.ContainsKey("ProjA.A"));
        Assert.True(merged.Types.ContainsKey("ProjB.B"));
        Assert.Single(merged.Calls);
    }
}
