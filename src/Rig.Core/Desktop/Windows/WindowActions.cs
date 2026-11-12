namespace Rig.Core.Desktop.Windows;

/// <summary>
/// Moving, raising and closing windows, ending processes and typing, as restore does them. Ported from the write half
/// of <c>desktop_manager.win32.geometry</c>, <c>close_current_desktop_windows</c>, <c>terminate_process_id</c> and
/// <c>wait_for_process_exit</c> in <c>desktop_manager.win32.inventory</c>, and <c>send_input</c>.
/// </summary>
/// <param name="win32">The Win32 writes.</param>
/// <param name="inventory">The reads the writes are planned from.</param>
public sealed class WindowActions(IWin32Actions win32, WindowInventory inventory)
{
    /// <summary>
    /// Restore the window and move its visible frame onto <paramref name="rect"/>, then bob it through topmost so it
    /// lands at the top of the z-order without taking focus.
    /// </summary>
    /// <exception cref="DesktopException">Windows refused the move, as it does for an elevated window.</exception>
    public void Move(long handle, Rect rect)
    {
        var adjusted = inventory.AdjustForFrame(handle, rect);
        _ = win32.Restore(handle);
        Require(win32.MoveWindow(handle, adjusted), handle, "move");
        Require(win32.Restack(handle, ZOrder.Topmost), handle, "raise");
        Require(win32.Restack(handle, ZOrder.NotTopmost), handle, "lower");
    }

    /// <summary>
    /// Move a normal window's visible frame onto <paramref name="rect"/> without activating it or changing the
    /// z-order, so the user's foreground window and stacking stay as they were.
    /// </summary>
    /// <exception cref="DesktopException">Windows refused the move, as it does for an elevated window.</exception>
    public void Place(long handle, Rect rect) =>
        Require(win32.Reposition(handle, inventory.AdjustForFrame(handle, rect)), handle, "place");

    /// <summary>Bring the window to the top of the z-order without stealing focus; a failure is ignored.</summary>
    public void BringToFront(long handle)
    {
        _ = win32.ShowWithoutActivating(handle);
        _ = win32.Restack(handle, ZOrder.Top);
    }

    /// <summary>Ask the window to close, as its close button would.</summary>
    public void RequestClose(long handle) => _ = win32.PostClose(handle);

    /// <summary>Ask every user window on the current desktop, apart from <paramref name="preserved"/>, to close.</summary>
    /// <returns>How many were asked.</returns>
    public int CloseCurrentDesktopWindows(IReadOnlySet<long>? preserved = null)
    {
        var handles = inventory.CurrentDesktopWindowHandles(preserved);
        foreach (var handle in handles)
        {
            RequestClose(handle);
        }

        return handles.Count;
    }

    /// <summary>Make <paramref name="handle"/> the foreground window; whether Windows allowed it.</summary>
    public bool Focus(long handle) => win32.SetForeground(handle);

    /// <summary>Where <paramref name="placement"/> sits in pixels on the live monitor it names.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The screen it names is not connected.</exception>
    public Rect PlacementRectFor(PlacementSpec placement)
    {
        ArgumentNullException.ThrowIfNull(placement);
        return Geometry.PlacementRectIn(inventory.WorkAreaByNumber(placement.Screen), placement);
    }

    /// <summary>Send <paramref name="events"/> to whatever window has focus; whether Windows took all of them.</summary>
    public bool SendKeys(IReadOnlyList<KeyEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        return events.Count == 0 || win32.SendKeys(events) == events.Count;
    }

    /// <summary>End process <paramref name="pid"/> at once; whether the kill was issued.</summary>
    public bool TerminateProcess(int pid, int exitCode = 1) => win32.Terminate(pid, exitCode);

    /// <summary>Wait for process <paramref name="pid"/> to exit; true once it has, or when it cannot be opened.</summary>
    public bool WaitForProcessExit(int pid, TimeSpan? timeout = null)
    {
        var wait = timeout ?? TimeSpan.FromSeconds(5);
        return win32.WaitForExit(pid, wait < TimeSpan.Zero ? TimeSpan.Zero : wait) ?? true;
    }

    private static void Require(bool done, long handle, string what)
    {
        if (!done)
        {
            throw new DesktopException($"Windows refused to {what} window {handle}");
        }
    }
}
