namespace Rig.Core.Desktop.Windows;

/// <summary>
/// The undocumented shell interfaces one range of Windows builds exposes for virtual desktops, from
/// <paramref name="MinimumBuild"/> on.
/// </summary>
/// <param name="MinimumBuild">The first build these interfaces apply to.</param>
/// <param name="Release">The Windows release that build shipped as, for messages.</param>
/// <param name="ManagerInternal">The IID of <c>IVirtualDesktopManagerInternal</c>.</param>
/// <param name="VirtualDesktop">The IID of <c>IVirtualDesktop</c>.</param>
public sealed record VirtualDesktopBuild(int MinimumBuild, string Release, Guid ManagerInternal, Guid VirtualDesktop);

/// <summary>
/// Which <see cref="VirtualDesktopBuild"/> a Windows build uses. The IIDs and vtable layouts come from pyvda 0.5.0,
/// which rig used before; Microsoft changes them between releases without notice.
/// </summary>
/// <remarks>
/// Only the 24H2 layout is implemented: Windows 11 releases before it are out of support, and every machine rig runs
/// on is 24H2 or later. A later build is assumed to keep the newest layout, as pyvda assumes; if it does not, the
/// shell refuses the interface and <see cref="ComVirtualDesktops"/> says so naming the build.
/// </remarks>
public static class VirtualDesktopBuilds
{
    /// <summary>The 24H2 interfaces, build 26100 on.</summary>
    public static VirtualDesktopBuild Windows11Build26100 { get; } = new(
        26100,
        "Windows 11 24H2",
        new("53F5CA0B-158F-4124-900C-057158060B27"),
        new("3F07F4BE-B107-441A-AF0F-39D82529072C"));

    /// <summary>Every build range implemented, newest first.</summary>
    public static IReadOnlyList<VirtualDesktopBuild> Supported { get; } = [Windows11Build26100];

    /// <summary>The interfaces Windows build <paramref name="build"/> uses; a <see cref="DesktopException"/> for one that predates them all.</summary>
    public static VirtualDesktopBuild For(int build) =>
        Supported.FirstOrDefault(supported => build >= supported.MinimumBuild)
        ?? throw new DesktopException(
            $"virtual desktops need Windows build {Supported[^1].MinimumBuild} ({Supported[^1].Release}) or later; this is build {build}");
}
