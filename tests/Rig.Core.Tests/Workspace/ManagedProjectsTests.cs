using Rig.Core.Workspace;

namespace Rig.Core.Tests.Workspace;

/// <summary>
/// <see cref="ManagedProjects"/> and <see cref="MachineConfig"/> over temp files. Ported from
/// <c>tests/test_workspace_cli.py</c>'s loading cases.
/// </summary>
public sealed class ManagedProjectsTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private string Write(string name, string json)
    {
        var path = _dir.File(name);
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void Every_field_is_read_and_retired_projects_follow_disabled()
    {
        var path = Write(ManagedProjects.FileName, """
            {"projects": [
              {"name": "rig", "repo": "git@github.com:o/rig.git", "description": "d", "domain": "dm", "stack": "s",
               "branch": "trunk", "orca": {"group": "tools"}},
              {"name": "off", "repo": "r", "enabled": false}
            ]}
            """);
        _ = Write(ManagedProjects.ArchivedFileName, """{"projects": [{"name": "old", "repo": "r2", "enabled": true}]}""");

        var specs = ManagedProjects.Load(path);

        Assert.Equal(
            [
                new ManagedRepoSpec("rig", "git@github.com:o/rig.git", "d", "dm", "s", "trunk", true, "tools"),
                new ManagedRepoSpec("off", "r", Enabled: false),
                new ManagedRepoSpec("old", "r2", Enabled: false),
            ],
            specs);
    }

    [Fact]
    public void A_missing_registry_has_no_projects_even_beside_an_archive()
    {
        _ = Write(ManagedProjects.ArchivedFileName, """{"projects": [{"name": "old", "repo": "r"}]}""");

        Assert.Empty(ManagedProjects.Load(Path.Join(_dir.Path, ManagedProjects.FileName)));
    }

    [Theory]
    [InlineData("{not json")]
    [InlineData("[]")]
    [InlineData("""{"projects": {}}""")]
    public void A_malformed_registry_has_no_projects(string json) =>
        Assert.Empty(ManagedProjects.Load(Write(ManagedProjects.FileName, json)));

    [Theory]
    [InlineData("""{"name": 1, "repo": "r"}""")]
    [InlineData("""{"name": "a", "repo": null}""")]
    [InlineData("""{"name": "a", "repo": "r", "enabled": "yes"}""")]
    [InlineData("""{"name": "a", "repo": "r", "branch": 3}""")]
    [InlineData("""{"name": "a", "repo": "r", "orca": "tools"}""")]
    [InlineData("""{"name": "a", "repo": "r", "orca": {"group": 1}}""")]
    [InlineData("7")]
    public void An_entry_with_a_wrong_type_is_skipped_and_the_rest_kept(string entry)
    {
        var specs = ManagedProjects.Load(Write(ManagedProjects.FileName, $$"""{"projects": [{{entry}}, {"name": "ok", "repo": "r", "branch": null, "orca": null}]}"""));

        Assert.Equal([new ManagedRepoSpec("ok", "r")], specs);
    }

    [Fact]
    public void Skip_repos_are_trimmed_and_blanks_dropped()
    {
        var path = Write("config.json", """{"tools": {"estate": {"skip_repos": [" a ", "", "b"]}}}""");

        Assert.Equal(["a", "b"], MachineConfig.SkipRepos(path).Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("{not json")]
    [InlineData("[]")]
    [InlineData("""{"tools": {"estate": {"skip_repos": "a"}}}""")]
    [InlineData("""{"tools": {"estate": {"skip_repos": ["a", 1]}}}""")]
    public void A_malformed_machine_config_skips_nothing(string json) =>
        Assert.Empty(MachineConfig.SkipRepos(Write("config.json", json)));

    [Fact]
    public void A_missing_machine_config_skips_nothing() =>
        Assert.Empty(MachineConfig.SkipRepos(Path.Join(_dir.Path, "none.json")));
}
