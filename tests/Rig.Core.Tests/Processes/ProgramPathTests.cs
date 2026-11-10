using Rig.Core.Processes;

namespace Rig.Core.Tests.Processes;

public sealed class ProgramPathTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Finds_the_first_folder_holding_the_program()
    {
        var first = _dir.Folder("a");
        var second = _dir.Folder("b");
        var tool = _dir.File("b/tool");

        Assert.Equal(tool, ProgramPath.Find("tool", string.Join(Path.PathSeparator, first, second)));
        Assert.Equal(_dir.File("a/tool"), ProgramPath.Find("tool", string.Join(Path.PathSeparator, first, second)));
    }

    [Fact]
    public void Tries_each_extension_as_windows_does()
    {
        var tool = _dir.File("bin/tool.cmd");

        Assert.Equal(tool, ProgramPath.Find("tool", _dir.Folder("bin"), ".EXE; .cmd"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Nothing_is_found_without_a_path(string? path) => Assert.Null(ProgramPath.Find("tool", path));

    [Fact]
    public void A_missing_program_is_null() => Assert.Null(ProgramPath.Find("rig-no-such-program", _dir.Path));

    [Fact]
    public void The_process_path_finds_git() => Assert.NotNull(ProgramPath.Find("git"));
}
