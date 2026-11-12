using System.Runtime.Versioning;
using Rig.Core.Desktop.Windows;
using Rig.Core.Desktop.Windows.Uia;

namespace Rig.Core.Tests.Desktop.Windows;

/// <summary>
/// The real UI Automation session, on the Windows CI job and Windows machines only: it opens, reads a live window's
/// element and its tree without throwing, and the answers hang together.
/// </summary>
[SupportedOSPlatform("windows8.1")]
public sealed class NativeUiAutomationTests
{
    [Fact]
    public void Reads_a_live_window()
    {
        Assert.SkipUnless(OperatingSystem.IsWindowsVersionAtLeast(6, 3), "UI Automation exists only on Windows.");
        var win32 = new NativeWin32Windows();
        var uia = new NativeUiAutomation();

        var handle = win32.TopLevelWindows().Where(win32.IsVisible).First(found => uia.ElementFromHandle(found) is not null);
        var element = uia.ElementFromHandle(handle);
        Assert.NotNull(element);

        _ = (element.Name, element.ClassName, element.ControlType, element.BoundingRectangle);
        _ = (element.IsOffscreen, element.HasKeyboardFocus);
        Assert.NotEmpty(element.RuntimeId);
        Assert.True(element.SameAs(element));
        Assert.Equal(element.RuntimeId, uia.ElementFromHandle(handle)!.RuntimeId);

        var children = element.FindAll(UiaScope.Children, new UiaCondition());
        var first = element.FirstChild(UiaView.Raw);
        if (first is not null)
        {
            Assert.NotNull(first.Parent(UiaView.Raw));
            _ = first.NextSibling();
        }

        Assert.All(children, child => _ = child.ClassName);
        _ = element.FindFirst(UiaScope.Descendants, new UiaCondition(UiaControlType.Button, Name: "no such button, surely"));
    }

    [Fact]
    public void A_gone_window_has_no_element()
    {
        Assert.SkipUnless(OperatingSystem.IsWindowsVersionAtLeast(6, 3), "UI Automation exists only on Windows.");

        Assert.Null(new NativeUiAutomation().ElementFromHandle(0x7FFF_FFF0));
    }
}
