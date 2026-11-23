namespace Rig.Core.Chrome.Extension;

/// <summary>The names the extension, its native host and rig agree on.</summary>
public static class ExtensionProtocol
{
    /// <summary>The native-messaging host name the extension connects to.</summary>
    public const string HostName = "dev.ryankelly.rig_chrome_tabs";

    /// <summary>
    /// The extension's id, fixed by the public <c>key</c> in <c>extensions/chrome-tabs/manifest.json</c> so the host
    /// manifest can name it wherever the extension is loaded from. A test derives it from the key.
    /// </summary>
    public const string ExtensionId = "gpfpmehjedofmgdbecdcieeempijapej";

    /// <summary>The origin Chrome passes the host, and the host manifest allows.</summary>
    public const string Origin = "chrome-extension://" + ExtensionId + "/";

    /// <summary>Answered by the host itself: the profile's account, the extension's version and the host's pid.</summary>
    public const string HelloCommand = "hello";

    /// <summary>Every normal window with its tabs, the tab groups, and the focused window.</summary>
    public const string WindowsCommand = "windows";

    /// <summary>Closes <c>tabId</c>.</summary>
    public const string CloseCommand = "close";

    /// <summary>Makes <c>tabId</c> its window's active tab.</summary>
    public const string ActivateCommand = "activate";

    /// <summary>Puts <c>tabIds</c> into <c>groupId</c>, or a new group in <c>windowId</c>, and sets <c>title</c>.</summary>
    public const string GroupCommand = "group";

    /// <summary>Takes <c>tabIds</c> out of their groups.</summary>
    public const string UngroupCommand = "ungroup";

    /// <summary>Moves <c>tabId</c> to the end of <c>windowId</c>.</summary>
    public const string MoveCommand = "move";
}

/// <summary>Where the native hosts' sockets live.</summary>
public static class ExtensionPlaces
{
    /// <summary>
    /// The folder each running host puts its socket in: <c>$XDG_RUNTIME_DIR/rig/chrome-tabs</c>, which only this user
    /// can reach and which logout clears, else <c>~/.local/state/rig/chrome-tabs</c>.
    /// </summary>
    public static string SocketFolder(Func<string, string?> environment, string home)
    {
        ArgumentNullException.ThrowIfNull(environment);
        return environment("XDG_RUNTIME_DIR") is { Length: > 0 } runtime
            ? Path.Join(runtime, "rig", "chrome-tabs")
            : Path.Join(home, ".local", "state", "rig", "chrome-tabs");
    }

    /// <summary>The socket of the host with process id <paramref name="pid"/>.</summary>
    public static string SocketFor(string folder, int pid) => Path.Join(folder, $"{pid}.sock");

    /// <summary>Creates <paramref name="folder"/> readable only by this user, where the platform has modes.</summary>
    public static void CreatePrivate(string folder)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(folder);
        }
        else
        {
            Directory.CreateDirectory(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}
