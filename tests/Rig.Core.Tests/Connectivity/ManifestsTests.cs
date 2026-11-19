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

        var inventory = await Manifests.ReadAsync(_estate.Git, _estate.Paket, root, "owner/huge", EstateFixture.ListTimeout, Token);

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

        var inventory = await Manifests.ReadAsync(_estate.Git, _estate.Paket, root, "owner/odd", EstateFixture.ListTimeout, Token);

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

        var inventory = await Manifests.ReadAsync(_estate.Git, _estate.Paket, root, "owner/utf16", EstateFixture.ListTimeout, Token);

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

        var inventory = await Manifests.ReadAsync(_estate.Git, _estate.Paket, root, "owner/dup", EstateFixture.ListTimeout, Token);

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

        var inventory = await Manifests.ReadAsync(_estate.Git, _estate.Paket, root, "owner/gen", EstateFixture.ListTimeout, Token);

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

        var inventory = await Manifests.ReadAsync(_estate.Git, _estate.Paket, root, "owner/shapes", EstateFixture.ListTimeout, Token);

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

        var inventory = await Manifests.ReadAsync(_estate.Git, _estate.Paket, root, "owner/partial", EstateFixture.ListTimeout, Token);

        Assert.Equal(2, inventory.Warnings.Count);
        Assert.Empty(inventory.Publications);
        Assert.Empty(inventory.Consumptions);
    }

    [Fact]
    public async Task Paket_references_take_their_versions_from_the_nearest_paket_dependencies()
    {
        var root = await _estate.CheckoutAsync("paket", new Dictionary<string, string>
        {
            ["paket.dependencies"] = string.Empty,
            ["src/app/paket.references"] = string.Empty,
            ["nested/paket.dependencies"] = string.Empty,
            ["nested/app/paket.references"] = string.Empty,
        });
        _estate.Paket
            .Dependencies(Path.Join(root, "paket.dependencies"), ("Managed.Lib", "1.0"), ("Ranged", ">= 2.0 < 3.0"), ("Bare", ""), ("Equals", "== 3.0"))
            .References(Path.Join(root, "src/app/paket.references"), "Managed.Lib", "Ranged", "Bare", "Equals", "Unlisted")
            .Dependencies(Path.Join(root, "nested/paket.dependencies"), ("Managed.Lib", "2.0"))
            .References(Path.Join(root, "nested/app/paket.references"), "managed.lib");

        var inventory = await Manifests.ReadAsync(_estate.Git, _estate.Paket, root, "owner/paket", EstateFixture.ListTimeout, Token);

        Assert.Empty(inventory.Warnings);
        Assert.Equal(
            [
                ("managed.lib", "1.0", "src/app/paket.references"),
                ("ranged", ">= 2.0 < 3.0", "src/app/paket.references"),
                ("bare", "unconstrained", "src/app/paket.references"),
                ("equals", "3.0", "src/app/paket.references"),
                ("unlisted", "centrally resolved", "src/app/paket.references"),
                ("managed.lib", "2.0", "nested/app/paket.references"),
            ],
            inventory.Consumptions.Select(c => (c.Package, c.Requirement, c.Evidence)).OrderBy(c => c.Evidence == "src/app/paket.references" ? 0 : 1));
        Assert.All(inventory.Consumptions, c => Assert.Equal("nuget", c.Registry));
    }

    [Fact]
    public async Task Every_paket_file_of_a_repo_is_read_in_one_call()
    {
        var root = await _estate.CheckoutAsync("paketonce", new Dictionary<string, string>
        {
            ["paket.dependencies"] = string.Empty,
            ["a/paket.references"] = string.Empty,
            ["b/paket.references"] = string.Empty,
        });

        _ = await Manifests.ReadAsync(_estate.Git, _estate.Paket, root, "owner/paketonce", EstateFixture.ListTimeout, Token);

        Assert.Equal(
            ((string[])["a/paket.references", "b/paket.references", "paket.dependencies"]).Select(file => Path.GetFullPath(Path.Join(root, file))).Order(StringComparer.Ordinal),
            Assert.Single(_estate.Paket.Batches).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_repo_without_paket_files_never_starts_the_reader()
    {
        var root = await _estate.CheckoutAsync("nopaket", new Dictionary<string, string> { ["package.json"] = "{}" });

        _ = await Manifests.ReadAsync(_estate.Git, _estate.Paket, root, "owner/nopaket", EstateFixture.ListTimeout, Token);

        Assert.Empty(_estate.Paket.Batches);
    }

    [Fact]
    public async Task Paket_references_use_the_group_of_the_dependencies_they_name()
    {
        var root = await _estate.CheckoutAsync("paketgroups", new Dictionary<string, string> { ["paket.dependencies"] = string.Empty, ["app/paket.references"] = string.Empty });
        _estate.Paket
            .Add(new PaketFile(
                Path.Join(root, "paket.dependencies"),
                [new PaketGroup("Main", [new PaketPackage("Shared", "1.0")]), new PaketGroup("Test", [new PaketPackage("Shared", "9.0")])],
                null,
                null))
            .Add(new PaketFile(
                Path.Join(root, "app/paket.references"),
                [new PaketGroup("Main", [new PaketPackage("Shared", null)]), new PaketGroup("Test", [new PaketPackage("Shared", null)])],
                null,
                null));

        var inventory = await Manifests.ReadAsync(_estate.Git, _estate.Paket, root, "owner/paketgroups", EstateFixture.ListTimeout, Token);

        Assert.Equal(["1.0", "9.0"], inventory.Consumptions.Select(c => c.Requirement));
    }

    [Fact]
    public async Task An_unreadable_paket_file_is_a_warning_not_a_failure()
    {
        var root = await _estate.CheckoutAsync("paketbad", new Dictionary<string, string> { ["paket.dependencies"] = string.Empty, ["app/paket.references"] = string.Empty, ["package.json"] = "{\"dependencies\":{\"left-pad\":\"1.0.0\"}}" });
        _estate.Paket
            .Add(new PaketFile(Path.Join(root, "paket.dependencies"), [], "Unrecognized token: garbage", null))
            .References(Path.Join(root, "app/paket.references"), "Alpha");

        var inventory = await Manifests.ReadAsync(_estate.Git, _estate.Paket, root, "owner/paketbad", EstateFixture.ListTimeout, Token);

        Assert.Equal("paket.dependencies: Unrecognized token: garbage", Assert.Single(inventory.Warnings));
        Assert.Equal(["alpha", "left-pad"], inventory.Consumptions.Select(c => c.Package).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_paket_lock_that_was_not_used_is_a_warning()
    {
        var root = await _estate.CheckoutAsync("paketlock", new Dictionary<string, string> { ["paket.dependencies"] = string.Empty });
        _estate.Paket.Add(new PaketFile(Path.Join(root, "paket.dependencies"), [], null, "paket.lock was not used: truncated"));

        var inventory = await Manifests.ReadAsync(_estate.Git, _estate.Paket, root, "owner/paketlock", EstateFixture.ListTimeout, Token);

        Assert.Equal("paket.dependencies: paket.lock was not used: truncated", Assert.Single(inventory.Warnings));
    }

    [Fact]
    public async Task A_paket_file_the_reader_does_not_answer_for_is_a_warning()
    {
        var root = await _estate.CheckoutAsync("paketsilent", new Dictionary<string, string> { ["app/paket.references"] = string.Empty });
        _estate.Paket.Answers = false;

        var inventory = await Manifests.ReadAsync(_estate.Git, _estate.Paket, root, "owner/paketsilent", EstateFixture.ListTimeout, Token);

        Assert.Equal("app/paket.references: the Paket sidecar did not answer for this file", Assert.Single(inventory.Warnings));
        Assert.Empty(inventory.Consumptions);
    }

    [Fact]
    public async Task Paket_group_names_match_whatever_their_case()
    {
        var root = await _estate.CheckoutAsync("paketcase", new Dictionary<string, string> { ["paket.dependencies"] = string.Empty, ["app/paket.references"] = string.Empty });
        _estate.Paket
            .Add(new PaketFile(Path.Join(root, "paket.dependencies"), [new PaketGroup("Test", [new PaketPackage("Shared", "9.0")])], null, null))
            .Add(new PaketFile(Path.Join(root, "app/paket.references"), [new PaketGroup("test", [new PaketPackage("Shared", null)])], null, null));

        var inventory = await Manifests.ReadAsync(_estate.Git, _estate.Paket, root, "owner/paketcase", EstateFixture.ListTimeout, Token);

        Assert.Equal("9.0", Assert.Single(inventory.Consumptions).Requirement);
    }

    [Fact]
    public async Task A_missing_paket_reader_skips_the_paket_files_with_one_warning()
    {
        var root = await _estate.CheckoutAsync("paketmissing", new Dictionary<string, string>
        {
            ["paket.dependencies"] = string.Empty,
            ["app/paket.references"] = string.Empty,
            ["package.json"] = "{\"dependencies\":{\"left-pad\":\"1.0.0\"}}",
        });
        _estate.Paket.Unavailable = "the Paket sidecar is not installed at /nowhere/Rig.Paket";

        var inventory = await Manifests.ReadAsync(_estate.Git, _estate.Paket, root, "owner/paketmissing", EstateFixture.ListTimeout, Token);

        Assert.Equal("Paket manifests skipped: the Paket sidecar is not installed at /nowhere/Rig.Paket", Assert.Single(inventory.Warnings));
        Assert.Equal(["left-pad"], inventory.Consumptions.Select(c => c.Package));
        Assert.True(inventory.Scanned);
    }
}
