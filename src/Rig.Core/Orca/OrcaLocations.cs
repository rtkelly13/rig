namespace Rig.Core.Orca;

/// <summary>The operating system an Orca instance runs on, which decides its default folders.</summary>
public enum OrcaOs
{
    /// <summary>macOS.</summary>
    MacOs,

    /// <summary>Windows.</summary>
    Windows,

    /// <summary>Linux and the other Unixes.</summary>
    Linux,
}

/// <summary>
/// Where one Orca instance keeps its files. With a user-data folder (<c>--orca-data</c>, or
/// <c>ORCA_USER_DATA_PATH</c>) everything sits under it; without one the default instance is used, and
/// <c>ORCA_RUNTIME_JSON</c> and <c>ORCA_PROFILE_DIR</c> may point its two files elsewhere.
/// </summary>
/// <param name="Variable">Reads an environment variable.</param>
/// <param name="Home">The user's home folder.</param>
/// <param name="Os">The operating system.</param>
public sealed record OrcaLocations(Func<string, string?> Variable, string Home, OrcaOs Os)
{
    /// <summary>The variable the <c>orca</c> CLI reads to pick an instance's user-data folder.</summary>
    public const string UserDataVariable = "ORCA_USER_DATA_PATH";

    /// <summary>The file Orca writes its runtime transports and token to at start-up.</summary>
    public const string RuntimeFile = "orca-runtime.json";

    /// <summary>This process's environment, home and operating system.</summary>
    public static OrcaLocations Current { get; } = new(
        Environment.GetEnvironmentVariable,
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        OperatingSystem.IsMacOS() ? OrcaOs.MacOs : OperatingSystem.IsWindows() ? OrcaOs.Windows : OrcaOs.Linux);

    /// <summary>The user-data folder <c>--orca-data</c> named, else the one <c>ORCA_USER_DATA_PATH</c> names, else null for the default instance.</summary>
    public string? UserData(string? flag) => Set(flag) ?? Set(Variable(UserDataVariable));

    /// <summary>The default instance's user-data folder on this platform.</summary>
    public string DefaultUserData()
    {
        var parent = Os switch
        {
            OrcaOs.MacOs => Path.Join(Home, "Library", "Application Support"),
            OrcaOs.Windows => Set(Variable("APPDATA")) ?? Path.Join(Home, "AppData", "Roaming"),
            _ => Set(Variable("XDG_CONFIG_HOME")) ?? Path.Join(Home, ".config"),
        };
        return Path.Join(parent, "orca");
    }

    /// <summary>The instance's <c>orca-runtime.json</c>.</summary>
    public string RuntimeMetadata(string? userData) =>
        userData is not null ? Path.Join(userData, RuntimeFile)
        : Set(Variable("ORCA_RUNTIME_JSON")) is { } configured ? ExpandHome(configured)
        : Path.Join(DefaultUserData(), RuntimeFile);

    /// <summary>The instance's local profile folder, which holds its settings database.</summary>
    public string ProfileDir(string? userData) =>
        userData is not null ? LocalDefault(userData)
        : Set(Variable("ORCA_PROFILE_DIR")) is { } configured ? ExpandHome(configured)
        : LocalDefault(DefaultUserData());

    private static string LocalDefault(string userData) => Path.Join(userData, "profiles", "local-default");

    private static string? Set(string? value) => string.IsNullOrEmpty(value) ? null : value;

    private string ExpandHome(string path) =>
        path == "~" ? Home
        : path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal) ? Path.Join(Home, path[2..])
        : path;
}
