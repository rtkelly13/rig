using Rig.Core.Roots;

namespace Rig.Core.Tests.Roots;

public sealed class CodeRootTests
{
    [Fact]
    public void Marker_needs_agents_md_beside_personal()
    {
        using var dir = new TempDir();
        dir.File("AGENTS.md");

        Assert.False(CodeRoot.IsCodeRoot(dir.Path));
        dir.Folder("personal");
        Assert.True(CodeRoot.IsCodeRoot(dir.Path));
    }

    [Fact]
    public void Finds_the_nearest_marked_ancestor_from_a_nested_worktree()
    {
        using var dir = new TempDir();
        dir.AsCodeRoot();
        var worktree = dir.Folder("personal/rig/temp/rig/x");

        Assert.Equal(Path.GetFullPath(dir.Path), CodeRoot.Find(worktree));
    }

    [Fact]
    public void Finds_nothing_without_a_marker()
    {
        using var dir = new TempDir();

        Assert.Null(CodeRoot.Find(dir.Folder("a/b")));
    }

    [Fact]
    public void The_environment_wins_over_the_marker()
    {
        using var dir = new TempDir();
        dir.AsCodeRoot();

        Assert.Equal("/elsewhere", CodeRoot.Resolve("/elsewhere", dir.Path, "/home/me"));
    }

    [Theory]
    [InlineData("~", "")]
    [InlineData("~/code", "code")]
    [InlineData("~\\code", "code")]
    public void A_configured_root_expands_home(string configured, string under)
    {
        var home = Path.Combine(Path.GetTempPath(), "home");

        Assert.Equal(Path.Combine(home, under).TrimEnd(Path.DirectorySeparatorChar), CodeRoot.Resolve(configured, home, home).TrimEnd(Path.DirectorySeparatorChar));
    }

    [Fact]
    public void An_empty_configured_value_is_ignored()
    {
        using var dir = new TempDir();
        dir.AsCodeRoot();

        Assert.Equal(Path.GetFullPath(dir.Path), CodeRoot.Resolve(string.Empty, dir.Path, "/home/me"));
    }

    [Fact]
    public void Falls_back_to_code_under_home_when_it_exists()
    {
        using var start = new TempDir();
        using var home = new TempDir();
        var code = home.Folder("code");

        Assert.Equal(code, CodeRoot.Resolve(null, start.Path, home.Path));
    }

    [Fact]
    public void Falls_back_to_code_under_home_even_when_nothing_exists()
    {
        using var start = new TempDir();
        using var home = new TempDir();

        var expected = Path.Combine(home.Path, "code");
        var onDrive = Path.Combine(Path.GetPathRoot(home.Path)!, "code");
        Assert.Equal(Directory.Exists(onDrive) ? onDrive : expected, CodeRoot.HomeFallback(home.Path));
    }

    [Fact]
    public void The_current_root_honours_the_environment()
    {
        var before = Environment.GetEnvironmentVariable(CodeRoot.EnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(CodeRoot.EnvironmentVariable, "/configured/root");
            Assert.Equal("/configured/root", CodeRoot.Current());
        }
        finally
        {
            Environment.SetEnvironmentVariable(CodeRoot.EnvironmentVariable, before);
        }
    }
}
