using System.Text.Json;
using Rig.Core.Roots;

namespace Rig.Core.Tests.Roots;

/// <summary><see cref="EstateScopes"/> over temp files. Ported from <c>tests/test_scopes.py</c>'s loading cases.</summary>
public sealed class EstateScopesTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private string Write(string json)
    {
        var path = _dir.File(EstateScopes.FileName);
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void The_owners_and_orgs_are_read_and_unknown_keys_ignored()
    {
        var scopes = EstateScopes.Load(Write("""{"personal": {"owners": ["rtkelly13"], "orgs": ["Resultful", "rtkelly-labs"], "x": 1}, "work": {}}"""));

        Assert.Equal(["rtkelly13"], scopes.Owners);
        Assert.Equal(["Resultful", "rtkelly-labs"], scopes.Orgs);
    }

    [Fact]
    public void A_missing_file_or_section_has_no_scopes()
    {
        Assert.Empty(EstateScopes.Load(Path.Join(_dir.Path, "none", EstateScopes.FileName)).Orgs);
        Assert.Empty(EstateScopes.Load(Write("{}")).Owners);
    }

    [Theory]
    [InlineData("{not json")]
    [InlineData("null")]
    [InlineData("""{"personal": {"orgs": [null]}}""")]
    public void A_malformed_file_throws(string json)
    {
        var path = Write(json);

        _ = Assert.ThrowsAny<JsonException>(() => EstateScopes.Load(path));
    }

    [Fact]
    public void Org_folders_sit_beside_the_personal_folder()
    {
        var scopes = new EstateScopes([], ["Resultful", "rtkelly-labs"]);
        var personal = Path.Join(_dir.Path, "code", "personal");

        Assert.Equal(
            [personal, Path.Join(_dir.Path, "code", "Resultful"), Path.Join(_dir.Path, "code", "rtkelly-labs")],
            scopes.RepoParentDirs(personal));
        Assert.Equal(scopes.OrgDirs(personal), scopes.OrgDirs(personal + Path.DirectorySeparatorChar));
    }
    [Fact]
    public void An_org_owned_project_sits_in_its_org_folder_and_the_rest_in_the_estate()
    {
        var scopes = new EstateScopes(["rtkelly13"], ["rtkelly-labs"]);
        var personal = Path.Join(_dir.Path, "code", "personal");

        Assert.Equal("rtkelly-labs", scopes.OrgFolder("RTKelly-Labs"));
        Assert.Null(scopes.OrgFolder("rtkelly13"));
        Assert.Equal("rtkelly-labs", scopes.OrgFolderOf("git@github.com:RTKELLY-LABS/lake.git"));
        Assert.Null(scopes.OrgFolderOf("local/scratch"));
        Assert.Equal("../rtkelly-labs/lake", scopes.ProjectRelPath("lake", "https://github.com/rtkelly-labs/lake"));
        Assert.Equal("rig", scopes.ProjectRelPath("rig", "git@github.com:rtkelly13/rig.git"));
        Assert.Equal(Path.Join(_dir.Path, "code", "rtkelly-labs", "lake"), scopes.ProjectDir(personal + Path.DirectorySeparatorChar, "lake", "git@github.com:rtkelly-labs/lake.git"));
        Assert.Equal(Path.Join(personal, "rig"), scopes.ProjectDir(personal, "rig", "git@github.com:rtkelly13/rig.git"));
    }
}
