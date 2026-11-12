using System.Runtime.Versioning;
using Rig.Core.Desktop;
using Rig.Core.Desktop.Windows;

namespace Rig.Core.Tests.Desktop;

/// <summary>
/// The shell's own virtual desktops through <see cref="ComVirtualDesktops"/>. Read-only, and Windows only; they skip
/// where no shell runs in the session. Creating, renaming, switching and removing a desktop disturbs the person at
/// the machine, so that check runs only with <c>RIG_LIVE_DESKTOP_WRITES=1</c>. Marked Windows-only for the platform
/// analyzer; off Windows each test skips before it reaches the shell.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ComVirtualDesktopsTests
{
    [Fact]
    public void The_shell_reports_a_current_desktop_among_its_named_ones()
    {
        var desktops = Open();

        var names = desktops.Names();

        Assert.Equal(desktops.Count, names.Count);
        Assert.InRange(desktops.CurrentNumber, 1, names.Count);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(123456789L)]
    public void A_window_the_shell_has_no_view_for_is_on_the_current_desktop_and_no_numbered_one(long handle)
    {
        var desktops = Open();

        Assert.True(desktops.IsOnCurrentDesktop(handle));
        Assert.Null(desktops.DesktopNumberOf(handle));
    }

    [Fact]
    public void A_missing_desktop_is_a_desktop_exception()
    {
        var desktops = Open();

        Assert.Throws<DesktopException>(() => desktops.SwitchTo(desktops.Count + 1));
        Assert.Throws<DesktopException>(() => desktops.Remove(1, 1));
    }

    [Fact]
    public void A_desktop_made_for_the_test_is_named_shown_and_removed_leaving_the_rest_as_found()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("RIG_LIVE_DESKTOP_WRITES") == "1", "Set RIG_LIVE_DESKTOP_WRITES=1 to change the desktops.");
        var desktops = Open();
        var before = desktops.Names();
        var start = desktops.CurrentNumber;
        Assert.DoesNotContain("rig-test-desktop", before);

        desktops.Create();
        try
        {
            Assert.Equal(before.Count + 1, desktops.Count);
            Assert.True(VirtualDesktopOperations.Rename(desktops, desktops.Count, "rig-test-desktop"));
            Assert.True(VirtualDesktopOperations.GoTo(desktops, desktops.Count));
            Assert.True(VirtualDesktopOperations.GoTo(desktops, start));
        }
        finally
        {
            Assert.Equal(["rig-test-desktop"], VirtualDesktopOperations.RetireSwitchedOff(desktops, before.Count, ["rig-test-desktop"]));
        }

        Assert.Equal(before, desktops.Names());
        Assert.Equal(start, desktops.CurrentNumber);
    }

    private static ComVirtualDesktops Open()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Virtual desktops are a Windows shell feature.");
        }

        try
        {
            return ComVirtualDesktops.Open();
        }
        catch (DesktopException error) when (error.Message.StartsWith("the Windows shell is not reachable", StringComparison.Ordinal))
        {
            Assert.Skip(error.Message);
            throw;
        }
    }
}
