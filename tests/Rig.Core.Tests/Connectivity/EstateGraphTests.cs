using System.Text.Json.Nodes;
using Rig.Core.Connectivity;

namespace Rig.Core.Tests.Connectivity;

/// <summary>Real manifest evidence, package aliases, incomplete scans, cycle-preserving DAGs and the snapshot's reuse.</summary>
public sealed class EstateGraphTests : IDisposable
{
    private const string LibraryPackage = "<Project><PackageId>Managed.Lib</PackageId></Project>";
    private const string CentralOne = """<Project><PackageVersion Include="Managed.Lib" Version="1.0"/></Project>""";

    private readonly EstateFixture _estate = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _estate.Dispose();

    [Fact]
    public async Task The_graph_matches_what_the_Python_scan_recorded()
    {
        var folder = Path.Join(AppContext.BaseDirectory, "Connectivity");
        await _estate.LayOutAsync(JsonNode.Parse(await File.ReadAllTextAsync(Path.Join(folder, "graph-fixture.json"), Token))!.AsObject());

        var actual = EstateFixture.Json(await _estate.BuildAsync());
        actual["scanned_at"] = "recorded";

        var expected = JsonNode.Parse(await File.ReadAllTextAsync(Path.Join(folder, "graph-recorded.json"), Token));
        Assert.Equal(expected!.ToJsonString(), actual.ToJsonString());
    }

    [Fact]
    public async Task Connections_join_real_npm_nuget_python_and_project_references()
    {
        await _estate.CheckoutAsync("ui", new Dictionary<string, string> { ["package.json"] = """{"name": "@scope/new-ui"}""" });
        await _estate.CheckoutAsync("library", new Dictionary<string, string> { ["lib.csproj"] = "<Project><PropertyGroup><PackageId>Managed.Lib</PackageId></PropertyGroup></Project>" });
        await _estate.CheckoutAsync("python", new Dictionary<string, string> { ["pyproject.toml"] = "[project]\nname=\"Managed_Tool\"\nversion=\"1.0\"\n" });
        await _estate.CheckoutAsync("app", new Dictionary<string, string>
        {
            ["package.json"] = """{"name": "app", "dependencies": {"@scope/new-ui": "^1.0"}, "devDependencies": {"old-ui": "npm:@scope/old-ui@^0.5"}}""",
            ["app.csproj"] = """<Project><ItemGroup><PackageReference Include="MANAGED.LIB" Version="1.2"/><ProjectReference Include="../library/lib.csproj"/></ItemGroup></Project>""",
            ["pyproject.toml"] = "[project]\nname=\"app-python\"\ndependencies=[\"managed-tool>=1\"]\n",
        });
        _estate.Projects(
        [
            new JsonObject
            {
                ["name"] = "ui",
                ["repo"] = "https://github.com/owner/ui",
                ["packages"] = new JsonArray(new JsonObject { ["registry"] = "npm", ["name"] = "@scope/old-ui", ["manifest"] = "package.json", ["workflow"] = "publish.yml" }),
            },
            new JsonObject { ["name"] = "library", ["repo"] = "https://github.com/owner/library" },
            new JsonObject { ["name"] = "python", ["repo"] = "https://github.com/owner/python" },
            new JsonObject { ["name"] = "app", ["repo"] = "https://github.com/owner/app" },
        ]);

        var graph = await _estate.BuildAsync();

        Assert.Equal(4, graph.Projects.Count);
        Assert.Equal(
            [("owner/library", "owner/app"), ("owner/python", "owner/app"), ("owner/ui", "owner/app")],
            graph.Dependencies.Select(d => (d.Source, d.Target)).Distinct().Order());
        Assert.Equal(["npm", "nuget", "project", "pypi"], graph.Dependencies.Select(d => d.Registry).Distinct().Order(StringComparer.Ordinal));
        Assert.Equal(["@scope/new-ui", "@scope/old-ui"], graph.Dependencies.Where(d => d.Registry == "npm").Select(d => d.Package).Order(StringComparer.Ordinal));
        Assert.Equal(["app.csproj", "package.json", "pyproject.toml"], graph.Dependencies.Select(d => d.Evidence).Distinct().Order(StringComparer.Ordinal));
        Assert.Contains(graph.Dependencies, d => d.Requirement == "^1.0");
        Assert.All(graph.Projects, p => Assert.True(p.Scanned));

        var repeated = await _estate.BuildAsync();
        Assert.Equal(graph.Dependencies, repeated.Dependencies);
        Assert.Equal(EstateFixture.Json(graph)["components"]!.ToJsonString(), EstateFixture.Json(repeated)["components"]!.ToJsonString());
    }

    [Fact]
    public async Task Cycles_are_condensed_without_dropping_the_original_evidence()
    {
        await _estate.CheckoutAsync("a", new Dictionary<string, string> { ["package.json"] = """{"name":"a","dependencies":{"b":"1"}}""" });
        await _estate.CheckoutAsync("b", new Dictionary<string, string> { ["package.json"] = """{"name":"b","dependencies":{"a":"1"}}""" });
        await _estate.CheckoutAsync("c", new Dictionary<string, string> { ["package.json"] = """{"dependencies":{"a":"1"}}""" });
        _estate.Owned("a", "b", "c");

        var graph = await _estate.BuildAsync();

        var cycle = Assert.Single(graph.Components, c => c.Cyclic);
        Assert.Equal(["owner/a", "owner/b"], cycle.Members);
        Assert.Equal(Condensation.CycleId(["owner/a", "owner/b"]), cycle.Id);
        Assert.Equal(3, graph.Dependencies.Count);
        Assert.Equal([new ComponentEdge(cycle.Id, "owner/c")], graph.Edges);
    }

    [Fact]
    public async Task Ambiguous_publishers_and_missing_checkouts_stay_explicit()
    {
        await _estate.CheckoutAsync("a", new Dictionary<string, string> { ["package.json"] = """{"name":"same"}""" });
        await _estate.CheckoutAsync("b", new Dictionary<string, string> { ["package.json"] = """{"name":"same"}""" });
        await _estate.CheckoutAsync("consumer", new Dictionary<string, string> { ["package.json"] = """{"dependencies":{"same":"1"}}""" });
        _estate.Owned("a", "b", "consumer", "missing");

        var graph = await _estate.BuildAsync();

        Assert.Empty(graph.Dependencies);
        Assert.Equal(["Ambiguous publisher for npm:same; no connection was guessed."], graph.Warnings);
        var missing = Assert.Single(graph.Projects, p => p.Name == "missing");
        Assert.False(missing.Scanned);
        Assert.Equal(["Local checkout is unavailable; consumption was not scanned."], missing.Warnings);
    }

    [Fact]
    public async Task NuGet_comments_are_ignored_and_central_versions_come_from_the_nearest_props()
    {
        await _estate.CheckoutAsync("library", new Dictionary<string, string> { ["lib.csproj"] = LibraryPackage });
        await _estate.CheckoutAsync("app", new Dictionary<string, string>
        {
            ["Directory.Packages.props"] = CentralOne,
            ["app.csproj"] = """<Project><!-- <PackageReference Include="Managed.Lib" Version="99"/> --><PackageReference Include="Managed.Lib"/></Project>""",
            ["nested/Directory.Packages.props"] = """<Project><PackageVersion Include="Managed.Lib" Version="2.0"/></Project>""",
            ["nested/test.csproj"] = """<Project><PackageReference Include="Managed.Lib"/></Project>""",
        });
        _estate.Owned("library", "app");

        var graph = await _estate.BuildAsync();

        Assert.Equal(["1.0", "2.0"], graph.Dependencies.Select(d => d.Requirement).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_declared_connection_needs_no_local_checkout()
    {
        await _estate.CheckoutAsync("provider", new Dictionary<string, string> { ["package.json"] = """{"name":"managed"}""" });
        _estate.Projects(
        [
            new JsonObject { ["name"] = "provider", ["repo"] = "https://github.com/owner/provider" },
            new JsonObject { ["name"] = "consumer", ["repo"] = "https://github.com/owner/consumer", ["depends_on"] = new JsonArray("provider", "unknown") },
        ]);

        var graph = await _estate.BuildAsync();

        var declared = Assert.Single(graph.Dependencies);
        Assert.Equal(new Dependency("owner/provider", "owner/consumer", "declared", "provider", "declared dependency", "runtime", "config/managed-projects.json · depends_on"), declared);
        Assert.Equal(["Unknown or self dependency unknown for owner/consumer"], graph.Warnings);
    }

    [Fact]
    public async Task NuGet_updates_need_an_include_and_explicit_metadata_wins()
    {
        await _estate.CheckoutAsync("library", new Dictionary<string, string> { ["lib.csproj"] = LibraryPackage });
        await _estate.CheckoutAsync("app", new Dictionary<string, string>
        {
            ["Directory.Packages.props"] = CentralOne,
            ["update.csproj"] = """<Project><PackageReference Update="Managed.Lib" Version="99"/></Project>""",
            ["child.csproj"] = """<Project><PackageReference Include="Managed.Lib"><Version>2.0</Version></PackageReference></Project>""",
            ["override.csproj"] = """<Project><PackageReference Include="Managed.Lib" VersionOverride="3.0"/></Project>""",
            ["child-override.csproj"] = """<Project><PackageReference Include="Managed.Lib" Version="2.0"><VersionOverride>4.0</VersionOverride></PackageReference></Project>""",
            ["included.csproj"] = """<Project><PackageReference Include="Managed.Lib"/><PackageReference Update="Managed.Lib" Version="5.0"/></Project>""",
        });
        _estate.Owned("library", "app");

        var graph = await _estate.BuildAsync();

        Assert.Equal(["2.0", "3.0", "4.0", "5.0"], graph.Dependencies.Select(d => d.Requirement).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(graph.Dependencies, d => d.Evidence == "update.csproj");
    }

    [Fact]
    public async Task A_malformed_manifest_is_a_warning_on_its_project_not_a_failure()
    {
        await _estate.CheckoutAsync("broken", new Dictionary<string, string>
        {
            ["package.json"] = "{not json",
            ["pyproject.toml"] = "[project\nname=",
            ["web/package.json"] = """{"name": 3}""",
        });
        _estate.Owned("broken");

        var project = Assert.Single((await _estate.BuildAsync()).Projects);

        Assert.True(project.Scanned);
        Assert.Equal(3, project.Manifests);
        Assert.Equal(["package.json", "pyproject.toml", "web/package.json"], project.Warnings.Select(w => w[..w.IndexOf(':', StringComparison.Ordinal)]));
    }

    [Fact]
    public async Task The_rig_checkout_answers_for_itself()
    {
        var rig = Path.GetDirectoryName(Path.GetDirectoryName(_estate.Registry))!;
        Directory.CreateDirectory(rig);
        await File.WriteAllTextAsync(Path.Join(rig, "package.json"), """{"name":"rig-kit"}""", Token);
        Assert.True((await _estate.Git.RunAsync(rig, ["init", "--quiet"], cancellationToken: Token)).Ok);
        Assert.True((await _estate.Git.RunAsync(rig, ["add", "-f", "package.json"], cancellationToken: Token)).Ok);
        Assert.True((await _estate.Git.RunAsync(rig, ["remote", "add", "origin", "git@github.com:owner/rig.git"], cancellationToken: Token)).Ok);
        await _estate.CheckoutAsync("consumer", new Dictionary<string, string> { ["package.json"] = """{"dependencies":{"rig-kit":"1"}}""" });
        _estate.Owned("rig", "consumer");

        var graph = await _estate.BuildAsync();

        Assert.All(graph.Projects, project => Assert.True(project.Scanned));
        var dependency = Assert.Single(graph.Dependencies);
        Assert.Equal(("owner/rig", "owner/consumer"), (dependency.Source, dependency.Target));
    }

    [Fact]
    public async Task A_folder_git_cannot_list_is_unscanned()
    {
        Directory.CreateDirectory(Path.Join(_estate.Personal, "plain"));
        _estate.Owned("plain");

        var project = Assert.Single((await _estate.BuildAsync()).Projects);

        Assert.False(project.Scanned);
        Assert.StartsWith(Manifests.Unavailable, Assert.Single(project.Warnings), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_recent_snapshot_is_reused_until_a_refresh()
    {
        await _estate.CheckoutAsync("a", new Dictionary<string, string> { ["package.json"] = """{"name":"a"}""" });
        _estate.Owned("a");
        var now = new FixedTime(DateTimeOffset.UtcNow);
        var graph = _estate.Graph(now);

        var first = await graph.LoadAsync(_estate.Registry, _estate.Personal, refresh: false, Token);
        _estate.Owned("a", "b");
        var kept = await graph.LoadAsync(_estate.Registry, _estate.Personal, refresh: false, Token);
        var refreshed = await graph.LoadAsync(_estate.Registry, _estate.Personal, refresh: true, Token);

        Assert.Single(first.Projects);
        Assert.Single(kept.Projects);
        Assert.Equal(first.ScannedAt, kept.ScannedAt);
        Assert.Equal(2, refreshed.Projects.Count);
        Assert.True(File.Exists(_estate.Snapshot));
    }

    [Fact]
    public async Task A_stale_or_torn_snapshot_is_rebuilt()
    {
        _estate.Owned("a");
        Directory.CreateDirectory(Path.GetDirectoryName(_estate.Snapshot)!);
        await File.WriteAllTextAsync(_estate.Snapshot, "{torn", Token);

        var rebuilt = await _estate.Graph().LoadAsync(_estate.Registry, _estate.Personal, refresh: false, Token);
        Assert.Single(rebuilt.Projects);

        var later = new FixedTime(DateTimeOffset.UtcNow + EstateGraph.Reuse + TimeSpan.FromSeconds(1));
        _estate.Owned("a", "b");
        Assert.Equal(2, (await _estate.Graph(later).LoadAsync(_estate.Registry, _estate.Personal, refresh: false, Token)).Projects.Count);
    }

    [Fact]
    public async Task Scanned_at_is_written_as_Python_wrote_it()
    {
        _estate.Owned("a");
        var graph = await _estate.Graph(new FixedTime(new DateTimeOffset(2026, 11, 12, 10, 0, 0, 123, 456, TimeSpan.Zero))).BuildAsync(_estate.Registry, _estate.Personal, Token);

        Assert.Equal("2026-11-12T10:00:00.123456+00:00", graph.ScannedAt);
    }

    [Theory]
    [InlineData("{}", "projects: expected a list")]
    [InlineData("""{"projects": [3]}""", "projects[0]: expected an object")]
    [InlineData("""{"projects": [{"name": "a", "repo": "https://github.com/owner/a", "depends_on": "b"}]}""", "projects[0].depends_on: expected a list")]
    [InlineData("""{"projects": [{"name": "a", "repo": "https://github.com/owner/a", "depends_on": [1]}]}""", "projects[0].depends_on: expected strings")]
    public async Task A_malformed_registry_is_invalid_data(string registry, string message)
    {
        _estate.Owned();
        await File.WriteAllTextAsync(_estate.Registry, registry, Token);

        var error = await Assert.ThrowsAnyAsync<Exception>(_estate.BuildAsync);
        Assert.True(error is InvalidDataException, error.ToString());
        Assert.EndsWith(message, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("git@github.com:Owner/Repo.git", "owner/repo")]
    [InlineData("https://github.com/Owner/Repo", "owner/repo")]
    [InlineData("Local/Thing.git", "local/thing")]
    public void A_slug_is_the_lower_case_identity(string value, string slug) => Assert.Equal(slug, EstateGraph.Slug(value));

    [Theory]
    [InlineData("pypi", "Managed__Tool.x", "managed-tool-x")]
    [InlineData("npm", "@Scope/UI", "@scope/ui")]
    [InlineData("nuget", "Managed.Lib", "managed.lib")]
    public void Package_names_are_canonical_per_registry(string registry, string name, string canonical) =>
        Assert.Equal(canonical, Manifests.Canonical(registry, name));

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
