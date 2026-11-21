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
    bool? SleepLockEnabled)
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
            installed ? ReadSleepLock(proc) : null);
    }

    private static JsonObject ReadShell(string path)
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
            return document?["font"] is TomlTable font && font.TryGetValue("base-size", out var size) && size is long value
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
        var ran = proc.Run(new ProcRequest(["systemctl", "--user", "is-enabled", SleepLockUnit]));
        if (!ran.Ok)
        {
            return ran.Code == 1 ? false : null;
        }

        return ran.OutOrEmpty switch
        {
            "enabled" or "enabled-runtime" => true,
            "disabled" or "masked" or "masked-runtime" => false,
            _ => null,
        };
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
