using Rig.Core.Processes;
using Rig.Core.Skills;
using Rig.Core.Tests.Fakes;

namespace Rig.Core.Tests.Skills;

/// <summary>Vendoring upstream skills; ported from <c>tests/test_skills_symlinks.py</c>. Git is always a fake: nothing is cloned.</summary>
public sealed class SkillVendorsTests : IDisposable
{
    private readonly TempDir _root = new TempDir().AsCodeRoot();
    private readonly FakeProc _proc = new();
    private readonly List<string> _said = [];

    public void Dispose() => _root.Dispose();

    private string Clones => Path.Join(_root.Path, "external", "skills");

    private string Vendored => Path.Join(_root.Path, "vendored");

    private SkillVendors Vendors() => new(new Git(_proc, _root.Path), Clones, Vendored, windows: false);

    private static SkillVendor Vendor(string name = "repo-x", IReadOnlyList<string>? include = null, IReadOnlyList<string>? exclude = null, IReadOnlyList<string>? allowlist = null) =>
        new(name, "https://example.com/x.git", string.Empty, "main", "skills", allowlist, include ?? ["*"], exclude ?? []);

    private string Repo(params string[] skills)
    {
        var repo = _root.Folder("mock-repo");
        foreach (var skill in skills)
        {
            SkillFixtures.Write(repo, $"skills/{skill}", skill);
        }

        return repo;
    }

    [Fact]
    public void Include_exclude_and_allowlist_filter_skills()
    {
        Assert.True(Vendor().IsIncluded("any-skill", false));
        Assert.True(Vendor(include: []).IsIncluded("any-skill", false));

        var pick = Vendor(include: ["subagent-*", "systematic-debugging"]);
        Assert.True(pick.IsIncluded("subagent-driven-development", false));
        Assert.True(pick.IsIncluded("systematic-debugging", false));
        Assert.False(pick.IsIncluded("brainstorming", false));

        var excluded = Vendor(exclude: ["legacy-*", "bad-skill"]);
        Assert.True(excluded.IsIncluded("good-skill", false));
        Assert.False(excluded.IsIncluded("legacy-old", false));
        Assert.False(excluded.IsIncluded("bad-skill", false));

        var allow = Vendor(allowlist: ["tdd"]);
        Assert.True(allow.IsIncluded("tdd", false));
        Assert.False(allow.IsIncluded("any-other-skill", false));
    }

    [Fact]
    public void Extract_copies_allowed_skills_and_purges_the_rest()
    {
        var repo = Repo("skill-a", "skill-b", "skill-c");
        File.WriteAllText(Path.Join(repo, "skills", "skill-a", "helper.py"), "print('hello')");
        File.WriteAllText(Path.Join(repo, "skills", "skill-a", ".env"), "secret");
        _root.Folder("mock-repo/skills/skill-a/node_modules/pkg");

        Assert.Equal(["skill-a", "skill-b"], Vendors().Extract(Vendor(include: ["skill-a", "skill-b"]), repo));
        var a = Path.Join(Vendored, "repo-x", "skill-a");
        Assert.True(File.Exists(Path.Join(a, "helper.py")));
        Assert.False(File.Exists(Path.Join(a, ".env")));
        Assert.False(Directory.Exists(Path.Join(a, "node_modules")));
        Assert.False(Directory.Exists(Path.Join(Vendored, "repo-x", "skill-c")));

        Assert.Equal(["skill-b"], Vendors().Extract(Vendor(include: ["skill-b"]), repo));
        Assert.False(Directory.Exists(a));
        Assert.True(Directory.Exists(Path.Join(Vendored, "repo-x", "skill-b")));
    }

    [Fact]
    public void Extract_searches_the_whole_repository_without_its_skills_folder()
    {
        var repo = _root.Folder("flat");
        SkillFixtures.Write(repo, "plugins/visual", "visual");

        Assert.Equal(["visual"], Vendors().Extract(Vendor(), repo));
    }

    [Fact]
    public async Task A_new_vendor_is_cloned_shallow_and_hardened()
    {
        _proc.Answer(new Ran(0, string.Empty, string.Empty), "git");

        var repo = await Vendors().CloneOrUpdateAsync(Vendor(), _said.Add, TestContext.Current.CancellationToken);

        Assert.Equal(Path.Join(Clones, "repo-x"), repo);
        var request = Assert.Single(_proc.Requests);
        Assert.Equal(["git", .. Git.HardeningArgs, "clone", "--depth", "1", "--branch", "main", "https://example.com/x.git", repo], request.Argv);
        Assert.Equal(Clones, request.WorkingDirectory);
        Assert.Equal(SkillVendors.NetworkTimeout, request.Timeout);
        Assert.Equal(["Cloning repo-x from https://example.com/x.git...", "✓ Cloned repo-x"], _said);
    }

    [Fact]
    public async Task A_failed_clone_throws()
    {
        _proc.Answer(new Ran(128, string.Empty, " fatal: no \n"), "git");

        var error = await Assert.ThrowsAsync<IOException>(() => Vendors().CloneOrUpdateAsync(Vendor(), _said.Add, TestContext.Current.CancellationToken));

        Assert.Equal("Git clone failed for repo-x: fatal: no", error.Message);
    }

    [Fact]
    public async Task An_existing_clone_fast_forwards()
    {
        _root.Folder("external/skills/repo-x/.git");
        _proc.Answer(new Ran(0, string.Empty, string.Empty), "git");

        await Vendors().CloneOrUpdateAsync(Vendor(), _said.Add, TestContext.Current.CancellationToken);

        Assert.Equal(["pull", "--ff-only"], Assert.Single(_proc.Requests).Argv.TakeLast(2));
        Assert.Equal(["Updating repo-x from https://example.com/x.git...", "✓ Updated repo-x"], _said);
    }

    [Fact]
    public async Task A_clone_that_cannot_fast_forward_is_reset_to_the_branch()
    {
        _root.Folder("external/skills/repo-x/.git");
        _proc.Answer(new Ran(0, string.Empty, string.Empty), "git").Answer(new Ran(1, string.Empty, "diverged"), ["git", .. Git.HardeningArgs, "pull"]);

        await Vendors().CloneOrUpdateAsync(Vendor(), _said.Add, TestContext.Current.CancellationToken);

        Assert.Equal(
            [["pull", "--ff-only"], ["fetch", "origin", "main"], ["reset", "--hard", "origin/main"]],
            _proc.Requests.Select(r => r.Argv.Skip(1 + Git.HardeningArgs.Count).ToArray()));
        Assert.Contains("Fast-forward failed, fetching origin/main...", _said);
    }

    [Fact]
    public async Task Update_clones_then_extracts_each_vendor()
    {
        _root.Folder("external/skills/repo-x/.git");
        SkillFixtures.Write(Path.Join(Clones, "repo-x"), "skills/tdd", "tdd");
        _proc.Answer(new Ran(0, string.Empty, string.Empty), "git");

        var results = await Vendors().UpdateAsync([Vendor()], _said.Add, TestContext.Current.CancellationToken);

        var (vendor, skills) = Assert.Single(results);
        Assert.Equal("repo-x", vendor);
        Assert.Equal(["tdd"], skills);
        Assert.Equal("✓ Extracted 1 skills from repo-x", _said[^1]);
    }

    [Fact]
    public void A_missing_or_empty_registry_gives_the_defaults()
    {
        Assert.Same(SkillVendors.Defaults, SkillVendors.Load(Path.Join(_root.Path, "none.json")));
        var path = _root.File("upstream.json");
        File.WriteAllText(path, "[]");
        Assert.Same(SkillVendors.Defaults, SkillVendors.Load(path));
        Assert.Equal("skills", SkillVendors.Defaults[0].CloneFolder);
    }

    [Fact]
    public void A_registry_loads_with_defaults_for_missing_fields()
    {
        var path = _root.File("upstream.json");
        File.WriteAllText(path, """
            [
              {"name": "test1", "url": "https://example.com/repo1.git", "folder_name": "repo1", "branch": "dev",
               "skills_subdir": "s", "include": ["skill-a", "skill-b"], "exclude": ["skill-c"], "allowlist": ["x"]},
              {"name": "test2", "url": "git@example.com:r.git", "allowlist": null}
            ]
            """);

        var vendors = SkillVendors.Load(path);

        Assert.Equal(new SkillVendor("test1", "https://example.com/repo1.git", "repo1", "dev", "s", vendors[0].Allowlist, vendors[0].Include, vendors[0].Exclude), vendors[0]);
        Assert.Equal(["x"], vendors[0].Allowlist);
        Assert.Equal(["skill-a", "skill-b"], vendors[0].Include);
        Assert.Equal(["skill-c"], vendors[0].Exclude);
        Assert.Equal("repo1", vendors[0].CloneFolder);
        Assert.Null(vendors[1].Allowlist);
        Assert.Equal(["*"], vendors[1].Include);
        Assert.Empty(vendors[1].Exclude);
        Assert.Equal(("main", "skills", "test2"), (vendors[1].Branch, vendors[1].SkillsSubdir, vendors[1].CloneFolder));
    }

    [Theory]
    [InlineData("""[{"name": "bad-vendor"}]""", "url is required")]
    [InlineData("""[{"name": "", "url": "u"}]""", "name must not be empty")]
    [InlineData("""[{"name": 1, "url": "u"}]""", "name must be a string")]
    [InlineData("""[{"name": "n", "url": "u", "include": [1]}]""", "include must be a list of strings")]
    [InlineData("""[{"name": "n", "url": "u", "allowlist": "x"}]""", "allowlist must be a list of strings")]
    [InlineData("""["n"]""", "a vendor must be an object")]
    [InlineData("""{}""", "the registry must be a list")]
    [InlineData("""not json""", "Schema validation error")]
    public void An_invalid_registry_is_rejected(string json, string problem)
    {
        var path = _root.File("upstream.json");
        File.WriteAllText(path, json);

        var error = Assert.Throws<InvalidDataException>(() => SkillVendors.Load(path));

        Assert.StartsWith("Schema validation error in ", error.Message, StringComparison.Ordinal);
        Assert.Contains(problem, error.Message, StringComparison.Ordinal);
    }
}
