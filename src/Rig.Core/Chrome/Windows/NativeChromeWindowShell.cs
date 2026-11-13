using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Shell.PropertiesSystem;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Rig.Core.Chrome.Windows;

/// <summary>
/// The shell property store and window placement, through CsWin32's generated interop. Ported from
/// <c>desktop_manager.chrome.window_info</c>.
/// </summary>
[SupportedOSPlatform("windows6.1")]
[ExcludeFromCodeCoverage(Justification = "Win32 interop; the Windows CI job smoke-tests it, the ubuntu coverage run cannot reach it.")]
public sealed unsafe class NativeChromeWindowShell : IChromeWindowShell
{
    /// <inheritdoc/>
    public WindowProfile Profile(long handle)
    {
        try
        {
            var riid = typeof(IPropertyStore).GUID;
            UiaNative.SHGetPropertyStoreForWindow(new HWND((nint)handle), &riid, out var unknown).ThrowOnFailure();
            var store = (IPropertyStore)unknown;
            var command = Read(store, UiaNative.PKEY_AppUserModel_RelaunchCommand);
            var display = Read(store, UiaNative.PKEY_AppUserModel_RelaunchDisplayNameResource);
            return new(WindowProfile.DirectoryFromRelaunchCommand(command), WindowProfile.NameFromRelaunchDisplayName(display));
        }
        catch (Exception error) when (error is COMException or InvalidCastException)
        {
            return new(null, null);
        }
    }

    /// <inheritdoc/>
    public WindowRect? RestoredRect(long handle)
    {
        var placement = new WINDOWPLACEMENT { length = (uint)sizeof(WINDOWPLACEMENT) };
        if (!UiaNative.GetWindowPlacement(new HWND((nint)handle), ref placement))
        {
            return null;
        }

        var rect = placement.rcNormalPosition;
        return new WindowRect(rect.left, rect.top, rect.right, rect.bottom);
    }

    private static string? Read(IPropertyStore store, PROPERTYKEY key)
    {
        store.GetValue(in key, out var value);
        try
        {
            if (UiaNative.PropVariantToStringAlloc(in value, out var text).Failed)
            {
                return null;
            }

            try
            {
                return text.ToString();
            }
            finally
            {
                UiaNative.CoTaskMemFree(text.Value);
            }
        }
        finally
        {
            _ = UiaNative.PropVariantClear(ref value);
        }
    }
}
