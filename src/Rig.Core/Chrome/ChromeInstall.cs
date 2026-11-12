namespace Rig.Core.Chrome;

/// <summary>
/// Where the scheduled Chrome Tabs run is installed and logs, and how long it may take. Ported from
/// <c>desktop_manager.chrome.install_location</c>.
/// </summary>
public static class ChromeInstall
{
    /// <summary>The scheduled run's log, under <see cref="Root"/>.</summary>
    public const string LogName = "organise.log";

    /// <summary>How long a scheduled run waits for the machine to go idle.</summary>
    public const int DefaultWaitMinutes = 45;

    /// <summary>How long Task Scheduler lets a run take.</summary>
    public const int TimeLimitMinutes = 55;

    /// <summary>
    /// Where the scheduled run logs and traces: <c>%LOCALAPPDATA%\shared-utilities\chrome-tabs</c>.
    /// </summary>
    /// <param name="environment">Reads an environment variable.</param>
    /// <param name="home">The user's home, for when <c>LOCALAPPDATA</c> is unset.</param>
    public static string Root(Func<string, string?> environment, string home)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var local = environment("LOCALAPPDATA") is { Length: > 0 } set ? set : Path.Join(home, "AppData", "Local");
        return Path.Join(local, "shared-utilities", "chrome-tabs");
    }
}
