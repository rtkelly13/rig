using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;
using Rig.Core.Runtime;

namespace Rig.Cli.Runtime;

/// <summary>
/// The User PATH in <c>HKCU\Environment</c>. Read and written raw, as <c>REG_EXPAND_SZ</c>, so a
/// <c>%LOCALAPPDATA%</c> in it survives; <see cref="Environment.SetEnvironmentVariable(string, string, EnvironmentVariableTarget)"/>
/// would write it back expanded. A write is broadcast so programs started afterwards see it.
/// </summary>
[SupportedOSPlatform("windows")]
[ExcludeFromCodeCoverage(Justification = "Registry and Win32 wiring; the logic is UserPathFix, covered through a fake IUserPath.")]
internal sealed partial class WindowsUserPath : IUserPath
{
    private const string Key = "Environment";
    private const string Name = "Path";

    /// <inheritdoc />
    public string? Read()
    {
        using var key = Registry.CurrentUser.OpenSubKey(Key);
        return key?.GetValue(Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
    }

    /// <inheritdoc />
    public void Write(string value)
    {
        using (var key = Registry.CurrentUser.CreateSubKey(Key))
        {
            key.SetValue(Name, value, RegistryValueKind.ExpandString);
        }

        // WM_SETTINGCHANGE with "Environment", as the System Properties dialog sends: Explorer rereads the environment,
        // so shells it starts next get the new PATH. Best effort; the registry already holds the change.
        _ = SendMessageTimeoutW(HwndBroadcast, WmSettingChange, 0, "Environment", AbortIfHung, 5000, out _);
    }

    private static readonly nint HwndBroadcast = 0xffff;
    private const uint WmSettingChange = 0x001A;
    private const uint AbortIfHung = 0x0002;

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint SendMessageTimeoutW(nint window, uint message, nuint wParam, string lParam, uint flags, uint timeout, out nuint result);
}
