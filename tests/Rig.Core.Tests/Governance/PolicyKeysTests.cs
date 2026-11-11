using Rig.Core.Governance;
using Rig.Core.Processes;
using Rig.Core.Roots;

namespace Rig.Core.Tests.Governance;

/// <summary>The key a local checkout's governance policy is looked up by.</summary>
public sealed class PolicyKeysTests : IDisposable
{
    private static readonly EstateScopes Scopes = new(["rtkelly13"], ["rtkelly-labs"]);

    private readonly TempDir _dir = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _dir.Dispose();

    [Theory]
    [InlineData("git@github.com:rtkelly-labs/oxbow.git")]
    [InlineData("https://github.com/rtkelly-labs/oxbow.git")]
    [InlineData("https://github.com/rtkelly-labs/oxbow")]
    [InlineData("ssh://git@github.com/rtkelly-labs/oxbow.git")]
    public void The_key_is_the_remote_slug(string remote) => Assert.Equal("rtkelly-labs/oxbow", PolicyKeys.From(Scopes, "/code/rtkelly-labs/oxbow", remote));

    [Fact]
    public void The_key_takes_the_remote_name_not_a_renamed_folder() =>
        Assert.Equal("rtkelly13/new-name", PolicyKeys.From(Scopes, "/code/personal/old-name", "git@github.com:rtkelly13/new-name.git"));

    [Fact]
    public void Without_a_github_remote_an_org_folder_supplies_the_owner()
    {
        Assert.Equal("rtkelly-labs/Lake.NET", PolicyKeys.From(Scopes, "/code/rtkelly-labs/Lake.NET", null));
        Assert.Equal("rtkelly-labs/tool", PolicyKeys.From(Scopes, "/code/RTKELLY-LABS/tool/", "https://gitlab.com/a/b"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Otherwise_the_key_is_the_bare_folder_name(string? origin) => Assert.Equal("blog", PolicyKeys.From(Scopes, "/code/personal/blog", origin));

    [Fact]
    public async Task The_origin_is_read_from_a_checkout_and_its_worktree()
    {
        var repo = _dir.Folder("personal/folder");
        var worktree = Path.Join(_dir.Path, "wt");
        var git = new Git(new SystemProc(), _dir.Path);
        string[][] steps =
        [
            ["init", "-q", "-b", "main"],
            ["-c", "user.name=t", "-c", "user.email=t@example.com", "commit", "-q", "--allow-empty", "-m", "init"],
            ["remote", "add", "origin", "git@github.com:rtkelly-labs/oxbow.git"],
            ["worktree", "add", "-q", worktree, "-b", "side"],
        ];
        foreach (var args in steps)
        {
            Assert.True((await git.RunAsync(repo, args, cancellationToken: Token)).Ok);
        }

        Assert.Equal("rtkelly-labs/oxbow", await PolicyKeys.OfAsync(git, Scopes, repo, Token));
        Assert.Equal("rtkelly-labs/oxbow", await PolicyKeys.OfAsync(git, Scopes, worktree, Token));
    }

    [Fact]
    public async Task A_plain_folder_is_not_asked_about_its_remote()
    {
        var plain = _dir.Folder("rtkelly-labs/not-a-repo");
        var proc = new Fakes.FakeProc();

        Assert.Equal("rtkelly-labs/not-a-repo", await PolicyKeys.OfAsync(new Git(proc, _dir.Path), Scopes, plain, Token));
        Assert.Empty(proc.Requests);
    }
}
