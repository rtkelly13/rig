using Rig.Cli.C4;
using Rig.Cli.Forwarding;
using Rig.Core.GitHub;
using Rig.Core.Processes;
using Rig.Core.Tests;
using Rig.Core.Tests.Fakes;

namespace Rig.Cli.Tests.C4;

/// <summary><c>rig c4</c> through the C# front door: check, model, tree, expand and derive, with no Python fallback.</summary>
public sealed class C4CommandTests : IDisposable
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

    private const string DeriveContext = """
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
    private readonly FakeProc _proc = new();
    private readonly StringWriter _output = new() { NewLine = "\n" };
    private readonly StringWriter _error = new() { NewLine = "\n" };

    public void Dispose()
    {
        _output.Dispose();
        _error.Dispose();
        _temp.Dispose();
    }

    private string Shop() => Write(_temp.Folder("shop"), new Dictionary<string, string>
    {
        ["context.md"] = Context,
        ["shop/containers.md"] = Containers,
        ["shop/api.md"] = Api,
    });

    private string Repo()
    {
        var root = Write(_temp.Folder("acme"), new Dictionary<string, string>
        {
            ["src/Acme.Gen/Acme.Gen.csproj"] = "<Project><PropertyGroup><TargetFramework>netstandard2.0</TargetFramework><IsRoslynComponent>true</IsRoslynComponent></PropertyGroup><ItemGroup><ProjectReference Include=\"..\\Acme.Runtime\\Acme.Runtime.csproj\" /></ItemGroup></Project>",
            ["src/Acme.Gen/Gen.cs"] = "namespace Acme.Gen;\npublic sealed class Gen : IIncrementalGenerator { void Run() {} }\n",
            ["src/Acme.Runtime/Acme.Runtime.csproj"] = "<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>",
            ["src/Acme.Runtime/Marker.cs"] = "namespace Acme.Runtime;\npublic sealed class Marker { }\n",
        });
        var graph = _temp.Folder("callgraph");
        File.WriteAllText(Path.Join(graph, "Acme.Gen.callgraph.txt"), "E:Acme.Gen.Gen.Run/() | Acme.Runtime.Marker.Marker/()\n");
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
    public async Task Check_prints_a_table_and_fails_only_on_errors_unless_strict()
    {
        var shop = Shop();
        Assert.Equal(0, await Run("c4", "check", shop));
        Assert.Contains("3 concepts", _output.ToString(), StringComparison.Ordinal);
        Assert.Contains("unverified", _output.ToString(), StringComparison.Ordinal);

        Reset();
        Assert.Equal(0, await Run("c4", "check", shop, "--quiet", "--json"));
        Assert.Equal("[]\n", _output.ToString());

        await File.WriteAllTextAsync(Path.Join(shop, "context.md"), Context.Replace("System_Ext(pay, \"Payments\")\n", string.Empty, StringComparison.Ordinal), TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Join(shop, "shop", "orphan.md"), Api.Replace("API components", "Orphan", StringComparison.Ordinal), TestContext.Current.CancellationToken);
        Reset();
        Assert.Equal(0, await Run("c4", "check", shop));
        Reset();
        Assert.Equal(1, await Run("c4", "check", shop, "--strict", "--json"));
        Assert.Contains("orphan", _output.ToString(), StringComparison.Ordinal);
        Assert.Empty(_proc.Requests);
    }

    [Fact]
    public async Task Check_exits_one_on_an_error_and_supports_src()
    {
        var shop = Shop();
        await File.WriteAllTextAsync(Path.Join(shop, "lonely.md"), "---\ntitle: no type\n---\nhi\n", TestContext.Current.CancellationToken);
        Assert.Equal(1, await Run("c4", "check", "--src", shop));
        Assert.Contains("okf-type", _output.ToString(), StringComparison.Ordinal);
        Assert.Empty(_error.ToString());
    }

    [Fact]
    public async Task Tree_prints_the_zoom_hierarchy_as_text_and_json()
    {
        var shop = Shop();
        Assert.Equal(0, await Run("c4", "tree", shop));
        Assert.Contains("API components", _output.ToString(), StringComparison.Ordinal);
        Reset();
        Assert.Equal(0, await Run("c4", "tree", "--src", shop, "--json"));
        Assert.Contains("\"id\": \"context\"", _output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Model_prints_every_alias_as_json()
    {
        Assert.Equal(0, await Run("c4", "model", Shop()));
        Assert.Contains("\"alias\": \"user\"", _output.ToString(), StringComparison.Ordinal);
        Assert.StartsWith("{\n  \"elements\": [", _output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Expand_prints_mermaid_or_fails_on_an_unknown_alias()
    {
        var shop = Shop();
        Assert.Equal(0, await Run("c4", "expand", "context", "shop", "--root", shop));
        Assert.Contains("System_Boundary(shop, \"Shop\") {", _output.ToString(), StringComparison.Ordinal);
        Reset();
        Assert.Equal(1, await Run("c4", "expand", "context", "nobody", "--root", shop));
        Assert.Contains("not a zoomable element", _output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Derive_writes_then_check_passes_until_a_file_goes_stale()
    {
        var repo = Repo();
        var output = _temp.Folder("bundle");
        await File.WriteAllTextAsync(Path.Join(output, "system-context.md"), DeriveContext, TestContext.Current.CancellationToken);
        var args = new[] { "c4", "derive", "dotnet", repo, output, "--parent", "system-context:acme", "--callgraph", Path.Join(_temp.Path, "callgraph") };

        Assert.Equal(0, await Run(args));
        Assert.Contains("wrote containers.md", _output.ToString(), StringComparison.Ordinal);
        Assert.Contains("relationships on both pathways", _output.ToString(), StringComparison.Ordinal);

        Reset();
        Assert.Equal(0, await Run([.. args, "--check"]));
        Assert.Contains("0 stale", _output.ToString(), StringComparison.Ordinal);

        await File.WriteAllTextAsync(Path.Join(output, "containers.md"), "edited\n", TestContext.Current.CancellationToken);
        Reset();
        Assert.Equal(1, await Run([.. args, "--check"]));
        Assert.Contains("stale containers.md", _output.ToString(), StringComparison.Ordinal);
        Assert.Empty(_proc.Requests);
    }

    [Fact]
    public async Task Derive_can_start_from_one_project()
    {
        var repo = Repo();
        var output = _temp.Folder("bundle");
        Assert.Equal(0, await Run("c4", "derive", "dotnet", repo, output, "--project", Path.Join(repo, "src/Acme.Gen/Acme.Gen.csproj")));
        Assert.True(File.Exists(Path.Join(output, "components", "gen.md")));
        Assert.True(File.Exists(Path.Join(output, "components", "runtime.md")));
    }

    [Fact]
    public async Task Help_lists_the_group_and_every_subcommand()
    {
        Assert.Equal(0, await Run("c4", "--help"));
        foreach (var name in new[] { "check", "derive", "expand", "model", "tree" })
        {
            Assert.Contains($"  {name} ", _output.ToString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Commands_handle_missing_directories_gracefully()
    {
        var missing = Path.Join(_temp.Path, "does-not-exist");
        Assert.Equal(1, await Run("c4", "check", missing));
        Assert.NotEmpty(_error.ToString());

        Reset();
        Assert.Equal(1, await Run("c4", "model", missing));
        Assert.NotEmpty(_error.ToString());

        Reset();
        Assert.Equal(1, await Run("c4", "tree", missing));
        Assert.NotEmpty(_error.ToString());

        Reset();
        Assert.Equal(1, await Run("c4", "expand", "context", "alias", "--root", missing));
        Assert.NotEmpty(_error.ToString());
    }

    private void Reset()
    {
        _output.GetStringBuilder().Clear();
        _error.GetStringBuilder().Clear();
    }

    private async Task<int> Run(params string[] args)
    {
        _output.GetStringBuilder().Clear();
        _error.GetStringBuilder().Clear();
        var host = new C4Host(Path.Join(_temp.Path, "shop"), (_, arguments) => Task.FromResult(arguments[0] switch
        {
            "remote" => "git@github.com:acme/acme.git",
            "rev-parse" => "abc1234def56",
            _ => "2026-11-14",
        }));
        var app = new RigApp(
            new PythonRig(_proc, _temp.Path),
            new FakeGitHub(),
            _ => Task.FromResult<RepoSlug?>(null),
            _output,
            _error,
            [new C4Command(host, _output, _error).Build]);
        return await app.RunAsync(args, TestContext.Current.CancellationToken);
    }
}
