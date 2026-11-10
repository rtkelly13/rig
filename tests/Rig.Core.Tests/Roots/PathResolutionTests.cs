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
}
