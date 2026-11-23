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
    public void A_checkout_in_a_personal_folder_implies_the_root_above_it()
    {
        using var home = new TempDir();
        var checkout = home.Folder("Work/personal/rig");

        Assert.Equal(Path.Combine(Path.GetFullPath(home.Path), "Work"), CodeRoot.FromCheckout(checkout));
    }

    [Theory]
    [InlineData("rig")]
    [InlineData("elsewhere/rig")]
    public void A_checkout_outside_a_personal_folder_implies_nothing(string under)
    {
        using var home = new TempDir();

        Assert.Null(CodeRoot.FromCheckout(home.Folder(under)));
        Assert.Null(CodeRoot.FromCheckout(null));
    }

    [Fact]
    public void The_running_checkout_decides_the_root_before_the_code_fallback()
    {
        using var start = new TempDir();
        using var home = new TempDir();
        home.Folder("code");
        var checkout = home.Folder("Work/personal/rig");

        Assert.Equal(Path.Combine(Path.GetFullPath(home.Path), "Work"), CodeRoot.Resolve(null, start.Path, home.Path, checkout));
    }

    [Fact]
    public void A_marked_ancestor_wins_over_the_checkout()
    {
        using var root = new TempDir();
        root.AsCodeRoot();
        using var home = new TempDir();
        var checkout = home.Folder("Work/personal/rig");

        Assert.Equal(Path.GetFullPath(root.Path), CodeRoot.Resolve(null, root.Folder("personal/x"), home.Path, checkout));
    }

    [Fact]
    public void A_checkout_outside_home_is_ignored()
    {
        using var start = new TempDir();
        using var home = new TempDir();
        using var other = new TempDir();
        var code = home.Folder("code");

        Assert.Equal(code, CodeRoot.Resolve(null, start.Path, home.Path, other.Folder("Work/personal/rig")));
    }

    [Fact]
    public void Display_abbreviates_home_with_forward_slashes()
    {
        using var home = new TempDir();
        using var other = new TempDir();

        Assert.Equal("~/Work", CodeRoot.Display(home.Folder("Work"), home.Path));
        Assert.Equal(Path.GetFullPath(other.Path).Replace('\\', '/'), CodeRoot.Display(other.Path, home.Path));
    }
}
