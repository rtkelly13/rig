using Rig.Core.Runtime;

namespace Rig.Core.Tests.Runtime;

/// <summary>The <c>rig</c> shim written on PATH: same text as the Python's, and never overwriting a foreign file.</summary>
public sealed class RigShimTests : IDisposable
{
    private readonly TempDir _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void The_posix_shim_execs_the_target_with_every_argument()
    {
        var text = RigShim.Render("/a b/current/rig/rig", windows: false);

        Assert.StartsWith("#!/bin/sh\n", text, StringComparison.Ordinal);
        Assert.Contains(RigShim.Marker, text, StringComparison.Ordinal);
        Assert.EndsWith("exec '/a b/current/rig/rig' \"$@\"\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_posix_shim_escapes_shell_expansion_characters_in_target()
    {
        var text = RigShim.Render("/tmp/$VAR`whoami`'dir/rig", windows: false);
        Assert.Contains("exec '/tmp/$VAR`whoami`'\\''dir/rig' \"$@\"\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_windows_shim_passes_every_argument()
    {
        var text = RigShim.Render(@"C:\rt\current\rig\rig.exe", windows: true);

        Assert.StartsWith("@echo off\r\n", text, StringComparison.Ordinal);
        Assert.Contains(RigShim.Marker, text, StringComparison.Ordinal);
        Assert.EndsWith("\"C:\\rt\\current\\rig\\rig.exe\" %*\r\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_windows_shim_escapes_literal_percent_signs_in_target()
    {
        var text = RigShim.Render(@"C:\rt\%TEMP%\current\rig.exe", windows: true);
        Assert.EndsWith("\"C:\\rt\\%%TEMP%%\\current\\rig.exe\" %*\r\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Install_writes_then_leaves_an_identical_shim()
    {
        var shim = Path.Join(_temp.Path, "bin", "rig");
        var root = Path.Join(_temp.Path, "runtime");

        Assert.Equal(ShimOutcome.Written, RigShim.Install(shim, root, windows: false));
        Assert.Contains(RuntimeManager.Published(RuntimeManager.CurrentLink(root), "rig", windows: false), File.ReadAllText(shim), StringComparison.Ordinal);
        Assert.Equal(ShimOutcome.Unchanged, RigShim.Install(shim, root, windows: false));
    }

    [Fact]
    public void Install_rewrites_its_own_older_shim()
    {
        var shim = Path.Join(_temp.Path, "rig");
        File.WriteAllText(shim, $"# {RigShim.Marker}\nstale-line\n");

        Assert.Equal(ShimOutcome.Written, RigShim.Install(shim, Path.Join(_temp.Path, "runtime"), windows: false));
        Assert.DoesNotContain("stale-line", File.ReadAllText(shim), StringComparison.Ordinal);
    }

    [Fact]
    public void Install_leaves_a_file_rig_did_not_write()
    {
        var shim = Path.Join(_temp.Path, "rig");
        File.WriteAllText(shim, "#!/bin/sh\necho mine\n");

        Assert.Equal(ShimOutcome.Foreign, RigShim.Install(shim, Path.Join(_temp.Path, "runtime"), windows: false));
        Assert.Equal("#!/bin/sh\necho mine\n", File.ReadAllText(shim));
    }

    [Fact]
    public void Install_windows_writes_and_rewrites_cmd_shim()
    {
        var shim = Path.Join(_temp.Path, "bin", "rig.cmd");
        var root = Path.Join(_temp.Path, "runtime");

        Assert.Equal(ShimOutcome.Written, RigShim.Install(shim, root, windows: true));
        Assert.Contains("@echo off", File.ReadAllText(shim), StringComparison.Ordinal);
        Assert.Equal(ShimOutcome.Unchanged, RigShim.Install(shim, root, windows: true));

        File.WriteAllText(shim, $"rem {RigShim.Marker}\r\nstale\r\n");
        Assert.Equal(ShimOutcome.Written, RigShim.Install(shim, root, windows: true));
        Assert.DoesNotContain("stale", File.ReadAllText(shim), StringComparison.Ordinal);

        File.WriteAllText(shim, "@echo off\r\necho foreign\r\n");
        Assert.Equal(ShimOutcome.Foreign, RigShim.Install(shim, root, windows: true));
    }

    [Fact]
    public void PathFor_returns_platform_appropriate_path()
    {
        Assert.EndsWith(Path.Join(".local", "bin", "rig"), RigShim.PathFor("/users/me", windows: false), StringComparison.Ordinal);
        Assert.EndsWith(Path.Join(".local", "bin", "rig.cmd"), RigShim.PathFor(@"C:\Users\me", windows: true), StringComparison.Ordinal);
    }

    [Fact]
    public void Render_throws_for_null_target()
    {
        Assert.Throws<ArgumentNullException>(() => RigShim.Render(null!, windows: false));
    }

    [Fact]
    public void Install_throws_for_null_shim()
    {
        Assert.Throws<ArgumentNullException>(() => RigShim.Install(null!, "/root", windows: false));
    }

    [Fact]
    public void Install_with_single_file_name_handles_null_parent_directory()
    {
        var root = Path.Join(_temp.Path, "runtime");
        var cwd = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(_temp.Path);
            Assert.Equal(ShimOutcome.Written, RigShim.Install("local-rig", root, windows: false));
            Assert.True(File.Exists(Path.Join(_temp.Path, "local-rig")));
        }
        finally
        {
            Directory.SetCurrentDirectory(cwd);
        }
    }
}
