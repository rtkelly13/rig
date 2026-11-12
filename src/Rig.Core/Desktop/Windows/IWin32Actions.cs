namespace Rig.Core.Desktop.Windows;

/// <summary>
/// The Win32 calls that change windows, processes and input, one call each with no policy of their own. Each answers
/// whether Windows accepted it; <see cref="WindowActions"/> decides which failures matter. The real one is
/// <see cref="NativeWin32Actions"/>.
/// </summary>
public interface IWin32Actions
{
    /// <summary><c>ShowWindow(SW_RESTORE)</c>: un-minimize or un-maximize, which also activates the window.</summary>
    bool Restore(long handle);

    /// <summary><c>ShowWindow(SW_SHOWNA)</c>: show in its current state without activating.</summary>
    bool ShowWithoutActivating(long handle);

    /// <summary><c>MoveWindow</c> to <paramref name="rect"/>, repainting.</summary>
    bool MoveWindow(long handle, Rect rect);

    /// <summary><c>SetWindowPos</c> to <paramref name="rect"/> without activating or touching the z-order.</summary>
    bool Reposition(long handle, Rect rect);

    /// <summary><c>SetWindowPos</c> into the order <paramref name="order"/> names, without moving, sizing or activating.</summary>
    bool Restack(long handle, ZOrder order);

    /// <summary>Posts <c>WM_CLOSE</c>, which asks the window to close as its close button would.</summary>
    bool PostClose(long handle);

    /// <summary><c>SetForegroundWindow</c>.</summary>
    bool SetForeground(long handle);

    /// <summary><c>SendInput</c> with one keyboard <c>INPUT</c> per event; answers how many Windows took.</summary>
    int SendKeys(IReadOnlyList<KeyEvent> events);

    /// <summary>Opens the process for termination and ends it; false when it cannot be opened or ended.</summary>
    bool Terminate(int pid, int exitCode);

    /// <summary>Waits for the process to exit: null when it cannot be opened, otherwise whether it exited in time.</summary>
    bool? WaitForExit(int pid, TimeSpan timeout);
}

/// <summary>Where <see cref="IWin32Actions.Restack"/> puts a window.</summary>
public enum ZOrder
{
    /// <summary><c>HWND_TOP</c>: above the other non-topmost windows.</summary>
    Top,

    /// <summary><c>HWND_TOPMOST</c>: above every non-topmost window, and kept there.</summary>
    Topmost,

    /// <summary><c>HWND_NOTOPMOST</c>: back among the non-topmost windows, at their top.</summary>
    NotTopmost,
}

/// <summary>
/// One keyboard event for <c>SendInput</c>: a virtual key, or with <see cref="KeyEvent.Unicode"/> a UTF-16 unit in
/// <paramref name="Scan"/>. Ported from <c>desktop_manager.win32.send_input.KeyEvent</c>.
/// </summary>
/// <param name="VirtualKey">The virtual-key code, or 0 for a Unicode unit.</param>
/// <param name="Scan">The scan code, or the UTF-16 unit.</param>
/// <param name="Flags">The <c>KEYEVENTF_</c> flags.</param>
public sealed record KeyEvent(ushort VirtualKey, ushort Scan, uint Flags)
{
    /// <summary><c>KEYEVENTF_KEYUP</c>.</summary>
    public const uint KeyUp = 0x2;

    /// <summary><c>KEYEVENTF_UNICODE</c>.</summary>
    public const uint Unicode = 0x4;

    /// <summary>Press and release one virtual key.</summary>
    public static IReadOnlyList<KeyEvent> Tap(ushort virtualKey) => [new(virtualKey, 0, 0), new(virtualKey, 0, KeyUp)];

    /// <summary>Type <paramref name="text"/> whatever the keyboard layout: a down and an up per UTF-16 unit.</summary>
    public static IReadOnlyList<KeyEvent> Text(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return [.. text.SelectMany(unit => new KeyEvent[] { new(0, unit, Unicode), new(0, unit, Unicode | KeyUp) })];
    }
}
