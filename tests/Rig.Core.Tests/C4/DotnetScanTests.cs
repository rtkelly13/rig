using Rig.Core.C4;

namespace Rig.Core.Tests.C4;

public sealed class DotnetScanTests : IDisposable
{
    private readonly TempDir _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void ReadProject_parses_frameworks_refs_and_compile_links()
    {
        var projPath = _temp.File("app.csproj");
        var content = """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFrameworks>net8.0;net10.0</TargetFrameworks>
                <RootNamespace>MyCompany.App</RootNamespace>
              </PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="..\Core\Core.csproj" />
                <ProjectReference Include="..\Analyzer\Analyzer.csproj" OutputItemType="Analyzer" />
                <Compile Include="..\Shared\Common.cs" Link="Common.cs" />
                <Compile Include="Auto\**\*.cs" />
              </ItemGroup>
            </Project>
            """;
        File.WriteAllText(projPath, content);

        var project = DotnetScan.ReadProject(projPath);
        Assert.Equal("app", project.Name);
        Assert.Equal("MyCompany.App", project.RootNamespace);
        Assert.Equal(["net8.0", "net10.0"], project.Frameworks);
        Assert.Equal(2, project.Refs.Count);
        Assert.Contains(project.Refs, r => !r.Analyzer);
        Assert.Contains(project.Refs, r => r.Analyzer);
        Assert.Single(project.CompileLinks);
    }

    [Fact]
    public void DiscoverProject_walks_dependencies()
    {
        var root = _temp.Folder("closure");
        var coreDir = Path.Join(root, "Core");
        var appDir = Path.Join(root, "App");
        Directory.CreateDirectory(coreDir);
        Directory.CreateDirectory(appDir);

        var corePath = Path.Join(coreDir, "Core.csproj");
        File.WriteAllText(corePath, "<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");

        var appPath = Path.Join(appDir, "App.csproj");
        File.WriteAllText(appPath, """
            <Project>
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <ItemGroup><ProjectReference Include="..\Core\Core.csproj" /></ItemGroup>
            </Project>
            """);

        var closure = DotnetScan.DiscoverProject(appPath);
        Assert.Equal(2, closure.Count);
        Assert.Contains(closure, p => p.Name == "App");
        Assert.Contains(closure, p => p.Name == "Core");
    }

    [Fact]
    public void Helper_methods_produce_expected_keys_and_indices()
    {
        Assert.Equal("Acme.Foo", DotnetScan.TypeOf("Acme.Foo.Bar/()"));
        Assert.Equal("Acme.Foo", DotnetScan.TypeOf("Acme.Foo.Bar"));
        Assert.Equal("NoDot", DotnetScan.TypeOf("NoDot"));

        var owners = new Dictionary<string, IReadOnlyList<string>>
        {
            ["comp1"] = ["TypeA", "TypeB"],
            ["comp2"] = ["TypeC"],
        };
        var index = DotnetScan.TypeIndex(owners);
        Assert.Equal("comp1", index["TypeA"]);
        Assert.Equal("comp2", index["TypeC"]);
    }

    [Fact]
    public void ReadSource_handles_records_enums_interfaces_and_structs()
    {
        var csPath = _temp.File("Types.cs");
        var code = """
            namespace Sample.Domain;

            public interface IOrderService { void PlaceOrder(); }
            public record struct OrderItem(string Sku, int Quantity);
            public enum OrderStatus { Pending, Completed }
            public ref struct SpanBuffer { }
            """;
        File.WriteAllText(csPath, code);

        var source = DotnetScan.ReadSource(csPath);
        Assert.Equal("Sample.Domain", source.Namespace);
        Assert.Equal(4, source.Types.Count);
        Assert.Contains(source.Types, t => t.Kind == "interface" && t.Name == "IOrderService");
        Assert.Contains(source.Types, t => t.Kind == "record" && t.Name == "OrderItem");
        Assert.Contains(source.Types, t => t.Kind == "enum" && t.Name == "OrderStatus");
        Assert.Contains(source.Types, t => t.Kind == "struct" && t.Name == "SpanBuffer");
    }

    [Fact]
    public void Sources_filters_bin_and_obj_directories()
    {
        var projDir = _temp.Folder("Project");
        var projFile = Path.Join(projDir, "Project.csproj");
        File.WriteAllText(projFile, "<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");

        var srcFile = Path.Join(projDir, "Valid.cs");
        File.WriteAllText(srcFile, "namespace Project; public class Valid {}");

        var objDir = Path.Join(projDir, "obj", "Debug");
        Directory.CreateDirectory(objDir);
        File.WriteAllText(Path.Join(objDir, "Generated.cs"), "namespace Project; public class Generated {}");

        var project = DotnetScan.ReadProject(projFile);
        var sources = DotnetScan.Sources(project);
        Assert.Single(sources);
        Assert.Equal("Valid.cs", Path.GetFileName(sources[0].Path));
    }
}
