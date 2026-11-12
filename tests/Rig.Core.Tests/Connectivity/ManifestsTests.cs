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

        var inventory = await Manifests.ReadAsync(_estate.Git, root, "owner/huge", Token);

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

        var inventory = await Manifests.ReadAsync(_estate.Git, root, "owner/odd", Token);

        Assert.True(inventory.Scanned);
        Assert.Equal(["bad/package.json", "big/package.json"], inventory.Warnings.Select(w => w[..w.IndexOf(':', StringComparison.Ordinal)]));
        Assert.Contains("Manifest exceeds 2 MB", inventory.Warnings[1], StringComparison.Ordinal);
        Assert.Equal("good", Assert.Single(inventory.Publications).Package);
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

        var inventory = await Manifests.ReadAsync(_estate.Git, root, "owner/gen", Token);

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

        var inventory = await Manifests.ReadAsync(_estate.Git, root, "owner/shapes", Token);

        Assert.Equal(
            ["a/pyproject.toml", "b/pyproject.toml", "c/pyproject.toml", "d/pyproject.toml", "e/pyproject.toml", "g/package.json", "h/package.json", "i/package.json"],
            inventory.Warnings.Select(w => w[..w.IndexOf(':', StringComparison.Ordinal)]));
        Assert.Empty(inventory.Publications);
    }
}
