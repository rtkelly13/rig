using System.Text.Json;
using Rig.Core.Workspace;

namespace Rig.Core.Tests.Workspace;

/// <summary>Port ranges and Vercel names in managed-projects.json. Replaces <c>test_workspace_ports</c> and <c>test_vercel_names</c>.</summary>
public sealed class UrlOwnersTests : IDisposable
{
    private static readonly string[] Site = ["site"];

    private readonly TempDir _temp = new();

    private static string Committed => Path.Join(Path.GetDirectoryName(RepoTemp.Root)!, "config", ManagedProjects.FileName);

    public void Dispose() => _temp.Dispose();

    private string Config(params object[] projects)
    {
        var path = Path.Join(_temp.Path, "managed-projects.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new { projects }));
        return path;
    }

    private static Dictionary<string, object> Project(string name, int start, int end, bool enabled = true) => new()
    {
        ["name"] = name,
        ["repo"] = $"git@github.com:rtkelly13/{name}.git",
        ["enabled"] = enabled,
        ["ports"] = new { start, end },
    };

    [Fact]
    public void Committed_claims_are_valid_and_rig_owns_its_triage_port()
    {
        var owners = UrlOwners.Load(Committed);
        Assert.Equal("rtkelly13/rig", owners.ClaimFor(8094)?.Slug);
        Assert.Equal("design-system", owners.ClaimForHost("design-system-storybook-diffra.vercel.app")?.Project);
        Assert.Equal("rtkelly13/rig", owners.SlugFor(new Uri("http://localhost:8094/triage")));
    }

    [Fact]
    public void Claims_map_ports_to_repos()
    {
        var claims = UrlOwners.LoadPortClaims(Config(Project("rig", 8090, 8099), Project("blog", 8100, 8109)));
        Assert.Equal(
            [new PortClaim("rig", "rtkelly13/rig", new(8090, 8099)), new PortClaim("blog", "rtkelly13/blog", new(8100, 8109))],
            claims);
        var owners = new UrlOwners(claims, []);
        Assert.Equal("blog", owners.ClaimFor(8105)?.Project);
        Assert.Null(owners.ClaimFor(8110));
    }

    [Fact]
    public void Overlapping_ranges_are_rejected_naming_both()
    {
        var error = Assert.Throws<InvalidDataException>(() => UrlOwners.LoadPortClaims(Config(Project("rig", 8090, 8099), Project("blog", 8095, 8105))));
        Assert.Equal("blog: ports 8095-8105 overlap rig's 8090-8099", error.Message);
    }

    [Theory]
    [InlineData(80, 90)]
    [InlineData(8099, 8090)]
    [InlineData(65530, 65540)]
    public void Out_of_bounds_or_reversed_ranges_are_rejected(int start, int end) =>
        Assert.Contains("must lie within", Assert.Throws<InvalidDataException>(() => UrlOwners.LoadPortClaims(Config(Project("rig", start, end)))).Message, StringComparison.Ordinal);

    [Fact]
    public void Disabled_projects_and_projects_without_ports_claim_nothing() =>
        Assert.Empty(UrlOwners.LoadPortClaims(Config(new { name = "docs", repo = "git@github.com:rtkelly13/docs.git" }, Project("old", 8090, 8099, enabled: false))));

    [Fact]
    public void Ports_must_belong_to_a_github_repo() =>
        Assert.Contains(
            "need a GitHub repo",
            Assert.Throws<InvalidDataException>(() => UrlOwners.LoadPortClaims(Config(new { name = "x", repo = "/srv/x.git", ports = new { start = 9000, end = 9009 } }))).Message,
            StringComparison.Ordinal);

    [Fact]
    public void A_name_declared_twice_is_rejected()
    {
        var path = Config(
            new { name = "a", repo = "git@github.com:o/a.git", vercel = Site },
            new { name = "b", repo = "git@github.com:o/b.git", vercel = Site });
        Assert.Equal("b: vercel project 'site' is already declared by a", Assert.Throws<InvalidDataException>(() => UrlOwners.LoadVercelClaims(path)).Message);
    }

    [Theory]
    [InlineData("Site")]
    [InlineData("-site")]
    [InlineData("site_x")]
    [InlineData("")]
    public void Names_must_be_vercel_names(string name) =>
        Assert.Contains(
            "not a Vercel project name",
            Assert.Throws<InvalidDataException>(() => UrlOwners.LoadVercelClaims(Config(new { name = "a", repo = "git@github.com:o/a.git", vercel = new[] { name } }))).Message,
            StringComparison.Ordinal);

    [Theory]
    [InlineData("""{"projects": [{"name": "a", "repo": "git@github.com:o/a.git", "ports": [1, 2]}]}""", "a: ports: expected an object")]
    [InlineData("""{"projects": [{"name": "a", "repo": "git@github.com:o/a.git", "ports": {"start": "1", "end": 2}}]}""", "a: ports.start: expected an integer")]
    [InlineData("""{"projects": [{"name": "a", "repo": "git@github.com:o/a.git", "enabled": "yes"}]}""", "a: enabled: expected a boolean")]
    [InlineData("""{"projects": [{"name": "a", "repo": "git@github.com:o/a.git", "ports": {"start": 1.5, "end": 2}}]}""", "a: ports.start: expected an integer")]
    [InlineData("""{"projects": [{"name": 1}]}""", "name: expected a string")]
    [InlineData("""{"projects": [{"name": {}}]}""", "name: expected a string")]
    [InlineData("""{"projects": [{"name": "a", "repo": "git@github.com:o/a.git", "ports": {"start": {}, "end": 2}}]}""", "a: ports.start: expected an integer")]
    [InlineData("""{"projects": {}}""", "projects: expected a list of objects")]
    [InlineData("""[]""", "expected a JSON object")]
    [InlineData("""{""", "")]
    public void Malformed_port_declarations_are_rejected(string json, string says)
    {
        var path = Path.Join(_temp.Path, "managed-projects.json");
        File.WriteAllText(path, json);
        Assert.Contains(says, Assert.Throws<InvalidDataException>(() => UrlOwners.LoadPortClaims(path)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Vercel_names_must_be_a_list_of_strings()
    {
        var path = Config(new { name = "a", repo = "git@github.com:o/a.git", vercel = new object[] { 1 } });
        Assert.Equal("a: vercel: expected a list of strings", Assert.Throws<InvalidDataException>(() => UrlOwners.LoadVercelClaims(path)).Message);
        path = Config(new { name = "a", repo = "git@github.com:o/a.git", vercel = "site" });
        Assert.Equal("a: vercel: expected a list of strings", Assert.Throws<InvalidDataException>(() => UrlOwners.LoadVercelClaims(path)).Message);
        Assert.Empty(UrlOwners.LoadVercelClaims(Config(new { name = "a", repo = "git@github.com:o/a.git", vercel = Array.Empty<string>() })));
    }

    [Fact]
    public void A_file_without_projects_claims_nothing()
    {
        var path = Path.Join(_temp.Path, "managed-projects.json");
        File.WriteAllText(path, "{}");
        var owners = UrlOwners.Load(path);
        Assert.Empty(owners.Ports);
        Assert.Empty(owners.Vercel);
    }

    [Fact]
    public void Urls_without_an_owner_have_no_slug()
    {
        var owners = UrlOwners.None;
        Assert.Null(owners.SlugFor(new Uri("/x", UriKind.Relative)));
        Assert.Null(owners.SlugFor(new Uri("https://example.com/")));
        Assert.Null(owners.ClaimForHost("example.vercel.app"));
        Assert.Equal("8090-8099", new PortRange(8090, 8099).ToString());
    }
}
