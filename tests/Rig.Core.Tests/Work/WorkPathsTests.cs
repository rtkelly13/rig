using Rig.Core.WorkItems;

namespace Rig.Core.Tests.WorkItems;

public sealed class WorkPathsTests
{
    [Theory]
    [InlineData("/a/./b/../c", "/a/c")]
    [InlineData("/a//b///c", "/a/b/c")]
    [InlineData("/a/../../b", "/b")]
    [InlineData("a/../b", "b")]
    [InlineData("../a", "../a")]
    [InlineData("a/..", ".")]
    [InlineData(".", ".")]
    public void Normalise_collapses_dots_and_separators_like_os_path_normpath(string path, string expected) =>
        Assert.Equal(Platform(expected), WorkPaths.Normalise(path));

    [Fact]
    public void IsUnder_is_true_for_the_folder_and_anything_below_it()
    {
        Assert.True(IsUnder("/code/app", "/code/app"));
        Assert.True(IsUnder("/code/app/temp/wt", "/code/app"));
        Assert.False(IsUnder("/code/application", "/code/app"));
        Assert.False(IsUnder("/code", "/code/app"));
    }

    // Normalise deliberately uses the platform separator, and these cases are written with the POSIX one.
    private static string Platform(string path) => path.Replace('/', Path.DirectorySeparatorChar);

    // IsUnder expects both inputs already normalised, so the tests normalise them exactly as the product code does.
    private static bool IsUnder(string path, string folder) =>
        WorkPaths.IsUnder(WorkPaths.Normalise(path), WorkPaths.Normalise(folder));
}
