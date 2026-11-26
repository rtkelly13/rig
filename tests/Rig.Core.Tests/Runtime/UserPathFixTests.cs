using Rig.Core.Runtime;

namespace Rig.Core.Tests.Runtime;

/// <summary>
/// <see cref="UserPathFix"/> over raw User PATH strings and temp folders. The incident behind it: the old Python
/// rig's venv <c>Scripts</c> folder sat on the User PATH ahead of the shim.
/// </summary>
public sealed class UserPathFixTests : IDisposable
{
    private static readonly string? Extensions = OperatingSystem.IsWindows() ? ".COM;.EXE;.BAT;.CMD" : null;

    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private string Home => Path.Join(_dir.Path, "home");

    private string RuntimeRoot => Path.Join(_dir.Path, "appdata", "runtime");

    private string ShimFolder => Path.Join(Home, ".local", "bin");

    private string CurrentFolder => Path.Join(RuntimeRoot, RuntimeManager.LinkName, "rig");

    private UserPathPlan Plan(string? userPath, Func<string, string>? expand = null) =>
        UserPathFix.Plan(userPath, expand ?? (entry => entry), Extensions, Home, RuntimeRoot);

    // A venv's Scripts folder holding a runnable rig, with pyvenv.cfg beside it.
    private string VenvScripts(string name)
    {
        var scripts = _dir.Folder(Path.Join(name, "Scripts"));
        File.WriteAllText(Path.Join(_dir.Path, name, "pyvenv.cfg"), string.Empty);
        var rig = Path.Join(scripts, OperatingSystem.IsWindows() ? "rig.exe" : "rig");
        File.WriteAllText(rig, string.Empty);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(rig, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return scripts;
    }

    [Fact]
    public void A_user_path_with_the_shim_folder_is_left_alone()
    {
        var plan = Plan($"C:\\tools;{ShimFolder}{Path.DirectorySeparatorChar}");

        Assert.Null(plan.Value);
        Assert.Empty(plan.Changes);
    }

    [Fact]
    public void The_current_copy_on_the_user_path_counts_as_well_as_the_shim()
    {
        var plan = Plan("%LOCALAPPDATA%\\rig\\runtime\\current\\rig;C:\\tools", entry => entry.Replace("%LOCALAPPDATA%\\rig\\runtime\\current\\rig", CurrentFolder, StringComparison.Ordinal));

        Assert.Null(plan.Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void An_empty_user_path_gets_the_shim_folder(string? userPath)
    {
        var plan = Plan(userPath);

        Assert.Equal(ShimFolder, plan.Value);
        Assert.Equal([$"Added {ShimFolder.Replace('\\', '/')} to the front of your User PATH, so `rig` runs the shim."], plan.Changes);
    }

    [Fact]
    public void The_shim_folder_goes_first_and_every_other_entry_keeps_its_variables()
    {
        var plan = Plan("%USERPROFILE%\\go\\bin;;C:\\tools", entry => entry.Replace("%USERPROFILE%", Home, StringComparison.Ordinal));

        Assert.Equal($"{ShimFolder};%USERPROFILE%\\go\\bin;C:\\tools", plan.Value);
    }

    [Fact]
    public void A_venv_scripts_folder_holding_rig_is_removed_and_the_shim_added()
    {
        var scripts = VenvScripts("rig/.venv");

        var plan = Plan($"\"{scripts}\";C:\\tools");

        Assert.Equal($"{ShimFolder};C:\\tools", plan.Value);
        Assert.Equal(
            [
                $"Removed {scripts.Replace('\\', '/')} from your User PATH: it holds the old Python rig CLI.",
                $"Added {ShimFolder.Replace('\\', '/')} to the front of your User PATH, so `rig` runs the shim.",
            ],
            plan.Changes);
    }

    [Fact]
    public void A_venv_without_a_rig_is_not_touched()
    {
        var scripts = _dir.Folder(Path.Join("other", "Scripts"));
        File.WriteAllText(Path.Join(_dir.Path, "other", "pyvenv.cfg"), string.Empty);

        var plan = Plan($"{ShimFolder};{scripts}");

        Assert.Null(plan.Value);
    }
}
