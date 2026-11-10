using Rig.Core.Processes;

namespace Rig.Core.Tests.Processes;

public sealed class ProgramPathTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private string Program(string relative)
    {
        var file = _dir.File(relative);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return file;
    }

    [Fact]
    public void Finds_the_first_folder_holding_the_program()
    {
        var path = string.Join(Path.PathSeparator, _dir.Folder("a"), _dir.Folder("b"));
        var later = Program("b/tool");

        Assert.Equal(later, ProgramPath.Find("tool", path));
        Assert.Equal(Program("a/tool"), ProgramPath.Find("tool", path));
    }

    [Fact]
    public void A_file_that_cannot_run_does_not_shadow_the_program_further_down_the_path()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows runs files by extension, not by an execute bit.");
        _dir.File("a/tool");
        var tool = Program("b/tool");

        Assert.Equal(tool, ProgramPath.Find("tool", string.Join(Path.PathSeparator, _dir.Folder("a"), _dir.Folder("b"))));
    }

    [Fact]
    public void Tries_each_extension_as_windows_does()
    {
        var tool = Program("bin/tool.cmd");

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
