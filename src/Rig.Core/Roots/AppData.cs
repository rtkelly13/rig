namespace Rig.Core.Roots;

/// <summary>
/// The per-user folder for rig's app state (runtime copies, logs, the shared database), where <c>rig.roots.app_data_dir</c>
/// put it, so the C# and the Python find the same installs.
/// </summary>
public static class AppData
{
    /// <summary>The folder's name, kept from before the rename so existing state is found.</summary>
    public const string Name = "shared-utilities";

    /// <summary>
    /// <c>LOCALAPPDATA</c> wins on every platform, not only Windows, so tests can point it at a temp folder. Otherwise
    /// <c>platformdirs</c>' answer: <c>XDG_DATA_HOME</c> when set (on macOS too), then <c>~/Library/Application Support</c>
    /// on macOS and <c>~/.local/share</c> elsewhere.
    /// </summary>
    public static string Resolve(string? localAppData, string? xdgDataHome, string home, bool macOs)
    {
        ArgumentNullException.ThrowIfNull(home);
        if (!string.IsNullOrEmpty(localAppData))
        {
            return Path.Join(localAppData, Name);
        }

        if (xdgDataHome?.Trim() is { Length: > 0 } xdg)
        {
            return Path.Join(xdg, Name);
        }

        return macOs ? Path.Join(home, "Library", "Application Support", Name) : Path.Join(home, ".local", "share", Name);
    }

    /// <summary>The folder for this process's user and environment.</summary>
    public static string Current() =>
        Resolve(
            Environment.GetEnvironmentVariable("LOCALAPPDATA")
                ?? (OperatingSystem.IsWindows() ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) : null),
            Environment.GetEnvironmentVariable("XDG_DATA_HOME"),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            OperatingSystem.IsMacOS());
}
