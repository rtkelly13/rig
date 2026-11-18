using System.Diagnostics.CodeAnalysis;
using System.Runtime.Versioning;

namespace Rig.Core.Desktop.Windows;

/// <summary>
/// The real Windows machine as align sees it.
/// </summary>
[SupportedOSPlatform("windows")]
[ExcludeFromCodeCoverage(Justification = "Win32 interop; the Windows CI job smoke-tests it, the ubuntu coverage run cannot reach it.")]
public sealed class NativeAlignPorts : IAlignPorts
{
    private readonly IVirtualDesktops _desktops;
    private readonly WindowInventory _inventory;
    private readonly WindowActions _windowActions;

    /// <summary>The machine over <paramref name="win32"/> and <paramref name="actions"/>.</summary>
    public NativeAlignPorts(IWin32Windows win32, IWin32Actions actions, IVirtualDesktops desktops)
    {
        ArgumentNullException.ThrowIfNull(win32);
        ArgumentNullException.ThrowIfNull(actions);
        ArgumentNullException.ThrowIfNull(desktops);
        _desktops = desktops;
        _inventory = new WindowInventory(win32, desktops);
        _windowActions = new WindowActions(actions, _inventory);
    }

    /// <inheritdoc/>
    public IReadOnlyList<string> DesktopNames() => _desktops.Names();

    /// <inheritdoc/>
    public int DesktopCount() => _desktops.Count;

    /// <inheritdoc/>
    public int? CurrentDesktop() => _desktops.CurrentNumber is >= 1 ? _desktops.CurrentNumber : null;

    /// <inheritdoc/>
    public long? ForegroundHwnd() => _inventory.ForegroundWindow();

    /// <inheritdoc/>
    public long? ConsoleHwnd() => _inventory.ConsoleWindow();

    /// <inheritdoc/>
    public IReadOnlyList<LiveWindow> Inventory(IReadOnlySet<long> excluded) => _inventory.ReusableWindows(excluded);

    /// <inheritdoc/>
    public ShowState ShowState(long hwnd) => _inventory.ShowStateOf(hwnd);

    /// <inheritdoc/>
    public Rect TargetRect(PlacementSpec placement) => _windowActions.PlacementRectFor(placement);

    /// <inheritdoc/>
    public bool IsWindow(long hwnd) => _inventory.IsWindow(hwnd);

    /// <inheritdoc/>
    public string? MoveToDesktop(long hwnd, int desktopIndex)
    {
        try
        {
            return VirtualDesktopOperations.MoveWindow(_desktops, hwnd, desktopIndex)
                ? null
                : $"Could not move window {hwnd} to desktop {desktopIndex}.";
        }
        catch (DesktopException error)
        {
            return error.Message;
        }
    }

    /// <inheritdoc/>
    public string? Place(long hwnd, Rect rect)
    {
        try
        {
            _windowActions.Place(hwnd, rect);
            return null;
        }
        catch (DesktopException error)
        {
            return error.Message;
        }
    }

    /// <inheritdoc/>
    public bool RestoreView(int? desktop, long? foreground, IReadOnlySet<long> movedHwnds)
    {
        ArgumentNullException.ThrowIfNull(movedHwnds);
        var restored = false;
        if (desktop is { } starting && _desktops.CurrentNumber != starting)
        {
            restored = VirtualDesktopOperations.GoTo(_desktops, starting);
        }

        if (foreground is { } window && _inventory.IsWindow(window) && !movedHwnds.Contains(window))
        {
            restored |= _windowActions.Focus(window);
        }

        return restored;
    }

    /// <inheritdoc/>
    public void EnsureDesktopCount(int count, double settleSeconds = 0.65)
    {
        _ = VirtualDesktopOperations.EnsureCount(_desktops, count);
        Thread.Sleep(TimeSpan.FromSeconds(settleSeconds));
    }

    /// <inheritdoc/>
    public bool RenameDesktop(int desktopIndex, string name) => VirtualDesktopOperations.Rename(_desktops, desktopIndex, name);

    /// <inheritdoc/>
    public IReadOnlyList<string> RetireDesktops(int keepCount, IReadOnlyList<string> retiredNames) =>
        VirtualDesktopOperations.RetireSwitchedOff(_desktops, keepCount, retiredNames);
}
