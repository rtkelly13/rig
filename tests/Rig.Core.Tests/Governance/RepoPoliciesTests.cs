using System.Text.Json.Nodes;
using Rig.Core.Governance;

namespace Rig.Core.Tests.Governance;

/// <summary>Reading <c>config/repo_policies.json</c> and resolving a repo's languages and entries from its layers.</summary>
public sealed class RepoPoliciesTests : IDisposable
{
    private const string Policy = """
        {
          "defaults": {
            "gitignore_entries": [{"name": "temp", "comment": "Temp", "write": ["temp/"]}],
            "agents_md_hygiene": {"bespoke_agent_files": ["GEMINI.md"]}
          },
          "profiles": {
            "labs": {"repos": ["lake"], "languages": ["DotNet"], "agents_md_hygiene": {}},
            "bare": {"repos": ["plain"], "gitignore_entries": []}
          },
          "owner_profiles": {"labs-org": "labs"},
          "repo_overrides": {
            "rig": {"languages": ["py", "node"], "gitignore_entries": [{"write": ["x/"], "satisfied_by": ["y"], "match_mode": "all"}]}
          },
          "language_presets": {
            "python": {"name": "Python", "aliases": ["PY"], "detect_files": ["pyproject.toml", "*.pyproj"],
                       "gitignore_entries": [{"name": "pyc", "write": ["__pycache__/"]}, {"name": "temp", "write": ["ignored/"]}]},
            "dotnet": {"name": ".NET", "aliases": ["csharp"], "detect_files": ["*.sln", "*.csproj"],
                       "gitignore_entries": [{"name": "bin", "write": ["bin/"]}]},
            "node": {"detect_files": ["package.json"]}
          }
        }
        """;

    private readonly TempDir _dir = new();

    private static RepoPolicies Parsed => RepoPolicies.Parse(JsonNode.Parse(Policy), "policy.json");

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void A_missing_or_unparseable_file_requires_nothing()
    {
        var garbled = _dir.File("bad.json");
        File.WriteAllText(garbled, "{ not json");

        Assert.Same(RepoPolicies.Empty, RepoPolicies.Load(Path.Join(_dir.Path, "absent.json")));
        Assert.Same(RepoPolicies.Empty, RepoPolicies.Load(garbled));
    }

    [Theory]
    [InlineData("[]", "the document is not an object")]
    [InlineData("""{"profiles": []}""", "profiles is not an object")]
    [InlineData("""{"defaults": {"languages": "py"}}""", "languages is not a list of strings")]
    [InlineData("""{"defaults": {"gitignore_entries": {}}}""", "gitignore_entries is not a list")]
    [InlineData("""{"defaults": {"gitignore_entries": [{"write": ["a"], "match_mode": "some"}]}}""", "match_mode 'some' is neither 'any' nor 'all'")]
    [InlineData("""{"defaults": {"agents_md_hygiene": []}}""", "agents_md_hygiene is not an object")]
    [InlineData("""{"owner_profiles": {"b": "nope", "a": "gone"}}""", "owner_profiles names undeclared profile(s): a -> gone, b -> nope")]
    public void A_file_of_the_wrong_shape_is_refused(string json, string why)
    {
        var file = _dir.File("policy.json");
        File.WriteAllText(file, json);

        var error = Assert.Throws<InvalidDataException>(() => RepoPolicies.Load(file));

        Assert.Equal($"{file}: {why}", error.Message);
    }

    [Theory]
    [InlineData("py", "python")]
    [InlineData(" Python ", "python")]
    [InlineData("CSharp", "dotnet")]
    [InlineData(".net", "dotnet")]
    [InlineData("Go", "go")]
    public void A_language_normalizes_to_its_preset(string given, string id) => Assert.Equal(id, Parsed.NormalizeLanguage(given));

    [Fact]
    public void Languages_come_from_the_caller_then_the_policy_then_the_files()
    {
        var repo = _dir.Folder("repo");
        _ = _dir.File("repo/backend/Api.csproj");
        _ = _dir.File("repo/node_modules/x/pyproject.toml");
        _ = _dir.File("repo/package.json");

        Assert.Equal(["python", "dotnet"], Parsed.ResolveLanguages("o/rig", repo, ["py, csharp", "python", " "]));
        Assert.Equal(["python", "node"], Parsed.ResolveLanguages("o/rig", repo));
        Assert.Equal(["dotnet"], Parsed.ResolveLanguages("labs/LAKE", repo));
        Assert.Equal(["dotnet", "node"], Parsed.ResolveLanguages("o/other", repo));
        Assert.Empty(Parsed.ResolveLanguages("o/other", Path.Join(_dir.Path, "absent")));
    }

    [Fact]
    public void Entries_are_the_layer_s_own_then_each_language_s_new_ones()
    {
        var defaults = Parsed.GitignoreEntries("o/other", languages: ["python"]);
        var overridden = Assert.Single(Parsed.GitignoreEntries("o/rig", languages: ["node"]));

        Assert.Equal(["temp", "pyc"], defaults.Select(e => e.Name));
        Assert.Equal("x/", overridden.Name);
        Assert.Equal(["y"], overridden.SatisfiedBy);
        Assert.True(overridden.MatchAll);
        Assert.Empty(Parsed.GitignoreEntries("plain", languages: []));
    }

    [Fact]
    public void Bespoke_files_resolve_like_any_key_and_a_bare_hygiene_block_means_the_defaults()
    {
        Assert.Equal(["GEMINI.md"], Parsed.BespokeAgentFilesFor("o/rig"));
        Assert.Equal(BespokeAgentFiles.Defaults, Parsed.BespokeAgentFilesFor("lake"));
        Assert.Equal(BespokeAgentFiles.Defaults, RepoPolicies.Empty.BespokeAgentFilesFor("x"));
    }

    [Fact]
    public void Presets_keep_the_file_s_order_and_default_their_name()
    {
        Assert.Equal(["python", "dotnet", "node"], Parsed.Presets.Select(p => p.Id));
        Assert.Equal("node", Parsed.Presets[2].Name);
        Assert.Equal(["py"], Parsed.Presets[0].Aliases);
    }

    [Fact]
    public void The_checked_in_policy_parses()
    {
        var policies = RepoPolicies.Load(Path.Join(Path.GetDirectoryName(RepoTemp.Root), "config", RepoPolicies.FileName));

        Assert.Contains(policies.Presets, p => p.Id == "python");
        Assert.Contains(policies.GitignoreEntries("rtkelly13/rig"), e => e.Name == "temp");
    }
}
