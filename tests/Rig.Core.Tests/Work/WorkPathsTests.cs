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
        Assert.Equal(expected, WorkPaths.Normalise(path));

    [Fact]
    public void IsUnder_is_true_for_the_folder_and_anything_below_it()
    {
        Assert.True(WorkPaths.IsUnder("/code/app", "/code/app"));
        Assert.True(WorkPaths.IsUnder("/code/app/temp/wt", "/code/app"));
        Assert.False(WorkPaths.IsUnder("/code/application", "/code/app"));
        Assert.False(WorkPaths.IsUnder("/code", "/code/app"));
    }
}
