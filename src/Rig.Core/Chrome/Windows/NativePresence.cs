using System.Diagnostics.CodeAnalysis;
using System.Runtime.Versioning;
using Microsoft.Win32;
using Windows.Win32;
using Windows.Win32.System.StationsAndDesktops;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace Rig.Core.Chrome.Windows;

/// <summary>
/// The input clock, the lock screen and the privacy settings' device use, through Win32. Ported from
/// <c>desktop_manager.chrome.presence</c>.
/// </summary>
[SupportedOSPlatform("windows6.1")]
[ExcludeFromCodeCoverage(Justification = "Win32 interop; the Windows CI job smoke-tests it, the ubuntu coverage run cannot reach it.")]
public sealed class NativePresence : IPresence
{
    private const string ConsentStore = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore";

    /// <summary>The privacy settings' names for the devices, and how a block reports them.</summary>
    private static readonly (string Store, string Device)[] Devices = [("microphone", "microphone"), ("webcam", "camera")];

    /// <inheritdoc/>
    public uint NowTick() => UiaNative.GetTickCount();

    /// <inheritdoc/>
    public unsafe uint LastInputTick()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)sizeof(LASTINPUTINFO) };
        return UiaNative.GetLastInputInfo(ref info) ? info.dwTime : NowTick();
    }

    /// <inheritdoc/>
    public bool SessionLocked()
    {
        using var desktop = UiaNative.OpenInputDesktop_SafeHandle(0, false, DESKTOP_ACCESS_FLAGS.DESKTOP_SWITCHDESKTOP);
        return desktop.IsInvalid || !UiaNative.SwitchDesktop(desktop);
    }

    /// <inheritdoc/>
    public IReadOnlyList<string> DevicesInUse() => DevicesInUse(Registry.CurrentUser, "chrome.exe");

    /// <summary>
    /// The devices <paramref name="executable"/> is using under <paramref name="currentUser"/>. Windows keeps, per app,
    /// when it last started and stopped using each device; a stop time of 0 means it still is.
    /// </summary>
    public static IReadOnlyList<string> DevicesInUse(RegistryKey currentUser, string executable)
    {
        ArgumentNullException.ThrowIfNull(currentUser);
        ArgumentNullException.ThrowIfNull(executable);
        var suffix = "#" + executable.ToLowerInvariant();
        List<string> inUse = [];
        foreach (var (store, device) in Devices)
        {
            using var apps = currentUser.OpenSubKey($@"{ConsentStore}\{store}\NonPackaged");
            if (apps is null)
            {
                continue;
            }

            foreach (var name in apps.GetSubKeyNames().Where(name => name.ToLowerInvariant().EndsWith(suffix, StringComparison.Ordinal)))
            {
                using var app = apps.OpenSubKey(name);
                if (app?.GetValue("LastUsedTimeStart") is long started
                    && app.GetValue("LastUsedTimeStop") is long stopped
                    && started != 0
                    && stopped == 0)
                {
                    inUse.Add(device);
                    break;
                }
            }
        }

        return inUse;
    }
}
