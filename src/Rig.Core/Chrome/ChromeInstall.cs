namespace Rig.Core.Chrome;

/// <summary>
/// Where the scheduled Chrome Tabs run keeps its state, and the <c>chrome-tabs</c> command
/// shim on PATH. Ported from <c>desktop_manager.chrome.install_location</c> and <c>desktop_manager.chrome.install</c>.
/// </summary>
public static class ChromeInstall
{
    /// <summary>How long a scheduled run waits for the machine to go idle.</summary>
    public const int DefaultWaitMinutes = 45;

    /// <summary>The command shim's file name, in <c>~/.local/bin</c>.</summary>
    public const string ShimName = "chrome-tabs.cmd";

    /// <summary>
    /// Where the scheduled run logs and traces: <c>%LOCALAPPDATA%\shared-utilities\chrome-tabs</c>.
    /// </summary>
    /// <param name="environment">Reads an environment variable.</param>
    /// <param name="home">The user's home, for when <c>LOCALAPPDATA</c> is unset.</param>
    public static string Root(Func<string, string?> environment, string home) =>
        Path.Join(Local(environment, home), "shared-utilities", "chrome-tabs");

    /// <summary>The <c>chrome-tabs.cmd</c> shim: <c>~/.local/bin/chrome-tabs.cmd</c>.</summary>
    /// <param name="home">The user's home.</param>
    public static string ShimPath(string home) => Path.Join(home, ".local", "bin", ShimName);

    /// <summary>
    /// <c>rig.exe</c> inside the runtime's <c>current</c> copy, which <c>rig runtime install</c>
    /// maintains: <c>%LOCALAPPDATA%\shared-utilities\runtime\current\.venv\Scripts\rig.exe</c>.
    /// </summary>
    /// <param name="environment">Reads an environment variable.</param>
    /// <param name="home">The user's home, for when <c>LOCALAPPDATA</c> is unset.</param>
    public static string RuntimeRig(Func<string, string?> environment, string home) =>
        Path.Join(Local(environment, home), "shared-utilities", "runtime", "current", ".venv", "Scripts", "rig.exe");

    /// <summary>The shim's text: a batch file that runs <paramref name="rig"/> <c>chrome-tabs</c> with every argument.</summary>
    /// <param name="rig">The <c>rig.exe</c> the shim runs.</param>
    public static string RenderShim(string rig) => $"@echo off\r\n\"{rig}\" chrome-tabs %*\r\n";

    private static string Local(Func<string, string?> environment, string home)
    {
        ArgumentNullException.ThrowIfNull(environment);
        return environment("LOCALAPPDATA") is { Length: > 0 } set ? set : Path.Join(home, "AppData", "Local");
    }
}
