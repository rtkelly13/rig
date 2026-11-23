using System.Globalization;
using System.Text.Json.Nodes;
using Rig.Core.Text;

namespace Rig.Core.Omarchy;

/// <summary>One setting as Omarchy has it and as the policy wants it.</summary>
/// <param name="Area">What it belongs to: <c>omarchy</c>, <c>idle</c>, <c>shell</c>, <c>theme</c>, <c>power</c>, <c>font</c>, <c>suspend</c> or <c>logind</c>.</param>
/// <param name="Setting">The setting's name within the area.</param>
/// <param name="Have">What Omarchy has.</param>
/// <param name="Want">What the policy declares.</param>
/// <param name="Status">Whether they agree, differ, or could not be checked.</param>
/// <param name="Fix">The command or Settings change that makes them agree; empty when they do.</param>
public sealed record OmarchyCheck(
    string Area,
    string Setting,
    string Have,
    string Want,
    CheckStatus Status,
    string Fix = "")
{
    /// <summary>Whether Omarchy already has what the policy wants.</summary>
    public bool Ok => Status == CheckStatus.Ok;
}

/// <summary><c>rig omarchy doctor</c>: every difference between <c>config/omarchy.json</c> and this machine.</summary>
public static class OmarchyPlan
{
    /// <summary>The keys the logind drop-in writes, and the policy field each comes from.</summary>
    private static readonly (string Key, Func<SuspendPolicy, string?> Value)[] LogindKeys =
    [
        ("HandleLidSwitch", policy => policy.Lid),
        ("HandleLidSwitchExternalPower", policy => policy.Lid),
        ("HandleLidSwitchDocked", policy => policy.Lid),
        ("HandlePowerKey", policy => policy.PowerKey),
        ("HandleSuspendKey", policy => policy.SuspendKey),
        ("HandleHibernateKey", policy => policy.HibernateKey),
        ("IdleAction", policy => policy.IdleAction),
    ];

    /// <summary>Every setting the policy declares, with how this machine stands.</summary>
    /// <param name="policy">What the machine should look like.</param>
    /// <param name="state">What it looks like now.</param>
    public static IReadOnlyList<OmarchyCheck> Compare(OmarchyPolicy policy, OmarchyState state)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(state);
        if (!state.Installed)
        {
            return
            [
                new("omarchy", "install", "missing", state.InstallRoot, CheckStatus.Fail,
                    "Install Omarchy, or point rig at the machine that runs it"),
            ];
        }

        List<OmarchyCheck> checks = [new("omarchy", "install", "installed", state.InstallRoot, CheckStatus.Ok)];
        if (policy.Idle is { } idle)
        {
            var current = state.Shell["idle"] as JsonObject;
            checks.Add(IdleCheck(current, "screensaver", idle.ScreensaverSeconds, "Omarchy Settings → Idle: set the screensaver timeout"));
            checks.Add(IdleCheck(current, "lock", idle.LockSeconds, "Omarchy Settings → Idle: set the lock timeout"));
        }

        if (policy.Shell is { } shell)
        {
            checks.AddRange(ShellDocument.Differences(state.Shell, shell)
                .Select(difference => new OmarchyCheck(
                    "shell",
                    difference.Path,
                    difference.Have,
                    difference.Want,
                    CheckStatus.Fail,
                    "Edit ~/.config/omarchy/shell.json or run rig omarchy sync --apply")));
        }

        if (policy.Theme is { } theme)
        {
            checks.Add(Simple("theme", "name", state.Theme, theme, $"omarchy theme set {theme}"));
        }

        if (policy.Power is { } power)
        {
            checks.Add(Simple("power", "ac", state.PowerAc, power.Ac, $"omarchy powerprofiles set ac {power.Ac}"));
            checks.Add(Simple("power", "battery", state.PowerBattery, power.Battery, $"omarchy powerprofiles set battery {power.Battery}"));
        }

        if (policy.Font is { } font)
        {
            checks.Add(Simple("font", "base_size", Number(state.FontBaseSize), Number(font.BaseSize), $"Run omarchy display text size {font.BaseSize}"));
        }

        if (policy.Suspend is { } suspend)
        {
            if (suspend.Menu is { } menu)
            {
                checks.Add(new(
                    "suspend",
                    "menu",
                    state.SuspendOff ? "hidden" : "available",
                    menu ? "available" : "hidden",
                    state.SuspendOff != menu ? CheckStatus.Ok : CheckStatus.Fail,
                    "omarchy toggle suspend"));
            }

            var logind = Logind(state.LogindText);
            checks.AddRange(LogindKeys
                .Where(key => key.Value(suspend) is not null)
                .Select(key => new OmarchyCheck(
                    "logind",
                    key.Key,
                    logind.GetValueOrDefault(key.Key, "(unset)"),
                    key.Value(suspend)!,
                    string.Equals(logind.GetValueOrDefault(key.Key), key.Value(suspend), StringComparison.Ordinal) ? CheckStatus.Ok : CheckStatus.Fail,
                    "Run rig omarchy sync --apply as your user; it asks sudo to write the drop-in")));
        }

        if (policy.LockBeforeSuspend is { } lockBeforeSuspend)
        {
            checks.Add(state.SleepLockEnabled is { } enabled
                ? new(
                    "logind",
                    "lock-before-suspend",
                    enabled ? "enabled" : "disabled",
                    lockBeforeSuspend ? "enabled" : "disabled",
                    enabled == lockBeforeSuspend ? CheckStatus.Ok : CheckStatus.Fail,
                    $"systemctl --user {(lockBeforeSuspend ? "enable --now" : "disable --now")} {OmarchyState.SleepLockUnit}")
                : new("logind", "lock-before-suspend", "unknown", lockBeforeSuspend ? "enabled" : "disabled", CheckStatus.Skip,
                    "systemd's user manager did not answer; check it in a login session"));
        }

        return checks;
    }

    /// <summary>The desired logind drop-in's body, one <c>Key=Value</c> per line, for the fields the policy names.</summary>
    /// <param name="suspend">The suspend section of the policy.</param>
    public static string LogindContents(SuspendPolicy suspend)
    {
        ArgumentNullException.ThrowIfNull(suspend);
        var lines = LogindKeys
            .Where(key => key.Value(suspend) is not null)
            .Select(key => $"{key.Key}={key.Value(suspend)}");
        return string.Join('\n', lines) + '\n';
    }

    private static OmarchyCheck Simple(string area, string setting, string? have, string want, string fix) =>
        new(area, setting, have ?? "(unset)", want, string.Equals(have, want, StringComparison.Ordinal) ? CheckStatus.Ok : CheckStatus.Fail, fix);

    private static OmarchyCheck IdleCheck(JsonObject? idle, string key, int want, string fix)
    {
        var have = Number(JsonShape.AsInt32(idle?[key]));
        return new("idle", key, have, Number(want), string.Equals(have, Number(want), StringComparison.Ordinal) ? CheckStatus.Ok : CheckStatus.Fail, fix);
    }

    private static string Number(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "(unset)";

    private static Dictionary<string, string> Logind(string? text)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (text is null)
        {
            return values;
        }

        foreach (var line in text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith('[', StringComparison.Ordinal) || line.StartsWith('#', StringComparison.Ordinal) || line.StartsWith(';', StringComparison.Ordinal))
            {
                continue;
            }

            var split = line.IndexOf('=', StringComparison.Ordinal);
            if (split > 0)
            {
                values[line[..split].Trim()] = line[(split + 1)..].Trim();
            }
        }

        return values;
    }
}
