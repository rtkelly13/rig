using Rig.Core.Connectivity;

namespace Rig.Core.Tests.Connectivity;

/// <summary>The bounds on a repo's manifest inventory: how many, how large, and in what encoding.</summary>
public sealed class ManifestsTests : IDisposable
{
    private readonly EstateFixture _estate = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _estate.Dispose();

    [Fact]
    public async Task Too_many_manifests_leave_the_repo_unscanned()
    {
        var files = Enumerable.Range(0, Manifests.MaxManifests + 1).ToDictionary(i => $"p{i}/package.json", _ => "{}");
        var root = await _estate.CheckoutAsync("huge", files);

        var inventory = await Manifests.ReadAsync(_estate.Git, root, "owner/huge", EstateFixture.ListTimeout, Token);

        Assert.False(inventory.Scanned);
        Assert.Contains($"More than {Manifests.MaxManifests}", Assert.Single(inventory.Warnings), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_oversized_or_undecodable_manifest_is_a_warning()
    {
        var root = await _estate.CheckoutAsync("odd", new Dictionary<string, string>
        {
            ["big/package.json"] = "{}",
            ["bad/package.json"] = "{}",
            ["good/package.json"] = """{"name": "good"}""",
        });
        await File.WriteAllTextAsync(Path.Join(root, "big", "package.json"), new string(' ', (int)Manifests.MaxBytes + 1), Token);
        await File.WriteAllBytesAsync(Path.Join(root, "bad", "package.json"), [0x7b, 0xff, 0x7d], Token);

        var inventory = await Manifests.ReadAsync(_estate.Git, root, "owner/odd", EstateFixture.ListTimeout, Token);

        Assert.True(inventory.Scanned);
        Assert.Equal(["bad/package.json", "big/package.json"], inventory.Warnings.Select(w => w[..w.IndexOf(':', StringComparison.Ordinal)]));
        Assert.Contains("Manifest exceeds 2 MB", inventory.Warnings[1], StringComparison.Ordinal);
        Assert.Equal("good", Assert.Single(inventory.Publications).Package);
    }

    [Fact]
    public async Task A_manifest_encoded_as_utf16_is_rejected_as_a_warning()
    {
        var root = await _estate.CheckoutAsync("utf16", new Dictionary<string, string>
        {
            ["package.json"] = "{}",
        });
        var utf16Bytes = System.Text.Encoding.Unicode.GetPreamble().Concat(System.Text.Encoding.Unicode.GetBytes("""{"name": "utf16"}""")).ToArray();
        await File.WriteAllBytesAsync(Path.Join(root, "package.json"), utf16Bytes, Token);

        var inventory = await Manifests.ReadAsync(_estate.Git, root, "owner/utf16", EstateFixture.ListTimeout, Token);

        Assert.True(inventory.Scanned);
        Assert.Empty(inventory.Publications);
        Assert.Contains("package.json", Assert.Single(inventory.Warnings), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Duplicate_properties_in_manifest_are_recorded_as_a_warning()
    {
        var root = await _estate.CheckoutAsync("dup", new Dictionary<string, string>
        {
            ["package.json"] = "{}",
        });
        await File.WriteAllTextAsync(Path.Join(root, "package.json"), """{"name":"a","name":"b"}""", Token);

        var inventory = await Manifests.ReadAsync(_estate.Git, root, "owner/dup", EstateFixture.ListTimeout, Token);

        Assert.True(inventory.Scanned);
        Assert.Empty(inventory.Publications);
        Assert.Contains("package.json", Assert.Single(inventory.Warnings), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Generated_names_and_references_are_not_guessed()
    {
        var root = await _estate.CheckoutAsync("gen", new Dictionary<string, string>
        {
            ["gen.csproj"] = """
                <Project>
                  <PackageId>$(Generated)</PackageId>
                  <PackageReference Include="$(Name)" Version="1"/>
                  <PackageReference Update="$(Name)" Version="2"/>
                  <ProjectReference Include="$(Root)/x.csproj"/>
                  <ProjectReference Include=""/>
                </Project>
                """,
        });

        var inventory = await Manifests.ReadAsync(_estate.Git, root, "owner/gen", EstateFixture.ListTimeout, Token);

        Assert.Empty(inventory.Publications);
        Assert.Empty(inventory.Consumptions);
        Assert.Empty(inventory.ProjectRefs);
        Assert.Empty(inventory.Warnings);
    }

    [Fact]
    public async Task Malformed_python_and_npm_shapes_are_warnings()
    {
        var root = await _estate.CheckoutAsync("shapes", new Dictionary<string, string>
        {
            ["a/pyproject.toml"] = "project = 3\n",
            ["b/pyproject.toml"] = "[project]\nname = 3\n",
            ["c/pyproject.toml"] = "[project]\ndependencies = \"x\"\n",
            ["d/pyproject.toml"] = "[project]\ndependencies = [3]\n",
            ["e/pyproject.toml"] = "[project]\noptional-dependencies = 3\n",
            ["f/pyproject.toml"] = "[tool.other]\nx = 1\n",
            ["g/package.json"] = "[]",
            ["h/package.json"] = """{"dependencies": []}""",
            ["i/package.json"] = """{"dependencies": {"x": 1}}""",
        });

        var inventory = await Manifests.ReadAsync(_estate.Git, root, "owner/shapes", EstateFixture.ListTimeout, Token);

        Assert.Equal(
            ["a/pyproject.toml", "b/pyproject.toml", "c/pyproject.toml", "d/pyproject.toml", "e/pyproject.toml", "g/package.json", "h/package.json", "i/package.json"],
            inventory.Warnings.Select(w => w[..w.IndexOf(':', StringComparison.Ordinal)]));
        Assert.Empty(inventory.Publications);
    }

    [Fact]
    public async Task A_malformed_manifest_adds_no_evidence_at_all()
    {
        var root = await _estate.CheckoutAsync("partial", new Dictionary<string, string>
        {
            ["a/package.json"] = """{"name": "shared", "dependencies": {"left": "1"}, "devDependencies": []}""",
            ["b/pyproject.toml"] = "[project]\nname = \"shared-py\"\ndependencies = [\"left\"]\noptional-dependencies = 3\n",
        });

        var inventory = await Manifests.ReadAsync(_estate.Git, root, "owner/partial", EstateFixture.ListTimeout, Token);

        Assert.Equal(2, inventory.Warnings.Count);
        Assert.Empty(inventory.Publications);
        Assert.Empty(inventory.Consumptions);
    }

    [Fact]
    public async Task Paket_references_take_their_versions_from_the_nearest_paket_dependencies()
    {
        var root = await _estate.CheckoutAsync("paket", new Dictionary<string, string>
        {
            ["paket.dependencies"] = "source https://api.nuget.org/v3/index.json\nstorage: none\n\n// nuget Commented == 9\nnuget Managed.Lib == 1.0 // pinned\nnuget Ranged ~> 2.0\nnuget Bare\nnuget Equals = 3.0\n",
            ["src/app/paket.references"] = "Managed.Lib\nRanged copy_local: false\nBare\nEquals\nUnlisted\n// Commented\nFile: shared.fs\ngroup Build\n",
            ["nested/paket.dependencies"] = "nuget Managed.Lib == 2.0\n",
            ["nested/app/paket.references"] = "managed.lib\n",
        });

        var inventory = await Manifests.ReadAsync(_estate.Git, root, "owner/paket", EstateFixture.ListTimeout, Token);

        Assert.Empty(inventory.Warnings);
        Assert.Equal(
            [
                ("managed.lib", "1.0", "src/app/paket.references"),
                ("ranged", "~> 2.0", "src/app/paket.references"),
                ("bare", "unconstrained", "src/app/paket.references"),
                ("equals", "3.0", "src/app/paket.references"),
                ("unlisted", "centrally resolved", "src/app/paket.references"),
                ("managed.lib", "2.0", "nested/app/paket.references"),
            ],
            inventory.Consumptions.Select(c => (c.Package, c.Requirement, c.Evidence)).OrderBy(c => c.Evidence == "src/app/paket.references" ? 0 : 1));
        Assert.All(inventory.Consumptions, c => Assert.Equal("nuget", c.Registry));
    }

    [Fact]
    public async Task An_unreadable_paket_dependencies_is_a_warning()
    {
        var root = await _estate.CheckoutAsync("paketbig", new Dictionary<string, string> { ["paket.dependencies"] = new string(' ', (int)Manifests.MaxBytes + 1) });

        var inventory = await Manifests.ReadAsync(_estate.Git, root, "owner/paketbig", EstateFixture.ListTimeout, Token);

        Assert.StartsWith("paket.dependencies:", Assert.Single(inventory.Warnings), StringComparison.Ordinal);
    }
}
