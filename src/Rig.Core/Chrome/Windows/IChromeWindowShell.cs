namespace Rig.Core.Chrome.Windows;

/// <summary>The two window reads the Chrome driver needs beyond the shared Win32 port.</summary>
public interface IChromeWindowShell
{
    /// <summary>The window's profile, from its shell relaunch properties; both parts null when they cannot be read.</summary>
    WindowProfile Profile(long handle);

    /// <summary>The window's restored rectangle, which a minimised window keeps while it sits at -32000.</summary>
    WindowRect? RestoredRect(long handle);
}
