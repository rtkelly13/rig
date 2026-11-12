using Rig.Core.Desktop;

namespace Rig.Core.Chrome.Windows;

/// <summary>
/// Windows' numbered virtual desktops for the Chrome run loops, over <see cref="IVirtualDesktops"/>. Ported from
/// <c>desktop_manager.chrome.desktops.WindowsDesktops</c>: an unreadable current desktop is null, and a switch or move
/// to a desktop that does not exist does nothing.
/// </summary>
/// <param name="desktops">The machine's virtual desktops.</param>
/// <param name="isWindow">Whether a handle is still a window: Win32's <c>IsWindow</c>.</param>
public sealed class ChromeVirtualDesktops(IVirtualDesktops desktops, Func<long, bool> isWindow) : IChromeDesktops
{
    /// <inheritdoc/>
    public int? Current()
    {
        try
        {
            return desktops.CurrentNumber;
        }
        catch (DesktopException)
        {
            return null;
        }
    }

    /// <inheritdoc/>
    public int? DesktopOf(long handle) => desktops.DesktopNumberOf(handle);

    /// <inheritdoc/>
    public void SwitchTo(int number) => VirtualDesktopOperations.GoTo(desktops, number);

    /// <inheritdoc/>
    public void MoveWindow(long handle, int number) => VirtualDesktopOperations.MoveWindow(desktops, handle, number);

    /// <inheritdoc/>
    public bool WindowExists(long handle) => isWindow(handle);
}
