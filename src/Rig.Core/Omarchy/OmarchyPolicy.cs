using System.Text.Json;
using System.Text.Json.Nodes;
using Rig.Core.Config;

namespace Rig.Core.Omarchy;

/// <summary>Omarchy's idle cycle: when the screensaver starts and when the session locks, in seconds since idle began.</summary>
/// <param name="ScreensaverSeconds">Zero disables the screensaver.</param>
/// <param name="LockSeconds">Zero disables the idle lock.</param>
public sealed record IdlePolicy(int ScreensaverSeconds, int LockSeconds);

/// <summary>Sleep behaviour: the system menu entry and what logind does with the lid and the power keys.</summary>
/// <param name="Menu">Whether Suspend appears in Omarchy's system menu.</param>
/// <param name="Lid">What closing the lid does, one of <see cref="Actions"/>.</param>
/// <param name="PowerKey">What the power key does, one of <see cref="Actions"/>.</param>
/// <param name="SuspendKey">What the suspend key does, one of <see cref="Actions"/>.</param>
/// <param name="HibernateKey">What the hibernate key does, one of <see cref="Actions"/>.</param>
/// <param name="IdleAction">What logind does when its own idle timer expires, one of <see cref="Actions"/>.</param>
public sealed record SuspendPolicy(
    bool? Menu = null,
    string? Lid = null,
    string? PowerKey = null,
    string? SuspendKey = null,
    string? HibernateKey = null,
    string? IdleAction = null)
{
    /// <summary>The actions logind accepts for these keys.</summary>
    public static readonly IReadOnlyList<string> Actions =
    [
        "ignore", "poweroff", "reboot", "halt", "suspend", "hibernate",
        "hybrid-sleep", "suspend-then-hibernate", "lock", "kexec", "soft-reboot",
    ];
}

/// <summary>The power profile Omarchy restores on AC and on battery.</summary>
/// <param name="Ac">The profile for external power, such as <c>performance</c>.</param>
/// <param name="Battery">The profile for battery power, such as <c>power-saver</c>.</param>
public sealed record PowerPolicy(string Ac, string Battery);

/// <summary>Omarchy's text size in px: the shell's <c>shell.toml</c> base size, with GTK and the terminals scaled to match.</summary>
/// <param name="BaseSize">The size in points.</param>
public sealed record FontPolicy(int BaseSize);

/// <summary>Hyprland's window spacing, in px: the gaps between windows, around them at the screen edge, and their border.</summary>
/// <param name="GapsIn">Hyprland's <c>general:gaps_in</c>; Omarchy's default is 5.</param>
/// <param name="GapsOut">Hyprland's <c>general:gaps_out</c>; Omarchy's default is 10.</param>
/// <param name="BorderSize">Hyprland's <c>general:border_size</c>; Omarchy's default is 2.</param>
public sealed record HyprlandPolicy(int GapsIn, int GapsOut, int BorderSize);

/// <summary>
/// What <c>config/omarchy.json</c> declares for this machine. A section left out is not checked or written.
/// </summary>
/// <param name="Idle">The idle timers, or null to leave them alone.</param>
/// <param name="Suspend">Suspend and logind behaviour, or null to leave it alone.</param>
/// <param name="LockBeforeSuspend">Whether Omarchy's sleep-lock user unit should run, or null to leave it alone.</param>
/// <param name="Theme">The theme slug to apply, such as <c>tokyo-night</c>, or null to leave the theme alone.</param>
/// <param name="Power">The AC and battery power profiles, or null to leave them alone.</param>
/// <param name="Font">The shell's base font size, or null to leave it alone.</param>
/// <param name="Shell">Settings deep-merged into <c>shell.json</c>, such as <c>bar</c>, <c>dock</c> and <c>plugins</c>.</param>
/// <param name="Hyprland">Hyprland's gaps and border, or null to leave them alone.</param>
public sealed record OmarchyPolicy(
    IdlePolicy? Idle,
    SuspendPolicy? Suspend,
    bool? LockBeforeSuspend,
    string? Theme,
    PowerPolicy? Power,
    FontPolicy? Font,
    JsonObject? Shell,
    HyprlandPolicy? Hyprland = null)
{
    /// <summary>The policy file's path under a rig checkout.</summary>
    public static readonly string RelativePath = Path.Join("config", "omarchy.json");

    /// <summary>A policy that declares nothing.</summary>
    public static readonly OmarchyPolicy Empty = new(null, null, null, null, null, null, null);

    /// <summary>The largest idle timeout Omarchy accepts: a day.</summary>
    public const int MaxIdleSeconds = 24 * 60 * 60;

    /// <summary>The smallest text size <c>omarchy display text size</c> accepts, in px.</summary>
    public const int MinFontSize = 9;

    /// <summary>The largest text size <c>omarchy display text size</c> accepts, in px.</summary>
    public const int MaxFontSize = 20;

    /// <summary>The largest gap or border the policy accepts, in px.</summary>
    public const int MaxSpacing = 100;

    /// <summary>Reads the policy file at <paramref name="path"/>.</summary>
    /// <exception cref="InvalidDataException">The file is malformed.</exception>
    public static OmarchyPolicy Load(string path)
    {
        try
        {
            return Decode(ConfigLoader.Load(path, OmarchyConfigJsonContext.Default.OmarchyConfig), path);
        }
        catch (ConfigLoadException e)
        {
            throw new InvalidDataException(e.Message, e);
        }
    }

    /// <summary>Decodes a policy document.</summary>
    /// <param name="text">The JSON document.</param>
    /// <param name="source">The file or document the text came from, named in a rejection.</param>
    /// <exception cref="InvalidDataException">It is malformed: not JSON, a field of the wrong type, or an unknown action.</exception>
    public static OmarchyPolicy Parse(string text, string source = "omarchy policy")
    {
        ArgumentNullException.ThrowIfNull(text);
        try
        {
            return Decode(ConfigLoader.Parse(text, source, OmarchyConfigJsonContext.Default.OmarchyConfig), source);
        }
        catch (ConfigLoadException e)
        {
            throw new InvalidDataException(e.Message, e);
        }
    }

    private static OmarchyPolicy Decode(OmarchyConfig config, string source) => new(
        config.Idle is { } idle
            ? new IdlePolicy(
                RequiredInt(idle.ScreensaverSeconds, "idle.screensaver_seconds", source),
                RequiredInt(idle.LockSeconds, "idle.lock_seconds", source))
            : null,
        config.Suspend is { } suspend
            ? new SuspendPolicy(
                suspend.Menu,
                Action(suspend.Lid, "suspend.lid", source),
                Action(suspend.PowerKey, "suspend.power_key", source),
                Action(suspend.SuspendKey, "suspend.suspend_key", source),
                Action(suspend.HibernateKey, "suspend.hibernate_key", source),
                Action(suspend.IdleAction, "suspend.idle_action", source))
            : null,
        config.LockBeforeSuspend,
        Text(config.Theme, "theme", source),
        config.Power is { } power
            ? new PowerPolicy(RequiredText(power.Ac, "power.ac", source), RequiredText(power.Battery, "power.battery", source))
            : null,
        config.Font is { } font
            ? new FontPolicy(RequiredInt(font.BaseSize, "font.base_size", source, MinFontSize, MaxFontSize))
            : null,
        ShellPolicy(config.Shell, source),
        config.Hyprland is { } hyprland
            ? new HyprlandPolicy(
                RequiredInt(hyprland.GapsIn, "hyprland.gaps_in", source, 0, MaxSpacing),
                RequiredInt(hyprland.GapsOut, "hyprland.gaps_out", source, 0, MaxSpacing),
                RequiredInt(hyprland.BorderSize, "hyprland.border_size", source, 0, MaxSpacing))
            : null);

    private static int RequiredInt(int? value, string path, string source, int minimum = 0, int maximum = MaxIdleSeconds) =>
        value is { } number && number >= minimum && number <= maximum
            ? number
            : throw new InvalidDataException($"{source}: {path}: a whole number from {minimum} to {maximum}");

    private static string RequiredText(string? value, string path, string source) =>
        Text(value, path, source) ?? throw new InvalidDataException($"{source}: {path}: must not be blank");

    private static string? Text(string? value, string path, string source) =>
        value is null
            ? null
            : value.Trim() is { Length: > 0 } trimmed
                ? trimmed
                : throw new InvalidDataException($"{source}: {path}: must not be blank");

    private static string? Action(string? value, string path, string source) =>
        value is null
            ? null
            : SuspendPolicy.Actions.Contains(value, StringComparer.Ordinal)
                ? value
                : throw new InvalidDataException($"{source}: {path}: one of {string.Join(", ", SuspendPolicy.Actions)}");

    private static JsonObject? ShellPolicy(JsonElement? element, string source)
    {
        if (element is not { } shell)
        {
            return null;
        }

        if (shell.ValueKind != JsonValueKind.Object
            || JsonNode.Parse(shell.GetRawText()) is not JsonObject document)
        {
            throw new InvalidDataException($"{source}: shell: an object");
        }

        if (document.ContainsKey("idle"))
        {
            throw new InvalidDataException($"{source}: shell.idle: use the top-level idle section");
        }

        return (JsonObject)document.DeepClone();
    }
}
