using Rig.Core.Desktop;
using Rig.Core.Desktop.Windows;

namespace Rig.Core.Tests.Desktop;

/// <summary>Which shell interfaces a Windows build uses for virtual desktops.</summary>
public sealed class VirtualDesktopBuildsTests
{
    [Theory]
    [InlineData(26100)]
    [InlineData(26200)]
    [InlineData(27000)]
    public void Build_26100_and_later_use_the_24H2_interfaces(int build) =>
        Assert.Same(VirtualDesktopBuilds.Windows11Build26100, VirtualDesktopBuilds.For(build));

    [Theory]
    [InlineData(22631)]
    [InlineData(19045)]
    [InlineData(0)]
    public void An_earlier_build_is_refused_naming_it_and_the_first_supported(int build)
    {
        var error = Assert.Throws<DesktopException>(() => VirtualDesktopBuilds.For(build));

        Assert.Contains($"build {build}", error.Message, StringComparison.Ordinal);
        Assert.Contains("26100 (Windows 11 24H2)", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_24H2_interface_ids_are_pyvdas()
    {
        var build = VirtualDesktopBuilds.Windows11Build26100;

        Assert.Equal(new Guid("53F5CA0B-158F-4124-900C-057158060B27"), build.ManagerInternal);
        Assert.Equal(new Guid("3F07F4BE-B107-441A-AF0F-39D82529072C"), build.VirtualDesktop);
    }

    [Fact]
    public void Supported_builds_run_newest_first() =>
        Assert.Equal(
            VirtualDesktopBuilds.Supported.OrderByDescending(build => build.MinimumBuild),
            VirtualDesktopBuilds.Supported);
}
