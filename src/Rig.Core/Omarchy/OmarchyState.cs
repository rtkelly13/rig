using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Rig.Core.Processes;
using Rig.Core.Text;
using Tomlyn;
using Tomlyn.Model;

namespace Rig.Core.Omarchy;

/// <summary>What Omarchy has on this machine, read once for the doctor and the sync.</summary>
/// <param name="Installed">Whether Omarchy's packaged files are present.</param>
/// <param name="InstallRoot">Where they were looked for, named when they are missing.</param>
/// <param name="Shell">The parsed <c>shell.json</c>; a missing file reads as the default document.</param>
/// <param name="Theme">The applied theme's slug, or null when no theme is recorded.</param>
/// <param name="PowerAc">The remembered power profile for AC, or null.</param>
/// <param name="PowerBattery">The remembered power profile for battery, or null.</param>
/// <param name="SuspendOff">Whether Omarchy's <c>suspend-off</c> toggle hides Suspend from the system menu.</param>
/// <param name="FontBaseSize">The shell's base font size from <c>shell.toml</c>, or null.</param>
/// <param name="LogindText">The logind drop-in rig owns, or null when it does not exist.</param>
/// <param name="SleepLockEnabled">Whether the sleep-lock user unit is enabled, or null when systemd could not be asked.</param>
/// <param name="Hyprland">Hyprland's live gaps and border, or null when Hyprland could not be asked.</param>
public sealed record OmarchyState(
    bool Installed,
    string InstallRoot,
    JsonObject Shell,
    string? Theme,
    string? PowerAc,
    string? PowerBattery,
    bool SuspendOff,
    int? FontBaseSize,
    string? LogindText,
    bool? SleepLockEnabled,
    HyprlandSpacing? Hyprland = null)
{
    /// <summary>The sleep-lock unit Omarchy ships to lock the session before it suspends.</summary>
    public const string SleepLockUnit = "omarchy-sleep-lock.service";

    /// <summary>Reads this machine's Omarchy state.</summary>
    /// <param name="paths">Where Omarchy keeps its files.</param>
    /// <param name="proc">Asks systemd about the sleep-lock unit.</param>
    /// <exception cref="InvalidDataException"><c>shell.json</c> or <c>shell.toml</c> is malformed.</exception>
    public static OmarchyState Read(OmarchyPaths paths, IProc proc)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(proc);
        var installed = Directory.Exists(paths.InstallRoot);
        return new(
            installed,
            paths.InstallRoot,
            ReadShell(paths.ShellFile),
            Text(paths.ThemeNameFile),
            Text(paths.PowerProfileFile("ac")),
            Text(paths.PowerProfileFile("battery")),
            File.Exists(paths.ToggleFile("suspend-off")),
            ReadFont(paths.ShellTomlFile),
            Text(paths.LogindDropIn),
            installed ? ReadSleepLock(proc) : null,
            installed ? ReadHyprland(proc) : null);
    }

    /// <summary>The parsed <c>shell.json</c>, or the default document when it does not exist.</summary>
    internal static JsonObject ReadShell(string path)
    {
        if (!File.Exists(path))
        {
            return new JsonObject { ["version"] = 1 };
        }

        return JsonFile.Parse(path) as JsonObject
            ?? throw new InvalidDataException($"{path}: expected a JSON object");
    }

    private static int? ReadFont(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var text = File.ReadAllText(path);
        try
        {
            var document = TomlSerializer.Deserialize<TomlTable>(text);
            return document is not null && document.TryGetValue("font", out var table) && table is TomlTable font
                && font.TryGetValue("base-size", out var size) && size is long value
                ? (int)value
                : null;
        }
        catch (TomlException e)
        {
            throw new InvalidDataException($"{path}: {e.Message}", e);
        }
    }

    private static bool? ReadSleepLock(IProc proc)
    {
        // is-enabled exits 1 for a disabled unit and for a user manager it cannot reach, so the
        // printed state decides: no recognised state reads as unknown, never as disabled.
        var ran = proc.Run(new ProcRequest(["systemctl", "--user", "is-enabled", SleepLockUnit]));
        if (ran.Code == Ran.NotFound)
        {
            return null;
        }

        return ran.Out.Trim() switch
        {
            "enabled" or "enabled-runtime" => true,
            "disabled" or "masked" or "masked-runtime" => false,
            _ => null,
        };
    }

    // hyprctl answers from the running compositor, so this is what is on screen, whichever file set it.
    private static HyprlandSpacing? ReadHyprland(IProc proc)
    {
        var values = new string?[3];
        string[] options = ["gaps_in", "gaps_out", "border_size"];
        for (var index = 0; index < options.Length; index++)
        {
            var ran = proc.Run(new ProcRequest(["hyprctl", "getoption", "general:" + options[index], "-j"]));
            if (!ran.Ok)
            {
                return null;
            }

            values[index] = HyprOption(ran.Out);
        }

        return new(values[0], values[1], values[2]);
    }

    /// <summary>One <c>hyprctl getoption -j</c> answer as text: its <c>int</c>, or its CSS gap with four equal sides collapsed to one.</summary>
    internal static string? HyprOption(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (root.TryGetProperty("int", out var number) && number.TryGetInt32(out var value))
            {
                return value.ToString(CultureInfo.InvariantCulture);
            }

            if (root.TryGetProperty("css", out var css) && css.GetString() is { } sides)
            {
                var parts = sides.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                return parts.Length > 0 && parts.All(part => part == parts[0]) ? parts[0] : sides.Trim();
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Text(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var text = File.ReadAllText(path).Trim();
        return text.Length > 0 ? text : null;
    }
}

/// <summary>Hyprland's live window spacing, each as <c>hyprctl</c> reports it, or null when it reported none.</summary>
/// <param name="GapsIn"><c>general:gaps_in</c>: one number, or four when the sides differ.</param>
/// <param name="GapsOut"><c>general:gaps_out</c>: one number, or four when the sides differ.</param>
/// <param name="BorderSize"><c>general:border_size</c>.</param>
public sealed record HyprlandSpacing(string? GapsIn, string? GapsOut, string? BorderSize);
