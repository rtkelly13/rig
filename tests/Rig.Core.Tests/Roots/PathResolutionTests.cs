using Rig.Core.Roots;

namespace Rig.Core.Tests.Roots;

public sealed class PathResolutionTests
{
    [Fact]
    public void Follows_a_linked_folder_along_the_path()
    {
        using var dir = new TempDir();
        var real = dir.Folder("real");
        var link = Path.Combine(dir.Path, "link");
        Directory.CreateSymbolicLink(link, real);

        Assert.Equal(
            PathResolution.Resolve(Path.Combine(real, "inside")),
            PathResolution.Resolve(Path.Combine(link, "inside")));
    }

    [Fact]
    public void Follows_a_relative_link()
    {
        using var dir = new TempDir();
        var real = dir.Folder("real");
        Directory.CreateSymbolicLink(Path.Combine(dir.Path, "rel"), "real");

        Assert.Equal(PathResolution.Resolve(real), PathResolution.Resolve(Path.Combine(dir.Path, "rel")));
    }

    [Fact]
    public void Stops_at_a_link_cycle()
    {
        using var dir = new TempDir();
        Directory.CreateSymbolicLink(Path.Combine(dir.Path, "a"), "b");
        Directory.CreateSymbolicLink(Path.Combine(dir.Path, "b"), "a");

        var resolved = PathResolution.Resolve(Path.Combine(dir.Path, "a"));

        Assert.Matches("^[ab]$", Path.GetFileName(resolved));
    }

    [Fact]
    public void Dot_dot_after_a_link_leaves_the_link_target_not_the_link_folder()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows collapses .. by name before following links.");
        using var dir = new TempDir();
        var external = dir.Folder(Path.Combine("external", "subdir"));
        dir.Folder("personal");
        Directory.CreateSymbolicLink(Path.Combine(dir.Path, "personal", "link"), external);

        var resolved = PathResolution.Resolve(Path.Combine(dir.Path, "personal", "link", "..", "repo"));

        Assert.Equal(Path.Combine(PathResolution.Resolve(Path.Combine(dir.Path, "external")), "repo"), resolved);
    }

    [Fact]
    public void Dot_dot_inside_a_link_target_walks_from_the_target()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows collapses .. by name before following links.");
        using var dir = new TempDir();
        var deep = dir.Folder(Path.Combine("a", "b"));
        Directory.CreateSymbolicLink(Path.Combine(dir.Path, "hop"), deep);
        Directory.CreateSymbolicLink(Path.Combine(dir.Path, "up"), Path.Combine("hop", ".."));

        Assert.Equal(PathResolution.Resolve(Path.Combine(dir.Path, "a")), PathResolution.Resolve(Path.Combine(dir.Path, "up")));
    }

    [Fact]
    public void Dot_components_are_dropped_and_dot_dot_stops_at_the_root()
    {
        var root = Path.GetPathRoot(Path.GetTempPath())!;
        using var dir = new TempDir();

        Assert.Equal(PathResolution.Resolve(dir.Path), PathResolution.Resolve(Path.Combine(dir.Path, ".", "x", "..")));
        Assert.Equal(root, PathResolution.Resolve(Path.Combine(root, "..", "..")));
    }

    [Fact]
    public void A_relative_path_is_taken_from_the_current_folder()
    {
        Assert.Equal(PathResolution.Resolve(Environment.CurrentDirectory), PathResolution.Resolve("."));
    }

    [Fact]
    public void Keeps_missing_components_as_written()
    {
        using var dir = new TempDir();

        Assert.EndsWith(Path.Combine("missing", "deeper"), PathResolution.Resolve(Path.Combine(dir.Path, "missing", "deeper")), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/code/external", "/code/external", true)]
    [InlineData("/code/external/repo", "/code/external", true)]
    [InlineData("/code/external/repo", "/code/external/", true)]
    [InlineData("/code/externalish", "/code/external", false)]
    [InlineData("/code/personal", "/code/external", false)]
    public void Under_means_the_folder_or_inside_it(string path, string folder, bool under)
    {
        var sep = Path.DirectorySeparatorChar;
        Assert.Equal(under, PathResolution.IsUnder(path.Replace('/', sep), folder.Replace('/', sep)));
    }

    [Fact]
    public void Same_ignores_a_trailing_separator_and_case_where_the_file_system_does()
    {
        var sep = Path.DirectorySeparatorChar;
        Assert.True(PathResolution.Same($"{sep}a{sep}wt", $"{sep}a{sep}wt{sep}"));
        Assert.False(PathResolution.Same($"{sep}a{sep}wt", $"{sep}a{sep}wt2"));
        Assert.Equal(!OperatingSystem.IsLinux(), PathResolution.Same($"{sep}A{sep}Wt", $"{sep}a{sep}wt"));
    }
}
