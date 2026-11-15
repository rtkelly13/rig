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
        var text = RigShim.Render("/a b/current/.venv/bin/rig", windows: false);

        Assert.StartsWith("#!/bin/sh\n", text, StringComparison.Ordinal);
        Assert.Contains(RigShim.Marker, text, StringComparison.Ordinal);
        Assert.EndsWith("exec \"/a b/current/.venv/bin/rig\" \"$@\"\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_windows_shim_passes_every_argument()
    {
        var text = RigShim.Render(@"C:\rt\current\.venv\Scripts\rig.exe", windows: true);

        Assert.StartsWith("@echo off\r\n", text, StringComparison.Ordinal);
        Assert.Contains(RigShim.Marker, text, StringComparison.Ordinal);
        Assert.EndsWith("\"C:\\rt\\current\\.venv\\Scripts\\rig.exe\" %*\r\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Install_writes_then_leaves_an_identical_shim()
    {
        var shim = Path.Join(_temp.Path, "bin", "rig");
        var root = Path.Join(_temp.Path, "runtime");

        Assert.Equal(ShimOutcome.Written, RigShim.Install(shim, root, windows: false));
        Assert.Contains(RuntimeManager.Executable(RuntimeManager.CurrentLink(root), "rig", windows: false), File.ReadAllText(shim), StringComparison.Ordinal);
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
}
