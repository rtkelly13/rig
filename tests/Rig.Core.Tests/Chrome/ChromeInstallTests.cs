using Rig.Core.Chrome;

namespace Rig.Core.Tests.Chrome;

public sealed class ChromeInstallTests
{
    [Fact]
    public void Root_uses_localappdata_when_present()
    {
        var root = ChromeInstall.Root(varName => varName == "LOCALAPPDATA" ? @"C:\Custom\AppData\Local" : null, @"C:\Users\me");
        Assert.Equal(Path.Join(@"C:\Custom\AppData\Local", "rig", "chrome-tabs"), root);
    }

    [Fact]
    public void Root_falls_back_to_home_when_localappdata_is_null_or_empty()
    {
        var root = ChromeInstall.Root(_ => null, @"C:\Users\me");
        Assert.Equal(Path.Join(@"C:\Users\me", "AppData", "Local", "rig", "chrome-tabs"), root);

        var rootEmpty = ChromeInstall.Root(_ => "", @"C:\Users\me");
        Assert.Equal(Path.Join(@"C:\Users\me", "AppData", "Local", "rig", "chrome-tabs"), rootEmpty);
    }

    [Fact]
    public void ShimPath_returns_path_under_home_local_bin()
    {
        var path = ChromeInstall.ShimPath(@"C:\Users\me");
        Assert.Equal(Path.Join(@"C:\Users\me", ".local", "bin", "chrome-tabs.cmd"), path);
    }

    [Fact]
    public void RuntimeRig_renders_full_path()
    {
        var path = ChromeInstall.RuntimeRig(_ => @"C:\Local", @"C:\Users\me");
        Assert.Equal(Path.Join(@"C:\Local", "rig", "runtime", "current", "rig", "rig.exe"), path);
    }

    [Fact]
    public void RenderShim_renders_batch_script()
    {
        var rendered = ChromeInstall.RenderShim(@"C:\bin\rig.exe");
        Assert.Equal("@echo off\r\n\"C:\\bin\\rig.exe\" chrome-tabs %*\r\n", rendered);
    }
}
