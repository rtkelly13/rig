using Rig.Core.GitHub;

namespace Rig.Core.Tests.GitHub;

/// <summary>Ported case for case from tests/test_repo_slug.py.</summary>
public sealed class RepoSlugTests
{
    [Theory]
    [InlineData("https://github.com/rtkelly13/rig", "rtkelly13", "rig")]
    [InlineData("https://github.com/rtkelly13/rig.git", "rtkelly13", "rig")]
    [InlineData("https://github.com/rtkelly13/rig.git/", "rtkelly13", "rig")]
    [InlineData("https://github.com/Owner/Repo/", "Owner", "Repo")]
    [InlineData("https://user:token@github.com/o/r.git", "o", "r")]
    [InlineData("http://www.github.com/o/r", "o", "r")]
    [InlineData("HTTPS://GitHub.com/o/r", "o", "r")]
    [InlineData("git+https://github.com/Owner/Repo.git", "Owner", "Repo")]
    [InlineData("ssh://git@github.com/rtkelly13/rig.git", "rtkelly13", "rig")]
    [InlineData("ssh://git@ssh.github.com:443/o/r.git", "o", "r")]
    [InlineData("git@github.com:rtkelly13/shared-utilities.git", "rtkelly13", "shared-utilities")]
    [InlineData("git@github.com:ryan/shared-utilities.git\n", "ryan", "shared-utilities")]
    [InlineData("github.com:o/r", "o", "r")]
    [InlineData("github.com/rtkelly-labs/Iceberg.NET", "rtkelly-labs", "Iceberg.NET")]
    [InlineData("  https://github.com/o/my_repo.v2  ", "o", "my_repo.v2")]
    [InlineData("https://github.com/o/r?tab=readme#top", "o", "r")]
    public void GitHub_remote_forms_parse(string remote, string owner, string name) =>
        Assert.Equal(new RepoSlug(owner, name), RepoSlug.FromRemote(remote));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("local/my-repo")]
    [InlineData("git@gitlab.com:rtkelly13/rig.git")]
    [InlineData("https://gitlab.com/o/r")]
    [InlineData("https://github.example.com/o/r.git")]
    [InlineData("git@ghe.corp.internal:o/r.git")]
    [InlineData("https://notgithub.com/o/r")]
    [InlineData("https://github.com/o")]
    [InlineData("https://github.com/o/r/tree/main")]
    [InlineData("git@github.com:o/r/extra.git")]
    [InlineData("/Users/me/code/personal/rig")]
    [InlineData(@"C:\code\personal\rig")]
    public void Anything_else_is_null(string? remote) => Assert.Null(RepoSlug.FromRemote(remote));

    [Fact]
    public void Full_name_keeps_case_and_key_folds_it()
    {
        var slug = new RepoSlug("rtkelly-labs", "Iceberg.NET");

        Assert.Equal("rtkelly-labs/Iceberg.NET", slug.FullName);
        Assert.Equal("rtkelly-labs/Iceberg.NET", slug.ToString());
        Assert.Equal("rtkelly-labs/iceberg.net", slug.Key);
    }

    [Theory]
    [InlineData("octo/repo", true)]
    [InlineData("/octo/repo.git/", true)]
    [InlineData("not-a-slug", false)]
    [InlineData("o/r/x", false)]
    [InlineData("o/r p", false)]
    public void A_path_is_owner_and_name(string path, bool valid) => Assert.Equal(valid, RepoSlug.FromPath(path) is not null);
}
