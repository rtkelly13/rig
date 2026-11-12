namespace Rig.Core.Desktop;

/// <summary>Which virtual desktop a window is on: all window inventory needs from the virtual-desktop layer.</summary>
public interface IWindowDesktops
{
    /// <summary>Whether window <paramref name="handle"/> is on the desktop showing now.</summary>
    bool IsOnCurrentDesktop(long handle);

    /// <summary>The desktop window <paramref name="handle"/> is on, numbered from 1, or null when it cannot be told.</summary>
    int? DesktopNumberOf(long handle);
}
