namespace Rig.Core.Omarchy;

/// <summary>
/// Where Omarchy keeps this machine's settings, and the one system file <c>rig omarchy</c> owns.
/// </summary>
/// <param name="Home">The user's home folder.</param>
/// <param name="ConfigHome"><c>$XDG_CONFIG_HOME</c>, which holds <c>omarchy/shell.json</c>.</param>
/// <param name="StateHome"><c>$XDG_STATE_HOME</c>, which holds the current theme, toggles and power profiles.</param>
/// <param name="SystemConfigRoot">The system configuration root, <c>/etc</c> on Omarchy.</param>
/// <param name="InstallRoot">Omarchy's packaged files, which prove it is installed.</param>
public sealed record OmarchyPaths(
    string Home,
    string ConfigHome,
    string StateHome,
    string SystemConfigRoot = "/etc",
    string InstallRoot = "/usr/share/omarchy")
{
    /// <summary>This process's environment and home folder.</summary>
    public static OmarchyPaths Current { get; } = For(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Environment.GetEnvironmentVariable);

    /// <summary>This machine's paths for <paramref name="home"/> and <paramref name="variable"/>.</summary>
    /// <param name="home">The user's home folder.</param>
    /// <param name="variable">Reads the XDG variables; a missing or blank value takes the default.</param>
    public static OmarchyPaths For(string home, Func<string, string?> variable)
    {
        ArgumentNullException.ThrowIfNull(variable);
        var config = variable("XDG_CONFIG_HOME");
        var state = variable("XDG_STATE_HOME");
        return new(
            home,
            string.IsNullOrWhiteSpace(config) ? Path.Join(home, ".config") : config,
            string.IsNullOrWhiteSpace(state) ? Path.Join(home, ".local", "state") : state);
    }

    /// <summary>Omarchy's user configuration folder, <c>~/.config/omarchy</c>.</summary>
    public string ConfigDir => Path.Join(ConfigHome, "omarchy");

    /// <summary>The shell's settings, <c>~/.config/omarchy/shell.json</c>.</summary>
    public string ShellFile => Path.Join(ConfigDir, "shell.json");

    /// <summary>The shell's TOML settings, which hold the font size.</summary>
    public string ShellTomlFile => Path.Join(ConfigDir, "shell.toml");

    /// <summary>Omarchy's runtime state folder, <c>~/.local/state/omarchy</c>.</summary>
    public string StateDir => Path.Join(StateHome, "omarchy");

    /// <summary>The applied theme's slug, written by <c>omarchy theme set</c>.</summary>
    public string ThemeNameFile => Path.Join(StateDir, "current", "theme.name");

    /// <summary>One of Omarchy's state toggles, such as <c>suspend-off</c>.</summary>
    /// <param name="name">The toggle's file name.</param>
    public string ToggleFile(string name) => Path.Join(StateDir, "toggles", name);

    /// <summary>Omarchy's remembered power profile for <c>ac</c> or <c>battery</c>.</summary>
    /// <param name="source"><c>ac</c> or <c>battery</c>.</param>
    public string PowerProfileFile(string source) => Path.Join(StateDir, "powerprofiles", source);

    /// <summary>The logind drop-in rig owns: <c>/etc/systemd/logind.conf.d/90-rig-omarchy.conf</c>.</summary>
    public string LogindDropIn => Path.Join(SystemConfigRoot, "systemd", "logind.conf.d", "90-rig-omarchy.conf");
}
