using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rig.Core.Omarchy;

/// <summary>
/// <c>config/omarchy.json</c> as the file spells it. Every section is optional, so a policy that leaves one out
/// checks and writes nothing for it.
/// </summary>
internal sealed record OmarchyConfig
{
    /// <summary>The idle timers, or null to leave them alone.</summary>
    public OmarchyIdleConfig? Idle { get; init; }

    /// <summary>Suspend and logind behaviour, or null to leave it alone.</summary>
    public OmarchySuspendConfig? Suspend { get; init; }

    /// <summary>Whether Omarchy's sleep-lock unit should run, or null to leave it alone.</summary>
    public bool? LockBeforeSuspend { get; init; }

    /// <summary>The theme slug to apply, or null to leave the theme alone.</summary>
    public string? Theme { get; init; }

    /// <summary>The AC and battery power profiles, or null to leave them alone.</summary>
    public OmarchyPowerConfig? Power { get; init; }

    /// <summary>The shell's base font size, or null to leave it alone.</summary>
    public OmarchyFontConfig? Font { get; init; }

    /// <summary>Settings deep-merged into <c>shell.json</c>, such as <c>bar</c>, <c>dock</c> and <c>plugins</c>.</summary>
    public JsonElement? Shell { get; init; }

    /// <summary>Hyprland's gaps and border, or null to leave them alone.</summary>
    public OmarchyHyprlandConfig? Hyprland { get; init; }
}

/// <summary>The <c>idle</c> section: seconds since idle began before the screensaver and the lock.</summary>
internal sealed record OmarchyIdleConfig
{
    /// <summary>Seconds before the screensaver starts; zero disables it.</summary>
    public int? ScreensaverSeconds { get; init; }

    /// <summary>Seconds before the session locks; zero disables the idle lock.</summary>
    public int? LockSeconds { get; init; }
}

/// <summary>The <c>suspend</c> section: the menu entry and the logind keys.</summary>
internal sealed record OmarchySuspendConfig
{
    /// <summary>Whether Suspend appears in Omarchy's system menu.</summary>
    public bool? Menu { get; init; }

    /// <summary>What closing the lid does.</summary>
    public string? Lid { get; init; }

    /// <summary>What the power key does.</summary>
    public string? PowerKey { get; init; }

    /// <summary>What the suspend key does.</summary>
    public string? SuspendKey { get; init; }

    /// <summary>What the hibernate key does.</summary>
    public string? HibernateKey { get; init; }

    /// <summary>What logind does when its own idle timer expires.</summary>
    public string? IdleAction { get; init; }
}

/// <summary>The <c>power</c> section: the profile Omarchy restores on AC and on battery.</summary>
internal sealed record OmarchyPowerConfig
{
    /// <summary>The profile for external power.</summary>
    public string? Ac { get; init; }

    /// <summary>The profile for battery power.</summary>
    public string? Battery { get; init; }
}

/// <summary>The <c>font</c> section: the shell's base font size.</summary>
internal sealed record OmarchyFontConfig
{
    /// <summary>The size in points.</summary>
    public int? BaseSize { get; init; }
}

/// <summary>The <c>hyprland</c> section: window spacing in px, named as Hyprland's <c>general</c> options are.</summary>
internal sealed record OmarchyHyprlandConfig
{
    /// <summary>The gap between windows.</summary>
    public int? GapsIn { get; init; }

    /// <summary>The gap between windows and the screen edge.</summary>
    public int? GapsOut { get; init; }

    /// <summary>The window border's width.</summary>
    public int? BorderSize { get; init; }
}

/// <summary>The <c>idle</c> object <c>rig omarchy sync</c> writes into <c>shell.json</c>.</summary>
internal sealed record ShellIdleConfig
{
    /// <summary>Seconds before the screensaver starts; zero disables it.</summary>
    public required int Screensaver { get; init; }

    /// <summary>Seconds before the session locks; zero disables the idle lock.</summary>
    public required int Lock { get; init; }
}

/// <summary>The compile-time serialization metadata for <c>config/omarchy.json</c> and the shell idle block.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    RespectNullableAnnotations = true)]
[JsonSerializable(typeof(OmarchyConfig))]
[JsonSerializable(typeof(ShellIdleConfig))]
internal sealed partial class OmarchyConfigJsonContext : JsonSerializerContext;
